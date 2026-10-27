using System;
using System.Globalization;
using System.IO;

namespace Courier
{
    /// <summary>
    /// <c>archive/</c> 的保留策略（SPEC §15.3：保留策略由实现决定，但 <b>MUST 有界</b>）。
    /// <para>
    /// 归档目录是保留策略的主要作用位置（SPEC §15.3、§22；在线 <c>deadletter/</c> 另按 §12 处理）。
    /// 做法是按日期目录整目录删除——<c>archive/&lt;yyyy-MM-dd&gt;/&lt;endpointId&gt;/</c>，日期严格早于截止日的才删。
    /// 整目录删而不是按文件删有两个理由：归档只追加、日期目录一旦过期整批都过期；
    /// 而逐文件删会和正在搬入的 <see cref="CourierIO.MoveOverwrite"/> 抢同一个文件。
    /// </para>
    /// <para>
    /// 只删自己端点的子目录：一个 root 下多个端点共享同一棵 archive 树，
    /// 删别人的归档等于替别人决定了保留策略。
    /// </para>
    /// </summary>
    internal static class CourierArchivePruner
    {
        /// <summary>归档日期目录的格式（SPEC §15.1，UTC 日期）。</summary>
        internal const string DATE_FORMAT = "yyyy-MM-dd";

        /// <summary>把 UTC 时刻格式化成归档用的日期目录名。</summary>
        /// <param name="utc">UTC 时刻。</param>
        /// <returns>形如 <c>2026-10-27</c> 的目录名。</returns>
        internal static string FormatDate(DateTime utc)
        {
            return utc.ToString(DATE_FORMAT, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 删掉 <paramref name="archiveRoot"/> 下日期早于 <paramref name="cutoffUtc"/> 那一天的、
        /// 属于 <paramref name="endpointId"/> 的归档子目录。
        /// </summary>
        /// <param name="archiveRoot">archive 根目录的绝对路径。</param>
        /// <param name="endpointId">本端点 ID（已校验）。</param>
        /// <param name="cutoffUtc">截止时刻；日期**严格早于**它的目录才会被删。必须是 UTC。</param>
        /// <returns>实际删掉的端点归档目录数。</returns>
        internal static int Prune(string archiveRoot, string endpointId, DateTime cutoffUtc)
        {
            if (cutoffUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("cutoff must be UTC (SPEC §15.3)", nameof(cutoffUtc));
            }

            string[] dateDirectories;
            try
            {
                dateDirectories = Directory.GetDirectories(archiveRoot);
            }
            catch (Exception caught) when (caught is DirectoryNotFoundException || caught is IOException || caught is UnauthorizedAccessException)
            {
                // 还没归档过任何东西：没有什么可清理的。
                return 0;
            }

            // 截止日：日期严格早于这一天的目录才过期。用日期而不是时刻比较，因为目录名只到天。
            string cutoffDate = FormatDate(cutoffUtc);
            int removed = 0;
            for (int index = 0; index < dateDirectories.Length; index++)
            {
                string dateDirectory = dateDirectories[index];
                string name = Path.GetFileName(dateDirectory);
                if (!IsExpiredDate(name, cutoffDate)) continue;

                string endpointDirectory = Path.Combine(dateDirectory, endpointId);
                if (Directory.Exists(endpointDirectory))
                {
                    try
                    {
                        Directory.Delete(endpointDirectory, true);
                        removed++;
                    }
                    catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException || caught is DirectoryNotFoundException)
                    {
                        // 删不掉（别的进程正在读、或文件被占）：留着，下一轮再说。
                        continue;
                    }
                }

                DeleteIfEmpty(dateDirectory);
            }

            return removed;
        }

        /// <summary>目录名是不是合法的归档日期，且严格早于截止日。</summary>
        private static bool IsExpiredDate(string name, string cutoffDate)
        {
            DateTime parsed;
            if (!DateTime.TryParseExact(name, DATE_FORMAT, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            {
                // 不是归档生成的目录（用户自己放的东西）：不猜、不动。
                return false;
            }

            // 定长同格式的日期目录，序数比较与时间比较等价。
            return string.CompareOrdinal(name, cutoffDate) < 0;
        }

        /// <summary>日期目录空了就一并删掉，避免 archive 下堆一层空目录。</summary>
        private static void DeleteIfEmpty(string dateDirectory)
        {
            try
            {
                if (Directory.GetFileSystemEntries(dateDirectory).Length > 0) return;
                Directory.Delete(dateDirectory, false);
            }
            catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException || caught is DirectoryNotFoundException)
            {
                // 不为一个空目录纠缠。
            }
        }
    }
}
