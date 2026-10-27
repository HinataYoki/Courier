using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Courier
{
    /// <summary>
    /// <see cref="CourierNode"/> 的服务者一侧，即 SPEC §16 的七步泵：心跳、取消、收割、续约、归档清理与注册表刷新。
    /// <para>命令如何被接纳与启动见 <c>CourierNode.Service.Start.cs</c>；终态如何写盘、归档与收尾也在本文件下半段。</para>
    /// </summary>
    public sealed partial class CourierNode
    {
        /// <summary>
        /// 驱动一轮服务者循环（SPEC §16）：写心跳 → 处理 cancels/ → 收割已完成的 in-flight → 续约仍在执行的
        /// in-flight → 按文件名升序在名额内启动至多 <c>MAX_TICK_BATCH</c> 条命令 → 清理终态 progress。
        /// <para>这是一个**非阻塞的泵**：启动 handler 后立即返回，MUST NOT 等 handler 跑完（SPEC §16.1）。MUST NOT 并发调用。</para>
        /// </summary>
        /// <exception cref="ObjectDisposedException">端点已关闭。</exception>
        public void Tick()
        {
            ThrowIfDisposed();
            // SPEC §5：第一次 Tick 之前是能力表的写入窗口，之后冻结。
            mStarted = true;

            // 热泵只在第一次 Tick 之后存在：在此之前本端点还不服务任何命令，
            // 让内存消息就能叫醒一个"还没开始服务"的端点，会绕过 SPEC §5 的能力表冻结语义。
            EnsureHotPump();

            // SPEC §16.1：Tick 与热泵都从这道门进，因此二者绝不会同时执行服务步骤。
            lock (mServiceGate)
            {
                ServiceOnce();
            }
        }

        /// <summary>
        /// 一轮完整的服务步骤，不含任何加锁与生命周期判断——调用方必须已持有 <see cref="mServiceGate"/>。
        /// <para>
        /// 每一步各自兜住 I/O 故障：任何一步失败都不该让服务循环停摆（SPEC §16.2 最后一条）。
        /// 兜住之后用 <see cref="RecordServiceFault"/> 留下最后一条记录，故障因此可查而不是无声消失。
        /// </para>
        /// </summary>
        private void ServiceOnce()
        {
            RunServiceStep("write-heartbeat", WriteHeartbeat);
            RunServiceStep("process-cancels", ProcessCancels);
            // 收割不走 RunServiceStep：Finish 自己逐条兜 I/O 故障，这里再包一层会重复记录。
            ReapInflight();
            RunServiceStep("renew-inflight", RenewInflight);
            RunServiceStep("start-commands", StartCommands);
            RunServiceStep("clean-progress", CleanProgress);
            // SPEC §15.3：archive 的保留策略 MUST 有界，所以清理必须挂在服务循环里而不是靠人手动跑。
            // 它限频执行（默认一小时一轮），把一次目录列举的成本摊薄到可以忽略。
            RunServiceStep("sweep-archive", SweepArchive);
            // SPEC §7：限频刷新注册表。它放在最后，把固定开销压在整轮的最尾巴上，
            // 且失败只降级不冒泡——注册表是可选的加速视图，绝不能反过来拖住服务循环。
            RunServiceStep("publish-registry", () => PublishRootRegistry(false));
        }

        /// <summary>
        /// 执行一个服务步骤，把 I/O 类故障兜住并留痕。<paramref name="step"/> 只用于故障记录，
        /// 不参与任何协议判定。
        /// </summary>
        /// <param name="step">步骤名，出现在 <see cref="LastServiceFault"/> 里。</param>
        /// <param name="action">要执行的步骤。</param>
        private void RunServiceStep(string step, Action action)
        {
            try
            {
                action();
            }
            catch (Exception caught) when (IsIoFailure(caught))
            {
                RecordServiceFault(step, caught);
            }
        }

        /// <summary>SPEC §13.1：把取消意图送到正在执行的 handler 手里，或对尚未开始的命令直接落终态。</summary>
        private void ProcessCancels()
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(mCancelsDirectory, "*" + CourierProtocol.JSON_EXTENSION);
            }
            catch (Exception caught) when (caught is DirectoryNotFoundException || caught is IOException)
            {
                return;
            }

            // 取消的处理顺序不影响结果，不做排序，省掉一次 O(n log n) 分配。
            for (int index = 0; index < files.Length; index++)
            {
                string requestId = Path.GetFileNameWithoutExtension(files[index]);
                if (!CourierProtocol.IsValidRequestId(requestId))
                {
                    CourierIO.TryDelete(files[index]);
                    continue;
                }

                // SPEC §13.1：对**正在执行**的 requestId 触发 CancellationToken。这正是 Tick 不被 handler
                // 阻塞换来的能力——handler 在别的线程上跑，Tick 照常进来把信号递到它手里。
                if (!TrySignalInflightCancel(requestId)) RejectCancelBeforeStart(requestId);

                // 取消标记本身是投递方的意图，处理完就删；没能生效的（命令正在别处执行）也删，
                // 因为取消是尽力而为（SPEC §13.1），留着只会让下一轮重复判定。
                CourierIO.TryDelete(files[index]);
            }
        }

        /// <summary>对正在执行的命令触发取消信号（SPEC §13.1）。</summary>
        /// <param name="requestId">要取消的命令标识。</param>
        /// <returns>该命令是否正由本端点执行。</returns>
        private bool TrySignalInflightCancel(string requestId)
        {
            lock (mGate)
            {
                InflightEntry? entry;
                if (!mInflight.TryGetValue(requestId, out entry) || entry == null) return false;
                try
                {
                    entry.Source.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // handler 已经停下、CTS 被回收：取消来得太晚，等价于"没赶上"。
                }

                return true;
            }
        }

        /// <summary>
        /// SPEC §13.1：命令尚未开始执行时，直接落 rejected/cancelled-before-start 并归档，不进 handler。
        /// 但"尚未开始"必须以 claims/ 为准（SPEC §10.1）：命令文件还在、claim 却新鲜，说明另一个进程正在跑，
        /// 此时写终态会让同一 requestId 出现两次结果，还留下一条永远删不掉的孤儿 claim。
        /// </summary>
        /// <param name="requestId">被取消的命令标识。</param>
        private void RejectCancelBeforeStart(string requestId)
        {
            string commandPath = Path.Combine(mCommandsDirectory, requestId + CourierProtocol.JSON_EXTENSION);
            if (IsClaimedByOther(Path.Combine(mClaimsDirectory, requestId + CourierProtocol.JSON_EXTENSION))) return;
            if (!File.Exists(commandPath)) return;

            WriteResultAndArchive(
                requestId,
                commandPath,
                CourierProtocol.STATUS_REJECTED,
                string.Empty,
                CourierProtocol.REASON_CANCELLED_BEFORE_START,
                true);
        }

        /// <summary>SPEC §16 第 4 步：把已完成（或已超时）的 in-flight 写成终态并释放名额。</summary>
        private void ReapInflight()
        {
            var ready = new List<InflightEntry>();
            lock (mGate)
            {
                foreach (InflightEntry entry in mInflight.Values)
                {
                    if (entry.Task.IsCompleted || DateTime.UtcNow > entry.DeadlineUtc) ready.Add(entry);
                }

                for (int index = 0; index < ready.Count; index++) mInflight.Remove(ready[index].RequestId);
            }

            for (int index = 0; index < ready.Count; index++)
            {
                // SPEC §16.2：收割路径的失败 MUST NOT 穿透 Tick。写 results 的 I/O 失败已在 Finish 内单独处理，
                // 这里再兜一层，防止归档或释放 CTS 时的意外把整个服务循环打死。
                try
                {
                    Finish(ready[index]);
                }
                catch (Exception caught) when (IsIoFailure(caught))
                {
                    RecordServiceFault("finish", caught);
                }
            }
        }

        /// <summary>SPEC §16.2 的 Reap：写终态、归档、删 claim、删 progress，每一步失败都不中断其余步骤。</summary>
        /// <param name="entry">要收尾的 in-flight 条目。</param>
        private void Finish(InflightEntry entry)
        {
            // SPEC §10.1：租约被别人接管后 MUST 放弃执行，且 MUST NOT 写 results/——
            // 新持有者会产出这条命令唯一的终态，我们这份结果属于已经被作废的那次执行。
            if (entry.Abandoned)
            {
                ObserveLateFailure(entry);
                DisposeSource(entry);
                return;
            }

            if (!entry.Task.IsCompleted)
            {
                // SPEC §16.2 第 4 条：超 HANDLER_TIMEOUT 必须立即写终态，MUST NOT 等 handler 返回——
                // 一个不响应取消的 handler 可能永远不返回，等它等于让这条命令永远没有结果。
                TryCancelSource(entry);
                Settle(entry, CourierProtocol.STATUS_FAILED, string.Empty, CourierProtocol.REASON_HANDLER_TIMEOUT, false);
                return;
            }

            string status = CourierProtocol.STATUS_OK;
            string payload = string.Empty;
            string? reason = null;
            if (entry.Task.IsCanceled)
            {
                // SPEC §13.1：handler 采纳了取消。
                status = CourierProtocol.STATUS_FAILED;
                reason = CourierProtocol.REASON_CANCELLED;
            }
            else if (entry.Task.IsFaulted)
            {
                // SPEC §11.1：业务逻辑执行了但失败，是 failed 而不是 rejected；异常不得穿透到宿主。
                Exception? error = entry.Task.Exception;
                status = CourierProtocol.STATUS_FAILED;
                reason = error != null && error.GetBaseException() is OperationCanceledException
                    ? CourierProtocol.REASON_CANCELLED
                    : CourierProtocol.REASON_HANDLER_ERROR;
            }
            else
            {
                payload = entry.Task.Result ?? string.Empty;
            }

            bool cancelRequested = entry.Source.IsCancellationRequested && status == CourierProtocol.STATUS_OK;
            Settle(entry, status, payload, reason, cancelRequested);
        }

        /// <summary>
        /// 把一条终态写盘、回铃、归档，并释放本次执行占用的资源。写不出终态就什么都不清——
        /// 命令留在 commands/、claim 留到租约到期，下一轮按 attempts + 1 重来（SPEC §1 不变式 3）。
        /// </summary>
        /// <param name="entry">要收尾的 in-flight 条目。</param>
        /// <param name="status">终态：<c>ok</c> 或 <c>failed</c>。</param>
        /// <param name="payload">终态载荷；失败时为空串。</param>
        /// <param name="reason">失败原因码；成功时为 null（SPEC §18）。</param>
        /// <param name="cancelRequested">终态成功但取消意愿已抵达时置 true（SPEC §13.1）。</param>
        private void Settle(InflightEntry entry, string status, string payload, string? reason, bool cancelRequested)
        {
            CourierResponse settled;
            try
            {
                settled = WriteResult(entry.RequestId, status, payload, reason, cancelRequested);
            }
            catch (Exception caught) when (IsIoFailure(caught))
            {
                // 写不出终态就什么都不清：先归档再删 claim 会让这条命令连证据一起消失，那才是真正不可恢复的丢件。
                RecordServiceFault("write-result", caught);
                ObserveLateFailure(entry);
                DisposeSource(entry);
                return;
            }

            // 终态已经落盘，才允许回铃叫醒等在对面的进程：顺序反过来就是"内存里说成功了，盘上却没有"。
            SignalHot(settled);

            if (status == CourierProtocol.STATUS_FAILED && entry.Task.IsCompleted) ObserveLateFailure(entry);
            ArchiveCommand(entry.RequestId, entry.CommandPath);
            CourierIO.TryDelete(Path.Combine(mClaimsDirectory, entry.RequestId + CourierProtocol.JSON_EXTENSION));
            DeleteProgress(entry.RequestId);
            DisposeSource(entry);
        }

        /// <summary>
        /// 对已经不打算再等的 handler 挂一个观察续延：它跑完或被取消之后，异常必须有人取走。
        /// 不挂的话，一个超时后被丢下的 handler 一旦抛异常，就只会在 GC 时变成
        /// <see cref="TaskScheduler.UnobservedTaskException"/>——那既不属于宿主也属于不了我们的诊断。
        /// </summary>
        /// <param name="entry">已经放弃等待的 in-flight 条目。</param>
        private static void ObserveLateFailure(InflightEntry entry)
        {
            Task<string> task = entry.Task;
            if (task.IsCompleted || task.IsFaulted)
            {
                // 已经落定：取一次 Exception 即视为观察过，任务不会被记成未观察异常。
                Observe(task);
                return;
            }

            task.ContinueWith(Observe, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        /// <summary>取走一个任务的异常，把它标记为已观察。绝不重抛：这条命令的终态早就写过了。</summary>
        /// <param name="task">要观察的任务。</param>
        private static void Observe(Task<string> task)
        {
            Exception? ignored = task.Exception;
            _ = ignored;
        }

        /// <summary>取消一个 in-flight 的令牌源；已经释放过的忽略。</summary>
        /// <param name="entry">目标 in-flight 条目。</param>
        private static void TryCancelSource(InflightEntry entry)
        {
            try
            {
                entry.Source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // handler 已经停下并释放了 CTS：取消来得太晚，等价于"没赶上"。
            }
        }

        /// <summary>
        /// 释放一个 in-flight 的 CancellationTokenSource，但只在 handler 已经停下时才释放。
        /// 对已经超时判负、却仍在自己跑的 handler 释放 CTS，会让它下一次访问 token 抛
        /// <see cref="ObjectDisposedException"/>——把协议的正确性建立在用户代码不再碰 token 上是不成立的。
        /// 宁可让一个跑飞的 handler 多留一个 CTS，也不能反过来打断它。
        /// </summary>
        /// <param name="entry">要释放的 in-flight 条目。</param>
        private static void DisposeSource(InflightEntry entry)
        {
            if (!entry.Task.IsCompleted) return;
            entry.Source.Dispose();
        }

        /// <summary>SPEC §16 第 5 步与 §10.1：对仍在执行的 in-flight 续约；发现租约已被接管就放弃执行。</summary>
        private void RenewInflight()
        {
            List<InflightEntry> snapshot;
            lock (mGate)
            {
                snapshot = new List<InflightEntry>(mInflight.Values);
            }

            DateTime now = DateTime.UtcNow;
            for (int index = 0; index < snapshot.Count; index++)
            {
                InflightEntry entry = snapshot[index];
                if (entry.Task.IsCompleted || entry.Abandoned) continue;
                if (now - entry.LastRenewedUtc < CourierProtocol.CLAIM_RENEW_INTERVAL) continue;

                string claimPath = Path.Combine(mClaimsDirectory, entry.RequestId + CourierProtocol.JSON_EXTENSION);
                string? claimText;
                CourierClaim? current = null;
                if (CourierIO.TryReadAllText(claimPath, out claimText)) current = CourierClaim.TryParse(claimText);
                if (current == null || !string.Equals(current.Owner, mEndpointId, StringComparison.Ordinal))
                {
                    // 租约丢了：别人已经接管，我们这次执行的结果没有资格落盘（SPEC §10.1）。
                    entry.Abandoned = true;
                    TryCancelSource(entry);
                    continue;
                }

                try
                {
                    CourierIO.WriteAtomic(claimPath, current.Renew(now).ToJson(), true);
                    entry.LastRenewedUtc = now;
                }
                catch (Exception caught) when (IsIoFailure(caught))
                {
                    // 这一次续约失败不要紧：租约还有大半个窗口，下一个 Tick 再试。
                }
            }
        }
    }
}