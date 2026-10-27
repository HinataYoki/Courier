using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

#pragma warning disable CA1416

namespace Courier
{
    /// <summary>
    /// 池的建立与打开：命名共享内存、头部发布、门铃与抢槽互斥体的句柄获取。
    /// <para>
    /// 这一半只做"把池拿到手"，不碰数据面。两条入口都不抛异常：建不出来或打不开就返回
    /// <c>null</c> 并留一条诊断，调用方据此关掉热通道、退回文件协议（docs/HOT-CHANNEL.md §5）。
    /// </para>
    /// </summary>
    internal sealed partial class CourierHotPool
    {
        /// <summary>打开池时轮询头部 magic 的间隔；宿主写 magic 只差几条指令，1 ms 足够。</summary>
        private const int OPEN_POLL_MS = 1;

        /// <summary>
        /// 滚出私有命名空间里的对象全名。私有命名空间下的对象用"别名\短名"寻址，
        /// 别名里已经带了 root 标签与 sessionId，所以不同代的宿主天然不撞名。
        /// </summary>
        /// <param name="alias">私有命名空间别名。</param>
        /// <param name="shortName">命名空间内的短名。</param>
        /// <returns>对象全名。</returns>
        private static string ObjectName(string alias, string shortName)
        {
            return alias + "\\" + shortName;
        }

        /// <summary>
        /// 建出这一代宿主的池。成功返回可用的池；失败返回 <c>null</c> 并记一条诊断。
        /// </summary>
        /// <param name="alias">私有命名空间别名（对象全名的前缀）。</param>
        /// <param name="rootDirectory">协议根目录（只用来推导门铃前缀，别名里已含 root 标签）。</param>
        /// <param name="endpointId">端点标识。</param>
        /// <param name="hostPid">宿主进程号，写进头部备诊断。</param>
        /// <returns>新建的池；任何一步失败都为 <c>null</c>。</returns>
        /// <remarks>
        /// 槽位数与容量取常量（<see cref="CourierHot.SLOT_COUNT"/> / <see cref="CourierHot.CAPACITY"/>），
        /// 不对外暴露成参数：两个进程必须对同一块池用同一个几何，参数化只会让"对端几何不同"变成新故障源。
        /// </remarks>
        internal static CourierHotPool? Create(string alias, string rootDirectory, string endpointId, int hostPid)
        {
            int slotCount = CourierHot.SLOT_COUNT;
            int capacity = CourierHot.CAPACITY;
            string doorPrefix = CourierHot.DoorPrefix(rootDirectory, endpointId, alias);

            MemoryMappedFile? file = null;
            MemoryMappedViewAccessor? view = null;
            EventWaitHandle? accept = null;
            Mutex? claimLock = null;
            EventWaitHandle[]? hostBells = null;
            EventWaitHandle[]? clientBells = null;
            try
            {
                long totalBytes = CourierHot.TotalBytes(slotCount, capacity);
                file = MemoryMappedFile.CreateNew(ObjectName(alias, CourierHot.POOL_OBJECT_NAME), totalBytes);
                view = file.CreateViewAccessor(0, totalBytes, MemoryMappedFileAccess.ReadWrite);
                InitializeHeader(view, slotCount, capacity, hostPid);

                accept = new EventWaitHandle(false, EventResetMode.AutoReset, ObjectName(alias, CourierHot.ACCEPT_OBJECT_NAME));
                claimLock = new Mutex(false, ObjectName(alias, CourierHot.CLAIM_OBJECT_NAME));
                hostBells = new EventWaitHandle[slotCount];
                clientBells = new EventWaitHandle[slotCount];
                for (int slot = 0; slot < slotCount; slot++)
                {
                    hostBells[slot] = new EventWaitHandle(false, EventResetMode.AutoReset, ObjectName(alias, CourierHot.DoorObjectName(slot, true)));
                    clientBells[slot] = new EventWaitHandle(false, EventResetMode.AutoReset, ObjectName(alias, CourierHot.DoorObjectName(slot, false)));
                }

                return new CourierHotPool(file, view, slotCount, capacity, doorPrefix, accept, claimLock, hostBells, clientBells, null, PeerState.Alive);
            }
            catch (Exception caught)
            {
                // 建池失败只说明这台机器上热通道不可用：服务退回文件协议，但必须留痕迹，
                // 否则"热通道为什么没生效"在宿主侧无从判断（docs/HOT-CHANNEL.md §5 的诊断要求）。
                CourierHotFaults.Record("create: " + Describe(caught));
                DisposeAfterFailedCreate(file, view, accept, claimLock, hostBells, clientBells);
                return null;
            }
        }

        /// <summary>
        /// 打开一个已经存在的池（客户端侧）。等不到节、头部不完整、几何对不上都返回 <c>null</c>。
        /// </summary>
        /// <param name="alias">私有命名空间别名。</param>
        /// <param name="rootDirectory">协议根目录（只用来推导门铃前缀）。</param>
        /// <param name="endpointId">端点标识。</param>
        /// <param name="hostPid">宿主进程号，用来打开死亡句柄。</param>
        /// <param name="timeoutMs">总预算；到点就走 <c>null</c>，绝不无限等。</param>
        /// <returns>可用的池；打不开时为 <c>null</c>。</returns>
        /// <remarks>
        /// 这里的轮询（<see cref="Thread.Sleep(int)"/>）是打开路径上唯一会阻塞的地方，最坏
        /// <paramref name="timeoutMs"/> 毫秒。调用方必须保证它不在任何共享锁里被调用。
        /// </remarks>
        internal static CourierHotPool? Open(string alias, string rootDirectory, string endpointId, int hostPid, int timeoutMs)
        {
            string doorPrefix = CourierHot.DoorPrefix(rootDirectory, endpointId, alias);
            MemoryMappedFile? file = OpenSectionWithWait(alias, timeoutMs);
            if (file == null) return null;

            MemoryMappedViewAccessor? view = null;
            EventWaitHandle? accept = null;
            Mutex? claimLock = null;
            WaitHandle? peerDeath = null;
            try
            {
                view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
                int slotCount;
                int capacity;
                if (!TryReadHeader(view, timeoutMs, out slotCount, out capacity)) return null;

                accept = OpenBell(ObjectName(alias, CourierHot.ACCEPT_OBJECT_NAME));
                claimLock = OpenLock(ObjectName(alias, CourierHot.CLAIM_OBJECT_NAME));

                PeerState peerState;
                peerDeath = CourierHot.OpenDeathHandle(hostPid, out peerState);
                return new CourierHotPool(file, view, slotCount, capacity, doorPrefix, accept, claimLock, null, null, peerDeath, peerState);
            }
            catch (Exception caught)
            {
                // 打开失败是常态（对端刚好退出、上一代残留、句柄被撤），静默回落到文件通道，
                // 但留一条诊断，让"热通道为什么没接上"可查。
                CourierHotFaults.Record("open: " + Describe(caught));
                DisposeAfterFailedOpen(view, accept, claimLock, peerDeath);
                file.Dispose();
                return null;
            }
        }

        /// <summary>把池头写满：几何字段先写，magic 最后写（唯一的发布点）。</summary>
        /// <param name="view">池映射。</param>
        /// <param name="slotCount">槽位数。</param>
        /// <param name="capacity">每个方向的环容量。</param>
        /// <param name="hostPid">宿主进程号。</param>
        private static void InitializeHeader(MemoryMappedViewAccessor view, int slotCount, int capacity, int hostPid)
        {
            view.Write(CourierHot.OFF_VERSION, VERSION);
            view.Write(CourierHot.OFF_SLOT_COUNT, slotCount);
            view.Write(CourierHot.OFF_CAPACITY, capacity);
            view.Write(CourierHot.OFF_HOST_PID, hostPid);
            view.Write(CourierHot.OFF_RING_BYTES, (int)CourierHot.RingBytesFor(capacity));
            view.Write(CourierHot.OFF_CREATED_UTC, DateTime.UtcNow.Ticks);
            for (int slot = 0; slot < slotCount; slot++) WriteSlotState(view, slot, STATE_FREE);
            Thread.MemoryBarrier();
            view.Write(CourierHot.OFF_MAGIC, MAGIC);
            Thread.MemoryBarrier();
        }

        /// <summary>打开已存在的节；节还没出现时就按 1 ms 轮询到预算用完。</summary>
        /// <param name="alias">私有命名空间别名。</param>
        /// <param name="timeoutMs">预算。</param>
        /// <returns>映射文件；预算用完仍打不开时为 <c>null</c>。</returns>
        private static MemoryMappedFile? OpenSectionWithWait(string alias, int timeoutMs)
        {
            string name = ObjectName(alias, CourierHot.POOL_OBJECT_NAME);
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs < 0 ? 0 : timeoutMs);
            while (true)
            {
                try
                {
                    return OpenSection(name);
                }
                catch (Exception caught) when (caught is FileNotFoundException || caught is DirectoryNotFoundException)
                {
                    // 宿主可能刚写完描述还没建节，也可能已经不在了：轮询到预算用完为止。
                    if (DateTime.UtcNow >= deadline) return null;
                    Thread.Sleep(OPEN_POLL_MS);
                }
            }
        }

        /// <summary>
        /// 等头部 magic 出现，然后在屏障保护下读几何并校验。
        /// </summary>
        /// <param name="view">池映射。</param>
        /// <param name="timeoutMs">预算。</param>
        /// <param name="slotCount">校验通过的槽位数。</param>
        /// <param name="capacity">校验通过的环容量。</param>
        /// <returns>头部完整且几何自洽时为 <c>true</c>。</returns>
        /// <remarks>
        /// 读 magic 前后都要屏障：magic 是发布点，但弱内存序平台（ARM64 Windows）上，
        /// 没有 acquire 的读者仍可能先看到 magic 再看到陈旧的几何字段。
        /// </remarks>
        private static bool TryReadHeader(MemoryMappedViewAccessor view, int timeoutMs, out int slotCount, out int capacity)
        {
            slotCount = 0;
            capacity = 0;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs < 0 ? 0 : timeoutMs);
            while (true)
            {
                Thread.MemoryBarrier();
                if (view.ReadInt32(CourierHot.OFF_MAGIC) == MAGIC) break;
                if (DateTime.UtcNow >= deadline) return false;
                Thread.Sleep(OPEN_POLL_MS);
            }

            Thread.MemoryBarrier();
            int version = view.ReadInt32(CourierHot.OFF_VERSION);
            int headerSlots = view.ReadInt32(CourierHot.OFF_SLOT_COUNT);
            int headerCapacity = view.ReadInt32(CourierHot.OFF_CAPACITY);
            long headerRingBytes = view.ReadInt32(CourierHot.OFF_RING_BYTES);
            Thread.MemoryBarrier();
            if (version != VERSION) return false;

            // 头部来自另一个进程：单看它自己永远自洽，唯一的客观事实是我们真映射到了多少字节。
            return CourierHot.TryValidateGeometry(view.Capacity, headerSlots, headerCapacity, headerRingBytes, out slotCount, out capacity);
        }

        /// <summary>建池失败后的收尾：把所有已经拿到手的资源放掉，异常不外传。</summary>
        /// <param name="file">映射文件。</param>
        /// <param name="view">映射视图。</param>
        /// <param name="accept">接入门铃。</param>
        /// <param name="claimLock">抢槽互斥体。</param>
        /// <param name="hostBells">宿主侧门铃组。</param>
        /// <param name="clientBells">客户端侧门铃组。</param>
        private static void DisposeAfterFailedCreate(
            MemoryMappedFile? file,
            MemoryMappedViewAccessor? view,
            EventWaitHandle? accept,
            Mutex? claimLock,
            EventWaitHandle[]? hostBells,
            EventWaitHandle[]? clientBells)
        {
            DisposeBells(hostBells);
            DisposeBells(clientBells);
            DisposeHandle(accept);
            DisposeMutex(claimLock);
            if (view != null) view.Dispose();
            if (file != null) file.Dispose();
        }

        /// <summary>打开失败后的收尾；与建池失败分开是因为这里不持有门铃组。</summary>
        /// <param name="view">映射视图。</param>
        /// <param name="accept">接入门铃。</param>
        /// <param name="claimLock">抢槽互斥体。</param>
        /// <param name="peerDeath">对端死亡句柄。</param>
        private static void DisposeAfterFailedOpen(
            MemoryMappedViewAccessor? view,
            EventWaitHandle? accept,
            Mutex? claimLock,
            WaitHandle? peerDeath)
        {
            DisposeHandle(accept);
            DisposeMutex(claimLock);
            DisposeHandle(peerDeath);
            if (view != null) view.Dispose();
        }

        /// <summary>放掉一个可等待句柄；<c>null</c> 直接跳过。</summary>
        /// <param name="handle">句柄。</param>
        private static void DisposeHandle(WaitHandle? handle)
        {
            if (handle == null) return;
            try { handle.Dispose(); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>放掉抢槽互斥体；被别的线程持有时释放句柄是安全的，异常只做兜底。</summary>
        /// <param name="mutex">互斥体。</param>
        private static void DisposeMutex(Mutex? mutex)
        {
            if (mutex == null) return;
            try { mutex.Dispose(); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>把异常压成一行短文本，供诊断使用；不抛异常。</summary>
        /// <param name="caught">捕获到的异常。</param>
        /// <returns>形如 <c>IOException: 消息</c> 的短文本。</returns>
        private static string Describe(Exception caught)
        {
            string message = caught.Message;
            return message.Length == 0 ? caught.GetType().Name : caught.GetType().Name + ": " + message;
        }
    }
}
