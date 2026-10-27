using System;
using System.Globalization;
using System.IO;

namespace Courier
{
    /// <summary>
    /// <see cref="CourierNode"/> 的终态与证据落盘、归档与保留策略（SPEC §12、§15）。
    /// </summary>
    public sealed partial class CourierNode
    {
        /// <summary>
        /// 磁盘保留策略：<c>archive/</c>（SPEC §15.3）与在线 <c>deadletter/</c>（§12、§15.3）。
        /// <para>
        /// archive 按日期目录整目录删本端点下严格早于"今天减去保留期"的归档；死信因为在线布局没有
        /// 日期目录，只能逐条读 <c>failedAtUtc</c>。两者各自限频、各自可配，共用这个节奏
        /// （<see cref="CourierNodeOptions.ArchiveSweepInterval"/>）。
        /// </para>
        /// <para>
        /// 只动自己端点的子目录：同一棵 archive 树下可能有别的端点在写，替别人决定保留期不是本端点的事。
        /// 任一保留期配成 0 表示关掉那一项清理（那等于该项无界增长，只能由宿主自己兜住）。
        /// </para>
        /// </summary>
        private void SweepArchive()
        {
            TimeSpan retention = mOptions.EffectiveArchiveRetention;
            TimeSpan deadletterRetention = mOptions.EffectiveDeadletterRetention;
            // 两种保留期各自独立：archive 配成 0（不清理）不该顺带关掉在线死信的清理。
            if (retention <= TimeSpan.Zero && deadletterRetention <= TimeSpan.Zero) return;

            DateTime nowUtc = DateTime.UtcNow;
            // 限频：默认一小时一轮。每轮只做一次 archive/ 的目录列举，比每 200 ms 扫一遍便宜几个数量级。
            if (mLastArchiveSweepUtc != DateTime.MinValue
                && nowUtc - mLastArchiveSweepUtc < mOptions.EffectiveArchiveSweepInterval)
            {
                return;
            }

            mLastArchiveSweepUtc = nowUtc;
            string archiveRoot = CourierLayout.Combine(mRootDirectory, mLayout.ArchiveDirectory);

            // SPEC §12、§15.3：在线的 deadletter/ 同样 MUST 有界。它比 archive 多一道闸——
            // 只有归档副本确实存在时才删在线那一份，两份都消失等于命令被静默丢弃（§1 不变式 3）。
            //
            // 顺序是硬性的：**先清在线死信，再清 archive**。反过来的话，同一轮里 archive 会先把它自己的
            // 日期目录整目录删掉，于是"归档副本存在"这道闸永远为假，在线死信反而一条也删不掉。
            if (deadletterRetention > TimeSpan.Zero)
            {
                CourierDeadletterPruner.Prune(mDeadletterDirectory, archiveRoot, mEndpointId, nowUtc - deadletterRetention);
            }

            if (retention > TimeSpan.Zero) CourierArchivePruner.Prune(archiveRoot, mEndpointId, nowUtc - retention);
        }

        /// <summary>SPEC §13.2 与 §16 第 7 步：终态已产生的命令，其 progress 文件必须消失。</summary>
        private void CleanProgress()
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(mProgressDirectory, "*" + CourierProtocol.JSON_EXTENSION);
            }
            catch (Exception caught) when (caught is DirectoryNotFoundException || caught is IOException)
            {
                return;
            }

            for (int index = 0; index < files.Length; index++)
            {
                string requestId = Path.GetFileNameWithoutExtension(files[index]);
                if (File.Exists(Path.Combine(mResultsDirectory, requestId + CourierProtocol.JSON_EXTENSION)))
                {
                    CourierIO.TryDelete(files[index]);
                }
            }
        }

        /// <summary>
        /// 写一条终态并把它归档（SPEC §15.1）。用于"命令根本没进 handler"的几条路径：未知动作、
        /// deadline 已过、取消在开始前抵达。
        /// </summary>
        /// <param name="requestId">命令标识。</param>
        /// <param name="commandPath">commands/ 下的命令文件。</param>
        /// <param name="status">终态：<c>ok</c>、<c>rejected</c> 或 <c>failed</c>（SPEC §11）。</param>
        /// <param name="payload">终态载荷；拒绝类通常为空串。</param>
        /// <param name="reason">原因码，取 SPEC §18 的集合；成功时为 null。</param>
        /// <param name="cancelRequested">是否在结果里标注取消意愿已抵达（SPEC §13.1）。</param>
        private void WriteResultAndArchive(string requestId, string commandPath, string status, string payload, string? reason, bool cancelRequested)
        {
            // SPEC §15.1：先写 results 再搬命令；反过来会让命令在崩溃窗口里凭空消失。
            // 写失败时不归档也不删 claim：让下一轮按 attempts + 1 重来，好过把证据清掉。
            CourierResponse settled;
            try
            {
                settled = WriteResult(requestId, status, payload, reason, cancelRequested);
            }
            catch (Exception caught) when (IsIoFailure(caught))
            {
                // 写失败时什么都不清：命令留在 commands/、claim 留到租约到期，下一轮按 attempts + 1 重来。
                // 留一条记录，否则"命令一直不出结果"在宿主侧看起来和"对面没投递过"一模一样。
                RecordServiceFault("write-result", caught);
                return;
            }

            // 与 Finish 同一条规矩：rejected / failed 也是终态，同样要把对面叫醒，否则它只能等满整个 deadline。
            SignalHot(settled);

            ArchiveCommand(requestId, commandPath);
            // SPEC §10：终态产生后 claim 必须消失，否则观察工具会看到一条永远"正在执行"的命令。
            CourierIO.TryDelete(Path.Combine(mClaimsDirectory, requestId + CourierProtocol.JSON_EXTENSION));
            DeleteProgress(requestId);
        }

        /// <summary>写终态证据，并把它交回调用方——热回铃必须送与 results/ 逐字节相同的那份结果。</summary>
        private CourierResponse WriteResult(string requestId, string status, string payload, string? reason, bool cancelRequested)
        {
            var response = new CourierResponse(requestId, status, payload, reason, cancelRequested, DateTime.UtcNow);
            CourierIO.WriteAtomic(Path.Combine(mResultsDirectory, requestId + CourierProtocol.JSON_EXTENSION), response.ToJson(), true);
            return response;
        }

        /// <summary>把命令文件从 commands/ 搬进当天的归档目录（SPEC §15.1）。</summary>
        /// <param name="requestId">命令标识，同时是归档文件名。</param>
        /// <param name="commandPath">还在 commands/ 里的命令文件。</param>
        private void ArchiveCommand(string requestId, string commandPath)
        {
            MoveToArchive(requestId, commandPath, mLayout.CommandsDirectory);
        }

        /// <summary>
        /// 把命令文件搬进 <c>archive/&lt;yyyy-MM-dd&gt;/&lt;endpointId&gt;/&lt;subdirectory&gt;/</c>（SPEC §15.1）。
        /// </summary>
        /// <param name="requestId">命令标识，同时是归档文件名。</param>
        /// <param name="commandPath">还在 commands/ 里的命令文件。</param>
        /// <param name="subdirectory">归档子目录：<c>commands</c> 或 <c>deadletter</c>。</param>
        private void MoveToArchive(string requestId, string commandPath, string subdirectory)
        {
            if (!File.Exists(commandPath)) return;
            try
            {
                string archiveDirectory = Path.Combine(
                    CourierLayout.Combine(mRootDirectory, mLayout.ArchiveDirectory),
                    DateTime.UtcNow.ToString(CourierProtocol.DATE_FORMAT, CultureInfo.InvariantCulture),
                    mEndpointId,
                    subdirectory);
                CourierIO.EnsureDirectory(archiveDirectory);
                // 覆盖写走 CourierIO.MoveOverwrite：File.Move 的三参重载不在 netstandard2.1 里。
                CourierIO.MoveOverwrite(commandPath, Path.Combine(archiveDirectory, requestId + CourierProtocol.JSON_EXTENSION));
            }
            catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException)
            {
                // SPEC §15.1：归档失败 MUST 被忽略并继续，不能因为搬不动就让整条命令重来。
            }
        }

        /// <summary>
        /// 把死信副本写进 <c>archive/&lt;yyyy-MM-dd&gt;/&lt;endpointId&gt;/deadletter/</c>（SPEC §15.1）。
        /// 归档副本是"死信一旦过期，证据还在"的那一半：在线 <c>deadletter/</c> 会被保留策略清掉（§15.3），
        /// 而这份副本跟着日期目录一起过期。
        /// </summary>
        /// <param name="requestId">死信对应的请求标识。</param>
        /// <param name="json">死信记录的 JSON 原文，与在线那份逐字节相同。</param>
        private void ArchiveDeadletter(string requestId, string json)
        {
            try
            {
                string archiveDirectory = Path.Combine(
                    CourierLayout.Combine(mRootDirectory, mLayout.ArchiveDirectory),
                    DateTime.UtcNow.ToString(CourierProtocol.DATE_FORMAT, CultureInfo.InvariantCulture),
                    mEndpointId,
                    mLayout.DeadletterDirectory);
                CourierIO.EnsureDirectory(archiveDirectory);
                CourierIO.WriteAtomic(Path.Combine(archiveDirectory, requestId + CourierProtocol.JSON_EXTENSION), json, true);
            }
            catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException)
            {
                // 归档副本写不出来只丢一份备份，在线死信仍然在：这不是"命令被静默丢弃"，不该影响终态。
            }
        }

        /// <summary>
        /// 把一条命令写进 <c>deadletter/</c> 并归档（SPEC §12）。写不出死信时保持原状：
        /// 命令与 claim 都还在，下一轮会再次走到这里，绝不在没有证据的情况下把命令扔掉。
        /// </summary>
        /// <param name="requestId">命令标识。</param>
        /// <param name="commandPath">commands/ 下的命令文件。</param>
        /// <param name="reason">原因码，取 SPEC §18 的集合。</param>
        /// <param name="detail">给人看的细节，说明为什么这条命令没法执行。</param>
        /// <param name="attempts">已经尝试过的次数。</param>
        private void DeadletterCommand(string requestId, string commandPath, string reason, string detail, int attempts)
        {
            string claimPath = Path.Combine(mClaimsDirectory, requestId + CourierProtocol.JSON_EXTENSION);
            var deadletter = CourierDeadletter.Create(requestId, reason, detail, DateTime.UtcNow, attempts);
            string json = deadletter.ToJson();
            try
            {
                CourierIO.WriteAtomic(Path.Combine(mDeadletterDirectory, requestId + CourierProtocol.JSON_EXTENSION), json, true);
            }
            catch (Exception caught) when (IsIoFailure(caught))
            {
                // 写不出 deadletter 就保持原状：命令与 claim 都还在，下一轮会再次走到这里（SPEC §16.2 最后一条）。
                // 但"下一轮再来"不能变成"永远悄悄再来"：留痕，让宿主知道死信落不了盘。
                RecordServiceFault("write-deadletter", caught);
                return;
            }

            // SPEC §15.1：死信同时留一份归档副本。在线那份会被保留策略清掉（§15.3），
            // 这份跟着日期目录过期——两次删除都发生在"证据已经写进 results/deadletter"之后。
            ArchiveDeadletter(requestId, json);
            ArchiveCommand(requestId, commandPath);
            CourierIO.TryDelete(claimPath);
            DeleteProgress(requestId);
        }

        /// <summary>删掉某条命令的进度快照（SPEC §13.2：终态产生后 progress MUST 删除）。删不掉不影响终态。</summary>
        /// <param name="requestId">命令标识。</param>
        private void DeleteProgress(string requestId)
        {
            CourierIO.TryDelete(Path.Combine(mProgressDirectory, requestId + CourierProtocol.JSON_EXTENSION));
        }

        /// <summary>claim 的租约未到期即视为别人正在执行（SPEC §10.1）；claim 缺失或不可解析时返回 false，让取消立即生效。</summary>
        private static bool IsClaimedByOther(string claimPath)
        {
            string? text;
            if (!CourierIO.TryReadAllText(claimPath, out text)) return false;
            CourierClaim? claim = CourierClaim.TryParse(text);
            return claim != null && !IsClaimExpired(claim.ExpiresAtUtc);
        }

        /// <summary>租约是否已经到期（SPEC §10.1）。到期且没有对应结果，持有者即视为已死。</summary>
        /// <param name="expiresAtUtc">租约的到期时刻。</param>
        /// <returns>租约是否已过期。</returns>
        private static bool IsClaimExpired(DateTime expiresAtUtc)
        {
            // SPEC §10.1：租约到期且没有对应 result，持有者即视为已死。
            return DateTime.UtcNow > expiresAtUtc;
        }
    }
}
