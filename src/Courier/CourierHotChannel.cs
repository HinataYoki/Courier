using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Courier
{
    /// <summary>
    /// 调用方持有的热通道会话：一个私有命名空间 + 一个槽的租约。
    /// <para>
    /// 契约只有一条：**热通道只负责叫醒，不负责记账**。请求与响应仍然由
    /// <see cref="CourierDispatcher"/> 与 CourierNode 的落盘路径写成
    /// <c>commands/</c> 与 <c>results/</c>；这里搬的只是"去看一眼"这句话。
    /// 因此任何一步失败都退回文件协议，终态证据的形态与没有热通道时逐字节相同（SPEC §5.2 第 2 条）。
    /// </para>
    /// <para>
    /// 本类型是"绝不把调用变糟"这条契约的最后一道防线：所有对外方法都只返回布尔或空值，
    /// 不把任何异常放给调用方。调用方拿到 <c>false</c> / <c>null</c> 时唯一的动作是回到文件通道。
    /// </para>
    /// </summary>
    internal sealed class CourierHotChannel : IDisposable
    {
        /// <summary>每个通道私有的接收缓冲：热通道的并发单位是通道，不是调用，所以不共享。</summary>
        private readonly byte[] mBuffer = new byte[CourierHot.BODY_BYTES];

        private readonly CourierHotNamespace mNamespace;
        private readonly CourierHotPool mPool;
        private readonly int mSlot;
        private readonly int mGeneration;
        private bool mDisposed;

        /// <summary>接管一套已经打开的资源；句柄所有权转给本对象。</summary>
        /// <param name="ns">私有命名空间。</param>
        /// <param name="pool">槽池。</param>
        /// <param name="slot">本通道占据的槽号。</param>
        /// <param name="generation">抢槽时拿到的代数，用于识别槽被回收后转手。</param>
        private CourierHotChannel(CourierHotNamespace ns, CourierHotPool pool, int slot, int generation)
        {
            mNamespace = ns;
            mPool = pool;
            mSlot = slot;
            mGeneration = generation;
        }

        /// <summary>本通道占据的槽号。</summary>
        internal int Slot
        {
            get { return mSlot; }
        }

        /// <summary>本次会话的私有命名空间别名。宿主重启会换 sessionId，别名随之改变，旧的连接必须作废。</summary>
        internal string Alias
        {
            get { return mNamespace.Alias; }
        }

        /// <summary>对端宿主是否还活着；跨完整性级别拿不到死亡句柄时按"活着"处理（见 <see cref="PeerState"/>）。</summary>
        /// <returns>可以继续使用这条通道时为 <c>true</c>。</returns>
        internal bool PeerAlive()
        {
            return !mDisposed && mPool.PeerAlive();
        }

        /// <summary>
        /// 槽是否仍归本进程（pid + 代数）。宿主会在空闲时清扫死主人的槽，复用前确认一次，
        /// 避免两条逻辑请求交叉写同一个槽的两个方向。
        /// </summary>
        /// <returns>仍归本进程时为 <c>true</c>。</returns>
        internal bool StillOwned()
        {
            if (mDisposed) return false;
            return mPool.StillOwned(mSlot, CourierHot.CurrentProcessId, mGeneration);
        }

        /// <summary>
        /// 打开目标端点的热通道并抢一个槽。任何一步不成立（平台不支持、端点没开热通道、命名空间打不开、
        /// 槽全被占）都返回 <c>null</c> —— 调用方据此走原来的纯文件路径，行为与没有热通道时一致。
        /// </summary>
        /// <param name="rootDirectory">协议根目录。</param>
        /// <param name="endpointId">端点标识。</param>
        /// <param name="descriptor">对端写下的会合描述；<c>null</c> 表示没有描述。</param>
        /// <param name="timeoutMs">建连预算（打开节、抢槽都算在里面）。</param>
        /// <returns>可用的通道，或 <c>null</c>。</returns>
        internal static CourierHotChannel? TryOpen(string rootDirectory, string endpointId, CourierHotDescriptor? descriptor, int timeoutMs)
        {
            if (descriptor == null || !CourierHot.IsSupported) return null;
            if (descriptor.Alias.Length == 0 || descriptor.HostPid <= 0) return null;

            CourierHotNamespace? ns = null;
            CourierHotPool? pool = null;
            try
            {
                // 私有命名空间必须先打开：池与门铃都用"别名\对象名"寻址，别名只在持有命名空间的进程里有意义。
                ns = CourierHotNamespace.Open(rootDirectory, endpointId, descriptor.Alias);
                if (ns == null) return null;
                pool = CourierHotPool.Open(ns.Alias, rootDirectory, endpointId, descriptor.HostPid, timeoutMs);
                if (pool == null)
                {
                    ns.Dispose();
                    return null;
                }

                int generation;
                int slot = pool.Claim(timeoutMs, out generation);
                if (slot < 0)
                {
                    pool.Dispose();
                    ns.Dispose();
                    return null;
                }

                return new CourierHotChannel(ns, pool, slot, generation);
            }
            catch (Exception caught) when (IsHotFailure(caught))
            {
                if (pool != null) pool.Dispose();
                if (ns != null) ns.Dispose();
                return null;
            }
        }

        /// <summary>
        /// 把"这条命令已经落盘了，去看一眼"发给宿主。帧正文放整条请求；装不下或环满时返回 false。
        /// </summary>
        /// <param name="requestId">请求标识（文件通道里那个文件名）。</param>
        /// <param name="issuedBy">发起方端点。</param>
        /// <param name="channel">命令通道名。</param>
        /// <param name="action">动作名。</param>
        /// <param name="payload">载荷；<c>null</c> 视作空串。</param>
        /// <param name="deadlineUtc">调用方的截止时刻，只用于让对端早点丢掉过期的登记。</param>
        /// <returns>帧已送达宿主侧环时为 <c>true</c>；<c>false</c> 表示调用方必须走文件通道。</returns>
        /// <remarks>
        /// 这里兜住的异常范围必须比"能想到的"更宽：热通道内部的任何异常（<see cref="ArgumentException"/>
        /// 来自越界偏移、<see cref="InvalidOperationException"/> 来自被撤掉的句柄）一旦漏出去，就会
        /// 变成调用方的一次失败——而文件通道本来是能成事的（P2-7）。
        /// </remarks>
        internal bool Announce(string requestId, string issuedBy, string channel, string action, string? payload, DateTime deadlineUtc)
        {
            if (mDisposed) return false;

            string? frame;
            byte[] body;
            try
            {
                frame = CourierHotFrame.BuildRequest(requestId, issuedBy, channel, action, payload, deadlineUtc);
                if (frame == null) return false;
                body = Encoding.UTF8.GetBytes(frame);
            }
            catch (Exception caught) when (IsHotFailure(caught))
            {
                CourierHotFaults.Record("announce: encode failed: " + caught.GetType().Name);
                return false;
            }

            try
            {
                return mPool.Send(mSlot, true, mSlot, CourierHot.KIND_REQUEST, body, body.Length);
            }
            catch (Exception caught) when (IsHotFailure(caught) || caught is ObjectDisposedException)
            {
                CourierHotFaults.Record("announce: send failed: " + caught.GetType().Name);
                return false;
            }
        }

        /// <summary>
        /// 阻塞等属于 <paramref name="requestId"/> 的响应通知。返回 null 表示超时、对端已死或通道失效，
        /// 调用方此时必须回到文件轮询 —— 结果本身早已落盘，热通道丢一条通知不会改变终态。
        /// </summary>
        /// <param name="requestId">请求标识。</param>
        /// <param name="timeoutMs">等待上限毫秒数。</param>
        /// <returns>解析成功的响应，或 <c>null</c>。</returns>
        internal CourierResponse? WaitResponse(string requestId, int timeoutMs)
        {
            if (mDisposed) return null;
            if (timeoutMs <= 0) return null;

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0) return null;

                try
                {
                    bool peerDead;
                    bool signalled = mPool.WaitForFrame(mSlot, remaining, out peerDead);
                    if (peerDead) return null;
                    if (!signalled) return null;

                    CourierResponse? response = DrainResponse(requestId);
                    if (response != null) return response;
                }
                catch (Exception caught) when (IsHotFailure(caught) || caught is ObjectDisposedException)
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// 把这个槽的宿主→客户端方向读空，找出属于 <paramref name="requestId"/> 的那条响应。
        /// </summary>
        /// <param name="requestId">请求标识。</param>
        /// <returns>匹配的响应；本轮没读到匹配项时为 <c>null</c>。</returns>
        /// <remarks>门铃会被内核合并：一次唤醒可能对应多条帧，所以必须读空而不是只读一条。</remarks>
        private CourierResponse? DrainResponse(string requestId)
        {
            while (mPool.Receive(mSlot, false, out _, out ushort kind, mBuffer, out int length))
            {
                if (kind != CourierHot.KIND_RESPONSE || length <= 0) continue;
                CourierResponse? response = CourierHotFrame.TryParseResponse(Encoding.UTF8.GetString(mBuffer, 0, length));
                if (response == null) continue;
                if (string.Equals(response.RequestId, requestId, StringComparison.Ordinal)) return response;
            }

            return null;
        }

        /// <summary>
        /// 热通道内部可预期的失败类型。判断集中在一处，避免每个 catch 各写一套。
        /// </summary>
        /// <param name="caught">捕获到的异常。</param>
        /// <returns>属于"通道不可用"这一类时为 <c>true</c>。</returns>
        private static bool IsHotFailure(Exception caught)
        {
            return caught is IOException ||
                caught is UnauthorizedAccessException ||
                caught is InvalidOperationException ||
                caught is ArgumentException ||
                caught is NotSupportedException ||
                caught is TimeoutException ||
                caught is WaitHandleCannotBeOpenedException;
        }

        /// <summary>释放槽租约与底层句柄；幂等。释放顺序是"先交槽，再关池与命名空间"。</summary>
        public void Dispose()
        {
            if (mDisposed) return;
            mDisposed = true;
            try { mPool.Release(mSlot); }
            catch (Exception caught) when (caught is ObjectDisposedException || caught is IOException) { }
            mPool.Dispose();
            mNamespace.Dispose();
        }
    }
}
