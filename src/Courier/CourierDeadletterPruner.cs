using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Courier
{
    /// <summary>
    /// 在线 <c>endpoints/&lt;id&gt;/deadletter/</c> 的保留清理（SPEC §12、§15.3）。
    /// <para>
    /// 死信是终态证据，所以这里比 <see cref="CourierArchivePruner"/> 谨慎一档：**只有归档副本确实存在时**
    /// 才允许删在线那一份。归档副本（<c>archive/&lt;date&gt;/&lt;id&gt;/&lt;subdirectory&gt;/</c>）跟着日期目录过期，
    /// 两份副本各自有界，而证据在两次删除之间始终至少存在一份。
    /// </para>
    /// <para>
    /// 与 archive 的按日期目录整目录删不同，在线死信**没有日期目录**——文件名只有 requestId，
    /// 过期与否只能读文件内容里的 <c>failedAtUtc</c>。所以这里逐文件判，且每轮只检查至多
    /// <see cref="MAX_FILES_PER_SWEEP"/> 个：requestId 以定长 UTC 时间戳开头，序数即时间序，
    /// 单轮工作量有界（SPEC §16 的维护步骤要求）。
    /// </para>
    /// </summary>
    internal static class CourierDeadletterPruner
    {
        /// <summary>单轮最多检查多少个在线死信。有界工作是 SPEC §16 对维护步骤的要求。</summary>
        internal const int MAX_FILES_PER_SWEEP = 256;

        /// <summary>断点表的容量上限。超过就整体清空——断点只是"别总从最旧的重来"，丢了只会让下一轮多做一点。</summary>
        private const int MAX_TRACKED_DIRECTORIES = 64;

        private static readonly object sCursorGate = new object();

        /// <summary>
        /// 每个 <c>deadletter/</c> 各自检查到哪个 requestId 为止。缺失表示下一轮从最旧的一个重新开始。
        /// <para>
        /// 没有这个断点，单轮窗口就永远落在最旧的那批文件上：它们若因为"归档副本还在保留期内"删不掉，
        /// 就会一直占满窗口，排在它们之后的过期死信永远轮不到检查（§12/§15.3 的"有界"要求的是文件数有界，
        /// 不是永远只看同一批）。有了断点，连续几轮就能覆盖整个目录。
        /// </para>
        /// <para>
        /// 按目录分键而不是全局一个值：同一个进程里可能有多个端点、测试里还有多个临时 root 并行跑，
        /// 共用一个断点会让它们互相跳到彼此的位置上，看起来像"随机漏扫"。
        /// </para>
        /// <para>
        /// 记 requestId 而不记下标：两次扫描之间目录内容会变（新死信进来、旧的被删），下标会指错位置，
        /// 而 requestId 是定长时间戳前缀，用序数比较定位始终成立。
        /// </para>
        /// </summary>
        private static readonly Dictionary<string, string> sResumeAfter = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// 清理在线死信，归档子目录名从布局里反查（协议根由 <paramref name="deadletterDirectory"/> 向上找）。
        /// </summary>
        /// <param name="deadletterDirectory">端点自己的 <c>deadletter/</c>。</param>
        /// <param name="archiveRoot">root 级的 <c>archive/</c>。</param>
        /// <param name="endpointId">本端点标识；只动自己那一份，别端点的死信由其自己决定保留期。</param>
        /// <param name="cutoffUtc">仅 UTC 有意义（SPEC §3.2）。<c>failedAtUtc</c> 严格早于它的记录参与判定。</param>
        /// <returns>实际删除的在线死信数。</returns>
        /// <exception cref="ArgumentException"><paramref name="cutoffUtc"/> 不是 UTC。</exception>
        /// <remarks>
        /// 归档副本的子目录名取自生效布局而不是写死的常量：写侧（<c>ArchiveDeadletter</c>）用的是
        /// <see cref="CourierLayout.DeadletterDirectory"/>，两边必须同名，否则自定义布局下副本永远找不到，
        /// 在线死信也就永远清不掉（SPEC §15.3 的 MUST 有界会静默失效）。
        /// </remarks>
        internal static int Prune(string deadletterDirectory, string archiveRoot, string endpointId, DateTime cutoffUtc)
        {
            CourierLayout layout = CourierEndpointReader.FindLayout(deadletterDirectory);
            return Prune(deadletterDirectory, archiveRoot, layout.DeadletterDirectory, endpointId, cutoffUtc);
        }

        /// <summary>清理在线死信，归档子目录名由调用方明确给出。</summary>
        /// <param name="deadletterDirectory">端点自己的 <c>deadletter/</c>。</param>
        /// <param name="archiveRoot">root 级的 <c>archive/</c>。</param>
        /// <param name="deadletterSubdirectory">归档树里死信副本所在的子目录名，必须与写侧一致。</param>
        /// <param name="endpointId">本端点标识。</param>
        /// <param name="cutoffUtc">仅 UTC 有意义（SPEC §3.2）。</param>
        /// <returns>实际删除的在线死信数。</returns>
        /// <exception cref="ArgumentException"><paramref name="cutoffUtc"/> 不是 UTC。</exception>
        internal static int Prune(string deadletterDirectory, string archiveRoot, string deadletterSubdirectory, string endpointId, DateTime cutoffUtc)
        {
            string? resumeAfter = LoadCursor(deadletterDirectory);
            int examinedCount;
            string? nextResumeAfter;
            int removed = PruneCore(
                deadletterDirectory,
                archiveRoot,
                deadletterSubdirectory,
                endpointId,
                cutoffUtc,
                resumeAfter,
                out examinedCount,
                out nextResumeAfter);
            _ = examinedCount;
            StoreCursor(deadletterDirectory, nextResumeAfter);
            return removed;
        }

        /// <summary>
        /// 真正干活的一段：从 <paramref name="resumeAfterRequestId"/> 之后开始，最多检查
        /// <see cref="MAX_FILES_PER_SWEEP"/> 个文件，并给出下一轮该从哪里继续。
        /// </summary>
        /// <param name="deadletterDirectory">端点自己的 <c>deadletter/</c>。</param>
        /// <param name="archiveRoot">root 级的 <c>archive/</c>。</param>
        /// <param name="deadletterSubdirectory">归档树里死信副本所在的子目录名。</param>
        /// <param name="endpointId">本端点标识。</param>
        /// <param name="cutoffUtc">仅 UTC 有意义（SPEC §3.2）。</param>
        /// <param name="resumeAfterRequestId">从该 requestId 之后开始检查；null 或空串表示从最旧的一个开始。</param>
        /// <param name="examinedCount">本次检查过的文件数。</param>
        /// <param name="nextResumeAfter">
        /// 下一轮的断点；本轮已经把目录扫到尾时为 null（下一轮从头开始）。
        /// </param>
        /// <returns>实际删除的在线死信数。</returns>
        /// <exception cref="ArgumentException"><paramref name="cutoffUtc"/> 不是 UTC。</exception>
        /// <remarks>任何一条 IO 失败都不抛出：删不掉的留到下一轮，读不懂的原样保留（SPEC §3.1、§3.7）。</remarks>
        private static int PruneCore(
            string deadletterDirectory,
            string archiveRoot,
            string deadletterSubdirectory,
            string endpointId,
            DateTime cutoffUtc,
            string? resumeAfterRequestId,
            out int examinedCount,
            out string? nextResumeAfter)
        {
            examinedCount = 0;
            nextResumeAfter = null;
            if (cutoffUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("cutoff must be UTC (SPEC §3.2)", nameof(cutoffUtc));
            }

            string[] files;
            if (!TryListDeadletters(deadletterDirectory, out files)) return 0;

            int start = FindResumeIndex(files, resumeAfterRequestId);
            int end = start + SweepLength(files.Length - start);

            int removed = 0;
            for (int index = start; index < end; index++)
            {
                examinedCount++;
                if (TryDeleteOne(files[index], archiveRoot, deadletterSubdirectory, endpointId, cutoffUtc)) removed++;
            }

            // 扫到尾就回到开头；还有剩余就从本轮的最后一个文件之后继续。
            if (end < files.Length && end > start) nextResumeAfter = Path.GetFileNameWithoutExtension(files[end - 1]);
            return removed;
        }


        /// <summary>列举 <c>deadletter/</c> 里的死信文件并排序；目录不在或读不到时返回 false 表示本轮无事可做。</summary>
        /// <param name="deadletterDirectory">端点自己的 <c>deadletter/</c>。</param>
        /// <param name="files">按序数升序的文件全路径。</param>
        /// <returns>拿到目录列表时为 true。</returns>
        private static bool TryListDeadletters(string deadletterDirectory, out string[] files)
        {
            files = Array.Empty<string>();
            try
            {
                // 目录不存在就是"没有死信"，不是错误（SPEC §3.7）。
                if (!Directory.Exists(deadletterDirectory)) return false;
                files = Directory.GetFiles(deadletterDirectory, "*" + CourierProtocol.JSON_EXTENSION);
            }
            catch (Exception caught) when (caught is DirectoryNotFoundException || caught is IOException || caught is UnauthorizedAccessException)
            {
                return false;
            }

            Array.Sort(files, StringComparer.Ordinal);
            return true;
        }

        /// <summary>本轮最多检查多少个文件；剩余量小于上限时就是剩余量本身。</summary>
        /// <param name="available">断点之后还剩的文件数。</param>
        /// <returns>本轮要检查的文件数。</returns>
        private static int SweepLength(int available)
        {
            return available > MAX_FILES_PER_SWEEP ? MAX_FILES_PER_SWEEP : available;
        }

        /// <summary>
        /// 判定并删除一条在线死信：名字不合规、读不出、未过期、或归档副本不在时都不动它。
        /// </summary>
        /// <param name="path">死信文件全路径。</param>
        /// <param name="archiveRoot">root 级的 <c>archive/</c>。</param>
        /// <param name="deadletterSubdirectory">归档树里死信副本所在的子目录名。</param>
        /// <param name="endpointId">本端点标识。</param>
        /// <param name="cutoffUtc">仅 <c>failedAtUtc</c> 早于此值才删。</param>
        /// <returns>真的删掉了一个文件时为 true。</returns>
        private static bool TryDeleteOne(string path, string archiveRoot, string deadletterSubdirectory, string endpointId, DateTime cutoffUtc)
        {
            string requestId = Path.GetFileNameWithoutExtension(path);
            // 名字不是合法 requestId 的文件是宿主自己放的东西：不猜、不动（与 §15.2 对 results 的规矩一致）。
            if (!CourierProtocol.IsValidRequestId(requestId)) return false;

            string? text;
            // 读不到就跳过：下一轮再来，绝不因为一次瞬时 IO 就把证据删掉。
            if (!CourierIO.TryReadAllText(path, out text)) return false;
            CourierDeadletter? record = CourierDeadletter.TryParse(text);
            if (record == null || record.FailedAtUtc >= cutoffUtc) return false;

            // 归档副本不在就绝不删在线那份：两份都消失等于命令被静默丢弃（SPEC §1 不变式 3）。
            if (!HasArchivedCopy(archiveRoot, deadletterSubdirectory, endpointId, requestId, record.FailedAtUtc)) return false;

            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException)
            {
                // 被占用或权限不足：留到下一轮，绝不因为删不掉就抛给服务循环。
                return false;
            }
        }

        /// <summary>
        /// 找出本轮该从哪个下标开始：第一个序数大于断点的文件。
        /// </summary>
        /// <param name="files">已按序数排序的文件全路径。</param>
        /// <param name="resumeAfterRequestId">上一轮的断点 requestId；null 或空串表示从头开始。</param>
        /// <returns>起始下标；断点已经在目录末尾（之后的文件都被删了）时回到 0，让本轮照常干活。</returns>
        private static int FindResumeIndex(string[] files, string? resumeAfterRequestId)
        {
            if (string.IsNullOrEmpty(resumeAfterRequestId)) return 0;
            for (int index = 0; index < files.Length; index++)
            {
                string current = Path.GetFileNameWithoutExtension(files[index]);
                if (string.CompareOrdinal(current, resumeAfterRequestId) > 0) return index;
            }

            return 0;
        }

        /// <summary>读某个 <c>deadletter/</c> 的断点。</summary>
        /// <param name="deadletterDirectory">端点自己的 <c>deadletter/</c>，作为断点表的键。</param>
        /// <returns>上一轮的断点 requestId；没有记录时为 null（从最旧的一个开始）。</returns>
        /// <remarks>键用全路径，避免不同 root 下同名端点的 <c>deadletter/</c> 互相串游标。</remarks>
        private static string? LoadCursor(string deadletterDirectory)
        {
            string key = Path.GetFullPath(deadletterDirectory);
            lock (sCursorGate)
            {
                string? value;
                return sResumeAfter.TryGetValue(key, out value) ? value : null;
            }
        }

        /// <summary>记下某个 <c>deadletter/</c> 下一轮该从哪里继续。</summary>
        /// <param name="deadletterDirectory">端点自己的 <c>deadletter/</c>，作为断点表的键。</param>
        /// <param name="value">下一轮的断点；null 表示下一轮从头开始（此时顺手删掉这一项）。</param>
        /// <remarks>
        /// 表满时整体清空而不是淘汰单条：断点丢失只影响"下一轮多做一点"，清理仍然是有界的、幂等的，
        /// 不需要为它做 LRU。
        /// </remarks>
        private static void StoreCursor(string deadletterDirectory, string? value)
        {
            string key = Path.GetFullPath(deadletterDirectory);
            lock (sCursorGate)
            {
                if (value == null)
                {
                    sResumeAfter.Remove(key);
                    return;
                }

                if (!sResumeAfter.ContainsKey(key) && sResumeAfter.Count >= MAX_TRACKED_DIRECTORIES) sResumeAfter.Clear();
                sResumeAfter[key] = value;
            }
        }

        /// <summary>
        /// 归档副本是否存在。<c>ArchiveDeadletter</c> 用落盘时刻的 UTC 日期命名，正常情况下与
        /// <c>failedAtUtc</c> 同一天；跨零点的那一次会落到下一天，所以两个日期都要看。
        /// </summary>
        /// <param name="archiveRoot">root 级的 <c>archive/</c>。</param>
        /// <param name="deadletterSubdirectory">归档树里死信副本所在的子目录名，必须与写侧一致。</param>
        /// <param name="endpointId">本端点标识。</param>
        /// <param name="requestId">要查的请求标识。</param>
        /// <param name="failedAtUtc">死信记录里的失败时刻，用来推两个候选日期目录。</param>
        /// <returns>任一候选日期目录下存在副本时为 true。</returns>
        private static bool HasArchivedCopy(string archiveRoot, string deadletterSubdirectory, string endpointId, string requestId, DateTime failedAtUtc)
        {
            string fileName = requestId + CourierProtocol.JSON_EXTENSION;
            for (int offset = 0; offset <= 1; offset++)
            {
                string date = failedAtUtc.AddDays(offset).ToString(CourierProtocol.DATE_FORMAT, CultureInfo.InvariantCulture);
                string candidate = Path.Combine(archiveRoot, date, endpointId, deadletterSubdirectory, fileName);
                if (File.Exists(candidate)) return true;
            }

            return false;
        }
    }
}
