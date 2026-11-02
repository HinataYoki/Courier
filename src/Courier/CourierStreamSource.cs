using System;

namespace Courier
{
    /// <summary>
    /// CourierStream 的宿主无关写入端。具体实现负责把记录放进进程内环，并通过选定的传输送出。
    /// </summary>
    internal interface ICourierStreamHost : IDisposable
    {
        /// <summary>当前已写入的最后一条记录序号；没有记录时为 0。</summary>
        long WrittenSequence { get; }

        /// <summary>因环满丢弃的累计记录数。</summary>
        long DroppedCount { get; }

        /// <summary>追加一条记录；正文超过上限时返回 <c>false</c>。</summary>
        /// <param name="payload">记录正文。</param>
        /// <returns>写入是否成功。</returns>
        bool TryWrite(ReadOnlySpan<byte> payload);
    }

    /// <summary>
    /// CourierStream 的读取来源。UDS 与 Windows mmap-ring 各自实现，公共读取器只依赖这一层。
    /// </summary>
    internal interface ICourierStreamSource : IDisposable
    {
        /// <summary>读取端是否仍连接可用。</summary>
        bool IsConnected { get; }

        /// <summary>
        /// 读取下一条记录。超时返回 <c>false</c>；连接断开或帧损坏时抛出异常。
        /// </summary>
        /// <param name="timeoutMs">等待毫秒数；0 表示立即检查，负数表示无限等待。</param>
        /// <param name="record">读到的记录；没有数据时为 <c>null</c>。</param>
        /// <returns>读到记录时为 <c>true</c>。</returns>
        bool TryRead(int timeoutMs, out CourierStreamRecord? record);
    }
}
