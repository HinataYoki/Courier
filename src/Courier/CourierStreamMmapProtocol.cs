using System;

namespace Courier
{
    /// <summary>
    /// Windows mmap-ring 的私有布局。该布局只服务同机同用户会话，不进入 Courier 文件协议。
    /// </summary>
    internal static class CourierStreamMmapProtocol
    {
        /// <summary>共享内存魔数，ASCII <c>CSM1</c> 的小端表示。</summary>
        internal const uint MAGIC = 0x314D5343u;

        /// <summary>布局版本。</summary>
        internal const int VERSION = 1;

        /// <summary>头部字节数。</summary>
        internal const int HEADER_BYTES = 128;

        /// <summary>一个记录槽的固定头字节数：sequence + timestamp + length + crc。</summary>
        internal const int SLOT_HEADER_BYTES = 24;

        internal const int OFF_MAGIC = 0;
        internal const int OFF_VERSION = 4;
        internal const int OFF_CAPACITY = 8;
        internal const int OFF_MAX_RECORD_BYTES = 12;
        internal const int OFF_STRIDE = 16;
        internal const int OFF_HEAD = 24;
        internal const int OFF_OLDEST = 32;
        internal const int OFF_DROPPED = 40;
        internal const int OFF_HOST_PID = 48;
        internal const int OFF_CLOSED = 52;

        internal const int OFF_SLOT_SEQUENCE = 0;
        internal const int OFF_SLOT_TIMESTAMP = 8;
        internal const int OFF_SLOT_LENGTH = 16;
        internal const int OFF_SLOT_CRC = 20;
        internal const int OFF_SLOT_PAYLOAD = 24;

        /// <summary>计算记录槽步长，按 8 字节对齐，保证所有 64 位字段天然对齐。</summary>
        /// <param name="maxRecordBytes">单条正文上限。</param>
        /// <returns>槽步长。</returns>
        internal static int StrideFor(int maxRecordBytes)
        {
            long raw = (long)SLOT_HEADER_BYTES + maxRecordBytes;
            long aligned = (raw + 7L) & ~7L;
            if (aligned > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(maxRecordBytes));
            return (int)aligned;
        }

        /// <summary>计算整块共享内存的字节数；容量与步长都来自已校验的配置。</summary>
        /// <param name="capacity">记录槽数。</param>
        /// <param name="maxRecordBytes">单条正文上限。</param>
        /// <returns>共享内存总字节数。</returns>
        internal static long TotalBytes(int capacity, int maxRecordBytes)
        {
            return HEADER_BYTES + ((long)capacity * StrideFor(maxRecordBytes));
        }

        /// <summary>判断当前实现是否可以使用 Windows 命名共享内存后端。</summary>
        internal static bool IsSupported
        {
            get { return CourierHot.IsSupported; }
        }
    }
}
