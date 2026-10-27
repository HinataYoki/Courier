using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Courier
{
    /// <summary>
    /// <see cref="CourierNode"/> 服务循环的"接纳"半边：SPEC §16.2 的第 1–9 步，从一份命令文件走到一个已派发的 handler。
    /// <para>
    /// 接纳的每一步都可能决定"这一轮不该启动它"，并通过返回 null 或 false 把决定交给调用方；
    /// 该落的盘（死信、终态）在各步骤内部已经落好，调用方不需要补齐任何副作用。
    /// </para>
    /// </summary>
    public sealed partial class CourierNode
    {
        /// <summary>SPEC §16 第 6 步：在名额内按文件名升序启动命令，启动即返回，不等 handler 结束。</summary>
        private void StartCommands()
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(mCommandsDirectory, "*" + CourierProtocol.JSON_EXTENSION);
            }
            catch (Exception caught) when (caught is DirectoryNotFoundException || caught is IOException)
            {
                // 目录被删或暂时不可读：本轮没有命令可启动，下一轮再看。
                return;
            }

            // SPEC §3.5：枚举顺序 MUST 按文件名升序，保证同一批积压的处理顺序在各实现间一致。
            // 就地排序：为了排序把整个路径数组再复制一份，在积压时是白花的内存。
            if (files.Length > 1) Array.Sort(files, StringComparer.Ordinal);

            int started = 0;
            for (int index = 0; index < files.Length && started < CourierProtocol.MAX_TICK_BATCH; index++)
            {
                string requestId = Path.GetFileNameWithoutExtension(files[index]);
                lock (mGate)
                {
                    // SPEC §16.1：名额满了就停，不阻塞等别人跑完。
                    if (mInflight.Count >= CourierProtocol.MAX_INFLIGHT) return;
                    if (mInflight.ContainsKey(requestId)) continue;
                }

                if (StartCommand(files[index])) started++;
            }
        }

        /// <summary>SPEC §16.2 的 Start：走完 1–8 步后把 handler 交给线程池；返回是否真的启动了。</summary>
        /// <param name="commandPath">commands/ 下的命令文件路径。</param>
        /// <returns>handler 是否已被派发。</returns>
        private bool StartCommand(string commandPath)
        {
            PendingCommand? pending = AdmitCommand(commandPath);
            if (pending == null) return false;

            HandlerEntry? handler;
            if (!mHandlers.TryGetValue(RegistrationKey(pending.Command.Channel, pending.Command.Action), out handler) || handler == null)
            {
                // 第 7 步（SPEC §9）：路由在写入时确定，本端点没有这个能力就只能 rejected/unknown-action。
                WriteResultAndArchive(
                    pending.RequestId,
                    pending.CommandPath,
                    CourierProtocol.STATUS_REJECTED,
                    string.Empty,
                    CourierProtocol.REASON_UNKNOWN_ACTION,
                    false);
                return false;
            }

            if (!TryWriteClaim(pending)) return false;
            LaunchHandler(pending, handler);
            return true;
        }

        /// <summary>
        /// SPEC §16.2 的第 1–6 步：幂等跳过、requestId 校验、租约判定、大小上限、解析与 deadline。
        /// <para>
        /// 返回 null 表示这一轮不该启动它——可能已经处理过、进了死信、被判 stale，或者另一个进程正持有租约。
        /// 这些分支各自已经落盘，调用方什么都不用补。
        /// </para>
        /// </summary>
        /// <param name="commandPath">commands/ 下的命令文件路径。</param>
        /// <returns>可以启动的命令；不该启动时返回 null。</returns>
        private PendingCommand? AdmitCommand(string commandPath)
        {
            string requestId = Path.GetFileNameWithoutExtension(commandPath);
            string resultPath = Path.Combine(mResultsDirectory, requestId + CourierProtocol.JSON_EXTENSION);
            string claimPath = Path.Combine(mClaimsDirectory, requestId + CourierProtocol.JSON_EXTENSION);

            // 第 1 步：results 已存在说明这条命令处理完了，删掉残留命令即可，绝不重复执行。
            if (File.Exists(resultPath))
            {
                // 幂等跳过也要清干净：崩溃可能停在"写完 results、还没删 progress"之间（SPEC §13.2）。
                ArchiveCommand(requestId, commandPath);
                CourierIO.TryDelete(claimPath);
                DeleteProgress(requestId);
                return null;
            }

            if (!CourierProtocol.IsValidRequestId(requestId))
            {
                DeadletterCommand(requestId, commandPath, CourierProtocol.REASON_INVALID_COMMAND, "requestId does not match the file name", 0);
                return null;
            }

            // 第 2/3 步（SPEC §10.1）：租约未到期就说明持有者还在跑，本轮跳过；到期则接管并把 attempts +1。
            int attempts;
            bool takeOver;
            if (!TryDecideClaim(claimPath, out attempts, out takeOver)) return null;

            // 第 4/5 步（SPEC §9.2）：先看长度再读入内存；解析失败进 deadletter 而不是抛给宿主。
            CourierCommand? command = LoadCommand(requestId, commandPath, attempts);
            if (command == null) return null;

            // 第 6 步（SPEC §9.1）：deadline 已过就 MUST NOT 执行，落 rejected/stale 并归档。
            if (DateTime.UtcNow > command.DeadlineUtc)
            {
                WriteResultAndArchive(requestId, commandPath, CourierProtocol.STATUS_REJECTED, string.Empty, CourierProtocol.REASON_STALE, false);
                return null;
            }

            return new PendingCommand(requestId, commandPath, claimPath, attempts, takeOver, command);
        }

        /// <summary>
        /// SPEC §10.1 的第 2/3 步：读现有 claim，决定这一轮是否接管这条命令、接管后是第几次执行。
        /// </summary>
        /// <param name="claimPath">claim 文件路径。</param>
        /// <param name="attempts">本次执行的序号（首次为 1，接管时在旧值上 +1）。</param>
        /// <param name="takeOver">是否接管了一条已存在的 claim（决定写入时能否覆盖）。</param>
        /// <returns>false 表示这一轮不该碰这条命令：租约还新鲜，或重试次数已耗尽。</returns>
        private bool TryDecideClaim(string claimPath, out int attempts, out bool takeOver)
        {
            attempts = 1;
            takeOver = false;
            string? claimText;
            CourierClaim? existingClaim = null;
            if (CourierIO.TryReadAllText(claimPath, out claimText)) existingClaim = CourierClaim.TryParse(claimText);

            // claim 文件在、内容却读不懂：这不是"别人正在跑"，而是一条没人能证明有效的租约。
            // 按"可以接管"处理，否则一颗崩溃留下的坏 claim 会永远卡住这条命令。
            if (existingClaim == null)
            {
                takeOver = File.Exists(claimPath);
                return true;
            }

            if (!IsClaimExpired(existingClaim.ExpiresAtUtc)) return false;
            attempts = existingClaim.Attempts + 1;
            takeOver = true;
            if (attempts <= CourierProtocol.MAX_ATTEMPTS) return true;

            DeadletterCommand(
                Path.GetFileNameWithoutExtension(claimPath),
                Path.Combine(mCommandsDirectory, Path.GetFileNameWithoutExtension(claimPath) + CourierProtocol.JSON_EXTENSION),
                CourierProtocol.REASON_HANDLER_ERROR,
                "retry limit exceeded",
                existingClaim.Attempts);
            return false;
        }

        /// <summary>
        /// SPEC §16.2 的第 4/5 步：先看文件长度再读入内存，然后解析。失败即进死信。
        /// </summary>
        /// <param name="requestId">命令标识。</param>
        /// <param name="commandPath">命令文件路径。</param>
        /// <param name="attempts">本次执行的序号，写进死信记录。</param>
        /// <returns>解析出来的命令；读不到或读不懂时返回 null。</returns>
        private CourierCommand? LoadCommand(string requestId, string commandPath, int attempts)
        {
            long length;
            try
            {
                length = new FileInfo(commandPath).Length;
            }
            catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException)
            {
                // 命令文件在枚举之后被归档或删掉了：本轮什么都不做，下一轮自然也不会再看到它。
                return null;
            }

            if (length > CourierProtocol.MAX_COMMAND_FILE_BYTES)
            {
                DeadletterCommand(requestId, commandPath, CourierProtocol.REASON_INVALID_COMMAND, "command file exceeds " + CourierProtocol.MAX_COMMAND_FILE_BYTES + " bytes", attempts);
                return null;
            }

            string? commandText;
            if (!CourierIO.TryReadAllText(commandPath, out commandText)) return null;

            CourierCommand? command = CourierCommand.TryParse(commandText, requestId);
            if (command == null)
            {
                DeadletterCommand(requestId, commandPath, CourierProtocol.REASON_INVALID_COMMAND, "command could not be parsed", attempts);
            }

            return command;
        }

        /// <summary>
        /// SPEC §10.1 第 8 步：写下本次的租约。首次认领用不覆盖语义，抢不到说明别人先到了；
        /// 接管过期租约时必须覆盖写——那条 claim 文件本来就还在盘上，继续用不覆盖语义会永远写不进去。
        /// </summary>
        /// <param name="pending">已通过前置检查的命令。</param>
        /// <returns>租约是否已写入。</returns>
        private bool TryWriteClaim(PendingCommand pending)
        {
            DateTime now = DateTime.UtcNow;
            CourierClaim claim = CourierClaim.Create(mEndpointId, now + CourierProtocol.CLAIM_WINDOW, pending.Attempts);
            try
            {
                CourierIO.WriteAtomic(pending.ClaimPath, claim.ToJson(), pending.TakeOver);
            }
            catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException)
            {
                // 抢不到 claim（别人先创建，或目录不可写）：本轮放弃，命令留在 commands/ 等下一轮。
                return false;
            }

            return true;
        }

        /// <summary>
        /// SPEC §16.2 第 9 步：把 handler 交给线程池，Tick 立刻继续往下走，不等它。
        /// <para>
        /// 上下文在这一刻构造：<c>Attempts</c> 已经是本次执行的次数，所以 handler 看到 <c>Attempt &gt; 1</c>
        /// 就知道上一次执行很可能已经产生了副作用（SPEC §9 的 at-least-once）。
        /// </para>
        /// </summary>
        /// <param name="pending">已写入租约的命令。</param>
        /// <param name="handler">路由到的处理函数。</param>
        private void LaunchHandler(PendingCommand pending, HandlerEntry handler)
        {
            var source = new CancellationTokenSource();
            CancellationToken token = source.Token;
            CourierCall call = new CourierCall(
                pending.RequestId,
                mEndpointId,
                pending.Command.Channel,
                pending.Command.Action,
                pending.Command.IssuedBy,
                pending.Command.IssuedAtUtc,
                pending.Command.DeadlineUtc,
                pending.Attempts,
                DateTime.UtcNow,
                pending.Command.Payload);

            // 委托与载荷先落到局部变量再进闭包：闭包只捕获这几个局部量，不再拖住整个 handler 条目与命令模型。
            // 两条调用路径在注册时就已经定好（见 HandlerEntry），这里只判断一次。
            Func<CourierCall, CancellationToken, Task<string>>? callAware = handler.CallAware;
            Func<string, CancellationToken, Task<string>> plain = handler.Handler;
            string payload = pending.Command.Payload;
            Task<string> task = callAware != null
                ? Task.Run(() => callAware(call, token))
                : Task.Run(() => plain(payload, token));

            // 命令落定后要叫醒两方：泵去写终态，以及 Dispose / WaitForIdle 不必再空转轮询。
            // 续延只做两次置位，且 ExecuteSynchronously —— 它不会给线程池添一条独立任务。
            task.ContinueWith(SettleWake, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            var entry = new InflightEntry(pending.RequestId, pending.CommandPath, pending.Command.DeadlineUtc, source, task);
            lock (mGate)
            {
                mInflight[pending.RequestId] = entry;
            }
        }
    }
}
