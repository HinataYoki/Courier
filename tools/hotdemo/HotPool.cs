using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace HotDemo
{
    /// <summary>
    /// 会话槽池：**一个映射、固定槽位、CAS 领取**。
    ///
    /// 为什么不用「每个客户端建一个环文件」：那条路每建立一次连接，
    /// 客户端要建文件 + mmap，宿主还要枚举目录 + 打开文件 + mmap + 开进程句柄。
    /// 实测宿主侧 accept→opened 要 5–19 ms，对「一次 CLI 调用只发一条命令」是不可接受的开销。
    ///
    /// 槽池把连接成本降到「客户端一次 mmap + 一次 CAS」：
    ///   - 宿主启动时建一次池，之后**永不参与连接建立**；
    ///   - 客户端映射同一个文件，用 Interlocked.CompareExchange 抢一个 FREE 槽；
    ///   - 门铃名由 (root 摘要, 槽号, 方向) 确定性推导，两端不需要交换任何信息。
    ///
    /// 不变量：
    /// 1. 数据面只走 mmap；文件只是命名与容量。
    /// 2. 提交只在 seq 一条字上（先写 payload/len/crc，屏障，再发布 seq）。
    /// 3. 环满返回 false，绝不阻塞、绝不覆盖；调用方必须回退文件通道。
    /// 4. 宿主存活由**进程句柄**判定（内核置 signaled），不用超时、不用心跳。
    /// 5. 槽的 ownerPid 死了，任何人可回收该槽（否则崩溃会永久漏槽）。
    /// </summary>
    internal sealed unsafe class HotPool : IDisposable
    {
        public const uint Magic = 0x34525543; // "CUR4"
        public const int Version = 1;

        const int HeaderBytes = 256;
        const int DescBytes = 128;
        const int RingHeaderBytes = 64;
        const int SlotBytes = 256;
        const int FrameHeader = 24;              // seq u64 | msgId u32 | kind u16 | flags u16 | len u32 | crc u32
        public const int MaxBodyBytes = SlotBytes - FrameHeader;

        // 头部
        const int OffMagic = 0;
        const int OffVersion = 4;
        const int OffSlotCount = 8;
        const int OffCapacity = 12;
        const int OffHostPid = 16;
        const int OffRingBytes = 24;

        // 槽描述符（描述符基址 = HeaderBytes + slot * DescBytes）
        const int OffState = 0;        // 0 FREE / 1 CLAIMED
        const int OffGeneration = 4;
        const int OffOwnerPid = 8;
        const int OffA2BHead = 16;
        const int OffA2BTail = 24;
        const int OffB2AHead = 32;
        const int OffB2ATail = 40;

        readonly MemoryMappedFile mFile;
        readonly MemoryMappedViewAccessor mView;
        readonly byte* mBase;
        readonly int mSlotCount;
        readonly int mCapacity;
        readonly int mRingBytes;
        readonly string mDoorPrefix;
        readonly EventWaitHandle[]? mHostBells;    // host 专用：A2B（收请求），索引即槽号
        readonly EventWaitHandle[]? mClientBells;  // host 专用：B2A（发响应）
        EventWaitHandle? mSendBell;                // client 专用：A2B（发请求），跨消息复用
        int mSendBellSlot = -1;
        readonly long mHostPid;
        readonly WaitHandle? mDeath;
        WaitHandle[]? mWaitSet;
        bool mDisposed;

        public int SlotCount => mSlotCount;
        public int Capacity => mCapacity;
        public long HostPid => mHostPid;

        /// <summary>宿主等待用：每个槽一条 a2b 门铃。数组索引就是槽号。</summary>
        public WaitHandle[]? HostBells => mHostBells;

        /// <summary>客户端用：宿主死亡句柄。null 表示打开时宿主已不在。</summary>
        public WaitHandle? HostDeath => mDeath;

        HotPool(MemoryMappedFile file, MemoryMappedViewAccessor view, byte* b, int slotCount, int capacity,
                string doorPrefix, EventWaitHandle[]? hostBells, EventWaitHandle[]? clientBells,
                long hostPid, WaitHandle? death)
        {
            mFile = file; mView = view; mBase = b;
            mSlotCount = slotCount; mCapacity = capacity;
            mRingBytes = RingHeaderBytes + SlotBytes * capacity;
            mDoorPrefix = doorPrefix;
            mHostBells = hostBells; mClientBells = clientBells;
            mHostPid = hostPid; mDeath = death;
        }

        static int TotalBytes(int slotCount, int capacity) => HeaderBytes + DescBytes * slotCount + (RingHeaderBytes + SlotBytes * capacity) * slotCount;
        byte* Desc(int slot) => mBase + HeaderBytes + slot * DescBytes;
        byte* Ring(int slot) => mBase + HeaderBytes + DescBytes * mSlotCount + slot * mRingBytes;

        // ------------------------------------------------------------------
        // 宿主：建池。命名节随最后一个句柄消失而消失，天然没有残留问题。
        // ------------------------------------------------------------------
        public static HotPool CreateHost(string name, int slotCount, int capacity, string doorPrefix)
        {
            int size = TotalBytes(slotCount, capacity);
            // 用页文件支撑的命名节，而不是文件支撑的映射 —— 这是本设计里最反直觉的一条。
            // 文件载体看着更"可靠"（有名字、有路径、能枚举），但只要往映射里写过，
            // 下次 open 就要等脏页写回：实测 open 4131us + close 1145us；
            // 命名节 open 12us + close 45us。文件载体把整个内存通道的收益吃掉 99%。
            MemoryMappedFile file;
            try { file = MemoryMappedFile.CreateNew(name, size); }
            catch (IOException ex)
            {
                // 命名节随最后一个句柄消失，撞名只可能是上一个宿主还活着（合法冲突，不是残留）
                throw new InvalidOperationException("another host already owns the pool section '" + name + "'", ex);
            }
            var view = file.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
            byte* b = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref b);
            b += view.PointerOffset;
            new Span<byte>(b, size).Clear();

            At.WriteU32(b + OffVersion, Version);
            At.WriteU32(b + OffSlotCount, (uint)slotCount);
            At.WriteU32(b + OffCapacity, (uint)capacity);
            At.WriteI64(b + OffHostPid, Environment.ProcessId);
            At.WriteU32(b + OffRingBytes, (uint)(RingHeaderBytes + SlotBytes * capacity));
            Thread.MemoryBarrier();
            At.WriteU32(b + OffMagic, Magic);   // magic 最后发布：对端看到 magic 就代表头部完整

            // 两个方向各一组门铃，全部由宿主创建并持有：
            //   DooorA2B(client→host)：宿主 WaitAny 用
            //   DoorB2A(host→client)：宿主 Set 用；客户端 OpenExisting 打开
            // 早前版本两个方向都用了 A2B，症状是客户端收不到响应、只能靠自旋撞运气，
            // 一旦自旋没赶上就 2 秒超时并永久降级。
            var inBells = new EventWaitHandle[slotCount];
            var outBells = new EventWaitHandle[slotCount];
            for (int i = 0; i < slotCount; i++)
            {
                inBells[i] = new EventWaitHandle(false, EventResetMode.AutoReset, DoorA2B(doorPrefix, i));
                outBells[i] = new EventWaitHandle(false, EventResetMode.AutoReset, DoorB2A(doorPrefix, i));
            }
            return new HotPool(file, view, b, slotCount, capacity, doorPrefix, inBells, outBells,
                               Environment.ProcessId, null);
        }

        // ------------------------------------------------------------------
        // 客户端：打开池，不等建环、不枚举目录
        // ------------------------------------------------------------------
        public static HotPool OpenClient(string name, long hostPid, string doorPrefix, int timeoutMs = 5000)
        {
            var sw = Stopwatch.StartNew();
            bool diag = Environment.GetEnvironmentVariable("HOTDEMO_DIAG") == "1";
            long lapAt = sw.ElapsedTicks;
            long freq = Stopwatch.Frequency;
            void Lap(string what)
            {
                if (!diag) return;
                long now = sw.ElapsedTicks;
                Console.Error.WriteLine($"[diag-open] {what}={(now - lapAt) * 1e6 / freq:F0}us (cum={now * 1e6 / freq:F0}us)");
                lapAt = now;
            }
            Lap("entry");
            MemoryMappedFile file;
            while (true)
            {
                try
                {
                    file = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
                    Lap("open");
                    break;
                }
                catch (FileNotFoundException)
                {
                    // 宿主可能刚写完 endpoint.json 还没建节，或已经退出
                    if (sw.ElapsedMilliseconds > timeoutMs) throw;
                    Thread.Sleep(1);
                }
            }
            // 命名节不给长度就是整段映射：OpenExisting 拿不到那次的容量参数，
            // 而头部里本来就写了 slotCount / capacity，映射整段后再读即可。
            var view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            byte* b = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref b);
            b += view.PointerOffset;
            Lap("view");
            while (At.ReadU32(b + OffMagic) != Magic)
            {
                if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("pool header magic never appeared: " + name);
                Thread.SpinWait(50);
            }
            int slotCount = (int)At.ReadU32(b + OffSlotCount);
            int capacity = (int)At.ReadU32(b + OffCapacity);
            Lap("header");
            var death = OpenDeath(hostPid);
            Lap("procHandle");
            return new HotPool(file, view, b, slotCount, capacity, doorPrefix, null, null, hostPid, death);
        }

        static string DoorA2B(string prefix, int slot) => prefix + "-s" + slot + "-a2b";
        static string DoorB2A(string prefix, int slot) => prefix + "-s" + slot + "-b2a";

        const uint SYNCHRONIZE = 0x00100000;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        /// <summary>
        /// 拿一个「对端死亡」的可等待对象。
        /// 用裸 OpenProcess 而不是 Process.GetProcessById：后者在冷进程里要 1.2ms 左右
        /// （它内部会去查进程信息、建 Process 对象），OpenProcess 只要十几 µs。
        /// 这个句柄是整条路径上唯一能在阻塞等待里同时监听「数据到了」和「对端死了」的东西，
        /// 没有它就只能靠超时猜死活，崩溃检测延迟会被绑到超时预算上。
        /// </summary>
        static WaitHandle? OpenDeath(long pid)
        {
            if (pid <= 0) return null;
            IntPtr h = OpenProcess(SYNCHRONIZE, false, (int)pid);
            if (h == IntPtr.Zero) return null;      // 打不开 = pid 已不存在 = 已经死了
            return new ProcessExitHandle(h);
        }

        // ------------------------------------------------------------------
        // 领取 / 释放
        // ------------------------------------------------------------------

        /// <summary>抢一个空槽。返回 -1 表示池满（调用方回退文件通道，不要重试）。</summary>
        public int Claim(int timeoutMs = 0)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                for (int i = 0; i < mSlotCount; i++)
                {
                    byte* d = Desc(i);
                    if (At.ReadI32(d + OffState) != 0) continue;
                    // CAS 是唯一的抢占方式：两个客户端同时抢同一槽，只有一个成功
                    if (Interlocked.CompareExchange(ref *(int*)(d + OffState), 1, 0) != 0) continue;
                    // 抢到之后才写 owner、清数据面，避免别人看到半个占用
                    At.WriteI32(d + OffGeneration, At.ReadI32(d + OffGeneration) + 1);
                    At.WriteI64(d + OffB2AHead, At.ReadI64(d + OffB2AHead));   // 读一次，确保没有被编译器折叠
                    byte* r = Ring(i);
                    At.WriteI64(r + 0, 0);   // a2bHead
                    At.WriteI64(r + 8, 0);   // a2bTail
                    At.WriteI64(r + 16, 0);  // b2aHead
                    At.WriteI64(r + 24, 0);  // b2aTail
                    At.WriteI64(d + OffB2ATail, 0);
                    Thread.MemoryBarrier();
                    At.WriteI64(d + OffOwnerPid, Environment.ProcessId);       // owner 最后写：看到 owner 即代表槽已清干净
                    return i;
                }
                // 池满：先回收 owner 已死的槽，再试一次。绝不无限等。
                SweepDeadOwners();
                if (sw.ElapsedMilliseconds >= timeoutMs) return -1;
                Thread.Sleep(1);
            }
        }

        /// <summary>回收 ownerPid 已经不在的槽。这是「客户端崩溃不漏槽」的唯一保证。</summary>
        public int SweepDeadOwners()
        {
            int freed = 0;
            for (int i = 0; i < mSlotCount; i++)
            {
                byte* d = Desc(i);
                if (At.ReadI32(d + OffState) != 1) continue;
                long owner = At.ReadI64(d + OffOwnerPid);
                if (owner == Environment.ProcessId) continue;   // 自己的槽不回收（同进程多会话）
                if (IsProcessAlive(owner)) continue;
                At.WriteI64(d + OffOwnerPid, 0);
                Thread.MemoryBarrier();
                At.WriteI32(d + OffState, 0);
                freed++;
            }
            return freed;
        }

        static bool IsProcessAlive(long pid)
        {
            if (pid <= 0) return false;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                return !p.HasExited;
            }
            catch (ArgumentException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }

        public void Release(int slot)
        {
            byte* d = Desc(slot);
            At.WriteI64(d + OffOwnerPid, 0);
            Thread.MemoryBarrier();
            At.WriteI32(d + OffState, 0);
        }

        public bool IsClaimed(int slot) => At.ReadI32(Desc(slot) + OffState) == 1;
        public long OwnerOf(int slot) => At.ReadI64(Desc(slot) + OffOwnerPid);
        public int GenerationOf(int slot) => At.ReadI32(Desc(slot) + OffGeneration);

        /// <summary>客户端打开自己槽的接收门铃（宿主建的，用 OpenExisting 打开）。</summary>
        public WaitHandle ClientBell(int slot)
            => EventWaitHandle.OpenExisting(DoorB2A(mDoorPrefix, slot));

        // ------------------------------------------------------------------
        // 数据面。clientToHost = true 表示走 a2b 环。
        // ------------------------------------------------------------------

        public bool Send(int slot, bool clientToHost, int msgId, ushort kind, ReadOnlySpan<byte> body)
        {
            if (body.Length > MaxBodyBytes) throw new ArgumentOutOfRangeException(nameof(body), "body too large");
            byte* r = Ring(slot);
            int offHead = clientToHost ? 0 : 16;
            int offTail = clientToHost ? 8 : 24;
            long head = At.ReadI64(r + offHead);
            long tail = At.ReadI64(r + offTail);
            if (head - tail >= mCapacity) return false;          // 满：绝不阻塞、绝不覆盖

            byte* p = r + RingHeaderBytes + (int)(head % mCapacity) * SlotBytes;
            At.WriteI32(p + 8, msgId);
            *(ushort*)(p + 12) = kind;
            *(ushort*)(p + 14) = 0;
            At.WriteI32(p + 16, body.Length);
            At.WriteU32(p + 20, Crc32.Compute(body));
            body.CopyTo(new Span<byte>(p + FrameHeader, MaxBodyBytes));
            Thread.MemoryBarrier();
            At.WriteI64(p, head + 1);                            // 唯一的提交点
            At.WriteI64(r + offHead, head + 1);

            // 门铃句柄必须缓存：每条消息都 new 一个命名事件是几十微秒级的固定开销，
            // 比通道本身的 5µs 还贵。
            if (clientToHost)
            {
                if (mSendBell == null || mSendBellSlot != slot)
                {
                    mSendBell?.Dispose();
                    mSendBell = new EventWaitHandle(false, EventResetMode.AutoReset, DoorA2B(mDoorPrefix, slot));
                    mSendBellSlot = slot;
                }
                mSendBell.Set();
            }
            else
            {
                var bell = mClientBells != null ? mClientBells[slot] : null;
                if (bell != null) bell.Set();
            }
            return true;
        }

        public bool Receive(int slot, bool clientToHost, out int msgId, out ushort kind, Span<byte> body, out int bodyLen)
        {
            byte* r = Ring(slot);
            int offHead = clientToHost ? 0 : 16;
            int offTail = clientToHost ? 8 : 24;
            long head = At.ReadI64(r + offHead);
            long tail = At.ReadI64(r + offTail);
            if (tail > head) { At.WriteI64(r + offTail, head); tail = head; }   // 槽被新一轮会话重置过
            if (tail >= head) { msgId = 0; kind = 0; bodyLen = 0; return false; }

            byte* p = r + RingHeaderBytes + (int)(tail % mCapacity) * SlotBytes;
            if (At.ReadI64(p) != tail + 1) { msgId = 0; kind = 0; bodyLen = 0; return false; }  // 尚未提交
            int len = At.ReadI32(p + 16);
            if (len < 0 || len > MaxBodyBytes) throw new InvalidDataException("bad frame length " + len);
            var src = new ReadOnlySpan<byte>(p + FrameHeader, len);
            if (Crc32.Compute(src) != At.ReadU32(p + 20)) throw new InvalidDataException("frame crc mismatch at " + tail);
            src.CopyTo(body);
            bodyLen = len;
            msgId = At.ReadI32(p + 8);
            kind = *(ushort*)(p + 12);
            At.WriteI64(p, 0);
            At.WriteI64(r + offTail, tail + 1);
            return true;
        }

        /// <summary>客户端等响应：一次内核等待同时盯「门铃」与「宿主存活」。</summary>
        public bool WaitResponse(int slot, WaitHandle bell, out int msgId, out ushort kind, Span<byte> body,
                                 out int bodyLen, out bool hostDead, int spinRounds = 2000, int timeoutMs = 2000)
        {
            hostDead = false;
            for (int i = 0; i < spinRounds; i++)
            {
                if (Receive(slot, false, out msgId, out kind, body, out bodyLen)) return true;
                Thread.SpinWait(20);
            }
            WaitHandle? death = mDeath;
            if (death == null) { hostDead = true; msgId = 0; kind = 0; bodyLen = 0; return false; }

            var sw = Stopwatch.StartNew();
            var set = mWaitSet ??= new[] { bell, death };   // 每消息分配数组会直接进 p99
            while (true)
            {
                int idx = WaitHandle.WaitAny(set, Math.Max(1, timeoutMs - (int)sw.ElapsedMilliseconds));
                if (idx == 1) { hostDead = true; msgId = 0; kind = 0; bodyLen = 0; return false; }
                if (idx == WaitHandle.WaitTimeout) { msgId = 0; kind = 0; bodyLen = 0; return false; }
                if (Receive(slot, false, out msgId, out kind, body, out bodyLen)) return true;
                // 空唤醒（上一条留下的信号）：继续等，不能当成结论
                if (sw.ElapsedMilliseconds >= timeoutMs) { msgId = 0; kind = 0; bodyLen = 0; return false; }
            }
        }

        /// <summary>
        /// 非阻塞存活查询。拿到句柄却打不开进程就是已死——保守方向必须朝「死」，
        /// 反过来（拿不到句柄当成活）会让崩溃后的每条请求都白等满超时。
        /// </summary>
        public bool HostAlive() => mDeath != null && mDeath.WaitOne(0) == false;

        sealed class ProcessExitHandle : WaitHandle
        {
            public ProcessExitHandle(IntPtr native)
            {
                SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(native, false);
            }
        }

        public void Dispose()
        {
            if (mDisposed) return;    // 幂等：二次 ReleasePointer 抛 ObjectDisposedException
            mDisposed = true;
            if (mHostBells != null) foreach (var b in mHostBells) b?.Dispose();
            if (mClientBells != null) foreach (var b in mClientBells) b?.Dispose();
            mSendBell?.Dispose();
            mView.SafeMemoryMappedViewHandle.ReleasePointer();
            mView.Dispose();
            mFile.Dispose();
        }
    }

    internal static unsafe class At
    {
        public static long ReadI64(byte* p) => Volatile.Read(ref *(long*)p);
        public static void WriteI64(byte* p, long v) => Volatile.Write(ref *(long*)p, v);
        public static int ReadI32(byte* p) => Volatile.Read(ref *(int*)p);
        public static void WriteI32(byte* p, int v) => Volatile.Write(ref *(int*)p, v);
        public static uint ReadU32(byte* p) => Volatile.Read(ref *(uint*)p);
        public static void WriteU32(byte* p, uint v) => Volatile.Write(ref *(uint*)p, v);
    }

    internal static class Crc32
    {
        static readonly uint[] Table = Build();
        static uint[] Build()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }
        public static uint Compute(ReadOnlySpan<byte> d)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = 0; i < d.Length; i++) c = Table[(c ^ d[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }
}
