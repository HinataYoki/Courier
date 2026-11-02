using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

#pragma warning disable CA1416

namespace Courier
{
    /// <summary>
    /// Windows mmap-ring 的只读来源。打开时读取一次头部，之后只跟随写者发布的
    /// <c>head</c>，按序号追赶记录；不会写共享内存。
    /// </summary>
    internal sealed class CourierStreamMmapSource : ICourierStreamSource
    {
        private const int READ_RETRIES = 4;
        private const int READ_SPIN_WAIT = 32;

        private readonly MemoryMappedFile mFile;
        private readonly MemoryMappedViewAccessor mView;
        private readonly EventWaitHandle mDataAvailable;
        private readonly int mCapacity;
        private readonly int mMaxRecordBytes;
        private readonly int mStride;
        private long mNextSequence;
        private bool mDisposed;

        /// <summary>接管已经打开的共享内存、事件与头部快照。</summary>
        private CourierStreamMmapSource(
            MemoryMappedFile file,
            MemoryMappedViewAccessor view,
            EventWaitHandle dataAvailable,
            int capacity,
            int maxRecordBytes,
            int stride,
            long oldestSequence,
            long hostNextSequence,
            long startSequence,
            long droppedBefore)
        {
            mFile = file;
            mView = view;
            mDataAvailable = dataAvailable;
            mCapacity = capacity;
            mMaxRecordBytes = maxRecordBytes;
            mStride = stride;
            mNextSequence = startSequence;
            OldestSequence = oldestSequence;
            NextSequence = hostNextSequence;
            DroppedBefore = droppedBefore;
        }

        /// <summary>连接时环里最旧的记录序号。</summary>
        internal long OldestSequence { get; }

        /// <summary>连接时宿主下一次写入将使用的序号。</summary>
        internal long NextSequence { get; }

        /// <summary>宿主在连接前因环满丢弃的累计记录数。</summary>
        internal long DroppedBefore { get; }

        /// <summary>读取端是否仍可用。</summary>
        public bool IsConnected
        {
            get
            {
                if (mDisposed) return false;
                try
                {
                    Thread.MemoryBarrier();
                    return mView.ReadInt32(CourierStreamMmapProtocol.OFF_CLOSED) == 0;
                }
                catch (Exception caught) when (caught is ObjectDisposedException || caught is IOException)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// 打开描述对应的共享内存与数据门铃，并校验布局。
        /// </summary>
        /// <param name="descriptor">mmap-ring 描述。</param>
        /// <param name="sinceSequence">请求的起始序号；小于等于 0 表示从当前最旧记录开始。</param>
        /// <returns>已连接的读取来源。</returns>
        internal static CourierStreamMmapSource Open(CourierStreamDescriptor descriptor, long sinceSequence)
        {
            if (descriptor.MapName == null || descriptor.DataEventName == null)
            {
                throw new InvalidDataException("courier mmap-ring descriptor is incomplete");
            }

            MemoryMappedFile? file = null;
            MemoryMappedViewAccessor? view = null;
            EventWaitHandle? dataAvailable = null;
            try
            {
                file = MemoryMappedFile.OpenExisting(descriptor.MapName);
                view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                if (view.Capacity < CourierStreamMmapProtocol.HEADER_BYTES)
                {
                    throw new InvalidDataException("courier mmap-ring header is truncated");
                }

                WaitForHeader(view);
                int capacity = view.ReadInt32(CourierStreamMmapProtocol.OFF_CAPACITY);
                int maxRecordBytes = view.ReadInt32(CourierStreamMmapProtocol.OFF_MAX_RECORD_BYTES);
                int stride = view.ReadInt32(CourierStreamMmapProtocol.OFF_STRIDE);
                Thread.MemoryBarrier();
                if (capacity != descriptor.Capacity
                    || maxRecordBytes != descriptor.MaxRecordBytes
                    || stride != CourierStreamMmapProtocol.StrideFor(descriptor.MaxRecordBytes))
                {
                    throw new InvalidDataException("courier mmap-ring geometry does not match its descriptor");
                }

                long requiredBytes = CourierStreamMmapProtocol.TotalBytes(capacity, maxRecordBytes);
                if (view.Capacity < requiredBytes)
                {
                    throw new InvalidDataException("courier mmap-ring mapping is truncated");
                }

                Thread.MemoryBarrier();
                long oldest = view.ReadInt64(CourierStreamMmapProtocol.OFF_OLDEST);
                long next = view.ReadInt64(CourierStreamMmapProtocol.OFF_HEAD);
                long dropped = view.ReadInt64(CourierStreamMmapProtocol.OFF_DROPPED);
                dataAvailable = EventWaitHandle.OpenExisting(descriptor.DataEventName);
                if (oldest <= 0 || next < oldest) throw new InvalidDataException("courier mmap-ring bounds are invalid");

                long start = sinceSequence <= 0 ? oldest : sinceSequence;
                if (start < oldest) start = oldest;
                var source = new CourierStreamMmapSource(
                    file,
                    view,
                    dataAvailable,
                    capacity,
                    maxRecordBytes,
                    stride,
                    oldest,
                    next,
                    start,
                    dropped);
                return source;
            }
            catch
            {
                if (dataAvailable != null) dataAvailable.Dispose();
                if (view != null) view.Dispose();
                if (file != null) file.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 读取下一条记录。超时返回 <c>false</c>；记录已损坏时抛出 <see cref="InvalidDataException"/>。
        /// </summary>
        /// <param name="timeoutMs">等待毫秒数；0 表示立即检查，负数表示无限等待。</param>
        /// <param name="record">读到的记录；没有数据时为 <c>null</c>。</param>
        /// <returns>读到记录时为 <c>true</c>。</returns>
        public bool TryRead(int timeoutMs, out CourierStreamRecord? record)
        {
            record = null;
            if (mDisposed) return false;

            DateTime deadline = timeoutMs < 0
                ? DateTime.MaxValue
                : DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                if (!TryReadBounds(out long oldest, out long head, out bool closed)) return false;
                if (closed) return false;

                if (mNextSequence < oldest) mNextSequence = oldest;
                if (mNextSequence < head)
                {
                    if (TryReadRecord(mNextSequence, out record))
                    {
                        mNextSequence = record!.Sequence + 1;
                        return true;
                    }

                    // 写者可能刚好覆盖了旧槽或推进了边界，重新取头部再追一次。
                    Thread.SpinWait(READ_SPIN_WAIT);
                    continue;
                }

                int waitMs;
                if (timeoutMs < 0)
                {
                    waitMs = Timeout.Infinite;
                }
                else
                {
                    double remaining = (deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (remaining <= 0) return false;
                    waitMs = (int)Math.Ceiling(remaining);
                }

                try
                {
                    if (!mDataAvailable.WaitOne(waitMs)) return false;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }
        }

        /// <summary>释放映射与门铃；幂等。</summary>
        public void Dispose()
        {
            if (mDisposed) return;
            mDisposed = true;
            try { mDataAvailable.Dispose(); }
            catch (ObjectDisposedException) { }
            mView.Dispose();
            mFile.Dispose();
        }

        /// <summary>读一次头部边界；共享内存已关闭时返回 <c>false</c>。</summary>
        private bool TryReadBounds(out long oldest, out long head, out bool closed)
        {
            oldest = 0;
            head = 0;
            closed = false;
            try
            {
                Thread.MemoryBarrier();
                closed = mView.ReadInt32(CourierStreamMmapProtocol.OFF_CLOSED) != 0;
                oldest = mView.ReadInt64(CourierStreamMmapProtocol.OFF_OLDEST);
                head = mView.ReadInt64(CourierStreamMmapProtocol.OFF_HEAD);
                Thread.MemoryBarrier();
                return true;
            }
            catch (Exception caught) when (caught is ObjectDisposedException || caught is IOException)
            {
                return false;
            }
        }

        /// <summary>
        /// 尝试读取指定序号。记录槽用提交序号做 seqlock：写者先清空、写完再发布；
        /// 读取完成后再次核对提交序号，避免把写到一半的槽当成有效记录。
        /// </summary>
        private bool TryReadRecord(long sequence, out CourierStreamRecord? record)
        {
            record = null;
            long slotBase = SlotBase(sequence);
            for (int attempt = 0; attempt < READ_RETRIES; attempt++)
            {
                Thread.MemoryBarrier();
                if (mView.ReadInt64(slotBase + CourierStreamMmapProtocol.OFF_SLOT_SEQUENCE) != sequence)
                {
                    Thread.SpinWait(READ_SPIN_WAIT);
                    continue;
                }

                long timestamp = mView.ReadInt64(slotBase + CourierStreamMmapProtocol.OFF_SLOT_TIMESTAMP);
                int length = mView.ReadInt32(slotBase + CourierStreamMmapProtocol.OFF_SLOT_LENGTH);
                uint crc = (uint)mView.ReadInt32(slotBase + CourierStreamMmapProtocol.OFF_SLOT_CRC);
                Thread.MemoryBarrier();
                if (length < 0 || length > mMaxRecordBytes)
                {
                    throw new InvalidDataException("courier mmap-ring record length is invalid");
                }

                var payload = new byte[length];
                if (length > 0)
                {
                    mView.ReadArray(slotBase + CourierStreamMmapProtocol.OFF_SLOT_PAYLOAD, payload, 0, length);
                }

                Thread.MemoryBarrier();
                if (mView.ReadInt64(slotBase + CourierStreamMmapProtocol.OFF_SLOT_SEQUENCE) != sequence)
                {
                    Thread.SpinWait(READ_SPIN_WAIT);
                    continue;
                }

                if (Crc32.Compute(payload, 0, payload.Length) != crc)
                {
                    throw new InvalidDataException("courier mmap-ring record crc mismatch");
                }

                record = new CourierStreamRecord(sequence, timestamp, payload);
                return true;
            }

            return false;
        }

        /// <summary>计算指定序号的记录槽起始偏移。</summary>
        private long SlotBase(long sequence)
        {
            return CourierStreamMmapProtocol.HEADER_BYTES + ((sequence % mCapacity) * mStride);
        }

        /// <summary>等头部魔数出现；描述写出前写者已经发布头部，因此正常路径只会命中一次。</summary>
        private static void WaitForHeader(MemoryMappedViewAccessor view)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                Thread.MemoryBarrier();
                if ((uint)view.ReadInt32(CourierStreamMmapProtocol.OFF_MAGIC) == CourierStreamMmapProtocol.MAGIC)
                {
                    Thread.MemoryBarrier();
                    int version = view.ReadInt32(CourierStreamMmapProtocol.OFF_VERSION);
                    if (version != CourierStreamMmapProtocol.VERSION)
                    {
                        throw new InvalidDataException("courier mmap-ring version is unsupported");
                    }

                    return;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    throw new InvalidDataException("courier mmap-ring header was not published");
                }

                Thread.Sleep(1);
            }
        }
    }
}
