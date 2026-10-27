using System;
using System.Collections.Generic;
using System.IO;

namespace Courier
{
    /// <summary>
    /// <c>results/</c> 的回收（SPEC §15.2）。
    /// <para>
    /// <c>results/</c> 是幂等检查（§10.1）与"这条命令处理过没有"的唯一依据，所以 MUST NOT 被第三方搬动或删除
    /// （§2、§15.2）。只有服务者自己可以清理，而且必须**先写墓碑再删文件**：读者看到 <c>gc.json</c>
    /// 才知道"某点之前的结果已被回收"，而不是误判成"从没处理过"。
    /// </para>
    /// <para>
    /// 顺序是硬性的，不能反：先删文件再写墓碑，中间崩溃就会留下"结果凭空消失且没有任何解释"的状态。
    /// </para>
    /// </summary>
    internal static class CourierResultsGc
    {
        /// <summary>墓碑文件名。它本身永远不是删除对象（SPEC §15.2）。</summary>
        internal const string TOMBSTONE_FILE_NAME = "gc.json";

        /// <summary>
        /// 回收 <paramref name="cutoffUtc"/> 之前完成的响应。
        /// </summary>
        /// <param name="resultsDirectory">端点自己的 <c>results/</c>。</param>
        /// <param name="commandsDirectory">端点自己的 <c>commands/</c>；非 null 时，命令还在就说明这条请求尚未处理完，跳过。</param>
        /// <param name="claimsDirectory">端点自己的 <c>claims/</c>；非 null 时，claim 还在就说明它正被另一个服务循环持有，跳过。</param>
        /// <param name="cutoffUtc">完成时刻早于它的响应会被删除。</param>
        /// <returns>实际删除的响应文件数。</returns>
        /// <exception cref="IOException">写墓碑失败时抛出——墓碑写不出来就 MUST NOT 开始删文件。</exception>
        internal static int Collect(string resultsDirectory, string? commandsDirectory, string? claimsDirectory, DateTime cutoffUtc)
        {
            var removable = new List<string>();
            var survivors = new List<string>();
            if (!ClassifyResults(resultsDirectory, commandsDirectory, claimsDirectory, cutoffUtc, removable, survivors)) return 0;

            // removedBefore 取"删除后仍存在的最小 requestId"。删除是确定性的，所以这个下界可以在删之前算出来：
            // 活下来的那些里序数最小的那个。全部都会被删掉时是空串——不猜一个不存在的 ID。
            string removedBefore = Smallest(survivors);

            // SPEC §15.2 第 1 步：先写墓碑。写失败直接抛出：没有墓碑就不能开始删。
            WriteTombstone(resultsDirectory, cutoffUtc, removedBefore);

            int removed = DeleteRemovable(removable, survivors);

            // 更正一次的墓碑：删除失败会让实际幸存集合比预估的多，removedBefore 必须跟着退回到真实边界，
            // 否则读者会以为"这条结果已经被回收了"。只在值确实变了时才写，正常路径上没有第二次落盘。
            string actual = Smallest(survivors);
            if (!string.Equals(actual, removedBefore, StringComparison.Ordinal))
            {
                WriteTombstone(resultsDirectory, cutoffUtc, actual);
            }

            return removed;
        }

        /// <summary>
        /// 把 <c>results/</c> 里的文件分成"该删"与"该留"，并收集留下来那些的 requestId。
        /// </summary>
        /// <param name="resultsDirectory">端点自己的 <c>results/</c>。</param>
        /// <param name="commandsDirectory">端点自己的 <c>commands/</c>，可为 null。</param>
        /// <param name="claimsDirectory">端点自己的 <c>claims/</c>，可为 null。</param>
        /// <param name="cutoffUtc">完成时刻早于它的响应会被删除。</param>
        /// <param name="removable">输出：待删除文件的绝对路径。</param>
        /// <param name="survivors">输出：留下来的 requestId。</param>
        /// <returns>目录本身可读时为 true；目录不存在或读不到时为 false（此时不用写墓碑）。</returns>
        private static bool ClassifyResults(string resultsDirectory, string? commandsDirectory, string? claimsDirectory, DateTime cutoffUtc, List<string> removable, List<string> survivors)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(resultsDirectory, "*" + CourierProtocol.JSON_EXTENSION);
            }
            catch (Exception caught) when (caught is DirectoryNotFoundException || caught is IOException || caught is UnauthorizedAccessException)
            {
                // 目录没有或读不到：没有可回收的东西，也不需要写墓碑（写了反而是噪音）。
                return false;
            }

            Array.Sort(files, StringComparer.Ordinal);
            for (int index = 0; index < files.Length; index++)
            {
                string requestId = Path.GetFileNameWithoutExtension(files[index]);
                // 墓碑自身与协议外文件（名字不是合法 requestId）一律不动：GC 只回收响应。
                if (string.Equals(requestId, TOMBSTONE_FILE_NAME, StringComparison.Ordinal)) continue;
                if (!CourierProtocol.IsValidRequestId(requestId))
                {
                    // SPEC §15.2：removedBefore 必须是一个**真实存在**的 requestId，MUST NOT 猜。
                    // 宿主自己放进来的文件（notes.json 之类）名字不是 requestId，一旦进了 survivor 集合，
                    // Smallest 就可能把 "notes" 当成边界写进墓碑——读方拿它做序数比较，会把所有比它小的
                    // 缺失结果都误判成"已被回收"。这类文件一律原样保留，且不参与墓碑计算。
                    continue;
                }

                if (IsRemovable(files[index], requestId, commandsDirectory, claimsDirectory, cutoffUtc)) removable.Add(files[index]);
                else survivors.Add(requestId);
            }

            return true;
        }

        /// <summary>
        /// 按 <paramref name="removable"/> 逐个删文件，删失败的把那一条的 requestId 记回 <paramref name="survivors"/>。
        /// </summary>
        /// <param name="removable">待删除文件的绝对路径。</param>
        /// <param name="survivors">存活集合；删除失败时补进去。</param>
        /// <returns>实际删除的文件数。</returns>
        private static int DeleteRemovable(List<string> removable, List<string> survivors)
        {
            // 单个文件的失败不中断整轮——下一轮会重试，而墓碑已经解释了"这里本该是空的"。
            int removed = 0;
            for (int index = 0; index < removable.Count; index++)
            {
                try
                {
                    File.Delete(removable[index]);
                    removed++;
                }
                catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException)
                {
                    // 被占用或权限不足：留到下一轮。
                    survivors.Add(Path.GetFileNameWithoutExtension(removable[index]));
                }
            }

            return removed;
        }

        /// <summary>
        /// 判断一个缺失结果的 <paramref name="requestId"/> 是否可能已被回收（SPEC §15.2 的读者条款）。
        /// <para>
        /// 墓碑的 <c>removedBefore</c> 是"删除后仍然存在的最小 requestId"，所以比它小的一律已被回收；
        /// 空串表示当时的回收把结果删光了，同样一律算已回收。读者用一个**缺失**的结果加上墓碑，
        /// 就能把"已被回收"与"从没处理过"分开——这正是墓碑存在的理由。
        /// </para>
        /// <para>
        /// 只读一次文件，读不到就返回 false（§3.7：读不到等于"还没有"，不能因此断言被回收）。
        /// </para>
        /// </summary>
        /// <param name="resultsDirectory">端点自己的 <c>results/</c>。</param>
        /// <param name="requestId">结果文件缺失的那条请求。</param>
        /// <returns>墓碑存在且该 requestId 落在回收区间内时为 true。</returns>
        internal static bool IsPossiblyReclaimed(string resultsDirectory, string requestId)
        {
            CourierGcTombstone? tombstone = TryReadTombstone(resultsDirectory);
            if (tombstone == null) return false;
            if (tombstone.RemovedBefore.Length == 0) return true;
            return string.CompareOrdinal(requestId, tombstone.RemovedBefore) < 0;
        }

        /// <summary>读回墓碑；不存在或不可解析时为 null（SPEC §3.7：读不到等于"还没有"）。</summary>
        /// <param name="resultsDirectory">端点自己的 <c>results/</c>。</param>
        /// <returns>墓碑三元组，或 null。</returns>
        internal static CourierGcTombstone? TryReadTombstone(string resultsDirectory)
        {
            string? text;
            if (!CourierIO.TryReadAllText(Path.Combine(resultsDirectory, TOMBSTONE_FILE_NAME), out text)) return null;
            return CourierGcTombstone.TryParse(text);
        }

        /// <summary>
        /// 判定一份响应能不能删：只有在途检查全过、响应解析得出来、且完成时间早于 cutoff 时才删（SPEC §15.2）。
        /// </summary>
        /// <param name="path">响应文件绝对路径。</param>
        /// <param name="requestId">文件名里的 requestId。</param>
        /// <param name="commandsDirectory">命令目录，null 表示调用方不提供在途信息。</param>
        /// <param name="claimsDirectory">claim 目录，null 同上。</param>
        /// <param name="cutoffUtc">完成时间早于此值才删。</param>
        /// <returns>可以删除时为 true。</returns>
        private static bool IsRemovable(string path, string requestId, string? commandsDirectory, string? claimsDirectory, DateTime cutoffUtc)
        {
            // 在途绝不删：命令还在 commands/ 说明它还没产出终态，claim 还在说明别的服务循环正持有它。
            // 这两条检查放在读响应之前，避免为一条不会被删的文件做一次全文解析。
            if (commandsDirectory != null && File.Exists(Path.Combine(commandsDirectory, requestId + CourierProtocol.JSON_EXTENSION))) return false;
            if (claimsDirectory != null && File.Exists(Path.Combine(claimsDirectory, requestId + CourierProtocol.JSON_EXTENSION))) return false;

            string? text;
            if (!CourierIO.TryReadAllText(path, out text)) return false;
            CourierResponse? response = CourierResponse.TryParse(text);
            // 解析不出来的响应不算"已完成的响应"：删掉它就等于删掉了一条我们还读不懂的证据。
            if (response == null) return false;
            return response.CompletedAtUtc < cutoffUtc;
        }

        /// <summary>取一批 requestId 里序数最小的那个，用作墓碑的 <c>removedBefore</c>；一无所有时返回空串。</summary>
        /// <param name="ids">候选 requestId。</param>
        /// <returns>最小的 requestId，或空串。</returns>
        private static string Smallest(List<string> ids)
        {
            string smallest = string.Empty;
            for (int index = 0; index < ids.Count; index++)
            {
                string current = ids[index];
                if (current.Length == 0) continue;
                // requestId 以定长 UTC 时间戳开头，序数比较即时间序（SPEC §3.5）。
                if (smallest.Length == 0 || string.CompareOrdinal(current, smallest) < 0) smallest = current;
            }

            return smallest;
        }

        /// <summary>先把墓碑 <c>results/gc.json</c> 原子写下去，之后才允许动任何响应文件（SPEC §15.2 的顺序要求）。</summary>
        /// <param name="resultsDirectory">结果目录。</param>
        /// <param name="cutoffUtc">本次使用的截止时间。</param>
        /// <param name="removedBefore">删除后仍然存在的最小 requestId。</param>
        private static void WriteTombstone(string resultsDirectory, DateTime cutoffUtc, string removedBefore)
        {
            string json = new CourierJsonObject()
                .AddString("cutoffUtc", CourierProtocol.FormatUtc(cutoffUtc))
                .AddString("removedBefore", removedBefore)
                .AddString("generatedAtUtc", CourierProtocol.FormatUtc(DateTime.UtcNow))
                .Build();
            CourierIO.WriteAtomic(Path.Combine(resultsDirectory, TOMBSTONE_FILE_NAME), json, true);
        }
    }

    /// <summary><c>results/gc.json</c> 的解析结果（SPEC §15.2）。</summary>
    internal sealed class CourierGcTombstone
    {
        internal DateTime CutoffUtc { get; private set; }
        internal string RemovedBefore { get; private set; } = string.Empty;
        internal DateTime GeneratedAtUtc { get; private set; }

        /// <summary>解析 <c>results/gc.json</c>；字段缺失、时间不可解析时返回 null，由调用方当作没有墓碑。</summary>
        /// <param name="text">文件原文。</param>
        /// <returns>解析结果或 null。</returns>
        internal static CourierGcTombstone? TryParse(string? text)
        {
            Dictionary<string, object?>? document;
            if (!CourierJson.TryParseObject(text, out document)) return null;
            DateTime cutoffUtc;
            DateTime generatedAtUtc;
            if (!CourierProtocol.TryParseUtc(CourierJson.GetString(document, "cutoffUtc"), out cutoffUtc)) return null;
            if (!CourierProtocol.TryParseUtc(CourierJson.GetString(document, "generatedAtUtc"), out generatedAtUtc)) return null;
            return new CourierGcTombstone
            {
                CutoffUtc = cutoffUtc,
                RemovedBefore = CourierJson.GetString(document, "removedBefore") ?? string.Empty,
                GeneratedAtUtc = generatedAtUtc
            };
        }
    }
}
