using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Courier
{
    /// <summary>
    /// 一次调用的结果（SPEC §9）。四种 status 覆盖全部情形：<c>ok</c>、<c>rejected</c>、<c>failed</c>
    /// 三种来自对端落盘的 results 文件，<c>unreachable</c> 由调用方在超时后本地合成、没有对应文件（SPEC §9.2）。
    /// </summary>
    public sealed class CourierResponse
    {
        /// <summary>调用方生成的请求标识，等于对端 commands/ 与 results/ 下的文件名（去掉扩展名）。</summary>
        public string RequestId { get; }

        /// <summary><c>ok</c>、<c>rejected</c>、<c>failed</c> 或 <c>unreachable</c>。</summary>
        public string Status { get; }

        /// <summary>ok 时是 handler 的返回值；其余情形是诊断文本，可能为空串。Courier 从不解析它（SPEC §7）。</summary>
        public string Payload { get; }

        /// <summary>status 不是 ok 时的原因码，取值见 SPEC §15；ok 时为 null。</summary>
        public string? Reason { get; }

        /// <summary>handler 观察到取消请求但未采纳时，响应会带上该标记（SPEC §11.1）。</summary>
        public bool CancelRequested { get; }

        /// <summary>对端完成处理的时刻；unreachable 时是本地放弃等待的时刻。</summary>
        public DateTime CompletedAtUtc { get; }

        /// <summary>status 是否为 ok 的便捷判断。</summary>
        public bool IsOk
        {
            get { return string.Equals(Status, CourierProtocol.StatusOk, StringComparison.Ordinal); }
        }

        internal CourierResponse(string requestId, string status, string payload, string? reason, bool cancelRequested, DateTime completedAtUtc)
        {
            RequestId = requestId;
            Status = status;
            Payload = payload;
            Reason = reason;
            CancelRequested = cancelRequested;
            CompletedAtUtc = completedAtUtc;
        }

        /// <summary>从 results/ 里的响应文件构造；字段缺失或非法时返回 null，由调用方当作损坏文件处理。</summary>
        internal static CourierResponse? TryParse(string? text)
        {
            Dictionary<string, object?>? document;
            if (!CourierJson.TryParseObject(text, out document)) return null;
            string? status = CourierJson.GetString(document, "status");
            if (status == null) return null;
            if (!string.Equals(status, CourierProtocol.StatusOk, StringComparison.Ordinal)
                && !string.Equals(status, CourierProtocol.StatusRejected, StringComparison.Ordinal)
                && !string.Equals(status, CourierProtocol.StatusFailed, StringComparison.Ordinal))
            {
                return null;
            }

            DateTime completedAtUtc;
            if (!CourierProtocol.TryParseUtc(CourierJson.GetString(document, "completedAtUtc"), out completedAtUtc))
            {
                completedAtUtc = DateTime.UtcNow;
            }

            string? requestId = CourierJson.GetString(document, "requestId");
            bool cancelRequested = document.TryGetValue("cancelRequested", out object? flag) && flag is bool requested && requested;
            // status 已被上面的三值校验排除为 null，这里直接传。
            return new CourierResponse(
                requestId ?? string.Empty,
                status,
                CourierJson.GetString(document, "payload") ?? string.Empty,
                CourierJson.GetString(document, "reason"),
                cancelRequested,
                completedAtUtc);
        }

        /// <summary>序列化为 results/ 下的单行紧凑 JSON（SPEC §9）。reason 只在非 ok 时出现。</summary>
        internal string ToJson()
        {
            var builder = new CourierJsonObject()
                .AddString("requestId", RequestId)
                .AddString("status", Status)
                .AddString("payload", Payload)
                .AddString("completedAtUtc", CourierProtocol.FormatUtc(CompletedAtUtc));
            if (Reason != null) builder.AddString("reason", Reason);
            if (CancelRequested) builder.AddBoolean("cancelRequested", true);
            return builder.Build();
        }
    }

    /// <summary>endpoint.json 里一条已注册的能力（SPEC §5）。</summary>
    public sealed class CourierActionInfo
    {
        /// <summary>能力所属信道。</summary>
        public string Channel { get; }

        /// <summary>信道内的动作名。</summary>
        public string Action { get; }

        /// <summary>面向人和工具的简短说明。</summary>
        public string Summary { get; }

        internal CourierActionInfo(string channel, string action, string summary)
        {
            Channel = channel;
            Action = action;
            Summary = summary;
        }
    }

    /// <summary>快通道声明（SPEC §5）。本实现只走文件通道，这里仅做透传，不参与任何调度。</summary>
    public sealed class CourierHotInfo
    {
        /// <summary><c>namedPipe</c>、<c>sharedMemory</c> 或 <c>fileMapping</c>。</summary>
        public string Kind { get; }

        /// <summary>快通道地址，语义由 kind 决定。</summary>
        public string Address { get; }

        /// <summary>快通道自身的协议标识。</summary>
        public string Protocol { get; }

        /// <summary>快通道不可用时的回退方式，SPEC 要求恒为 <c>file</c>。</summary>
        public string Fallback { get; }

        internal CourierHotInfo(string kind, string address, string protocol, string fallback)
        {
            Kind = kind;
            Address = address;
            Protocol = protocol;
            Fallback = fallback;
        }
    }

    /// <summary>
    /// 目录扫描得到的一个端点视图（SPEC §6）。字段与 endpoint.json 一一对应，另加由 heartbeat 推算的
    /// <see cref="State"/>；只读快照，不反映后续变化。
    /// </summary>
    public sealed class CourierEndpointInfo
    {
        /// <summary>端点标识，同时是 endpoints/ 下的目录名。</summary>
        public string EndpointId { get; }

        /// <summary>端点自述。</summary>
        public string Description { get; }

        /// <summary>写入 endpoint.json 时的进程号，仅作诊断。</summary>
        public int ProcessId { get; }

        /// <summary>端点启动时刻。</summary>
        public DateTime StartedAtUtc { get; }

        /// <summary>端点声明的协议版本。</summary>
        public int ProtocolVersion { get; }

        /// <summary>已注册的能力。</summary>
        public IReadOnlyList<CourierActionInfo> Actions { get; }

        /// <summary>快通道声明；没有声明时为 null。</summary>
        public CourierHotInfo? Hot { get; }

        /// <summary>
        /// 心跳年龄（毫秒，SPEC §6）；读不到心跳时为 null。判活与"多少秒之前"共用这一个读数，
        /// 观察工具不必为了显示年龄再读一次 heartbeat——两次读之间端点可能刚好换了状态。
        /// </summary>
        public long? HeartbeatAgeMs { get; }

        /// <summary>按心跳年龄推算的存活状态：<c>alive</c>、<c>stale</c> 或 <c>dead</c>（SPEC §6）。</summary>
        public string State
        {
            get { return CourierProtocol.StateFromHeartbeatAge(HeartbeatAgeMs); }
        }

        internal CourierEndpointInfo(
            string endpointId,
            string description,
            int processId,
            DateTime startedAtUtc,
            int protocolVersion,
            List<CourierActionInfo> actions,
            CourierHotInfo? hot,
            long? heartbeatAgeMs)
        {
            EndpointId = endpointId;
            Description = description;
            ProcessId = processId;
            StartedAtUtc = startedAtUtc;
            ProtocolVersion = protocolVersion;
            Actions = new ReadOnlyCollection<CourierActionInfo>(actions);
            Hot = hot;
            HeartbeatAgeMs = heartbeatAgeMs;
        }
    }

    /// <summary>commands/ 下一条命令的解析结果（SPEC §7）。任一字段缺失或非法都不得执行。</summary>
    internal sealed class CourierCommand
    {
        internal string RequestId { get; private set; } = string.Empty;
        internal string Channel { get; private set; } = string.Empty;
        internal string Action { get; private set; } = string.Empty;
        internal string Payload { get; private set; } = string.Empty;
        internal DateTime IssuedAtUtc { get; private set; }
        internal DateTime DeadlineUtc { get; private set; }
        internal string IssuedBy { get; private set; } = string.Empty;

        /// <summary>
        /// 解析命令文件。requestId 必须与文件名一致（SPEC §7），因此文件名由调用方传入比对。
        /// 任何缺失或非法都返回 null，由调用方进 deadletter（SPEC §10）。
        /// </summary>
        internal static CourierCommand? TryParse(string? text, string expectedRequestId)
        {
            Dictionary<string, object?>? document;
            if (!CourierJson.TryParseObject(text, out document)) return null;

            string? requestId = CourierJson.GetString(document, "requestId");
            string? channel = CourierJson.GetString(document, "channel");
            string? action = CourierJson.GetString(document, "action");
            if (requestId == null || !string.Equals(requestId, expectedRequestId, StringComparison.Ordinal)) return null;
            if (!CourierProtocol.IsValidName(channel) || !CourierProtocol.IsValidName(action)) return null;

            DateTime issuedAtUtc;
            DateTime deadlineUtc;
            if (!CourierProtocol.TryParseUtc(CourierJson.GetString(document, "issuedAtUtc"), out issuedAtUtc)) return null;
            if (!CourierProtocol.TryParseUtc(CourierJson.GetString(document, "deadlineUtc"), out deadlineUtc)) return null;

            // payload 是不透明字符串，可以为空串；非字符串（含 null）说明发送方违反了 SPEC §7。
            object? rawPayload;
            if (!document.TryGetValue("payload", out rawPayload)) return null;
            string? payload = rawPayload as string;
            if (payload == null) return null;

            return new CourierCommand
            {
                RequestId = requestId,
                // IsValidName 对 null 返回 false，走到这里两者必非 null。
                Channel = channel,
                Action = action,
                Payload = payload,
                IssuedAtUtc = issuedAtUtc,
                DeadlineUtc = deadlineUtc,
                IssuedBy = CourierJson.GetString(document, "issuedBy") ?? string.Empty
            };
        }

        /// <summary>按 SPEC §7 的字段顺序序列化，供测试与 CLI 构造命令使用。</summary>
        internal string ToJson()
        {
            return new CourierJsonObject()
                .AddString("requestId", RequestId)
                .AddString("channel", Channel)
                .AddString("action", Action)
                .AddString("payload", Payload)
                .AddString("issuedAtUtc", CourierProtocol.FormatUtc(IssuedAtUtc))
                .AddString("deadlineUtc", CourierProtocol.FormatUtc(DeadlineUtc))
                .AddString("issuedBy", IssuedBy)
                .Build();
        }

        internal static CourierCommand Create(string requestId, string channel, string action, string payload, string issuedBy, DateTime issuedAtUtc, DateTime deadlineUtc)
        {
            return new CourierCommand
            {
                RequestId = requestId,
                Channel = channel,
                Action = action,
                Payload = payload,
                IssuedBy = issuedBy,
                IssuedAtUtc = issuedAtUtc,
                DeadlineUtc = deadlineUtc
            };
        }
    }

    /// <summary>
    /// claims/ 下的租约（SPEC §8.1）。attempts 是重试计数的唯一存放处——命令文件 MUST NOT 被改写。
    /// expiresAtUtc 是租约到期时刻，不是认领时刻：持有者在执行期间持续把它往后推，
    /// 别的服务循环只在它过期之后才接管。
    /// </summary>
    internal sealed class CourierClaim
    {
        internal string Owner { get; private set; } = string.Empty;
        internal DateTime ExpiresAtUtc { get; private set; }
        internal int Attempts { get; private set; }

        internal static CourierClaim? TryParse(string? text)
        {
            Dictionary<string, object?>? document;
            if (!CourierJson.TryParseObject(text, out document)) return null;
            DateTime expiresAtUtc;
            if (!CourierProtocol.TryParseUtc(CourierJson.GetString(document, "expiresAtUtc"), out expiresAtUtc)) return null;
            long? attempts = CourierJson.GetInteger(document, "attempts");
            if (attempts == null || attempts.Value < 1 || attempts.Value > int.MaxValue) return null;
            return new CourierClaim
            {
                Owner = CourierJson.GetString(document, "owner") ?? string.Empty,
                ExpiresAtUtc = expiresAtUtc,
                Attempts = (int)attempts.Value
            };
        }

        internal string ToJson()
        {
            return new CourierJsonObject()
                .AddString("owner", Owner)
                .AddString("expiresAtUtc", CourierProtocol.FormatUtc(ExpiresAtUtc))
                .AddNumber("attempts", Attempts)
                .Build();
        }

        internal static CourierClaim Create(string owner, DateTime expiresAtUtc, int attempts)
        {
            return new CourierClaim { Owner = owner, ExpiresAtUtc = expiresAtUtc, Attempts = attempts };
        }

        /// <summary>SPEC §8.1：把租约推到 now + ClaimWindow，保留 owner 与 attempts。</summary>
        internal CourierClaim Renew(DateTime nowUtc)
        {
            return new CourierClaim { Owner = Owner, ExpiresAtUtc = nowUtc + CourierProtocol.ClaimWindow, Attempts = Attempts };
        }
    }

    /// <summary>deadletter/ 下的失败证据（SPEC §10）。写入前必须先落 results 再搬命令，避免命令凭空消失。</summary>
    internal sealed class CourierDeadletter
    {
        internal string RequestId { get; private set; } = string.Empty;
        internal string Reason { get; private set; } = string.Empty;
        internal string Detail { get; private set; } = string.Empty;
        internal DateTime FailedAtUtc { get; private set; }
        internal int Attempts { get; private set; }

        internal static CourierDeadletter? TryParse(string? text)
        {
            Dictionary<string, object?>? document;
            if (!CourierJson.TryParseObject(text, out document)) return null;
            string? requestId = CourierJson.GetString(document, "requestId");
            string? reason = CourierJson.GetString(document, "reason");
            if (requestId == null || reason == null) return null;
            DateTime failedAtUtc;
            if (!CourierProtocol.TryParseUtc(CourierJson.GetString(document, "failedAtUtc"), out failedAtUtc)) return null;
            long? attempts = CourierJson.GetInteger(document, "attempts");
            return new CourierDeadletter
            {
                RequestId = requestId,
                Reason = reason,
                Detail = CourierJson.GetString(document, "detail") ?? string.Empty,
                FailedAtUtc = failedAtUtc,
                Attempts = attempts == null ? 0 : (int)attempts.Value
            };
        }

        internal string ToJson()
        {
            return new CourierJsonObject()
                .AddString("requestId", RequestId)
                .AddString("reason", Reason)
                .AddString("detail", Detail)
                .AddString("failedAtUtc", CourierProtocol.FormatUtc(FailedAtUtc))
                .AddNumber("attempts", Attempts)
                .Build();
        }

        internal static CourierDeadletter Create(string requestId, string reason, string detail, DateTime failedAtUtc, int attempts)
        {
            return new CourierDeadletter
            {
                RequestId = requestId,
                Reason = reason,
                Detail = detail ?? string.Empty,
                FailedAtUtc = failedAtUtc,
                Attempts = attempts
            };
        }
    }
}
