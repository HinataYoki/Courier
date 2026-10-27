using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Threading;

// 命名节、命名事件、命名互斥体都只存在于 Windows。本文件对平台的判断集中在 CourierHot.IsSupported
// 一处（非 Windows 直接返回 null，调用方回退纯文件协议），但平台兼容分析器只认它自己的标注方式，
// 看不到这个判断，所以在文件级关掉 CA1416 —— 换来的代价是本文件里新增的 Windows-only API 调用
// 需要人工确认，代价可控；换来的收益是不会把真错误淹没在一片重复告警里。
#pragma warning disable CA1416

namespace Courier
{
    /// <summary>
    /// 热通道的共享内存池：一整块命名共享内存，切成 <c>slot</c> 个互不相干的槽，
    /// 每个槽是一条双向环（客户端 → 宿主、宿主 → 客户端各一条）。
    /// <para>
    /// 这个类型只搬字节，不搬承诺：一帧消息即使成功送达，也不会让任何文件发生变化。
    /// 谁在什么时机落盘，由 <see cref="CourierNode"/> 决定 —— 那是协议层的职责，不是通道层的。
    /// </para>
    /// <para>
    /// 全部访问都走 <see cref="MemoryMappedViewAccessor"/> 的读写方法，不使用 unsafe 指针：
    /// 产品源码要能作为一组文件被 netstandard2.1 的宿主工程直接编译（tools/compat 就在验这件事），
    /// 引入 <c>AllowUnsafeBlocks</c> 会把这条约定打破。
    /// </para>
    /// <para>
    /// 原子性靠"单写者"而不是靠比较交换：每个方向的头部指针只由该方向的写者推进，
    /// 提交点是 <c>seq</c> 这一个 64 位字，两侧各加一次 <see cref="Thread.MemoryBarrier"/>。
    /// 单写者只在"每个进程只有一个线程写这个方向的同一个槽"时成立，所以本类型为每个槽配一把
    /// 进程内的写锁（<see cref="mSendLocks"/>）：宿主侧泵线程与服务线程都会往 b2a 方向写，
    /// 少了这把锁就是两个写者交替推进同一个 head。
    /// </para>
    /// <para>
    /// 本类型按职责拆成四个 partial 文件：核心状态与句柄（本文件）、建池与开池
    /// （<c>CourierHotPool.Lifecycle.cs</c>）、环读写（<c>CourierHotPool.Ring.cs</c>）、
    /// 抢槽与回收（<c>CourierHotPool.Claim.cs</c>）。
    /// </para>
    /// </summary>
    internal sealed partial class CourierHotPool : IDisposable
    {
        /// <summary>魔数（"CUR5"）；头部最后写它，它是"这块池可用"的发布点。</summary>
        private const int MAGIC = 0x35525543;

        /// <summary>池布局版本；与 <see cref="CourierHot.FORMAT_VERSION"/> 相互独立。</summary>
        private const int VERSION = 2;

        /// <summary>槽空闲。</summary>
        private const int STATE_FREE = 0;

        /// <summary>槽已被某进程占据。</summary>
        private const int STATE_CLAIMED = 1;

        /// <summary>兼容标记：历史上用过的"正在清扫"中间态。现在清扫在抢槽锁里做，不再需要它。</summary>
        private const int STATE_SWEEPING = 2;

        private readonly MemoryMappedFile mFile;
        private readonly MemoryMappedViewAccessor mView;
        private readonly int mSlotCount;
        private readonly int mCapacity;
        private readonly int mRingBytes;
        private readonly string mDoorPrefix;
        private readonly EventWaitHandle mAccept;
        private readonly Mutex mClaimLock;
        private readonly EventWaitHandle[]? mHostBells;
        private readonly EventWaitHandle[]? mClientBells;
        private readonly WaitHandle? mPeerDeath;
        private readonly PeerState mPeerState;

        /// <summary>每槽一把进程内写锁：保证同一实例对同一槽的写入串行（见类型注释里的单写者前提）。</summary>
        private readonly object[] mSendLocks;

        private WaitHandle[]? mWaitSet;
        private int mWaitSetSlot = -1;
        private EventWaitHandle? mSendBell;
        private int mSendBellSlot = -1;

        // 客户端侧的接收门铃同样按槽缓存：命名事件的创建是几十微秒级固定开销，
        // 每条消息都重开一次会把热通道的意义抹掉。
        private EventWaitHandle? mRecvBell;
        private int mRecvBellSlot = -1;
        private bool mDisposed;

        /// <summary>由 <c>Create</c>/<c>Open</c> 在映射、门铃与互斥量都拿到之后调用；参数全部是已经校验过的既有句柄。</summary>
        private CourierHotPool(
            MemoryMappedFile file,
            MemoryMappedViewAccessor view,
            int slotCount,
            int capacity,
            string doorPrefix,
            EventWaitHandle accept,
            Mutex claimLock,
            EventWaitHandle[]? hostBells,
            EventWaitHandle[]? clientBells,
            WaitHandle? peerDeath,
            PeerState peerState)
        {
            mFile = file;
            mView = view;
            mSlotCount = slotCount;
            mCapacity = capacity;
            mRingBytes = (int)CourierHot.RingBytesFor(capacity);
            mDoorPrefix = doorPrefix;
            mAccept = accept;
            mClaimLock = claimLock;
            mHostBells = hostBells;
            mClientBells = clientBells;
            mPeerDeath = peerDeath;
            mPeerState = peerState;
            mSendLocks = new object[slotCount];
            for (int slot = 0; slot < slotCount; slot++) mSendLocks[slot] = new object();
        }

        /// <summary>槽位数。</summary>
        internal int SlotCount
        {
            get { return mSlotCount; }
        }

        /// <summary>是否由本进程创建（即本进程是宿主）。</summary>
        internal bool IsHost
        {
            get { return mHostBells != null; }
        }

        /// <summary>宿主侧：每个槽一个"有新请求"门铃，顺序与槽号一致；<c>null</c> 表示本进程是客户端。</summary>
        internal EventWaitHandle[]? HostBells
        {
            get { return mHostBells; }
        }

        /// <summary>宿主侧：客户端接入时的"有新连接"门铃。</summary>
        internal EventWaitHandle AcceptBell
        {
            get { return mAccept; }
        }

        /// <summary>对端进程的死亡句柄；<c>null</c> 表示拿不到（见 <see cref="PeerState"/>）。</summary>
        internal WaitHandle? PeerDeath
        {
            get { return mPeerDeath; }
        }

        /// <summary>
        /// 对端是否还活着。
        /// <para>
        /// 拿得到死亡句柄时以它为唯一依据。拿不到时**不判死**：跨完整性级别（宿主提权、调用方未提权）
        /// 会让 <c>OpenProcess</c> 返回"拒绝访问"，那说明进程在、只是打不开句柄；把它当成死亡会让
        /// 每一次调用都丢弃并重建热连接，热通道从此只剩建连成本。
        /// </para>
        /// </summary>
        /// <returns>可以认为对端仍在时为 <c>true</c>。</returns>
        internal bool PeerAlive()
        {
            if (mDisposed) return false;
            WaitHandle? death = mPeerDeath;
            if (death != null)
            {
                try
                {
                    return !death.WaitOne(0);
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }

            return mPeerState != PeerState.Dead;
        }

        /// <summary>槽号是否落在这块池的实际范围内。</summary>
        /// <param name="slot">槽号。</param>
        /// <returns>合法时为 <c>true</c>。</returns>
        private bool IsSlotValid(int slot)
        {
            return slot >= 0 && slot < mSlotCount;
        }

        /// <summary>打开已存在的共享内存节。</summary>
        /// <param name="name">全名（别名 + 短名）。</param>
        /// <returns>映射文件。</returns>
        private static MemoryMappedFile OpenSection(string name)
        {
            return MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
        }

        /// <summary>打开已存在的命名门铃。</summary>
        /// <param name="name">全名。</param>
        /// <returns>门铃句柄。</returns>
        private static EventWaitHandle OpenBell(string name)
        {
            return EventWaitHandle.OpenExisting(name);
        }

        /// <summary>打开已存在的命名互斥体（抢槽锁）。</summary>
        /// <param name="name">全名。</param>
        /// <returns>互斥体句柄。</returns>
        private static Mutex OpenLock(string name)
        {
            return Mutex.OpenExisting(name);
        }

        /// <summary>把一个槽描述符写成初始状态（建池时用）。</summary>
        /// <param name="view">池映射。</param>
        /// <param name="slot">槽号。</param>
        /// <param name="value">状态值。</param>
        private static void WriteSlotState(MemoryMappedViewAccessor view, int slot, int value)
        {
            long descBase = CourierHot.HEADER_BYTES + ((long)CourierHot.DESC_BYTES * slot);
            view.Write(descBase + CourierHot.OFF_STATE, value);
            view.Write(descBase + CourierHot.OFF_GENERATION, 0);
            view.Write(descBase + CourierHot.OFF_OWNER_PID, 0);
            view.Write(descBase + CourierHot.OFF_A2_BHEAD, 0L);
            view.Write(descBase + CourierHot.OFF_A2_BTAIL, 0L);
            view.Write(descBase + CourierHot.OFF_B2_AHEAD, 0L);
            view.Write(descBase + CourierHot.OFF_B2_ATAIL, 0L);
        }

        /// <summary>释放全部内核句柄；幂等。泵线程必须已经停下，否则它会拿着关掉的句柄继续等。</summary>
        public void Dispose()
        {
            if (mDisposed) return;
            mDisposed = true;
            DisposeBell(ref mSendBell);
            DisposeBell(ref mRecvBell);
            DisposeBells(mHostBells);
            DisposeBells(mClientBells);
            try { mAccept.Dispose(); }
            catch (ObjectDisposedException) { }
            try { mClaimLock.Dispose(); }
            catch (ObjectDisposedException) { }
            if (mPeerDeath != null)
            {
                try { mPeerDeath.Dispose(); }
                catch (ObjectDisposedException) { }
            }

            mView.Dispose();
            mFile.Dispose();
        }

        /// <summary>释放一个按槽缓存的门铃并把字段置空。</summary>
        /// <param name="bell">门铃字段的引用。</param>
        private static void DisposeBell(ref EventWaitHandle? bell)
        {
            EventWaitHandle? cached = bell;
            bell = null;
            if (cached == null) return;
            try { cached.Dispose(); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>释放整组门铃；<c>null</c> 表示本进程不持有这一组。</summary>
        /// <param name="bells">门铃数组。</param>
        private static void DisposeBells(EventWaitHandle[]? bells)
        {
            if (bells == null) return;
            for (int index = 0; index < bells.Length; index++)
            {
                EventWaitHandle? bell = bells[index];
                if (bell == null) continue;
                try { bell.Dispose(); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    /// <summary>
    /// 热通道的诊断出口。热通道的失败被有意设计成"静默回落到文件通道"，但静默不等于不留痕迹：
    /// 这里保留累计计数并在调试输出上留一行，测试也可以断言"没有意外故障"。
    /// </summary>
    internal static class CourierHotFaults
    {
        private static int sCount;

        /// <summary>累计发生的通道级故障数（环指针不可信、帧校验失败、抢槽锁超时等）。</summary>
        internal static int Count
        {
            get { return Volatile.Read(ref sCount); }
        }

        /// <summary>记一条通道级故障。绝不抛异常：诊断本身不能变成新的失败源。</summary>
        /// <param name="reason">故障原因（调用方拼好的短句）。</param>
        internal static void Record(string reason)
        {
            Interlocked.Increment(ref sCount);
            try
            {
                Debug.WriteLine("[courier-hot] " + reason);
            }
            catch (ObjectDisposedException)
            {
                // 调试监听器在进程收尾时可能已经被拆掉：诊断写入失败不影响数据面。
            }
        }
    }
}
