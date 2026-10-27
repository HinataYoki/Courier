using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Courier
{
    /// <summary>
    /// 一次调用的结果（SPEC §11）。四种 status 覆盖全部情形：<c>ok</c>、<c>rejected</c>、<c>failed</c>
    /// 三种来自对端落盘的 results 文件，<c>unreachable</c> 由调用方在超时后本地合成、没有对应文件（SPEC §11.2）。
    /// </summary>
    public sealed class CourierResponse
    {
        /// <summary>调用方生成的请求标识，等于对端 commands/ 与 results/ 下的文件名（去掉扩展名）。</summary>
        public string RequestId { get; }

        /// <summary><c>ok</c>、<c>rejected</c>、<c>failed</c> 或 <c>unreachable</c>。</summary>
        public string Status { get; }

        /// <summary>ok 时是 handler 的返回值；其余情形是诊断文本，可能为空串。Courier 从不解析它（SPEC §9）。</summary>
        public string Payload { get; }

        /// <summary>status 不是 ok 时的原因码，取值见 SPEC §18；ok 时为 null。</summary>
        public string? Reason { get; }

        /// <summary>handler 观察到取消请求但未采纳时，响应会带上该标记（SPEC §13.1）。</summary>
        public bool CancelRequested { get; }

        /// <summary>对端完成处理的时刻；unreachable 时是本地放弃等待的时刻。</summary>
        public DateTime CompletedAtUtc { get; }

        /// <summary>
        /// 结果文件缺失、但 <c>results/gc.json</c> 的墓碑说明它**可能已经被回收**（SPEC §15.2）。
        /// <para>
        /// 只是诊断信息，不是终态：status 仍是 <c>unreachable</c>，原因码集合不变，不落盘、不进
        /// <c>results/</c>，也不改变退出码。协议不允许新增 status 或原因码，所以"已回收"只能这样表达。
        /// </para>
        /// </summary>
        public bool Reclaimed { get; }

        /// <summary>status 是否为 ok 的便捷判断。</summary>
        public bool IsOk
        {
            get { return string.Equals(Status, CourierProtocol.STATUS_OK, StringComparison.Ordinal); }
        }

        /// <summary>由服务泵或解析结果构造；<paramref name="reclaimed"/> 表示这条响应是调用方按墓碑合成出来的，不是真的落盘过。</summary>
        internal CourierResponse(string requestId, string status, string payload, string? reason, bool cancelRequested, DateTime completedAtUtc, bool reclaimed = false)
        {
            RequestId = requestId;
            Status = status;
            Payload = payload;
            Reason = reason;
            CancelRequested = cancelRequested;
            CompletedAtUtc = completedAtUtc;
            Reclaimed = reclaimed;
        }

        /// <summary>从 results/ 里的响应文件构造；字段缺失或非法时返回 null，由调用方当作损坏文件处理。</summary>
        internal static CourierResponse? TryParse(string? text)
        {
            Dictionary<string, object?>? document;
            if (!CourierJson.TryParseObject(text, out document)) return null;
            string? status = CourierJson.GetString(document, "status");
            if (status == null) return null;
            if (!string.Equals(status, CourierProtocol.STATUS_OK, StringComparison.Ordinal)
                && !string.Equals(status, CourierProtocol.STATUS_REJECTED, StringComparison.Ordinal)
                && !string.Equals(status, CourierProtocol.STATUS_FAILED, StringComparison.Ordinal))
            {
                return null;
            }

            // SPEC §11：completedAtUtc 是 MUST 字段。缺失或解析不出来时不能拿 UtcNow 顶上——
            // 那会把"这条证据已经坏了"伪装成"刚刚完成"，回收判定（§15.2 只删早于 cutoff 的完成响应）
            // 于是永远为假，损坏的响应文件再也清不掉。返回 null 让调用方按"读不懂的证据"处理：
            // 等待侧继续等下一轮（可能正被替换），GC 侧原样跳过。
            DateTime completedAtUtc;
            if (!CourierProtocol.TryParseUtc(CourierJson.GetString(document, "completedAtUtc"), out completedAtUtc)) return null;

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

        /// <summary>序列化为 results/ 下的单行紧凑 JSON（SPEC §11）。reason 只在非 ok 时出现。</summary>
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

    /// <summary>endpoint.json 里一条已注册的能力（SPEC §5、§5.1）。</summary>
    public sealed class CourierActionInfo
    {
        /// <summary>能力所属信道。</summary>
        public string Channel { get; }

        /// <summary>信道内的动作名。</summary>
        public string Action { get; }

        /// <summary>面向人和工具的简短说明。</summary>
        public string Summary { get; }

        /// <summary>
        /// 注册方附加的元数据（SPEC §5.1），单行紧凑 JSON 对象原文；未提供时为 null。
        /// 协议不解释它——宿主可以在这里写 <c>risk</c>、<c>inputSchema</c>、<c>outputSchema</c>、<c>version</c>，
        /// 读方原样转述即可。为 null 时读方 MUST NOT 输出 <c>null</c>，而是省略整个字段。
        /// </summary>
        public string? Metadata { get; }

        /// <summary>不带 metadata 的重载，转发给四参构造。</summary>
        internal CourierActionInfo(string channel, string action, string summary)
            : this(channel, action, summary, null)
        {
        }

        /// <summary>带 metadata 的构造；<paramref name="metadata"/> 是单行紧凑 JSON 原文，null 表示没有。</summary>
        internal CourierActionInfo(string channel, string action, string summary, string? metadata)
        {
            Channel = channel;
            Action = action;
            Summary = summary;
            Metadata = metadata;
        }
    }

    /// <summary>endpoint.json 里声明的一条状态（SPEC §5、§14）。只是名字与说明，值在 state/ 下按需读取。</summary>
    public sealed class CourierStateInfo
    {
        /// <summary>状态名，满足 SPEC §4.3，对应 <c>state/&lt;name&gt;.json</c>。</summary>
        public string Name { get; }

        /// <summary>面向人和工具的简短说明。</summary>
        public string Summary { get; }

        /// <summary>声明方附加的元数据（SPEC §14），单行紧凑 JSON 对象原文；未提供时为 null。</summary>
        public string? Metadata { get; }

        /// <summary>不带 metadata 的重载，转发给三参构造。</summary>
        internal CourierStateInfo(string name, string summary)
            : this(name, summary, null)
        {
        }

        /// <summary>带 metadata 的构造；null 表示没有，读方据此省略字段而不是输出 null。</summary>
        internal CourierStateInfo(string name, string summary, string? metadata)
        {
            Name = name;
            Summary = summary;
            Metadata = metadata;
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

        /// <summary>本次运行的会话标识（SPEC §5）；读不到时为空串。变了即代表端点是新的一轮运行。</summary>
        public string SessionId { get; }

        /// <summary>已注册的能力。</summary>
        public IReadOnlyList<CourierActionInfo> Actions { get; }

        /// <summary>已声明的状态名；没有声明时为空列表。</summary>
        public IReadOnlyList<CourierStateInfo> States { get; }

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

        /// <summary>
        /// 端点级元数据（SPEC §5），单行紧凑 JSON 对象原文；未提供时为 null。
        /// 与 <see cref="CourierActionInfo.Metadata"/> 同一约定：协议不解释，读方原样转述，无则省略字段。
        /// </summary>
        public string? Metadata { get; }

        /// <summary>
        /// 端点自述实现了哪些**可选能力**（SPEC §5）：<c>state</c>、<c>cancels</c>、<c>progress</c>、
        /// <c>registry</c>、<c>host-lock</c> 等。没有声明时是空列表。
        /// <para>
        /// 这是给读方判断"要不要降级"用的：§21 要求读方在对方没实现某个可选能力时行为正确退化，
        /// 而在此之前读方无从判断。声明只是自述，读方 MUST NOT 因为缺声明就拒绝投递——
        /// 能力声明与 metadata 一样，不参与路由、不改变原因码。
        /// </para>
        /// </summary>
        public IReadOnlyList<string> Capabilities { get; }

        /// <summary>最简重载：没有 metadata、没有能力声明，转发给全参构造。</summary>
        internal CourierEndpointInfo(
            string endpointId,
            string description,
            string sessionId,
            int processId,
            DateTime startedAtUtc,
            int protocolVersion,
            List<CourierActionInfo> actions,
            List<CourierStateInfo> states,
            long? heartbeatAgeMs)
            : this(endpointId, description, sessionId, processId, startedAtUtc, protocolVersion, actions, states, heartbeatAgeMs, null, null)
        {
        }

        /// <summary>带 metadata、没有能力声明，转发给全参构造。</summary>
        internal CourierEndpointInfo(
            string endpointId,
            string description,
            string sessionId,
            int processId,
            DateTime startedAtUtc,
            int protocolVersion,
            List<CourierActionInfo> actions,
            List<CourierStateInfo> states,
            long? heartbeatAgeMs,
            string? metadata)
            : this(endpointId, description, sessionId, processId, startedAtUtc, protocolVersion, actions, states, heartbeatAgeMs, metadata, null)
        {
        }

        /// <summary>全参构造；只有 <see cref="CourierEndpointReader"/> 会用它。</summary>
        internal CourierEndpointInfo(
            string endpointId,
            string description,
            string sessionId,
            int processId,
            DateTime startedAtUtc,
            int protocolVersion,
            List<CourierActionInfo> actions,
            List<CourierStateInfo> states,
            long? heartbeatAgeMs,
            string? metadata,
            List<string>? capabilities)
        {
            EndpointId = endpointId;
            Description = description;
            SessionId = sessionId;
            ProcessId = processId;
            StartedAtUtc = startedAtUtc;
            ProtocolVersion = protocolVersion;
            Actions = new ReadOnlyCollection<CourierActionInfo>(actions);
            States = new ReadOnlyCollection<CourierStateInfo>(states);
            HeartbeatAgeMs = heartbeatAgeMs;
            Metadata = metadata;
            Capabilities = new ReadOnlyCollection<string>(capabilities ?? new List<string>());
        }
    }

    /// <summary>commands/ 下一条命令的解析结果（SPEC §9）。任一字段缺失或非法都不得执行。</summary>
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
        /// 解析命令文件。requestId 必须与文件名一致（SPEC §9），因此文件名由调用方传入比对。
        /// 任何缺失或非法都返回 null，由调用方进 deadletter（SPEC §12）。
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

            // payload 是不透明字符串，可以为空串；非字符串（含 null）说明发送方违反了 SPEC §9。
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

        /// <summary>按 SPEC §9 的字段顺序序列化，供测试与 CLI 构造命令使用。</summary>
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

        /// <summary>按 SPEC §9 组装一条命令；issuedAtUtc 与 deadlineUtc 由调用方给定，不在这里取"现在"。</summary>
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
    /// claims/ 下的租约（SPEC §10.1）。attempts 是重试计数的唯一存放处——命令文件 MUST NOT 被改写。
    /// expiresAtUtc 是租约到期时刻，不是认领时刻：持有者在执行期间持续把它往后推，
    /// 别的服务循环只在它过期之后才接管。
    /// </summary>
    internal sealed class CourierClaim
    {
        internal string Owner { get; private set; } = string.Empty;
        internal DateTime ExpiresAtUtc { get; private set; }
        internal int Attempts { get; private set; }

        /// <summary>解析 claims/ 里的租约文件；expiresAtUtc 不可解析或 attempts 越界时返回 null。</summary>
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

        /// <summary>序列化成单行 JSON，字段顺序固定为 owner/expiresAtUtc/attempts（SPEC §10）。</summary>
        internal string ToJson()
        {
            return new CourierJsonObject()
                .AddString("owner", Owner)
                .AddString("expiresAtUtc", CourierProtocol.FormatUtc(ExpiresAtUtc))
                .AddNumber("attempts", Attempts)
                .Build();
        }

        /// <summary>新建一份租约；attempts 由调用方算好（首次为 1，接管后递增）。</summary>
        internal static CourierClaim Create(string owner, DateTime expiresAtUtc, int attempts)
        {
            return new CourierClaim { Owner = owner, ExpiresAtUtc = expiresAtUtc, Attempts = attempts };
        }

        /// <summary>SPEC §10.1：把租约推到 now + ClaimWindow，保留 owner 与 attempts。</summary>
        internal CourierClaim Renew(DateTime nowUtc)
        {
            return new CourierClaim { Owner = Owner, ExpiresAtUtc = nowUtc + CourierProtocol.CLAIM_WINDOW, Attempts = Attempts };
        }
    }

    /// <summary>deadletter/ 下的失败证据（SPEC §12）。写入前必须先落 results 再搬命令，避免命令凭空消失。</summary>
    internal sealed class CourierDeadletter
    {
        internal string RequestId { get; private set; } = string.Empty;
        internal string Reason { get; private set; } = string.Empty;
        internal string Detail { get; private set; } = string.Empty;
        internal DateTime FailedAtUtc { get; private set; }
        internal int Attempts { get; private set; }

        /// <summary>解析 deadletter/ 里的记录；requestId 与 reason 缺失即视为损坏。</summary>
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

        /// <summary>序列化成单行 JSON，字段顺序固定为 requestId/reason/detail/failedAtUtc/attempts（SPEC §12）。</summary>
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

        /// <summary>新建一条死信；<paramref name="detail"/> 为 null 时按空串写。</summary>
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
