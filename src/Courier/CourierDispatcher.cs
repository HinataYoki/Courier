using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Courier
{
    /// <summary>
    /// 投递侧的协议实现（SPEC §9.3、§17.1）。库的 <see cref="CourierNode.CallAsync"/> 与 CLI 的 call 共用这一份前置检查，
    /// 避免"CLI 少做一项检查"这类只在一条路径上出现的偏差。
    /// </summary>
    internal static class CourierDispatcher
    {
        /// <summary>
        /// 原子写入一条命令。返回 null 表示已落盘；非 null 是本地合成的早期结果，此时一个字节都没写（SPEC §9.3、§11.2）。
        /// 标识符合法性由调用方先行校验，本方法只做协议层的前置检查。
        /// </summary>
        /// <param name="rootDirectory">协议根目录。</param>
        /// <param name="layout">布局。</param>
        /// <param name="issuedBy">发起方标识（SPEC §9，非凭据）。</param>
        /// <param name="endpointId">目标端点。</param>
        /// <param name="channel">通道。</param>
        /// <param name="action">动作。</param>
        /// <param name="payload">不透明载荷，可为 null。</param>
        /// <param name="deadline">投递方期望的完成期限，超过上限时被截断（SPEC §9.1）。</param>
        /// <param name="requestId">落盘时的文件名；本地拒绝或不可达时为空串。</param>
        /// <returns>本地合成的早期结果，或 null。</returns>
        internal static CourierResponse? Deliver(string rootDirectory, CourierLayout layout, string issuedBy, string endpointId, string channel, string action, string? payload, TimeSpan deadline, out string requestId)
        {
            requestId = string.Empty;

            string targetDirectory = Path.Combine(CourierLayout.Combine(rootDirectory, layout.EndpointsDirectory), endpointId);
            // 与写方同一个布局文件名：投递方若读协议常量，配了自定义 endpoints 目录的布署会
            // "投递成功"却没任何进程在扫那个目录，而扫描侧也看不见它——两侧不对称最难排查。
            string endpointPath = Path.Combine(targetDirectory, layout.EndpointFileName);

            // SPEC §3.1：逃逸/链接必须先于任何读取被拦住；否则一条布局就能让投递去读 root 外面。
            if (!CourierFsGuard.IsSafe(rootDirectory, endpointPath, out _)) return Unreachable();

            Dictionary<string, object?>? endpointDocument = ReadEndpoint(endpointPath);
            if (endpointDocument == null) return Unreachable();

            CourierResponse? rejected = CheckProtocolVersion(endpointDocument);
            if (rejected != null) return rejected;

            string targetCommands = Path.Combine(targetDirectory, layout.CommandsDirectory);
            CourierResponse? early = CheckBackpressure(targetCommands) ?? CheckPayloadSize(payload);
            if (early != null) return early;

            deadline = ClampDeadline(deadline);

            // 只有真的要把命令写到盘上时才生成 requestId：它是文件名，也是调用方后续等待的依据，
            // 在会被本地拒绝的路径上先占一个 id，会让"这次调用有没有落盘"变得无法从 id 上读出来。
            requestId = CourierProtocol.NewRequestId();
            DateTime issuedAtUtc = DateTime.UtcNow;
            CourierCommand command = CourierCommand.Create(requestId, channel, action, payload ?? string.Empty, issuedBy, issuedAtUtc, issuedAtUtc + deadline);
            string commandJson = command.ToJson();

            // 序列化之后才准的检查：转义会放大长度，只看 payload 长度会漏判（SPEC §9.2）。
            if (Encoding.UTF8.GetByteCount(commandJson) > CourierProtocol.MAX_COMMAND_FILE_BYTES)
            {
                requestId = string.Empty;
                return Rejected(string.Empty, "payload exceeds the command file limit", CourierProtocol.REASON_INVALID_COMMAND);
            }

            return TryWriteCommand(targetCommands, requestId, commandJson);
        }

        /// <summary>
        /// 背压检查（SPEC §9.3）：由投递方判定，被拒的命令一个字节都不写。
        /// </summary>
        /// <param name="targetCommands">目标 <c>commands/</c> 目录。</param>
        /// <returns>压力过高时的本地拒绝，否则为 null。</returns>
        private static CourierResponse? CheckBackpressure(string targetCommands)
        {
            if (CourierIO.CountJsonFiles(targetCommands) < CourierProtocol.MAX_COMMANDS_BACKPRESSURE) return null;
            return Rejected(string.Empty, "commands directory is full", CourierProtocol.REASON_BACKPRESSURE);
        }

        /// <summary>
        /// 载荷粗检（SPEC §9.2）。上限是**字节**：按 UTF-16 码元算会让纯中文内容通过检查却写出三倍大小的文件。
        /// </summary>
        /// <remarks>
        /// 这一步仍是空 requestId：本地合成的拒绝不是一次投递，不该带一个盘上根本不存在的 id（§11.2）。
        /// 序列化之后的复检在 <see cref="Deliver"/> 里，因为转义还会放大长度。
        /// </remarks>
        /// <param name="payload">不透明载荷，可为 null。</param>
        /// <returns>超限时的本地拒绝，否则为 null。</returns>
        private static CourierResponse? CheckPayloadSize(string? payload)
        {
            if (payload == null || Encoding.UTF8.GetByteCount(payload) <= CourierProtocol.MAX_COMMAND_FILE_BYTES) return null;
            return Rejected(string.Empty, "payload exceeds the command file limit", CourierProtocol.REASON_INVALID_COMMAND);
        }

        /// <summary>
        /// 截断调用方给的期限（SPEC §9.1）：超过 <see cref="CourierProtocol.MAX_DEADLINE"/> 时截断而不是报错，
        /// 一个误传的极大值不该让命令在盘上躺到下一次重启；负数按零处理。
        /// </summary>
        /// <param name="deadline">调用方请求的期限。</param>
        /// <returns>实际生效的期限。</returns>
        private static TimeSpan ClampDeadline(TimeSpan deadline)
        {
            if (deadline > CourierProtocol.MAX_DEADLINE) return CourierProtocol.MAX_DEADLINE;
            return deadline < TimeSpan.Zero ? TimeSpan.Zero : deadline;
        }

        /// <summary>
        /// 读目标端点的 <c>endpoint.json</c> 并解析。读不到或不是 JSON 对象都返回 null：两种情况对投递方
        /// 是同一件事——那个端点现在不可服务（SPEC §11.2 的 unreachable）。
        /// </summary>
        /// <param name="endpointPath">目标端点描述文件的绝对路径。</param>
        /// <returns>解析出的对象，或 null。</returns>
        private static Dictionary<string, object?>? ReadEndpoint(string endpointPath)
        {
            string? endpointText;
            if (!CourierIO.TryReadAllText(endpointPath, out endpointText)) return null;
            Dictionary<string, object?>? document;
            return CourierJson.TryParseObject(endpointText, out document) ? document : null;
        }

        /// <summary>
        /// 版本检查（SPEC §4.4）：不匹配 MUST 在使用之前发现，命令一个字节都不写。
        /// </summary>
        /// <param name="endpointDocument">已解析的端点描述。</param>
        /// <returns>版本不匹配时的本地拒绝，匹配时为 null。</returns>
        private static CourierResponse? CheckProtocolVersion(Dictionary<string, object?> endpointDocument)
        {
            long? version = CourierJson.GetInteger(endpointDocument, "protocolVersion");
            if (version != null && version.Value == CourierProtocol.PROTOCOL_VERSION) return null;

            string found = version == null ? "missing" : version.Value.ToString(CultureInfo.InvariantCulture);
            return Rejected(string.Empty, "protocolVersion mismatch: expected " + CourierProtocol.PROTOCOL_VERSION + ", found " + found, CourierProtocol.REASON_INVALID_COMMAND);
        }

        /// <summary>
        /// 原子落盘一条命令；失败一律变成本地拒绝，绝不让异常穿到宿主（SPEC §3.1）。
        /// </summary>
        /// <param name="targetCommands">目标 <c>commands/</c> 目录。</param>
        /// <param name="requestId">已经生成好的 requestId，同时是文件名。</param>
        /// <param name="commandJson">序列化后的命令。</param>
        /// <returns>成功时为 null，否则为本地合成的拒绝。</returns>
        private static CourierResponse? TryWriteCommand(string targetCommands, string requestId, string commandJson)
        {
            try
            {
                // SPEC §17.1：投递必须原子写且不覆盖；同名 requestId 撞车时让调用方重来，绝不静默覆盖别人的命令。
                CourierIO.WriteAtomic(Path.Combine(targetCommands, requestId + CourierProtocol.JSON_EXTENSION), commandJson, false);
                return null;
            }
            catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException)
            {
                // UnauthorizedAccessException 是 WriteAtomic 新建临时文件时最常见的失败（目录 ACL、
                // 杀软锁），它和 IOException 是同一种失败：命令没落盘。只捕 IOException 会让它一路穿出
                // CallAsync，§3.1 明令禁止把异常抛到宿主；这里统一变成 rejected。
                return Rejected(requestId, "failed to deliver command (SPEC §17.1): " + caught.Message, CourierProtocol.REASON_INVALID_COMMAND);
            }
        }

        /// <summary>合成一个本地不可达响应（SPEC §11.2）：不落盘，requestId 为空串。</summary>
        /// <returns>status 为 unreachable 的响应。</returns>
        private static CourierResponse Unreachable()
        {
            // SPEC §11.2：对端根本没启动时，结果由调用方本地合成，不落盘。
            return new CourierResponse(string.Empty, CourierProtocol.STATUS_UNREACHABLE, string.Empty, null, false, DateTime.UtcNow);
        }

        /// <summary>合成一个本地拒绝响应（SPEC §11.2）：不落盘。</summary>
        /// <param name="requestId">已经生成过 requestId 时带上它，否则为空串。</param>
        /// <param name="payload">给调用方看的说明。</param>
        /// <param name="reason">SPEC §18 的原因码。</param>
        /// <returns>status 为 rejected 的响应。</returns>
        private static CourierResponse Rejected(string requestId, string payload, string reason)
        {
            return new CourierResponse(requestId, CourierProtocol.STATUS_REJECTED, payload, reason, false, DateTime.UtcNow);
        }
    }
}
