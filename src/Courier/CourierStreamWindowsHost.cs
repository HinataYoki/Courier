using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

#pragma warning disable CA1416

namespace Courier
{
    /// <summary>
    /// Windows 的 mmap-ring 写入端：命名共享内存承载有界环，命名事件只负责唤醒读取端。
    /// <para>
    /// 写入端独占共享内存头部的发布权；读取端只读头部和记录槽。记录槽先清空提交序号，
    /// 写完整条记录后再用序号发布，避免读取端把半条记录当成有效数据。
    /// </para>
    /// </summary>
    internal sealed class CourierStreamWindowsHost : ICourierStreamHost
    {
        private readonly object mGate = new object();
        private readonly MemoryMappedFile mFile;
        private readonly MemoryMappedViewAccessor mView;
        private readonly EventWaitHandle mDataAvailable;
        private readonly int mCapacity;
        private readonly int mMaxRecordBytes;
        private readonly int mStride;
        private readonly byte[] mWriteBuffer;
        private long mHead = 1;
        private long mOldest = 1;
        private long mDropped;
        private bool mDisposed;

        /// <summary>
        /// 创建共享内存与数据门铃，并发布布局头部。
        /// </summary>
        /// <param name="mapName">共享内存对象名。</param>
        /// <param name="dataEventName">数据门铃对象名。</param>
        /// <param name="capacity">记录槽数。</param>
        /// <param name="maxRecordBytes">单条正文上限。</param>
        internal CourierStreamWindowsHost(
            string mapName,
            string dataEventName,
            int capacity,
            int maxRecordBytes)
        {
            if (!IsSupported)
            {
                throw new PlatformNotSupportedException("courier mmap-ring requires Windows");
            }

            if (string.IsNullOrEmpty(mapName)) throw new ArgumentException("map name is required", nameof(mapName));
            if (string.IsNullOrEmpty(dataEventName)) throw new ArgumentException("event name is required", nameof(dataEventName));

            mCapacity = capacity;
            mMaxRecordBytes = maxRecordBytes;
            mStride = CourierStreamMmapProtocol.StrideFor(maxRecordBytes);
            mWriteBuffer = new byte[maxRecordBytes];

            bool eventCreated;
            mDataAvailable = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                dataEventName,
                out eventCreated);
            if (!eventCreated)
            {
                mDataAvailable.Dispose();
                throw new IOException("courier stream event already exists: " + dataEventName);
            }

            MemoryMappedFile? file = null;
            MemoryMappedViewAccessor? view = null;
            try
            {
                long totalBytes = CourierStreamMmapProtocol.TotalBytes(capacity, maxRecordBytes);
                file = MemoryMappedFile.CreateNew(mapName, totalBytes);
                view = file.CreateViewAccessor(
                    0,
                    totalBytes,
                    MemoryMappedFileAccess.ReadWrite);
                InitializeHeader(view);
            }
            catch
            {
                if (view != null) view.Dispose();
                if (file != null) file.Dispose();
                try { mDataAvailable.Dispose(); }
                catch (ObjectDisposedException) { }
                throw;
            }

            mFile = file!;
            mView = view!;
        }

        /// <summary>当前实现是否支持 mmap-ring。</summary>
        internal static bool IsSupported
        {
            get { return CourierStreamMmapProtocol.IsSupported; }
        }

        /// <summary>为某次会话和流生成共享内存对象名。</summary>
        /// <param name="sessionId">端点会话标识。</param>
        /// <param name="streamName">流名称。</param>
        /// <returns>本地命名空间对象名。</returns>
        internal static string MapNameFor(string sessionId, string streamName)
        {
            return "Local\\CourierStream." + sessionId + "." + streamName + ".map";
        }

        /// <summary>为某次会话和流生成数据门铃对象名。</summary>
        /// <param name="sessionId">端点会话标识。</param>
        /// <param name="streamName">流名称。</param>
        /// <returns>本地命名空间对象名。</returns>
        internal static string DataEventNameFor(string sessionId, string streamName)
        {
            return "Local\\CourierStream." + sessionId + "." + streamName + ".data";
        }

        /// <summary>当前已写入的最后一条记录序号；没有记录时为 0。</summary>
        public long WrittenSequence
        {
            get
            {
                lock (mGate)
                {
                    return mHead - 1;
                }
            }
        }

        /// <summary>因环满丢弃的累计记录数。</summary>
        public long DroppedCount
        {
            get
            {
                lock (mGate)
                {
                    return mDropped;
                }
            }
        }

        /// <summary>
        /// 追加一条记录。环满时推进最旧序号并继续写入，绝不阻塞调用方。
        /// </summary>
        /// <param name="payload">记录正文。</param>
        /// <returns>正文在上限内且写入完成为 <c>true</c>。</returns>
        public bool TryWrite(ReadOnlySpan<byte> payload)
        {
            if (payload.Length > mMaxRecordBytes) return false;

            lock (mGate)
            {
                if (mDisposed) return false;

                long sequence = mHead;
                if (sequence - mOldest >= mCapacity)
                {
                    mOldest++;
                    mDropped++;
                }

                long slotBase = SlotBase(sequence);
                mView.Write(slotBase + CourierStreamMmapProtocol.OFF_SLOT_SEQUENCE, 0L);
                Thread.MemoryBarrier();
                mView.Write(slotBase + CourierStreamMmapProtocol.OFF_SLOT_TIMESTAMP, DateTime.UtcNow.Ticks);
                mView.Write(slotBase + CourierStreamMmapProtocol.OFF_SLOT_LENGTH, payload.Length);
                payload.CopyTo(mWriteBuffer.AsSpan(0, payload.Length));
                mView.Write(
                    slotBase + CourierStreamMmapProtocol.OFF_SLOT_CRC,
                    (int)Crc32.Compute(mWriteBuffer, 0, payload.Length));
                if (payload.Length > 0)
                {
                    mView.WriteArray(
                        slotBase + CourierStreamMmapProtocol.OFF_SLOT_PAYLOAD,
                        mWriteBuffer,
                        0,
                        payload.Length);
                }

                Thread.MemoryBarrier();
                mView.Write(slotBase + CourierStreamMmapProtocol.OFF_SLOT_SEQUENCE, sequence);
                Thread.MemoryBarrier();

                mView.Write(CourierStreamMmapProtocol.OFF_OLDEST, mOldest);
                mView.Write(CourierStreamMmapProtocol.OFF_DROPPED, mDropped);
                Thread.MemoryBarrier();
                mView.Write(CourierStreamMmapProtocol.OFF_HEAD, sequence + 1);
                Thread.MemoryBarrier();
                mHead = sequence + 1;
            }

            try
            {
                mDataAvailable.Set();
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            return true;
        }

        /// <summary>标记流已关闭、唤醒等待中的读取端，然后释放内核对象。</summary>
        public void Dispose()
        {
            lock (mGate)
            {
                if (mDisposed) return;
                mDisposed = true;
                mView.Write(CourierStreamMmapProtocol.OFF_CLOSED, 1);
                Thread.MemoryBarrier();
            }

            try { mDataAvailable.Set(); }
            catch (ObjectDisposedException) { }

            mView.Dispose();
            mFile.Dispose();
            mDataAvailable.Dispose();
        }

        /// <summary>写布局头部，魔数最后写，作为“整块共享内存可用”的发布点。</summary>
        private void InitializeHeader(MemoryMappedViewAccessor view)
        {
            view.Write(CourierStreamMmapProtocol.OFF_VERSION, CourierStreamMmapProtocol.VERSION);
            view.Write(CourierStreamMmapProtocol.OFF_CAPACITY, mCapacity);
            view.Write(CourierStreamMmapProtocol.OFF_MAX_RECORD_BYTES, mMaxRecordBytes);
            view.Write(CourierStreamMmapProtocol.OFF_STRIDE, mStride);
            view.Write(CourierStreamMmapProtocol.OFF_HEAD, mHead);
            view.Write(CourierStreamMmapProtocol.OFF_OLDEST, mOldest);
            view.Write(CourierStreamMmapProtocol.OFF_DROPPED, 0L);
            view.Write(CourierStreamMmapProtocol.OFF_HOST_PID, CourierProtocol.CurrentProcessId());
            view.Write(CourierStreamMmapProtocol.OFF_CLOSED, 0);
            Thread.MemoryBarrier();
            view.Write(CourierStreamMmapProtocol.OFF_MAGIC, (int)CourierStreamMmapProtocol.MAGIC);
            Thread.MemoryBarrier();
        }

        /// <summary>计算指定序号的记录槽起始偏移。</summary>
        /// <param name="sequence">记录序号，从 1 开始。</param>
        /// <returns>共享内存中的绝对偏移。</returns>
        private long SlotBase(long sequence)
        {
            long index = sequence % mCapacity;
            return CourierStreamMmapProtocol.HEADER_BYTES + (index * mStride);
        }
    }
}
