using System;
using System.Threading;

namespace Courier
{
    /// <summary>
    /// 单写者、单读者语义的有界内存环。
    /// <para>
    /// 写入方在游戏线程调用；读取方由 UDS 服务线程消费。环满时丢弃最旧记录并累计
    /// <see cref="DroppedCount"/>，绝不阻塞写入方。记录正文在创建时预分配到固定缓冲，
    /// 正常写入不产生新的托管分配。
    /// </para>
    /// </summary>
    internal sealed class CourierStreamRing : IDisposable
    {
        private sealed class Slot
        {
            internal Slot(int maxRecordBytes)
            {
                Buffer = new byte[maxRecordBytes];
            }

            internal byte[] Buffer { get; }
            internal long Sequence { get; set; }
            internal long TimestampUtcTicks { get; set; }
            internal int Length { get; set; }
        }

        private readonly object mGate = new object();
        private readonly Slot[] mSlots;
        private readonly int mCapacity;
        private readonly int mMaxRecordBytes;
        private readonly AutoResetEvent mDataAvailable = new AutoResetEvent(false);
        private long mHead = 1;
        private long mTail = 1;
        private long mDropped;
        private bool mDisposed;

        /// <summary>
        /// 按配置预分配环槽。
        /// </summary>
        internal CourierStreamRing(int capacity, int maxRecordBytes)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (maxRecordBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxRecordBytes));

            mCapacity = capacity;
            mMaxRecordBytes = maxRecordBytes;
            mSlots = new Slot[capacity];
            for (int index = 0; index < capacity; index++)
            {
                mSlots[index] = new Slot(maxRecordBytes);
            }
        }

        /// <summary>当前下一次写入将使用的序号。</summary>
        internal long NextSequence
        {
            get
            {
                lock (mGate)
                {
                    return mHead;
                }
            }
        }

        /// <summary>当前最旧的可用序号。</summary>
        internal long OldestSequence
        {
            get
            {
                lock (mGate)
                {
                    return mTail;
                }
            }
        }

        /// <summary>因环满被丢弃的记录数。</summary>
        internal long DroppedCount
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
        /// 追加一条记录；正文超过上限时返回 <c>false</c>。环满时丢最旧记录并继续写入。
        /// </summary>
        /// <param name="payload">记录正文。</param>
        /// <param name="sequence">成功写入时返回记录序号。</param>
        /// <returns>写入是否成功。</returns>
        internal bool TryAppend(ReadOnlySpan<byte> payload, out long sequence)
        {
            sequence = 0;
            if (payload.Length > mMaxRecordBytes) return false;

            lock (mGate)
            {
                if (mDisposed) return false;
                if (mHead - mTail >= mCapacity)
                {
                    mTail++;
                    mDropped++;
                }

                int index = (int)(mHead % mCapacity);
                Slot slot = mSlots[index];
                slot.Sequence = mHead;
                slot.TimestampUtcTicks = DateTime.UtcNow.Ticks;
                slot.Length = payload.Length;
                payload.CopyTo(slot.Buffer.AsSpan(0, payload.Length));
                sequence = mHead;
                mHead++;
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

        /// <summary>
        /// 复制指定序号的记录；序号已不在环内时返回 <c>false</c>。
        /// </summary>
        internal bool TryCopy(long sequence, byte[] destination, out int length, out long timestampUtcTicks)
        {
            length = 0;
            timestampUtcTicks = 0;
            if (destination == null) return false;

            lock (mGate)
            {
                if (mDisposed || sequence < mTail || sequence >= mHead) return false;
                Slot slot = mSlots[(int)(sequence % mCapacity)];
                if (slot.Sequence != sequence) return false;
                if (destination.Length < slot.Length) return false;
                Buffer.BlockCopy(slot.Buffer, 0, destination, 0, slot.Length);
                length = slot.Length;
                timestampUtcTicks = slot.TimestampUtcTicks;
                return true;
            }
        }

        /// <summary>
        /// 取当前可用范围；用于 reader 握手和服务端追赶。
        /// </summary>
        internal void GetBounds(out long oldestSequence, out long nextSequence, out long droppedCount)
        {
            lock (mGate)
            {
                oldestSequence = mTail;
                nextSequence = mHead;
                droppedCount = mDropped;
            }
        }

        /// <summary>
        /// 等待写入方追加记录，或直到超时。
        /// </summary>
        internal bool WaitForData(int timeoutMs)
        {
            try
            {
                return mDataAvailable.WaitOne(timeoutMs);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        /// <summary>释放等待句柄；释放后写入返回 <c>false</c>。</summary>
        public void Dispose()
        {
            lock (mGate)
            {
                if (mDisposed) return;
                mDisposed = true;
            }

            try
            {
                mDataAvailable.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
