using System;
using System.Threading;
using System.Threading.Tasks;

namespace Courier
{
    /// <summary>
    /// <see cref="CourierNode"/> 的能力表注册（SPEC §5）：actions、states、capabilities 在第一次 Tick 前写完即冻结。
    /// </summary>
    public sealed partial class CourierNode
    {
        /// <summary>注册一个同步能力。同一个 (channel, action) 在一个端点上只能注册一次，重复注册是编程错误（SPEC §5）。</summary>
        /// <param name="channel">信道名，须满足 SPEC §4.3。</param>
        /// <param name="action">动作名，须满足 SPEC §4.3。</param>
        /// <param name="summary">面向人和工具的说明，写入 endpoint.json。</param>
        /// <param name="handler">处理函数；<c>token</c> 在投递方请求取消时被触发（SPEC §13.1）。</param>
        /// <exception cref="ArgumentException">channel 或 action 非法，或 (channel, action) 已注册。</exception>
        /// <exception cref="ArgumentNullException">handler 为 null。</exception>
        /// <exception cref="InvalidOperationException">第一次 Tick 之后才注册（SPEC §5：能力表已冻结）。</exception>
        public void Handle(string channel, string action, string summary, Func<string, CancellationToken, string> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            Register(channel, action, summary, (payload, token) => Task.FromResult(handler(payload, token)));
        }

        /// <summary>注册一个忽略取消的同步能力。仅用于不需要响应取消的短 handler。</summary>
        /// <param name="channel">信道名，须满足 SPEC §4.3。</param>
        /// <param name="action">动作名，须满足 SPEC §4.3。</param>
        /// <param name="summary">面向人和工具的说明，写入 endpoint.json。</param>
        /// <param name="handler">处理函数，入参与返回值都是不透明字符串（SPEC §9）。</param>
        public void Handle(string channel, string action, string summary, Func<string, string> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            Register(channel, action, summary, (payload, token) => Task.FromResult(handler(payload)));
        }

        /// <summary>
        /// 注册一个同步能力，并附带宿主的元数据（SPEC §5.1）。metadata 是自由 JSON 对象，
        /// 协议不解释它——宿主播 <c>risk</c>、<c>inputSchema</c>、<c>version</c> 这类给工具读的描述，
        /// 不认识的读方原样转述或忽略。为 null 或空白时字段整个缺席（MUST NOT 写 null）。
        /// </summary>
        /// <param name="channel">信道名，须满足 SPEC §4.3。</param>
        /// <param name="action">动作名，须满足 SPEC §4.3。</param>
        /// <param name="summary">面向人和工具的说明，写入 endpoint.json。</param>
        /// <param name="metadata">单行 JSON 对象原文；非对象或不可解析时抛出 <see cref="ArgumentException"/>。</param>
        /// <param name="handler">处理函数。</param>
        /// <exception cref="ArgumentException">名字非法、(channel, action) 已注册，或 metadata 不是 JSON 对象。</exception>
        /// <exception cref="InvalidOperationException">第一次 Tick 之后才注册（SPEC §5）。</exception>
        public void Handle(string channel, string action, string summary, string? metadata, Func<string, string> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            RegisterWithMetadata(channel, action, summary, metadata, (payload, token) => Task.FromResult(handler(payload)));
        }

        /// <summary>注册一个带元数据的同步能力，并让 handler 看见 <see cref="CourierCall"/>（SPEC §5.1、§9）。</summary>
        /// <param name="channel">信道名，须满足 SPEC §4.3。</param>
        /// <param name="action">动作名，须满足 SPEC §4.3。</param>
        /// <param name="summary">面向人和工具的说明。</param>
        /// <param name="metadata">单行 JSON 对象原文，或 null。</param>
        /// <param name="handler">处理函数，入参是本次调用的上下文。</param>
        public void Handle(string channel, string action, string summary, string? metadata, Func<CourierCall, string> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            RegisterCallAware(channel, action, summary, metadata, (call, token) => Task.FromResult(handler(call)));
        }

        /// <summary>注册一个带元数据的异步能力（SPEC §5.1）。长命宿主 SHOULD 使用它。</summary>
        /// <param name="channel">信道名，须满足 SPEC §4.3。</param>
        /// <param name="action">动作名，须满足 SPEC §4.3。</param>
        /// <param name="summary">面向人和工具的说明。</param>
        /// <param name="metadata">单行 JSON 对象原文，或 null。</param>
        /// <param name="handler">返回 <see cref="Task{String}"/> 的处理函数。</param>
        public void HandleAsync(string channel, string action, string summary, string? metadata, Func<string, Task<string>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            RegisterWithMetadata(channel, action, summary, metadata, (payload, token) => handler(payload));
        }

        /// <summary>注册一个带元数据、且能看见 <see cref="CourierCall"/> 的异步能力（SPEC §5.1、§9）。</summary>
        /// <param name="channel">信道名，须满足 SPEC §4.3。</param>
        /// <param name="action">动作名，须满足 SPEC §4.3。</param>
        /// <param name="summary">面向人和工具的说明。</param>
        /// <param name="metadata">单行 JSON 对象原文，或 null。</param>
        /// <param name="handler">返回 <see cref="Task{String}"/> 的处理函数，入参是上下文与取消令牌。</param>
        public void HandleAsync(string channel, string action, string summary, string? metadata, Func<CourierCall, CancellationToken, Task<string>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            RegisterCallAware(channel, action, summary, metadata, handler);
        }

        /// <summary>
        /// 旧式注册路径（内部）：handler 只看得到 payload 字符串，看不到 <see cref="CourierCall"/>。
        /// 与 <see cref="RegisterCallAware"/> 共用同一套校验与冻结逻辑，差别只在装进表的委托。
        /// </summary>
        private void RegisterWithMetadata(string channel, string action, string summary, string? metadata, Func<string, CancellationToken, Task<string>> handler)
        {
            ValidateRegistration(channel, action);
            mHandlers.Add(RegistrationKey(channel, action), new HandlerEntry(channel, action, summary ?? string.Empty, NormalizeMetadata(metadata, nameof(metadata)), handler));
            WriteEndpointJson();
        }

        /// <summary>注册一个异步能力。长命宿主 SHOULD 使用它，handler 与 Tick 并发推进（SPEC §16.1）。</summary>
        /// <param name="channel">信道名，须满足 SPEC §4.3。</param>
        /// <param name="action">动作名，须满足 SPEC §4.3。</param>
        /// <param name="summary">面向人和工具的说明，写入 endpoint.json。</param>
        /// <param name="handler">返回 <see cref="Task{String}"/> 的处理函数；<c>token</c> 在取消请求到达时触发。</param>
        public void HandleAsync(string channel, string action, string summary, Func<string, CancellationToken, Task<string>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            Register(channel, action, summary, handler);
        }

        /// <summary>注册一个忽略取消的异步能力。</summary>
        /// <param name="channel">信道名，须满足 SPEC §4.3。</param>
        /// <param name="action">动作名，须满足 SPEC §4.3。</param>
        /// <param name="summary">面向人和工具的说明，写入 endpoint.json。</param>
        /// <param name="handler">返回 <see cref="Task{String}"/> 的处理函数。</param>
        public void HandleAsync(string channel, string action, string summary, Func<string, Task<string>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            Register(channel, action, summary, (payload, token) => handler(payload));
        }

        /// <summary>
        /// 注册一个能看见 <see cref="CourierCall"/> 的同步能力（SPEC §9）。上下文里带着 <c>requestId</c>、
        /// 投递方与执行次数，handler 因此能把自己的日志与调用方对齐。
        /// <para>
        /// <b>投递是 at-least-once。</b>同一个 <c>requestId</c> 可能到达不止一次（服务者崩溃、租约到期后被接管），
        /// 需要恰好一次副作用的 handler MUST 自己按 <see cref="CourierCall.RequestId"/> 去重。
        /// </para>
        /// </summary>
        /// <param name="channel">信道名，须满足 SPEC §4.3。</param>
        /// <param name="action">动作名，须满足 SPEC §4.3。</param>
        /// <param name="summary">面向人和工具的说明，写入 endpoint.json。</param>
        /// <param name="handler">处理函数，入参是本次调用的上下文。</param>
        /// <param name="withContext">
        /// 仅为消除与 <see cref="Handle(string, string, string, Func{string, string})"/> 的重载二义性而存在，
        /// MUST 保持默认值——传 false 没有任何额外含义，两个重载都会注册带上下文的 handler。
        /// </param>
        /// <exception cref="ArgumentException">channel 或 action 非法，或 (channel, action) 已注册。</exception>
        /// <exception cref="ArgumentNullException">handler 为 null。</exception>
        /// <exception cref="InvalidOperationException">第一次 Tick 之后才注册（SPEC §5：能力表已冻结）。</exception>
        public void Handle(string channel, string action, string summary, Func<CourierCall, string> handler, bool withContext = true)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            RegisterCallAware(channel, action, summary, null, (call, token) => Task.FromResult(handler(call)));
        }

        /// <summary>注册一个能看见 <see cref="CourierCall"/> 且响应取消的同步能力（SPEC §9、§13.1）。</summary>
        /// <param name="channel">信道名，须满足 SPEC §4.3。</param>
        /// <param name="action">动作名，须满足 SPEC §4.3。</param>
        /// <param name="summary">面向人和工具的说明。</param>
        /// <param name="handler">处理函数，入参是本次调用的上下文与取消令牌。</param>
        /// <param name="withContext">只为消除重载二义性而存在的尾参，见另一个重载的说明。</param>
        public void Handle(string channel, string action, string summary, Func<CourierCall, CancellationToken, string> handler, bool withContext = true)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            RegisterCallAware(channel, action, summary, null, (call, token) => Task.FromResult(handler(call, token)));
        }

        /// <summary>注册一个能看见 <see cref="CourierCall"/> 的异步能力（SPEC §9）。长命宿主 SHOULD 使用它。</summary>
        /// <param name="channel">信道名，须满足 SPEC §4.3。</param>
        /// <param name="action">动作名，须满足 SPEC §4.3。</param>
        /// <param name="summary">面向人和工具的说明。</param>
        /// <param name="handler">返回 <see cref="Task{String}"/> 的处理函数，入参是本次调用的上下文。</param>
        /// <param name="withContext">只为消除重载二义性而存在的尾参。</param>
        public void HandleAsync(string channel, string action, string summary, Func<CourierCall, Task<string>> handler, bool withContext = true)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            RegisterCallAware(channel, action, summary, null, (call, token) => handler(call));
        }

        /// <summary>注册一个能看见 <see cref="CourierCall"/> 且响应取消的异步能力（SPEC §9、§13.1）。</summary>
        /// <param name="channel">信道名，须满足 SPEC §4.3。</param>
        /// <param name="action">动作名，须满足 SPEC §4.3。</param>
        /// <param name="summary">面向人和工具的说明。</param>
        /// <param name="handler">返回 <see cref="Task{String}"/> 的处理函数，入参是上下文与取消令牌。</param>
        /// <param name="withContext">只为消除重载二义性而存在的尾参。</param>
        public void HandleAsync(string channel, string action, string summary, Func<CourierCall, CancellationToken, Task<string>> handler, bool withContext = true)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            RegisterCallAware(channel, action, summary, null, handler);
        }

        /// <summary>
        /// 注册一个带上下文的处理函数（内部入口，与旧式注册共用同一套校验与冻结逻辑）。
        /// 方法名与 <c>Register(Func&lt;string,…&gt;)</c> 区分开：两个同名重载会让每个 lambda 都变成二义调用。
        /// </summary>
        private void RegisterCallAware(string channel, string action, string summary, string? metadata, Func<CourierCall, CancellationToken, Task<string>> handler)
        {
            ValidateRegistration(channel, action);
            mHandlers.Add(RegistrationKey(channel, action), new HandlerEntry(channel, action, summary ?? string.Empty, NormalizeMetadata(metadata, nameof(metadata)), handler));
            WriteEndpointJson();
        }

        /// <summary>声明一个状态名，写入 endpoint.json 的 states 数组（SPEC §14）。</summary>
        /// <param name="name">状态名，须满足 SPEC §4.3。</param>
        /// <param name="summary">面向人和工具的说明。</param>
        /// <param name="metadata">
        /// 可选的自由 JSON 对象，随声明写进 endpoint.json 的 <c>metadata</c>（SPEC §5.1）。
        /// 宿主播 <c>version</c>、<c>schema</c> 这类给工具看的附加描述；客户端不认识就忽略。
        /// </param>
        /// <exception cref="ArgumentException">名字非法，或同一个名字已声明过。</exception>
        /// <exception cref="InvalidOperationException">第一次 Tick 之后才声明（SPEC §5：能力表已冻结）。</exception>
        public void DeclareState(string name, string summary, string? metadata)
        {
            ThrowIfDisposed();
            // SPEC §5：states 与 actions 同属 endpoint.json，冻结时机一致。
            if (mStarted) throw new InvalidOperationException("capabilities are frozen after the first Tick (SPEC §5)");
            if (!CourierProtocol.IsValidName(name)) throw new ArgumentException("invalid state name: must match ^[A-Za-z0-9_.-]{1,64}$", nameof(name));
            for (int index = 0; index < mStates.Count; index++)
            {
                if (string.Equals(mStates[index].Name, name, StringComparison.Ordinal))
                {
                    throw new ArgumentException("duplicate state declaration for " + name, nameof(name));
                }
            }

            if (mStates.Count >= CourierProtocol.MAX_STATE_NAMES)
            {
                throw new InvalidOperationException("too many state names (MAX_STATE_NAMES = " + CourierProtocol.MAX_STATE_NAMES + ")");
            }

            mStates.Add(new StateEntry(name, summary ?? string.Empty, NormalizeMetadata(metadata, nameof(metadata))));
            WriteEndpointJson();
        }

        /// <summary>声明一个不带 metadata 的状态名（等价于 <c>metadata</c> 为 null 的重载）。</summary>
        /// <param name="name">状态名，须满足 SPEC §4.3。</param>
        /// <param name="summary">面向人和工具的说明。</param>
        public void DeclareState(string name, string summary)
        {
            DeclareState(name, summary, null);
        }

        /// <summary>
        /// 声明本端点实现了某个**可选能力**（SPEC §5）。可选能力是 <c>state</c>、<c>cancels</c>、
        /// <c>progress</c>、<c>registry</c>、<c>host-lock</c> 这类「不做也能互操作，但读方需要知道」的东西。
        /// <para>
        /// 声明的意义是让 §21 的「读方在对方没实现可选能力时行为正确退化」变成**可判定**的：没有这份自述，
        /// 读方无从区分「对端没实现」与「对端实现了但这次没用上」。
        /// </para>
        /// </summary>
        /// <param name="name">能力名，须满足 SPEC §4.3 的名字规则（与 channel / action / state 同一套）。</param>
        /// <exception cref="ArgumentException">名字非法，或同一个能力已声明过。</exception>
        /// <exception cref="InvalidOperationException">
        /// 第一次 Tick 之后才声明（SPEC §5：能力表已冻结），或声明的能力数量超过上限。
        /// </exception>
        public void DeclareCapability(string name)
        {
            ThrowIfDisposed();
            // SPEC §5：capabilities 与 actions / states 同属 endpoint.json，冻结时机完全一致。
            if (mStarted) throw new InvalidOperationException("capabilities are frozen after the first Tick (SPEC §5)");
            if (!CourierProtocol.IsValidName(name)) throw new ArgumentException("invalid capability name: must match ^[A-Za-z0-9_.-]{1,64}$", nameof(name));
            for (int index = 0; index < mCapabilities.Count; index++)
            {
                if (string.Equals(mCapabilities[index], name, StringComparison.Ordinal))
                {
                    throw new ArgumentException("duplicate capability declaration for " + name, nameof(name));
                }
            }

            if (mCapabilities.Count >= CourierProtocol.MAX_CAPABILITIES)
            {
                throw new InvalidOperationException("too many capability declarations (MAX_CAPABILITIES = " + CourierProtocol.MAX_CAPABILITIES + ")");
            }

            mCapabilities.Add(name);
            WriteEndpointJson();
        }

        /// <summary>
        /// 校验并归一化 metadata：null 或空白等同于"没有 metadata"（SPEC §5.1 要求缺少时才省略字段，
        /// 而不是写一个 null）；不是 JSON 对象就当场报错——坏 metadata 不该在 endpoint.json 里留下读不懂的一块。
        /// </summary>
        private static string? NormalizeMetadata(string? metadata, string parameterName)
        {
            if (metadata == null) return null;
            if (metadata.Trim().Length == 0) return null;
            if (!CourierJson.TryParseObject(metadata, out _))
            {
                throw new ArgumentException("metadata must be a JSON object (SPEC §5.1)", parameterName);
            }

            return metadata.Trim();
        }

        /// <summary>不带 metadata 的旧式注册入口（内部）：等价于 metadata 为 null 的那条路径。</summary>
        private void Register(string channel, string action, string summary, Func<string, CancellationToken, Task<string>> handler)
        {
            RegisterWithMetadata(channel, action, summary, null, handler);
        }

        /// <summary>
        /// 三条注册路径共用的校验（SPEC §5）：先在第一次 Tick 前注册，名字合法，且 (channel, action) 未被占用。
        /// 重复注册是编程错误，MUST 在注册时报错，不能悄悄覆盖。
        /// </summary>
        /// <param name="channel">信道名，须满足 SPEC §4.3。</param>
        /// <param name="action">动作名，须满足 SPEC §4.3。</param>
        /// <exception cref="ArgumentException">名字非法，或 (channel, action) 已注册。</exception>
        /// <exception cref="InvalidOperationException">第一次 Tick 之后才注册（SPEC §5：能力表已冻结）。</exception>
        private void ValidateRegistration(string channel, string action)
        {
            ThrowIfDisposed();
            // SPEC §5：能力表在第一次 Tick 之前写完即冻结，运行期改它等于让"这个端点能做什么"随时间变化。
            if (mStarted) throw new InvalidOperationException("capabilities are frozen after the first Tick (SPEC §5)");
            if (!CourierProtocol.IsValidName(channel)) throw new ArgumentException("invalid channel: must match ^[A-Za-z0-9_.-]{1,64}$", nameof(channel));
            if (!CourierProtocol.IsValidName(action)) throw new ArgumentException("invalid action: must match ^[A-Za-z0-9_.-]{1,64}$", nameof(action));
            if (mHandlers.ContainsKey(RegistrationKey(channel, action)))
            {
                throw new ArgumentException("duplicate registration for " + channel + "/" + action, nameof(action));
            }
        }

        /// <summary>
        /// 能力表的键：(channel, action) 用 NUL 拼接。NUL 不在两者各自的合法字符集里（SPEC §4.3），
        /// 因此拼接不会有歧义——<c>a/bc</c> 与 <c>ab/c</c> 不会撞到同一个键。
        /// </summary>
        private static string RegistrationKey(string channel, string action)
        {
            return channel + "\u0000" + action;
        }
    }
}
