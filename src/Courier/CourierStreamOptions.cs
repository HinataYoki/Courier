using System;

namespace Courier
{
    /// <summary>
    /// 流式数据面的容量配置。
    /// </summary>
    public sealed class CourierStreamOptions
    {
        /// <summary>默认保留的记录数。</summary>
        public const int DEFAULT_CAPACITY = 2048;

        /// <summary>默认单条记录正文上限。</summary>
        public const int DEFAULT_MAX_RECORD_BYTES = 8 * 1024;

        /// <summary>单个流最多占用的记录缓冲字节数，避免高容量与高单条上限相乘导致巨量分配。</summary>
        internal const long MAX_BUFFER_BYTES = 256L * 1024 * 1024;

        /// <summary>
        /// 环形缓冲最多保留多少条记录。
        /// </summary>
        public int Capacity { get; set; } = DEFAULT_CAPACITY;

        /// <summary>
        /// 单条记录正文的字节上限。
        /// </summary>
        public int MaxRecordBytes { get; set; } = DEFAULT_MAX_RECORD_BYTES;

        /// <summary>
        /// 校验配置并返回一个不可变快照；不合法时抛出 <see cref="ArgumentOutOfRangeException"/>。
        /// </summary>
        internal CourierStreamOptions Validate()
        {
            if (Capacity <= 0 || Capacity > 65536)
            {
                throw new ArgumentOutOfRangeException(nameof(Capacity), "capacity must be between 1 and 65536");
            }

            if (MaxRecordBytes <= 0 || MaxRecordBytes > 1024 * 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxRecordBytes), "maxRecordBytes must be between 1 and 1048576");
            }

            if ((long)Capacity * MaxRecordBytes > MAX_BUFFER_BYTES)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(Capacity),
                    "capacity multiplied by maxRecordBytes must not exceed 268435456 bytes");
            }

            return new CourierStreamOptions
            {
                Capacity = Capacity,
                MaxRecordBytes = MaxRecordBytes,
            };
        }
    }
}
