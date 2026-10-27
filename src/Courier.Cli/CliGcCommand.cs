using System;
using System.Collections.Generic;
using System.IO;

namespace Courier.Cli
{
    /// <summary>
    /// `courier gc --older-than &lt;duration&gt;`：回收端点 <c>results/</c> 里已经完成且足够旧的响应（SPEC §15.2）。
    /// <para>
    /// 这是唯一一条**会删数据**的 CLI 命令，因此它只碰 <c>results/</c> 里的响应文件，
    /// 而且顺序与库内 <see cref="CourierResultsGc"/> 完全一致：先写墓碑 <c>results/gc.json</c>，再删。
    /// 在途命令（命令还在 commands/、或 claim 还在 claims/）永远不动。
    /// </para>
    /// <para>
    /// 没有服务者也能跑：GC 是目录层面的维护动作，不需要端点在线，也不需要开 <see cref="CourierNode"/>。
    /// 这与 SPEC §15.2 的"只有服务者自己可以清理"不冲突——命令的语义就是"我代表这个服务者清理"。
    /// </para>
    /// <para>
    /// 单个端点失败**不中断整轮**：已经删掉的结果是既成事实，必须出现在输出里；
    /// 一个删不动的端点也不该让其余端点的磁盘继续涨下去。所以失败被记下来、继续扫，
    /// 最后用"部分成功"的摘要收场，退出码说明这一轮是否全部成功。
    /// </para>
    /// </summary>
    internal static class CliGcCommand
    {
        /// <summary>执行命令。</summary>
        /// <param name="arguments">已解析的命令行。</param>
        /// <param name="root">已解析的协议根目录。</param>
        /// <param name="layout">root 下生效的目录布局。</param>
        /// <returns>全部端点都清理成功时是 0；有端点失败时是 <see cref="CliExitCode.FAILED"/>。</returns>
        internal static int Run(CliArguments arguments, string root, CourierLayout layout)
        {
            TimeSpan age;
            if (!TryResolveAge(arguments, out age)) return CliExitCode.USAGE;

            List<string>? targets = ResolveTargets(arguments, root, layout);
            if (targets == null) return CliExitCode.USAGE;

            // cutoff 只算一次：同一次 gc 里每个端点必须用同一个时间点，否则输出里的 cutoffUtc 与实际删的东西对不上。
            DateTime cutoffUtc = DateTime.UtcNow - age;
            List<GcRecord> records = Sweep(root, layout, targets, cutoffUtc, out int removedTotal, out int failedTotal);

            if (arguments.Json) WriteJson(records, removedTotal, failedTotal, cutoffUtc);
            else WriteHuman(records, removedTotal, failedTotal, age, cutoffUtc);

            // 删掉的结果已经是既成事实，所以有端点失败时仍然输出完整摘要；退出码只区分"这一轮是否全部成功"。
            return failedTotal == 0 ? CliExitCode.OK : CliExitCode.FAILED;
        }

        /// <summary>
        /// 决定这一轮要清理哪些端点：不带位置参数就是 root 下全部端点，带一个就只清它。
        /// <para>
        /// 显式给出却不在册的端点直接判用法错误，而不是"这个端点没有可清理的东西"：
        /// 前者是名字写错了，后者会让调用方以为清理跑过了。
        /// </para>
        /// </summary>
        /// <param name="arguments">已解析的命令行。</param>
        /// <param name="root">协议根目录。</param>
        /// <param name="layout">root 下生效的目录布局。</param>
        /// <returns>端点列表；参数不合法时写出错误并返回 null。</returns>
        private static List<string>? ResolveTargets(CliArguments arguments, string root, CourierLayout layout)
        {
            if (arguments.Positionals.Count > 1)
            {
                CliOutput.WriteError(arguments.Json, "invalid-argument", "gc expects at most one <endpointId> (got " + arguments.Positionals.Count + " argument(s))");
                return null;
            }

            var targets = new List<string>();
            if (arguments.Positionals.Count == 0)
            {
                IReadOnlyList<CourierEndpointInfo> endpoints = CliDirectory.ReadEndpoints(root, layout);
                for (int index = 0; index < endpoints.Count; index++) targets.Add(endpoints[index].EndpointId);
                return targets;
            }

            string endpointId = arguments.Positionals[0];
            // SPEC §4.1：endpointId 是唯一的路径拼接入口，非法值绝不能拼进路径。
            if (!CliIdentifiers.RequireEndpointId(arguments, endpointId)) return null;
            if (!CliDirectory.HasRegisteredEndpoint(root, layout, endpointId))
            {
                CliOutput.WriteError(arguments.Json, "endpoint-not-found", "no endpoint named \"" + endpointId + "\" under " + CliDirectory.EndpointsDirectory(root, layout));
                return null;
            }

            targets.Add(endpointId);
            return targets;
        }

        /// <summary>
        /// 逐个端点回收 <c>results/</c>。单个端点失败只记账、不中断：已经删掉的结果必须出现在输出里，
        /// 一个删不动的端点也不该让其余端点的磁盘继续涨下去。
        /// </summary>
        /// <param name="root">协议根目录。</param>
        /// <param name="layout">root 下生效的目录布局。</param>
        /// <param name="targets">这一轮要清理的端点。</param>
        /// <param name="cutoffUtc">本轮统一的截止时刻。</param>
        /// <param name="removedTotal">所有端点删掉的结果文件总数。</param>
        /// <param name="failedTotal">清理失败的端点个数。</param>
        /// <returns>逐端点的回收结果，顺序与 <paramref name="targets"/> 一致。</returns>
        private static List<GcRecord> Sweep(string root, CourierLayout layout, List<string> targets, DateTime cutoffUtc, out int removedTotal, out int failedTotal)
        {
            var records = new List<GcRecord>(targets.Count);
            removedTotal = 0;
            failedTotal = 0;
            for (int index = 0; index < targets.Count; index++)
            {
                string endpointId = targets[index];
                string directory = CliDirectory.EndpointDirectory(root, layout, endpointId);
                int removed;
                try
                {
                    removed = CourierResultsGc.Collect(
                        Path.Combine(directory, layout.ResultsDirectory),
                        Path.Combine(directory, layout.CommandsDirectory),
                        Path.Combine(directory, layout.ClaimsDirectory),
                        cutoffUtc);
                }
                catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException)
                {
                    // 墓碑写不出来时库里一个文件都没删（CourierResultsGc 的顺序保证），所以这个端点的计数是 0，
                    // 不是"删了一半"。失败原因带上端点，否则多端点扫下来没人知道是哪一个卡住了。
                    failedTotal++;
                    records.Add(new GcRecord(endpointId, 0, null, caught.Message));
                    continue;
                }

                removedTotal += removed;
                records.Add(new GcRecord(endpointId, removed, CourierResultsGc.TryReadTombstone(Path.Combine(directory, layout.ResultsDirectory)), null));
            }

            return records;
        }

        /// <summary>
        /// 解析 --older-than。没给就没有"够旧"的定义：宁可报错也不要猜一个默认值，
        /// 因为这条命令删的是证据（SPEC §15.2）。错误码与 --timeout 共用 <see cref="CliDuration.ERROR_CODE"/>。
        /// </summary>
        /// <param name="arguments">已解析的命令行。</param>
        /// <param name="age">解析成功时的"够旧"阈值。</param>
        /// <returns>是否拿到了合法的 --older-than。</returns>
        private static bool TryResolveAge(CliArguments arguments, out TimeSpan age)
        {
            age = TimeSpan.Zero;
            if (arguments.OlderThanText == null)
            {
                CliOutput.WriteError(arguments.Json, "invalid-argument", "gc expects --older-than <duration> such as 1h, 30m or 3600s");
                return false;
            }

            if (CliDuration.TryParse(arguments.OlderThanText, out age)) return true;
            CliOutput.WriteError(
                arguments.Json,
                CliDuration.ERROR_CODE,
                CliDuration.DescribeFailure("--older-than", arguments.OlderThanText));
            return false;
        }

        /// <summary>--json 输出：总数、逐端点结果（含失败），以及墓碑里记的 cutoff。</summary>
        /// <param name="records">逐端点的回收结果，失败的那条带 <see cref="GcRecord.Detail"/>。</param>
        /// <param name="removedTotal">所有端点删掉的结果文件总数。</param>
        /// <param name="failedTotal">清理失败的端点个数。</param>
        /// <param name="cutoffUtc">本轮统一的截止时刻。</param>
        private static void WriteJson(List<GcRecord> records, int removedTotal, int failedTotal, DateTime cutoffUtc)
        {
            var items = new List<string>(records.Count);
            for (int index = 0; index < records.Count; index++)
            {
                GcRecord record = records[index];
                var item = new CourierJsonObject()
                    .AddString("endpointId", record.EndpointId)
                    .AddNumber("removed", record.Removed);
                if (record.Detail != null)
                {
                    // 失败也要带 removed（此时是 0）：逐条结果的形状不因成败而变，读方不必写两种分支。
                    item.AddString("error", "gc-failed").AddString("detail", record.Detail);
                }

                CourierGcTombstone? tombstone = record.Tombstone;
                // 墓碑是"这里本该有什么"的唯一解释：没有就省略，写 null 会让读方以为读到了一条空墓碑。
                if (tombstone != null)
                {
                    item.AddString("cutoffUtc", CourierProtocol.FormatUtc(tombstone.CutoffUtc));
                    item.AddString("removedBefore", tombstone.RemovedBefore);
                    item.AddString("generatedAtUtc", CourierProtocol.FormatUtc(tombstone.GeneratedAtUtc));
                }

                items.Add(item.Build());
            }

            CliOutput.WriteLine(new CourierJsonObject()
                .AddNumber("removed", removedTotal)
                .AddNumber("endpoints", records.Count)
                .AddNumber("failed", failedTotal)
                .AddString("cutoffUtc", CourierProtocol.FormatUtc(cutoffUtc))
                .AddRaw("results", CourierJson.Array(items))
                .Build());
        }

        /// <summary>人类可读输出：逐端点一行，末尾一行汇总；有失败时再逐条写出原因。</summary>
        /// <param name="records">逐端点的回收结果。</param>
        /// <param name="removedTotal">所有端点删掉的结果文件总数。</param>
        /// <param name="failedTotal">清理失败的端点个数。</param>
        /// <param name="age">--older-than 的解析结果，用于汇总句。</param>
        /// <param name="cutoffUtc">本轮统一的截止时刻。</param>
        private static void WriteHuman(List<GcRecord> records, int removedTotal, int failedTotal, TimeSpan age, DateTime cutoffUtc)
        {
            // 一条都没删也必须输出统计：静默成功的 GC 与"根本没找到端点"看起来是一样的。
            for (int index = 0; index < records.Count; index++)
            {
                GcRecord record = records[index];
                CliOutput.WriteLine(record.Detail == null
                    ? record.EndpointId + ": removed " + record.Removed + " result(s)"
                    : record.EndpointId + ": failed, removed " + record.Removed + " result(s) (" + record.Detail + ")");
            }

            // cutoff 与 --json 分支用同一个格式化器：手写格式串既没有小数秒、也不做 ToUniversalTime，
            // 两条分支对"同一个截止时刻"会给出两个不同的字符串。
            CliOutput.WriteLine("removed " + removedTotal + " result(s) from " + records.Count + " endpoint(s) older than "
                + CliOutput.FormatSeconds((long)age.TotalMilliseconds)
                + " (cutoff " + CourierProtocol.FormatUtc(cutoffUtc) + ")");
            if (failedTotal > 0)
            {
                CliOutput.WriteLine("gc finished with " + failedTotal + " failed endpoint(s); the counts above are partial");
            }
        }

        /// <summary>一个端点的回收结果：删了多少、留下的墓碑是什么、失败原因是什么。</summary>
        private sealed class GcRecord
        {
            /// <summary>构造一条回收记录；<paramref name="detail"/> 非 null 表示这个端点清理失败。</summary>
            /// <param name="endpointId">端点标识。</param>
            /// <param name="removed">实际删掉的结果文件数（失败时恒为 0）。</param>
            /// <param name="tombstone">清理后的墓碑；读不到时为 null。</param>
            /// <param name="detail">失败原因；成功时为 null。</param>
            internal GcRecord(string endpointId, int removed, CourierGcTombstone? tombstone, string? detail)
            {
                EndpointId = endpointId;
                Removed = removed;
                Tombstone = tombstone;
                Detail = detail;
            }

            /// <summary>端点标识。</summary>
            internal string EndpointId { get; }

            /// <summary>实际删掉的结果文件数。</summary>
            internal int Removed { get; }

            /// <summary>清理后写下的墓碑；读不到时为 null。</summary>
            internal CourierGcTombstone? Tombstone { get; }

            /// <summary>失败原因；成功时为 null。</summary>
            internal string? Detail { get; }
        }
    }
}
