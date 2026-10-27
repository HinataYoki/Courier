using System;

namespace Courier
{
    /// <summary>
    /// 帧正文的 CRC32（多项式 0xEDB88320）。查表法，表在类型初始化时建一次；
    /// 校验只作用于内存里的帧，不参与持久化格式。
    /// </summary>
    internal static class Crc32
    {
        /// <summary>256 项查找表。</summary>
        private static readonly uint[] sTable = CreateTable();

        /// <summary>建表；只在类型初始化时执行一次。</summary>
        /// <returns>查找表。</returns>
        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < 256; index++)
            {
                uint value = index;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? (value >> 1) ^ 0xEDB88320u : value >> 1;
                }

                table[index] = value;
            }

            return table;
        }

        /// <summary>算一段字节的 CRC32。</summary>
        /// <param name="buffer">缓冲。</param>
        /// <param name="offset">起始偏移。</param>
        /// <param name="count">字节数。</param>
        /// <returns>CRC32 值。</returns>
        internal static uint Compute(byte[] buffer, int offset, int count)
        {
            uint crc = 0xFFFFFFFFu;
            int end = offset + count;
            for (int index = offset; index < end; index++)
            {
                crc = (crc >> 8) ^ sTable[(crc ^ buffer[index]) & 0xFF];
            }

            return crc ^ 0xFFFFFFFFu;
        }
    }
}
