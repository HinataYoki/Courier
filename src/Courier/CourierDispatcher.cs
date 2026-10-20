using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Courier
{
    /// <summary>
    /// 投递侧的协议实现（SPEC §7.3、§14.1）。库的 <see cref="CourierNode.CallAsync"/> 与 CLI 的 call 共用这一份前置检查，
    /// 避免"CLI 少做一项检查"这类只在一条路径上出现的偏差。
    /// </summary>
    internal static class CourierDispatcher
    {
        /// <summary>
        /// 原子写入一条命令。返回 null 表示已落盘；非 null 是本地合成的早期结果，此时一个字节都没写（SPEC §7.3、§9.2）。
        /// 标识符合法性由调用方先行校验，本方法只做协议层的前置检查。
        /// </summary>
        internal static CourierResponse? Deliver(string rootDirectory, string issuedBy, string endpointId, string channel, string action, string? payload, TimeSpan deadline, out string requestId)
        {
            requestId = string.Empty;
            string targetDirectory = Path.Combine(rootDirectory, CourierProtocol.EndpointsDirectory, endpointId);
            string? endpointText;
            if (!CourierIO.TryReadAllText(Path.Combine(targetDirectory, CourierProtocol.EndpointFileName), out endpointText)) return Unreachable();

            Dictionary<string, object?>? endpointDocument;
            if (!CourierJson.TryParseObject(endpointText, out endpointDocument)) return Unreachable();

            // SPEC §4.4：版本不匹配 MUST 在使用之前发现，命令一个字节都不写。
            long? version = CourierJson.GetInteger(endpointDocument, "protocolVersion");
            if (version == null || version.Value != CourierProtocol.ProtocolVersion)
            {
                string found = version == null ? "missing" : version.Value.ToString(CultureInfo.InvariantCulture);
                return Rejected(string.Empty, "protocolVersion mismatch: expected " + CourierProtocol.ProtocolVersion + ", found " + found, CourierProtocol.ReasonInvalidCommand);
            }

            // SPEC §7.3：背压由投递方判定，被拒的命令不落盘。
            string targetCommands = Path.Combine(targetDirectory, CourierProtocol.CommandsDirectory);
            if (CourierIO.CountJsonFiles(targetCommands) >= CourierProtocol.MaxCommandsBackpressure) return Rejected(string.Empty, "commands directory is full", CourierProtocol.ReasonBackpressure);

            requestId = CourierProtocol.NewRequestId();
            DateTime issuedAtUtc = DateTime.UtcNow;
            // SPEC §7.2 的上限是**字节**：payload 长度按 UTF-16 码元算会让纯中文内容通过检查却写出三倍大小的文件。
            if (payload != null && Encoding.UTF8.GetByteCount(payload) > CourierProtocol.MaxCommandFileBytes) return Rejected(requestId, "payload exceeds the command file limit", CourierProtocol.ReasonInvalidCommand);

            // SPEC §7.1：deadline 由调用方按次指定，超过 MAX_DEADLINE 时截断而不是报错——
            // 一个误传的极大值不该让命令在盘上躺到下一次重启。
            if (deadline > CourierProtocol.MaxDeadline) deadline = CourierProtocol.MaxDeadline;
            if (deadline < TimeSpan.Zero) deadline = TimeSpan.Zero;
            CourierCommand command = CourierCommand.Create(requestId, channel, action, payload ?? string.Empty, issuedBy, issuedAtUtc, issuedAtUtc + deadline);
            string commandJson = command.ToJson();
            // 序列化之后才准的检查：转义会放大长度，只看 payload 长度会漏判（SPEC §7.2）。
            if (Encoding.UTF8.GetByteCount(commandJson) > CourierProtocol.MaxCommandFileBytes) return Rejected(requestId, "payload exceeds the command file limit", CourierProtocol.ReasonInvalidCommand);

            try
            {
                // SPEC §14.1：投递必须原子写且不覆盖；同名 requestId 撞车时让调用方重来，绝不静默覆盖别人的命令。
                CourierIO.WriteAtomic(Path.Combine(targetCommands, requestId + CourierProtocol.JsonExtension), commandJson, false);
            }
            catch (IOException)
            {
                return Rejected(requestId, "failed to deliver command", CourierProtocol.ReasonInvalidCommand);
            }

            return null;
        }

        private static CourierResponse Unreachable()
        {
            // SPEC §9.2：对端根本没启动时，结果由调用方本地合成，不落盘。
            return new CourierResponse(string.Empty, CourierProtocol.StatusUnreachable, string.Empty, null, false, DateTime.UtcNow);
        }

        private static CourierResponse Rejected(string requestId, string payload, string reason)
        {
            return new CourierResponse(requestId, CourierProtocol.StatusRejected, payload, reason, false, DateTime.UtcNow);
        }
    }
}
