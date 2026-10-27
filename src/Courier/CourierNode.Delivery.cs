using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Courier
{
    /// <summary>
    /// <see cref="CourierNode"/> 的调用方一侧：投递命令、等待终态、列端点、清理 results（SPEC §9、§11、§15.2）。
    /// </summary>
    public sealed partial class CourierNode
    {
        /// <summary>异步投递并等待结果（SPEC §17）。等待期间不会驱动本节点的 Tick，因此可在 Tick 循环的线程上安全调用。</summary>
        /// <param name="endpointId">目标端点，须满足 SPEC §4.1。</param>
        /// <param name="channel">目标信道。</param>
        /// <param name="action">目标动作。</param>
        /// <param name="payload">不透明字符串，可为 null 或空串。</param>
        /// <param name="timeout">等待上限，同时作为命令的 deadline（SPEC §9.1）；超时返回 <c>unreachable</c>，不落盘（SPEC §11.2）。</param>
        /// <returns>四种结果之一，永不返回 null。</returns>
        /// <exception cref="ArgumentException">参数非法，或 timeout 不是正数。</exception>
        public async Task<CourierResponse> CallAsync(string endpointId, string channel, string action, string? payload, TimeSpan timeout)
        {
            ThrowIfDisposed();
            ValidateCall(endpointId, channel, action, timeout);
            string requestId;
            // SPEC §9.1：deadline 由调用方按次指定。等待上限就是这条命令的 deadline——
            // 比等待时间更短的 deadline 会让服务者先判 stale，比它更长则让命令在调用方放弃后还占着队列。
            CourierResponse? early = CourierDispatcher.Deliver(mRootDirectory, mLayout, mEndpointId, endpointId, channel, action, payload, timeout, out requestId);
            if (early != null) return early;

            DateTime deadline = DateTime.UtcNow + timeout;

            // 热通道旁路（docs/HOT-CHANNEL.md §4）：命令已经落盘，这里只是再叫醒一次。
            // 命中就省掉整个轮询；任何一步不成立都退回下面的文件等待——终态证据的形态与没有热通道时逐字节相同。
            CourierResponse? awoken = await TryAwaitHotAsync(endpointId, requestId, channel, action, payload, deadline).ConfigureAwait(false);
            if (awoken != null) return awoken;

            CourierResponse? settled = await WaitForResultFileAsync(endpointId, requestId, deadline).ConfigureAwait(false);
            if (settled != null) return settled;

            // SPEC §15.2 的读者条款：结果文件缺失时，墓碑能把"已被回收"与"从没处理过"分开。
            // 这只是诊断信息——status 仍然是 unreachable，原因码集合不变，也没有任何文件被写。
            string resultsDirectory = Path.Combine(EndpointDirectoryOf(endpointId), mLayout.ResultsDirectory);
            bool reclaimed = CourierResultsGc.IsPossiblyReclaimed(resultsDirectory, requestId);

            // SPEC §11.2：unreachable 是调用方合成的结果，不写任何文件。
            return new CourierResponse(requestId, CourierProtocol.STATUS_UNREACHABLE, string.Empty, null, false, DateTime.UtcNow, reclaimed);
        }

        /// <summary>
        /// 热通道旁路等待（docs/HOT-CHANNEL.md §4）：命令落盘之后再叫醒服务者一次，然后在内核门铃上等一小段预算。
        /// </summary>
        /// <remarks>
        /// 这是纯优化路径：拿不到通道、预算耗尽、或对端看起来已经不可信，都返回 null 让调用方退回文件等待。
        /// 它 MUST NOT 改变终态证据——拿到的响应仍然是从 <c>results/</c> 读出来的那一份。
        /// </remarks>
        /// <param name="endpointId">目标端点。</param>
        /// <param name="requestId">本次投递的命令标识。</param>
        /// <param name="channel">目标信道。</param>
        /// <param name="action">目标动作。</param>
        /// <param name="payload">不透明载荷。</param>
        /// <param name="deadline">本次等待的绝对上限。</param>
        /// <returns>叫醒期间拿到的响应；没有就是 null。</returns>
        private async Task<CourierResponse?> TryAwaitHotAsync(string endpointId, string requestId, string channel, string action, string? payload, DateTime deadline)
        {
            CourierHotChannel? link = AnnounceHot(endpointId, requestId, channel, action, payload, deadline - DateTime.UtcNow);
            if (link == null) return null;

            long budgetMilliseconds = (long)(deadline - DateTime.UtcNow).TotalMilliseconds;
            if (budgetMilliseconds > HOT_WAIT_CAP_MS) budgetMilliseconds = HOT_WAIT_CAP_MS;

            CourierResponse? awoken = null;
            if (budgetMilliseconds > 0)
            {
                // 门铃是内核等待，不能占住调用方的线程；一次 ThreadPool 派发的成本远小于它省下的轮询。
                CourierHotChannel waiting = link;
                string waitingId = requestId;
                int budget = (int)budgetMilliseconds;
                awoken = await Task.Run(() => waiting.WaitResponse(waitingId, budget)).ConfigureAwait(false);
            }

            // 通道的存活判定独立于"这一次有没有等到结果"：一条被回收的通道必须从池里摘掉，
            // 否则后续每一次调用都会先白等一个来回。
            bool healthy = link.PeerAlive() && link.StillOwned();
            mHotClient.Return(endpointId, link, healthy);
            if (!healthy) InvalidateHotDescriptor(endpointId);
            return awoken;
        }

        /// <summary>
        /// SPEC §17.2 的文件等待：10 ms 起指数退避到 200 ms 上限，MUST NOT 无限等待。
        /// </summary>
        /// <param name="endpointId">目标端点。</param>
        /// <param name="requestId">本次投递的命令标识。</param>
        /// <param name="deadline">本次等待的绝对上限。</param>
        /// <returns>终态响应；到点仍未出现时返回 null。</returns>
        private async Task<CourierResponse?> WaitForResultFileAsync(string endpointId, string requestId, DateTime deadline)
        {
            string resultPath = Path.Combine(
                EndpointDirectoryOf(endpointId),
                mLayout.ResultsDirectory,
                requestId + CourierProtocol.JSON_EXTENSION);

            int delayMilliseconds = 10;
            while (true)
            {
                string? text;
                if (CourierIO.TryReadAllText(resultPath, out text))
                {
                    CourierResponse? response = CourierResponse.TryParse(text);
                    // 读到了文件但解析不出来：它可能正被另一个写者替换，下一轮再读一次比直接判负更接近事实。
                    if (response != null) return response;
                }

                long remaining = (long)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0) return null;

                int delay = remaining < delayMilliseconds ? (int)remaining : delayMilliseconds;
                await Task.Delay(delay).ConfigureAwait(false);
                delayMilliseconds *= 2;
                if (delayMilliseconds > 200) delayMilliseconds = 200;
            }
        }

        /// <summary>同步投递并等待结果。只在没有 Tick 循环的短命进程里安全（SPEC §17.3）。</summary>
        /// <param name="endpointId">目标端点，须满足 SPEC §4.1。</param>
        /// <param name="channel">目标信道。</param>
        /// <param name="action">目标动作。</param>
        /// <param name="payload">不透明字符串，可为 null 或空串。</param>
        /// <param name="timeout">等待上限，同时作为命令的 deadline。</param>
        /// <returns>四种结果之一，永不返回 null。</returns>
        /// <exception cref="ArgumentException">参数非法，或 timeout 不是正数。</exception>
        public CourierResponse Call(string endpointId, string channel, string action, string? payload, TimeSpan timeout)
        {
            // 只保留一份等待循环：同步版直接阻塞在异步版上，两条路径的退避与超时语义不会再各自漂移。
            return CallAsync(endpointId, channel, action, payload, timeout).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        /// <summary>扫描 root 下所有端点，读取 endpoint.json 与 heartbeat 推算存活状态（SPEC §6）。只做一次目录扫描，不缓存结果。</summary>
        /// <returns>按 endpointId 升序排列的只读快照。</returns>
        public IReadOnlyList<CourierEndpointInfo> Endpoints()
        {
            ThrowIfDisposed();
            return CourierEndpointReader.ReadAll(CourierLayout.Combine(mRootDirectory, mLayout.EndpointsDirectory));
        }

        /// <summary>
        /// 回收本端点 <c>results/</c> 里 <paramref name="cutoffUtc"/> 之前完成的响应（SPEC §15.2）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 顺序是硬性的：先原子写 <c>results/gc.json</c> 墓碑，再删结果。墓碑写不出来就不删任何东西——
        /// 否则读者只会看到"结果凭空消失"，分不清是回收还是从未处理过。
        /// </para>
        /// <para>
        /// 在途命令永远不动：命令还在 <c>commands/</c>、claim 还在 <c>claims/</c>、或响应读不懂，都会跳过。
        /// 重复调用是安全的：没有可删的东西时只刷新墓碑，<c>gc.json</c> 自己永远不会被删。
        /// </para>
        /// <para>
        /// 这是服务者自己的维护动作。第三方 MUST NOT 搬动或删除 <c>results/</c>（SPEC §2、§15.2）。
        /// </para>
        /// </remarks>
        /// <param name="cutoffUtc">完成时刻早于它的响应会被删除。必须是 UTC。</param>
        /// <returns>实际删除的响应文件数。</returns>
        /// <exception cref="IOException">墓碑写不出来（此时一个文件都没删）。</exception>
        /// <exception cref="ArgumentException"><paramref name="cutoffUtc"/> 的 <c>Kind</c> 不是 <see cref="DateTimeKind.Utc"/>。</exception>
        /// <exception cref="ObjectDisposedException">端点已关闭。</exception>
        public int CollectResults(DateTime cutoffUtc)
        {
            ThrowIfDisposed();
            if (cutoffUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("cutoff must be UTC (SPEC §15.2)", nameof(cutoffUtc));
            }

            return CourierResultsGc.Collect(mResultsDirectory, mCommandsDirectory, mClaimsDirectory, cutoffUtc);
        }

        /// <summary>投递前的本地校验：名字不合规或超时非正数都是编程错误，MUST 在写任何文件之前就报出来。</summary>
        /// <param name="endpointId">目标端点，须满足 SPEC §4.1。</param>
        /// <param name="channel">目标信道，须满足 SPEC §4.3。</param>
        /// <param name="action">目标动作，须满足 SPEC §4.3。</param>
        /// <param name="timeout">等待上限，必须为正。</param>
        /// <exception cref="ArgumentException">任一参数不合规。</exception>
        private static void ValidateCall(string endpointId, string channel, string action, TimeSpan timeout)
        {
            if (!CourierProtocol.IsValidEndpointId(endpointId)) throw new ArgumentException("invalid endpointId: must match ^[a-z0-9]([a-z0-9-]{0,62}[a-z0-9])?$", nameof(endpointId));
            if (!CourierProtocol.IsValidName(channel)) throw new ArgumentException("invalid channel: must match ^[A-Za-z0-9_.-]{1,64}$", nameof(channel));
            if (!CourierProtocol.IsValidName(action)) throw new ArgumentException("invalid action: must match ^[A-Za-z0-9_.-]{1,64}$", nameof(action));
            if (timeout <= TimeSpan.Zero) throw new ArgumentException("timeout must be positive", nameof(timeout));
        }
    }
}
