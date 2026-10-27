using System;
using System.Collections.Generic;
using System.Threading;

namespace Courier
{
    /// <summary>
    /// <see cref="CourierNode"/> 的热通道调用方：寻址对端、等回铃、失效重读（docs/HOT-CHANNEL.md）。
    /// </summary>
    public sealed partial class CourierNode
    {
        /// <summary>
        /// 向目标端点的热通道报一次信。返回 null 意味着"这次就当没有热通道"：目标没开、连不上、
        /// 抢不到槽、帧装不下，任何一种都退回文件等待，调用方不需要知道是哪一种。
        /// </summary>
        private CourierHotChannel? AnnounceHot(string endpointId, string requestId, string channel, string action, string? payload, TimeSpan timeout)
        {
            if (!CourierHot.IsSupported) return null;

            CourierHotDescriptor? descriptor = ResolveHotDescriptor(endpointId);
            if (descriptor == null) return null;

            CourierHotChannel? link = mHotClient.TryLease(mRootDirectory, endpointId, descriptor, HOT_OPEN_TIMEOUT_MS);
            if (link == null)
            {
                InvalidateHotDescriptor(endpointId);
                return null;
            }

            DateTime deadlineUtc = DateTime.UtcNow + timeout;
            DateTime latest = DateTime.UtcNow + CourierProtocol.MAX_DEADLINE;
            if (deadlineUtc > latest) deadlineUtc = latest;

            bool announced;
            try
            {
                announced = link.Announce(requestId, mEndpointId, channel, action, payload, deadlineUtc);
            }
            catch (ObjectDisposedException)
            {
                announced = false;
            }

            if (!announced)
            {
                mHotClient.Return(endpointId, link, false);
                InvalidateHotDescriptor(endpointId);
                return null;
            }

            return link;
        }

        /// <summary>
        /// 取目标端点的 hot 描述。读一次 endpoint.json 比一次热往返贵两个数量级，所以正结果缓存着复用；
        /// 负结果也缓存一小会儿，免得对着一个没开热通道的端点反复读盘。
        /// </summary>
        private CourierHotDescriptor? ResolveHotDescriptor(string endpointId)
        {
            DateTime now = DateTime.UtcNow;
            lock (mHotGate)
            {
                HotLookup? cached;
                if (mHotLookups.TryGetValue(endpointId, out cached) && cached != null && now < cached.RereadAfterUtc)
                {
                    return cached.Descriptor;
                }
            }

            CourierHotDescriptor? descriptor;
            if (!CourierHotDescriptor.TryRead(mRootDirectory, mLayout, endpointId, out descriptor)) descriptor = null;

            lock (mHotGate)
            {
                mHotLookups[endpointId] = new HotLookup(descriptor, now.AddSeconds(HOT_DESCRIPTOR_TTL_SECONDS));
            }

            return descriptor;
        }

        /// <summary>作废某端点的 hot 缓存：连接失败意味着对端换代或根本没有热通道，下一次调用重读 endpoint.json。</summary>
        private void InvalidateHotDescriptor(string endpointId)
        {
            lock (mHotGate)
            {
                mHotLookups.Remove(endpointId);
            }
        }

        /// <summary>清掉等太久的登记。调用方放弃后槽会归还，但 requestId 到槽的映射没人取走就会一直攒着。</summary>
        private void SweepHotWaiters()
        {
            long cutoff = DateTime.UtcNow.AddSeconds(-HOT_WAITER_LIFETIME_SECONDS).Ticks;
            lock (mHotGate)
            {
                if (mHotWaiters.Count == 0) return;
                List<string>? expired = null;
                foreach (KeyValuePair<string, HotWaiter> pair in mHotWaiters)
                {
                    if (pair.Value.CreatedUtcTicks >= cutoff) continue;
                    if (expired == null) expired = new List<string>();
                    expired.Add(pair.Key);
                }

                if (expired == null) return;
                for (int index = 0; index < expired.Count; index++) mHotWaiters.Remove(expired[index]);
            }
        }

        /// <summary>
        /// 关掉热通道：先停泵，再收客户端与三个宿主对象。顺序不能反——泵还活着就 Dispose 池，
        /// 它会拿着已经关掉的句柄继续等，最后变成一个没人叫醒的线程。
        /// <para>
        /// 账本必须最后关：它要把已经写出去的终态证据刷完盘。
        /// </para>
        /// </summary>
        private void StopHotChannel()
        {
            RemoveHotDescriptor();
            StopHotPump();
            mHotClient.Dispose();

            // 客户端退场即所有租约作废：对端看到的表现是"槽被释放"，而不是"槽的主人不见了"。
            lock (mHotGate)
            {
                mHotWaiters.Clear();
                mHotLookups.Clear();
            }

            DisposeHotJournal();
            DisposeHotPool();
            DisposeHotNamespace();
            mHotWaitSet = null;
        }

        /// <summary>
        /// 摘掉会合描述。必须先于拆池：对端看见的是一个"没有热通道"的端点，于是直接走文件协议，
        /// 而不是先按旧描述连一次、失败了再回退。描述文件缺失本来就是合法状态（SPEC §2）。
        /// </summary>
        /// <remarks>
        /// 删除必须核对归属（别名 + 进程号）：同一个端点目录里可以并排住着两个节点，
        /// 先退出的那个若无条件删，会把仍在服役那个的描述一起抹掉，对端于是每次都先撞一次连接失败。
        /// 命名空间还没建起来时没有本进程写下的描述，退回只按进程号核。
        /// </remarks>
        private void RemoveHotDescriptor()
        {
            if (mEndpointDirectory.Length == 0) return;
            CourierHotNamespace? ns = mHotNamespace;
            if (ns != null) CourierHotDescriptor.TryDeleteOwned(mEndpointDirectory, mLayout, ns.Alias, CourierProtocol.CurrentProcessId());
            else CourierHotDescriptor.TryDelete(mEndpointDirectory, mLayout);
        }

        /// <summary>停泵线程并等它在约定时间内退场；泵若已经自行结束，这里只是收回引用。</summary>
        private void StopHotPump()
        {
            try { mHotStop.Set(); }
            catch (ObjectDisposedException) { }

            Thread? pump = mHotPump;
            mHotPump = null;
            if (pump == null || ReferenceEquals(pump, Thread.CurrentThread)) return;

            try { pump.Join(HOT_PUMP_JOIN_MS); }
            catch (ThreadStateException) { }
        }

        /// <summary>关账本。它必须最后关：要把已经写出去的终态证据刷完盘。</summary>
        private void DisposeHotJournal()
        {
            CourierHotJournal? journal = mHotJournal;
            mHotJournal = null;
            if (journal == null) return;

            try { journal.Dispose(); }
            catch (Exception caught) when (IsIoFailure(caught)) { }
        }

        /// <summary>拆共享内存池。它比账本早退场，于是账本最后那几次写不会被一个已失效的池牵动。</summary>
        private void DisposeHotPool()
        {
            CourierHotPool? pool = mHotPool;
            mHotPool = null;
            if (pool == null) return;

            try { pool.Dispose(); }
            catch (Exception caught) when (IsIoFailure(caught)) { }
        }

        /// <summary>释放私有命名空间——这是本进程在系统里最后一份热通道痕迹。</summary>
        private void DisposeHotNamespace()
        {
            CourierHotNamespace? ns = mHotNamespace;
            mHotNamespace = null;
            if (ns != null) ns.Dispose();
        }

        /// <summary>泵的故障记账。写不进账本就算了——热通道本来就是可以整体缺席的旁路，不该因为它把 Dispose 卡住。</summary>
        private void RecordHotFault(Exception caught)
        {
            CourierHotJournal? journal = mHotJournal;
            if (journal == null) return;
            try
            {
                journal.Append(CourierHotFrame.BuildJournalLine(
                    string.Empty,
                    mEndpointId,
                    "hot",
                    "pump",
                    "failed",
                    caught.GetType().Name + ": " + caught.Message,
                    0,
                    0,
                    -1));
            }
            catch (Exception logged) when (IsIoFailure(logged) || logged is ObjectDisposedException) { }
        }

        /// <summary>
        /// 一次热等待的登记：这个 requestId 的终态该往哪个槽回，以及账本要记的那几个字段。
        /// <para>
        /// 用 requestId 而不是 frameId 做键：帧号属于传输层，而"谁在等这条命令"属于协议层。
        /// 两边对不上时宁可当作没人等，也绝不把 A 的结果回给 B。
        /// </para>
        /// </summary>
        private sealed class HotWaiter
        {
        /// <summary>热通道上一个正在等响应的请求；槽可能被回收转手，所以连同归属一并记下来。</summary>
            internal HotWaiter(int slot, int ownerPid, int generation, string issuedBy, string channel, string action, long createdUtcTicks)
            {
                Slot = slot;
                OwnerPid = ownerPid;
                Generation = generation;
                IssuedBy = issuedBy;
                Channel = channel;
                Action = action;
                CreatedUtcTicks = createdUtcTicks;
            }

            internal int Slot { get; }

            /// <summary>
            /// 登记时这个槽的主人进程号。回铃前要拿它和槽的当前主人比对：等结果的时间里槽可能
            /// 已经被回收、转手给了另一个客户端，此时把帧写进去就是写进了别人的环。
            /// </summary>
            internal int OwnerPid { get; }

            /// <summary>登记时这个槽的代数；<c>0</c> 表示当时没读到归属、回铃时不校验代数。</summary>
            internal int Generation { get; }

            internal string IssuedBy { get; }
            internal string Channel { get; }
            internal string Action { get; }

            /// <summary>登记时刻（UTC Ticks）。两个用途：账本里的耗时，以及清扫等太久的登记。</summary>
            internal long CreatedUtcTicks { get; }
        }

        /// <summary>
        /// 对端 hot 描述的缓存项。<see cref="Descriptor"/> 为 null 表示"确认过它没有热通道"，
        /// 这条否定结论同样值得缓存一会儿，否则每次调用都要多读一个文件。
        /// </summary>
        private sealed class HotLookup
        {
        /// <summary>热通道描述的缓存项：<paramref name="descriptor"/> 为 null 表示"确认这里没有热通道"，在 <paramref name="rereadAfterUtc"/> 之前不重读。</summary>
            internal HotLookup(CourierHotDescriptor? descriptor, DateTime rereadAfterUtc)
            {
                Descriptor = descriptor;
                RereadAfterUtc = rereadAfterUtc;
            }

            internal CourierHotDescriptor? Descriptor { get; }

            /// <summary>超过这个时刻就重读 endpoint.json：对端可能已经换代，也可能刚打开了热通道。</summary>
            internal DateTime RereadAfterUtc { get; }
        }
    }
}
