using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

#pragma warning disable CA1416

namespace Courier
{
    /// <summary>
    /// 池的数据面：两个方向的环读写、门铃投递与等待。
    /// <para>
    /// 环的几何：每个槽有一段环区，两个方向各占 <see cref="CourierHot.OneWayRingBytes"/> 字节
    /// （64 字节环头 + <see cref="CourierHot.CAPACITY"/> 个帧槽），互不重叠 —— 两个方向共用一段
    /// 帧数组必然互相覆写，这是本文件最重要的一条布局约束。指针本身住在槽描述符里
    /// （<see cref="CourierHot.OFF_A2_BHEAD"/> 等），环头保留给诊断，不参与提交协议。
    /// </para>
    /// <para>
    /// 提交协议：写方先把帧头与正文写满，一次 <see cref="Thread.MemoryBarrier"/>，再把
    /// <c>seq</c> 写成 <c>head + 1</c>（唯一提交点），屏障后推进 <c>head</c>；读方先屏障再读
    /// <c>head</c>、再屏障、然后比对 <c>seq == tail + 1</c>。任何一步不成立都不得让环停摆：
    /// 读方是唯一有权推进 <c>tail</c> 的一方，所以判槽不可信时必须由读方推进 <c>tail</c> 自愈。
    /// </para>
    /// </summary>
    internal sealed partial class CourierHotPool
    {
        /// <summary>提交字对不上时的重试次数。写方先写 seq 再推进 head，所以重试几次仍不符即为真损坏。</summary>
        private const int SEQUENCE_SPINS = 4;

        /// <summary>重试之间让出的时间（自旋指令数）。读方在等写方落完 seq，不能让出线程。</summary>
        private const int SEQUENCE_SPIN_WAIT = 64;

        /// <summary>
        /// 环形数据面的偏移：帧号与正文在一帧里的位置与 <see cref="CourierHot.OFF_SEQ"/> 同源。
        /// </summary>
        /// <param name="slot">槽号（调用方已校验）。</param>
        /// <returns>该槽环区的起始偏移。</returns>
        private long RingBase(int slot)
        {
            return CourierHot.HEADER_BYTES + ((long)CourierHot.DESC_BYTES * mSlotCount) + ((long)mRingBytes * slot);
        }

        /// <summary>槽描述符的起始偏移。</summary>
        /// <param name="slot">槽号（调用方已校验）。</param>
        /// <returns>描述符偏移。</returns>
        private long DescriptorBase(int slot)
        {
            return CourierHot.HEADER_BYTES + ((long)CourierHot.DESC_BYTES * slot);
        }

        /// <summary>
        /// 算出某一帧的起始偏移。<paramref name="index"/> 只用于取模，负值不会出现在这里
        /// （调用方已拒绝负指针），所以取模结果必然落在环内。
        /// </summary>
        /// <param name="slot">槽号（调用方已校验）。</param>
        /// <param name="clientToHost">方向。</param>
        /// <param name="index">环内序号（head 或 tail）。</param>
        /// <returns>帧的起始偏移。</returns>
        private long FrameBase(int slot, bool clientToHost, long index)
        {
            long oneWay = CourierHot.OneWayRingBytes(mCapacity);
            long start = RingBase(slot) + (clientToHost ? 0L : oneWay) + CourierHot.RING_HEADER_BYTES;
            return start + ((index % mCapacity) * CourierHot.FRAME_BYTES);
        }

        /// <summary>
        /// 往一个槽的某个方向投一帧。环满或指针不可信时返回 <c>false</c>，绝不阻塞、绝不覆盖。
        /// </summary>
        /// <param name="slot">目标槽号。</param>
        /// <param name="clientToHost"><c>true</c> 表示客户端 → 宿主方向。</param>
        /// <param name="frameId">调用方自选的关联号，原样随帧带回。</param>
        /// <param name="kind">帧类型。</param>
        /// <param name="body">正文缓冲。</param>
        /// <param name="length">正文有效字节数。</param>
        /// <returns>帧已提交并置位门铃时为 <c>true</c>；调用方必须据此回落文件通道。</returns>
        /// <remarks>
        /// 同一槽的写入在本进程内被 <see cref="mSendLocks"/> 串行化：宿主侧泵线程与执行线程都会往
        /// b2a 方向写同一个槽，而环的单写者原子性前提要求同一方向只有一个写者。
        /// </remarks>
        internal bool Send(int slot, bool clientToHost, int frameId, ushort kind, byte[] body, int length)
        {
            if (!IsSlotValid(slot))
            {
                // 越界槽号是调用方的编程错误，但热通道绝不能让一次编程错误炸掉宿主泵线程，
                // 所以这里只记诊断并放弃这一帧，绝不抛出 ObjectDisposedException 之类的误导性异常。
                CourierHotFaults.Record("send: slot out of range");
                return false;
            }

            if (body == null || length < 0 || length > CourierHot.BODY_BYTES || length > body.Length) return false;
            if (mDisposed) return false;

            object gate = mSendLocks[slot];
            bool taken = false;
            try
            {
                Monitor.Enter(gate, ref taken);
                return WriteFrame(slot, clientToHost, frameId, kind, body, length);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            finally
            {
                if (taken) Monitor.Exit(gate);
            }
        }

        /// <summary>
        /// 只在这个槽仍归指定主人时才投帧（P2-11：宿主可能拿着一个已经被回收并转手的槽号）。
        /// </summary>
        /// <param name="slot">目标槽号。</param>
        /// <param name="ownerPid">意图投递的对端进程号。</param>
        /// <param name="generation">投递方记录的代数；<c>0</c> 表示不校验代数。</param>
        /// <param name="clientToHost">方向。</param>
        /// <param name="frameId">关联号。</param>
        /// <param name="kind">帧类型。</param>
        /// <param name="body">正文缓冲。</param>
        /// <param name="length">正文有效字节数。</param>
        /// <returns>槽主人一致且帧已提交时为 <c>true</c>。</returns>
        internal bool SendToOwner(int slot, int ownerPid, int generation, bool clientToHost, int frameId, ushort kind, byte[] body, int length)
        {
            int currentPid;
            int currentGeneration;
            if (!TryReadOwnership(slot, out currentPid, out currentGeneration)) return false;
            if (currentPid != ownerPid) return false;
            if (generation != 0 && currentGeneration != generation)
            {
                CourierHotFaults.Record("send: slot " + slot.ToString(System.Globalization.CultureInfo.InvariantCulture) + " changed owner generation, drop response");
                return false;
            }

            return Send(slot, clientToHost, frameId, kind, body, length);
        }

        /// <summary>
        /// 从一个槽的某个方向取一帧。取不到返回 <c>false</c>；槽被判定不可信时推进 <c>tail</c> 自愈。
        /// </summary>
        /// <param name="slot">槽号。</param>
        /// <param name="clientToHost">方向。</param>
        /// <param name="frameId">帧里带的关联号。</param>
        /// <param name="kind">帧类型。</param>
        /// <param name="body">接收缓冲。</param>
        /// <param name="length">正文有效字节数。</param>
        /// <returns>取到一帧合法帧时为 <c>true</c>。</returns>
        internal bool Receive(int slot, bool clientToHost, out int frameId, out ushort kind, byte[] body, out int length)
        {
            frameId = 0;
            kind = 0;
            length = 0;
            if (mDisposed) return false;
            if (!IsSlotValid(slot))
            {
                CourierHotFaults.Record("receive: slot out of range");
                return false;
            }

            long head;
            long tail;
            bool suspicious;
            if (!TryReadRingPointers(slot, clientToHost, out head, out tail, out suspicious)) return false;
            if (suspicious)
            {
                HealRing(slot, clientToHost, head);
                return false;
            }

            if (head == tail) return false;
            long frameBase = FrameBase(slot, clientToHost, tail);
            if (!TryReadFrameAt(slot, clientToHost, head, tail, frameBase, body, out int frameLength)) return false;

            frameId = mView.ReadInt32(frameBase + CourierHot.OFF_FRAME_ID);
            kind = (ushort)mView.ReadInt16(frameBase + CourierHot.OFF_KIND);
            length = frameLength;
            AdvanceTail(slot, clientToHost, tail + 1);
            return true;
        }

        /// <summary>
        /// 在 <paramref name="frameBase"/> 处等写者把这一帧写完并校验它。
        /// 任何一项不成立都调 <see cref="HealRing"/> 推进 <c>tail</c>，否则这个方向会永久卡死。
        /// </summary>
        /// <param name="slot">槽号。</param>
        /// <param name="clientToHost">方向。</param>
        /// <param name="head">写者已推进到的序号。</param>
        /// <param name="tail">读者当前序号。</param>
        /// <param name="frameBase">这一帧在映射里的起始偏移。</param>
        /// <param name="body">接收缓冲，成功时被写入正文。</param>
        /// <param name="frameLength">正文有效字节数。</param>
        /// <returns>帧完整且 CRC 正确时为 true。</returns>
        private bool TryReadFrameAt(int slot, bool clientToHost, long head, long tail, long frameBase, byte[] body, out int frameLength)
        {
            frameLength = 0;
            Thread.MemoryBarrier();
            long sequence = mView.ReadInt64(frameBase + CourierHot.OFF_SEQ);
            for (int spin = 0; spin < SEQUENCE_SPINS && sequence != tail + 1; spin++)
            {
                Thread.MemoryBarrier();
                Thread.SpinWait(SEQUENCE_SPIN_WAIT);
                sequence = mView.ReadInt64(frameBase + CourierHot.OFF_SEQ);
            }

            if (sequence != tail + 1)
            {
                // 写方在"推进 head 之前"就已经把 seq 落好了，重试之后仍对不上只可能是崩溃留下的半写帧：
                // 不推进 tail 的话这个方向会永久卡死，而推进顶多丢一帧本来就不可信的帧。
                HealRing(slot, clientToHost, head);
                return false;
            }

            int length = mView.ReadInt32(frameBase + CourierHot.OFF_LENGTH);
            if (length < 0 || length > CourierHot.BODY_BYTES || length > body.Length)
            {
                HealRing(slot, clientToHost, head);
                return false;
            }

            mView.ReadArray(frameBase + CourierHot.FRAME_HEADER_BYTES, body, 0, length);
            if (Crc32.Compute(body, 0, length) != (uint)mView.ReadInt32(frameBase + CourierHot.OFF_CRC))
            {
                HealRing(slot, clientToHost, head);
                return false;
            }

            frameLength = length;
            return true;
        }

        /// <summary>
        /// 等这个槽的"有响应"门铃，同时也盯对端死亡句柄。
        /// </summary>
        /// <param name="slot">槽号。</param>
        /// <param name="timeoutMs">最长等待毫秒数。</param>
        /// <param name="peerDead">对端死亡句柄被置位时为 <c>true</c>，调用方必须停止等待。</param>
        /// <returns>门铃被置位（可能有帧可读）时为 <c>true</c>。</returns>
        /// <remarks>
        /// 拿不到死亡句柄时只等门铃（P2-6）：把"打不开句柄"当成死亡会让跨完整性级别的每一次调用
        /// 都丢掉热连接，而只等门铃的最坏后果是这一次多等到自己的截止时间，随后文件通道兜得住。
        /// </remarks>
        internal bool WaitForFrame(int slot, int timeoutMs, out bool peerDead)
        {
            peerDead = false;
            if (!IsSlotValid(slot) || mDisposed) return false;
            if (timeoutMs < 0) timeoutMs = 0;

            EventWaitHandle? bell = ReceiveBell(slot);
            WaitHandle? death = mPeerDeath;
            if (bell == null && death == null) return false;

            WaitHandle[] set = BuildWaitSet(slot, bell, death);
            int index;
            try
            {
                index = WaitHandle.WaitAny(set, timeoutMs);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            if (index == WaitHandle.WaitTimeout) return false;

            // 只有死亡句柄可等：被唤醒就说明对端已经退出。
            if (bell == null)
            {
                peerDead = true;
                return false;
            }

            // 两元素集合里第 1 位是死亡句柄；第 0 位永远是门铃。
            if (death != null && set.Length == 2 && index == 1)
            {
                peerDead = true;
                return false;
            }

            return true;
        }

        /// <summary>
        /// 取（必要时重建）本槽的等待集合：门铃在前、死亡句柄在后，缓存起来避免每次等待都分配数组。
        /// </summary>
        /// <param name="slot">槽号。</param>
        /// <param name="bell">接收门铃；<c>null</c> 表示打不开（此时只等死亡句柄）。</param>
        /// <param name="death">对端死亡句柄；<c>null</c> 表示拿不到（此时只等门铃，即 P2-6 的降级路径）。</param>
        /// <returns>长度 1 或 2 的等待集合，元素全部非空。</returns>
        private WaitHandle[] BuildWaitSet(int slot, EventWaitHandle? bell, WaitHandle? death)
        {
            int desired = death == null && bell != null ? 1 : 2;
            if (bell == null && death != null) desired = 1;

            WaitHandle[]? cached = mWaitSet;
            if (cached != null && mWaitSetSlot == slot && cached.Length == desired)
            {
                bool matches = bell == null || ReferenceEquals(cached[0], bell);
                if (desired == 2 && death != null) matches = matches && ReferenceEquals(cached[1], death);
                if (matches) return cached;
            }

            var built = new WaitHandle[desired];
            if (desired == 2)
            {
                built[0] = bell!;
                built[1] = death!;
            }
            else
            {
                built[0] = bell != null ? (WaitHandle)bell : death!;
            }

            mWaitSet = built;
            mWaitSetSlot = slot;
            return built;
        }

        /// <summary>
        /// 读出一个槽两个方向的头尾指针并做单调性校验（P2-9）。指针来自共享内存，可能被崩溃残留写坏。
        /// </summary>
        /// <param name="slot">槽号。</param>
        /// <param name="clientToHost">方向。</param>
        /// <param name="head">写指针。</param>
        /// <param name="tail">读指针。</param>
        /// <param name="suspicious">指针不合法（负值、读超过写、积压超过容量）时为 <c>true</c>。</param>
        /// <returns>槽号合法时为 <c>true</c>；否则 <paramref name="suspicious"/> 无意义。</returns>
        private bool TryReadRingPointers(int slot, bool clientToHost, out long head, out long tail, out bool suspicious)
        {
            head = 0;
            tail = 0;
            suspicious = false;
            if (!IsSlotValid(slot)) return false;

            long descriptor = DescriptorBase(slot);
            Thread.MemoryBarrier();
            head = mView.ReadInt64(descriptor + (clientToHost ? CourierHot.OFF_A2_BHEAD : CourierHot.OFF_B2_AHEAD));
            tail = mView.ReadInt64(descriptor + (clientToHost ? CourierHot.OFF_A2_BTAIL : CourierHot.OFF_B2_ATAIL));
            Thread.MemoryBarrier();
            if (tail < 0 || head < tail || head - tail > mCapacity) suspicious = true;
            return true;
        }

        /// <summary>
        /// 读方自愈：把 <c>tail</c> 拉到 <paramref name="head"/>，丢掉那段不可信的积压，并记一条诊断。
        /// </summary>
        /// <param name="slot">槽号。</param>
        /// <param name="clientToHost">方向。</param>
        /// <param name="head">当前写指针。</param>
        private void HealRing(int slot, bool clientToHost, long head)
        {
            long tail = head < 0 ? 0L : head;
            mView.Write(DescriptorBase(slot) + (clientToHost ? CourierHot.OFF_A2_BTAIL : CourierHot.OFF_B2_ATAIL), tail);
            Thread.MemoryBarrier();
            CourierHotFaults.Record("ring: dropped untrusted frames on slot " + slot.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>推进读指针并加屏障，保证写方立刻看得见腾出来的位置。</summary>
        /// <param name="slot">槽号。</param>
        /// <param name="clientToHost">方向。</param>
        /// <param name="tail">新的读指针。</param>
        private void AdvanceTail(int slot, bool clientToHost, long tail)
        {
            mView.Write(DescriptorBase(slot) + (clientToHost ? CourierHot.OFF_A2_BTAIL : CourierHot.OFF_B2_ATAIL), tail);
            Thread.MemoryBarrier();
        }

        /// <summary>
        /// 真正落一帧（调用方已持有该槽的写锁）。
        /// </summary>
        /// <param name="slot">槽号。</param>
        /// <param name="clientToHost">方向。</param>
        /// <param name="frameId">关联号。</param>
        /// <param name="kind">帧类型。</param>
        /// <param name="body">正文。</param>
        /// <param name="length">正文长度。</param>
        /// <returns>帧已提交时为 <c>true</c>。</returns>
        private bool WriteFrame(int slot, bool clientToHost, int frameId, ushort kind, byte[] body, int length)
        {
            long head;
            long tail;
            bool suspicious;
            if (!TryReadRingPointers(slot, clientToHost, out head, out tail, out suspicious)) return false;
            if (suspicious)
            {
                // 写方无权推进 tail（它属于另一个进程的读方），只能放弃这一帧并留痕。
                CourierHotFaults.Record("send: ring pointers out of range on slot " + slot.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return false;
            }

            if (head - tail >= mCapacity) return false;

            long frameBase = FrameBase(slot, clientToHost, head);
            mView.Write(frameBase + CourierHot.OFF_FRAME_ID, frameId);
            mView.Write(frameBase + CourierHot.OFF_KIND, (short)kind);
            mView.Write(frameBase + CourierHot.OFF_FLAGS, (short)0);
            mView.Write(frameBase + CourierHot.OFF_LENGTH, length);
            mView.Write(frameBase + CourierHot.OFF_CRC, (int)Crc32.Compute(body, 0, length));
            mView.WriteArray(frameBase + CourierHot.FRAME_HEADER_BYTES, body, 0, length);
            Thread.MemoryBarrier();
            mView.Write(frameBase + CourierHot.OFF_SEQ, head + 1);
            Thread.MemoryBarrier();
            mView.Write(DescriptorBase(slot) + (clientToHost ? CourierHot.OFF_A2_BHEAD : CourierHot.OFF_B2_AHEAD), head + 1);
            mView.Write(DescriptorBase(slot) + CourierHot.OFF_LAST_SEEN, DateTime.UtcNow.Ticks);
            Thread.MemoryBarrier();
            return RingBell(slot, clientToHost);
        }

        /// <summary>置位方向对应的门铃。帧已经提交，门铃失败只影响及时性，所以返回 <c>true</c>。</summary>
        /// <param name="slot">槽号。</param>
        /// <param name="clientToHost">方向。</param>
        /// <returns>恒为 <c>true</c>（帧已提交）；门铃丢失只记一条诊断。</returns>
        private bool RingBell(int slot, bool clientToHost)
        {
            try
            {
                EventWaitHandle? bell = DoorBell(slot, clientToHost);
                if (bell != null) bell.Set();
            }
            catch (ObjectDisposedException)
            {
                CourierHotFaults.Record("send: doorbell disposed");
            }

            return true;
        }

        /// <summary>
        /// 取某个方向的门铃。宿主用建池时留下的句柄，客户端按名字打开并缓存。
        /// </summary>
        /// <param name="slot">槽号。</param>
        /// <param name="a2b"><c>true</c> 表示客户端 → 宿主方向的"有新请求"门铃。</param>
        /// <returns>门铃；本进程既没有句柄也打不开时为 <c>null</c>。</returns>
        private EventWaitHandle? DoorBell(int slot, bool a2b)
        {
            if (a2b)
            {
                EventWaitHandle[]? hostBells = mHostBells;
                if (hostBells != null) return hostBells[slot];
                return SendBell(slot);
            }

            EventWaitHandle[]? clientBells = mClientBells;
            if (clientBells != null) return clientBells[slot];
            return ReceiveBell(slot);
        }

        /// <summary>客户端侧：缓存并返回 a2b 方向的发送门铃。</summary>
        /// <param name="slot">槽号。</param>
        /// <returns>门铃；打不开时为 <c>null</c>。</returns>
        private EventWaitHandle? SendBell(int slot)
        {
            EventWaitHandle? cached = mSendBell;
            if (cached != null && mSendBellSlot == slot) return cached;
            DisposeBell(ref mSendBell);
            mSendBellSlot = slot;
            mSendBell = OpenDoor(slot, true);
            return mSendBell;
        }

        /// <summary>客户端侧：缓存并返回 b2a 方向的接收门铃。</summary>
        /// <param name="slot">槽号。</param>
        /// <returns>门铃；打不开时为 <c>null</c>。</returns>
        private EventWaitHandle? ReceiveBell(int slot)
        {
            EventWaitHandle? cached = mRecvBell;
            if (cached != null && mRecvBellSlot == slot) return cached;
            DisposeBell(ref mRecvBell);
            mRecvBellSlot = slot;
            mRecvBell = OpenDoor(slot, false);
            return mRecvBell;
        }

        /// <summary>按名字打开一个门铃；打不开时返回 <c>null</c> 并记一条诊断。</summary>
        /// <param name="slot">槽号。</param>
        /// <param name="a2b">方向。</param>
        /// <returns>门铃句柄，或 <c>null</c>。</returns>
        private EventWaitHandle? OpenDoor(int slot, bool a2b)
        {
            try
            {
                return OpenBell(ObjectName(mDoorPrefix, CourierHot.DoorObjectName(slot, a2b)));
            }
            catch (Exception caught) when (caught is WaitHandleCannotBeOpenedException || caught is IOException || caught is UnauthorizedAccessException)
            {
                CourierHotFaults.Record("bell: " + Describe(caught));
                return null;
            }
        }
}
}
