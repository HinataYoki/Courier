using System;

namespace Courier
{
    /// <summary>
    /// 一次 handler 调用的上下文（SPEC §9）。
    /// <para>
    /// 只把 payload 交给 handler 时，handler 拿不到这条命令的 <c>requestId</c>，也就无法把结果与服务端
    /// 日志、端点状态以及投递方的等待关联起来。上下文把信封里已经解析出来的字段一并交过去，
    /// 让它能在自己的日志里写下"这条 requestId 我做了什么"。
    /// </para>
    /// <para>
    /// <b>投递是 at-least-once，不是 exactly-once。</b>同一个 <see cref="RequestId"/> 可能到达不止一次：
    /// 服务者崩溃、租约过期后被接管（SPEC §10.1）都会让同一条命令被执行两次。需要恰好一次副作用的
    /// handler MUST 自己按 <see cref="RequestId"/> 去重；协议不提供、也不假装提供 exactly-once。
    /// </para>
    /// </summary>
    public sealed class CourierCall
    {
        /// <summary>
        /// 请求标识，等于 <c>commands/&lt;requestId&gt;.json</c> 与 <c>results/&lt;requestId&gt;.json</c> 的文件名（去掉扩展名）。
        /// 服务端日志里写它就是与调用方、与终态证据对齐的那个键。
        /// </summary>
        public string RequestId { get; }

        /// <summary>服务者自身的端点标识（<c>endpoints/&lt;endpointId&gt;/</c>）。</summary>
        public string EndpointId { get; }

        /// <summary>信封里的 channel（SPEC §4.3）。</summary>
        public string Channel { get; }

        /// <summary>信封里的 action（SPEC §4.3）。</summary>
        public string Action { get; }

        /// <summary>
        /// 投递方声明的标识，取自信封的 <c>issuedBy</c>；信封里没有时回落到 <c>COURIER_ISSUED_BY</c>，
        /// 两者都没有则是空串。它只用于审计与回信，<b>不是凭据</b>（SPEC §9、§20）。
        /// </summary>
        public string IssuedBy { get; }

        /// <summary>投递时刻，取自信封的 <c>issuedAtUtc</c>。</summary>
        public DateTime IssuedAtUtc { get; }

        /// <summary>
        /// 这条命令最晚允许开始执行的时刻（SPEC §9.1）。超过它的命令根本不会进到 handler 里，
        /// 所以 handler 看到这个值时它一定还在有效期内。
        /// </summary>
        public DateTime DeadlineUtc { get; }

        /// <summary>
        /// 这是第几次执行（SPEC §10，从 1 开始）。大于 1 意味着上一位持有者的租约过期了、
        /// 命令被接管重做——也就是说，副作用很可能已经发生过一次。
        /// </summary>
        public int Attempt { get; }

        /// <summary>服务者取走这条命令的时刻（不是投递时刻）。</summary>
        public DateTime ReceivedAtUtc { get; }

        /// <summary>不透明载荷（SPEC §9）。Courier 从不解析它；为空命令时是空串。</summary>
        public string Payload { get; }

        /// <summary>由服务泵从已经校验过的命令与信封字段构造；载荷原样保留，COUNT 不在这里。</summary>
        /// <param name="requestId">命令文件名里的 requestId。</param>
        /// <param name="endpointId">当前端点标识。</param>
        /// <param name="channel">命令通道。</param>
        /// <param name="action">命令动作。</param>
        /// <param name="issuedBy">信封里的 issuedBy，缺省时由 <see cref="CourierNode"/> 填默认值。</param>
        /// <param name="issuedAtUtc">投递方声明的时间。</param>
        /// <param name="deadlineUtc">截断到 <c>MAX_DEADLINE</c> 之后的截止时间。</param>
        /// <param name="attempt">这是第几次尝试，接管过期租约时递增。</param>
        /// <param name="receivedAtUtc">服务者取走命令的时刻，不是投递时刻。</param>
        /// <param name="payload">不透明载荷，见 <see cref="Payload"/>。</param>
        internal CourierCall(
            string requestId,
            string endpointId,
            string channel,
            string action,
            string issuedBy,
            DateTime issuedAtUtc,
            DateTime deadlineUtc,
            int attempt,
            DateTime receivedAtUtc,
            string payload)
        {
            RequestId = requestId;
            EndpointId = endpointId;
            Channel = channel;
            Action = action;
            IssuedBy = issuedBy;
            IssuedAtUtc = issuedAtUtc;
            DeadlineUtc = deadlineUtc;
            Attempt = attempt;
            ReceivedAtUtc = receivedAtUtc;
            Payload = payload;
        }
    }
}
