namespace Courier
{
    /// <summary>
    /// Courier 流数据面的二进制协议常量。
    /// <para>
    /// 此协议只属于同机 CourierStream 实现，不进入 Courier 的 commands/results/state 契约。
    /// </para>
    /// </summary>
    internal static class CourierStreamProtocol
    {
        /// <summary>握手与帧的魔数，ASCII <c>CST1</c> 的小端表示。</summary>
        internal const uint MAGIC = 0x31545343u;

        /// <summary>协议版本。</summary>
        internal const int VERSION = 1;

        /// <summary>客户端握手请求字节数：magic + version + sinceSequence。</summary>
        internal const int REQUEST_BYTES = 4 + 4 + 8;

        /// <summary>服务端握手响应字节数：magic + version + oldest + next + dropped。</summary>
        internal const int RESPONSE_BYTES = 4 + 4 + 8 + 8 + 8;

        /// <summary>记录帧头字节数：magic + version + flags + sequence + timestamp + length + crc。</summary>
        internal const int FRAME_HEADER_BYTES = 4 + 2 + 2 + 8 + 8 + 4 + 4;
    }
}
