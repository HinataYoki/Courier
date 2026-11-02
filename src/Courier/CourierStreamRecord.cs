using System;

namespace Courier
{
    /// <summary>
    /// 从 <see cref="CourierStreamReader"/> 读到的一条记录。
    /// </summary>
    public sealed class CourierStreamRecord
    {
        /// <summary>创建一条记录；仅供流读取器构造。</summary>
        internal CourierStreamRecord(long sequence, long timestampUtcTicks, byte[] payload)
        {
            Sequence = sequence;
            TimestampUtcTicks = timestampUtcTicks;
            Payload = payload;
        }

        /// <summary>记录序号；同一流内单调递增。</summary>
        public long Sequence { get; }

        /// <summary>写入方的 UTC Ticks 时间戳。</summary>
        public long TimestampUtcTicks { get; }

        /// <summary>记录正文；调用方拥有这个数组。</summary>
        public byte[] Payload { get; }
    }
}
