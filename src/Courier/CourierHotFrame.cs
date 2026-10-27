using System;
using System.Globalization;

namespace Courier
{
    /// <summary>
    /// 热通道的帧正文编解码（见 docs/HOT-CHANNEL.md）。
    /// <para>
    /// 帧头只带一个 32 位关联号，语义信息全在正文里以 JSON 承载——这样热通道不需要演化自己的
    /// 二进制协议，请求与响应仍然是 <see cref="CourierCommand"/> / <see cref="CourierResponse"/>
    /// 的同一套字段，读写两侧不会各说一套话。
    /// </para>
    /// <para>
    /// 正文受 <see cref="CourierHot.BODY_BYTES"/> 限制；超长时编码方返回 <c>null</c>，
    /// 调用方必须回退文件通道，而不是截断。
    /// </para>
    /// </summary>
    internal static class CourierHotFrame
    {
        /// <summary>把一次请求编码成帧正文；超过帧容量时返回 <c>null</c>（调用方回退文件通道）。</summary>
        internal static string? BuildRequest(string requestId, string issuedBy, string channel, string action, string? payload, DateTime deadlineUtc)
        {
            string json = new CourierJsonObject()
                .AddString("requestId", requestId)
                .AddString("issuedBy", issuedBy)
                .AddString("channel", channel)
                .AddString("action", action)
                .AddString("payload", payload ?? string.Empty)
                .AddString("deadlineUtc", CourierProtocol.FormatUtc(deadlineUtc))
                .Build();
            return Fits(json) ? json : null;
        }

        /// <summary>解析帧正文里的请求；字段缺失或非法时返回 <c>false</c>。</summary>
        internal static bool TryParseRequest(
            string text,
            out string requestId,
            out string issuedBy,
            out string channel,
            out string action,
            out string payload,
            out DateTime deadlineUtc)
        {
            requestId = string.Empty;
            issuedBy = string.Empty;
            channel = string.Empty;
            action = string.Empty;
            payload = string.Empty;
            deadlineUtc = DateTime.MinValue;

            System.Collections.Generic.Dictionary<string, object?>? document;
            if (!CourierJson.TryParseObject(text, out document)) return false;

            string? parsedRequestId = CourierJson.GetString(document, "requestId");
            string? parsedChannel = CourierJson.GetString(document, "channel");
            string? parsedAction = CourierJson.GetString(document, "action");
            // 与文件通道同一套校验：requestId 是结果文件的文件名，channel/action 是能力表键。
            if (parsedRequestId == null || !CourierProtocol.IsValidRequestId(parsedRequestId)) return false;
            if (parsedChannel == null || !CourierProtocol.IsValidName(parsedChannel)) return false;
            if (parsedAction == null || !CourierProtocol.IsValidName(parsedAction)) return false;

            requestId = parsedRequestId;
            channel = parsedChannel;
            action = parsedAction;
            issuedBy = CourierJson.GetString(document, "issuedBy") ?? string.Empty;
            payload = CourierJson.GetString(document, "payload") ?? string.Empty;
            // deadline 缺失或不可解析时按默认 deadline 处理，而不是拒收——热通道不该比文件通道更苛刻。
            DateTime deadline;
            deadlineUtc = CourierProtocol.TryParseUtc(CourierJson.GetString(document, "deadlineUtc"), out deadline)
                ? deadline
                : DateTime.UtcNow + CourierProtocol.DEFAULT_DEADLINE;
            return true;
        }

        /// <summary>把结果编码成帧正文；超过帧容量时返回 <c>null</c>。</summary>
        internal static string? BuildResponse(CourierResponse response)
        {
            string json = response.ToJson();
            return Fits(json) ? json : null;
        }

        /// <summary>解析帧正文里的结果；不是合法结果时返回 <c>null</c>。</summary>
        internal static CourierResponse? TryParseResponse(string text)
        {
            return CourierResponse.TryParse(text);
        }

        /// <summary>判定一行 JSON 塞得进热通道帧体（<c>COURIER_HOT.BODY_BYTES</c>）；塞不进就必须回落文件通道。</summary>
        /// <param name="json">待发送的单行 JSON。</param>
        /// <returns>UTF-8 字节数不超过帧体上限时为 true。</returns>
        private static bool Fits(string json)
        {
            return System.Text.Encoding.UTF8.GetByteCount(json) <= CourierHot.BODY_BYTES;
        }

        /// <summary>把热通道的一次投递写成账本的一行（不含换行，调用方追加）。</summary>
        internal static string BuildJournalLine(
            string requestId,
            string issuedBy,
            string channel,
            string action,
            string status,
            string? reason,
            int payloadBytes,
            long elapsedMicros,
            int slot)
        {
            return new CourierJsonObject()
                .AddString("requestId", requestId)
                .AddString("issuedBy", issuedBy)
                .AddString("channel", channel)
                .AddString("action", action)
                .AddString("status", status)
                .AddString("reason", reason)
                .AddNumber("payloadBytes", payloadBytes)
                .AddNumber("elapsedMicros", elapsedMicros)
                .AddNumber("slot", slot)
                .AddString("atUtc", CourierProtocol.FormatUtc(DateTime.UtcNow))
                .Build();
        }

        /// <summary>把数字格式化成不变的十进制文本（账本里的耗时等）。</summary>
        internal static string Number(long value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
