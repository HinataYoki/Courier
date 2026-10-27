using System.Collections.Generic;

namespace Courier.Cli
{
    /// <summary>
    /// `courier state &lt;endpointId&gt; &lt;name&gt;`：读端点发布的状态最新值（SPEC §14）。
    /// <para>
    /// 这条命令没有副作用、不等待、不投递任何东西。文件不存在就是"还没发布过"，
    /// 按 SPEC §3.7 那不是错误，而是这一状态还没有值；但**端点根本不存在**是另一回事，
    /// 它说明 endpointId 写错了或那个软件从没启动过，必须报错而不是伪装成"未发布"。
    /// </para>
    /// </summary>
    internal static class CliStateCommand
    {
        /// <summary>执行命令。</summary>
        /// <param name="arguments">已解析的命令行。</param>
        /// <param name="root">已解析的协议根目录。</param>
        /// <param name="layout">root 下生效的目录布局。</param>
        /// <returns>退出码。</returns>
        internal static int Run(CliArguments arguments, string root, CourierLayout layout)
        {
            string endpointId = arguments.Positionals[0];
            string name = arguments.Positionals[1];
            if (!TryValidateTarget(arguments, root, layout, endpointId, name)) return CliExitCode.USAGE;

            string? text;
            if (!CliDirectory.TryReadState(root, layout, endpointId, name, out text))
            {
                // SPEC §3.7：读不到不是错误。字段集合与已发布时保持一致，只把 published 写成 false，
                // 读方因此可以只按 published 分支，而不用猜"哪些字段这次会缺"。
                CliOutput.WriteLine(arguments.Json ? NotPublished(endpointId, name) : endpointId + " has not published state \"" + name + "\"");
                return CliExitCode.OK;
            }

            // --raw 保留旧行为：stdout 就是状态文件的原文，不再包一层。
            if (arguments.Raw) CliOutput.WriteLine(text ?? string.Empty);
            else if (arguments.Json) CliOutput.WriteLine(Wrap(endpointId, name, text));
            else CliOutput.WriteLine(text ?? string.Empty);
            return CliExitCode.OK;
        }

        /// <summary>
        /// 校验端点标识与状态名，并确认端点确实登记过。
        /// </summary>
        /// <remarks>
        /// "端点不存在"与"端点还没发布这个状态"以前都落到 <c>published:false</c> 上，两者不可区分：
        /// 前者是调用方写错了名字，后者是正常的"还没有值"（SPEC §3.7 明确说那不是错误）。
        /// </remarks>
        /// <param name="arguments">已解析的命令行（用于 <c>--json</c> 分支与错误文案）。</param>
        /// <param name="root">协议根目录。</param>
        /// <param name="layout">布局。</param>
        /// <param name="endpointId">端点标识。</param>
        /// <param name="name">状态名。</param>
        /// <returns>两者都合法且端点已登记时为 true。</returns>
        private static bool TryValidateTarget(CliArguments arguments, string root, CourierLayout layout, string endpointId, string name)
        {
            // SPEC §4.1：endpointId 是唯一的路径拼接入口，非法值绝不能拼进路径。
            if (!CliIdentifiers.RequireEndpointId(arguments, endpointId)) return false;

            if (!CourierProtocol.IsValidName(name))
            {
                // SPEC §4.3：状态名同样是路径片段，先校验再拼。
                CliOutput.WriteError(arguments.Json, "invalid-state-name", "\"" + name + "\" is not a valid state name (must match ^[A-Za-z0-9_.-]{1,64}$)");
                return false;
            }

            if (CliDirectory.HasRegisteredEndpoint(root, layout, endpointId)) return true;

            CliOutput.WriteError(
                arguments.Json,
                "endpoint-not-found",
                "no endpoint named \"" + endpointId + "\" under " + CliDirectory.EndpointsDirectory(root, layout)
                    + " (it has never started, or its endpoint.json is gone)");
            return false;
        }

        /// <summary>
        /// "这个端点在册，但还没发布过这个状态"的结构化输出。
        /// 与已发布时同一套字段名，只把 <c>published</c> 置 false，不给读方留第二种 schema。
        /// </summary>
        /// <param name="endpointId">端点标识（已校验）。</param>
        /// <param name="name">状态名（已校验）。</param>
        /// <returns>一行紧凑 JSON。</returns>
        private static string NotPublished(string endpointId, string name)
        {
            return new CourierJsonObject()
                .AddString("endpointId", endpointId)
                .AddString("name", name)
                .AddBoolean("published", false)
                .Build();
        }

        /// <summary>
        /// 把 <c>state/&lt;name&gt;.json</c> 的原文包成一条带元信息的 JSON（SPEC §14）。
        /// <para>
        /// 包装层只补"这是哪个端点的哪个状态、第几版、什么时候写的"，<c>payload</c> 仍然是原文里的
        /// 那一块——不重新解释，也不因为读不懂就丢掉整条状态：文件能读但字段不全时，
        /// 缺的字段照样省略，已经能读出来的部分一字不改地输出。
        /// </para>
        /// </summary>
        /// <param name="endpointId">端点标识（已经过 SPEC §4.1 校验）。</param>
        /// <param name="name">状态名（已经过 SPEC §4.3 校验）。</param>
        /// <param name="text">状态文件原文。</param>
        /// <returns>一行紧凑 JSON。</returns>
        private static string Wrap(string endpointId, string name, string? text)
        {
            Dictionary<string, object?>? document = null;
            // 解析失败时 document 保持 null：下面每一项都会退化成"省略"，而不是抛异常或输出半截。
            CourierJson.TryParseObject(text, out document);

            // published 在两种情况下都出现（这里 true，未发布时 false），读方只需看这一个字段，
            // 不必靠"有没有 payload"去猜这一份是不是空的。
            var builder = new CourierJsonObject()
                .AddString("endpointId", endpointId)
                .AddString("name", name)
                .AddBoolean("published", true);
            string? updatedAtUtc = CourierJson.GetString(document, "updatedAtUtc");
            if (updatedAtUtc != null) builder.AddString("updatedAtUtc", updatedAtUtc);

            long? revision = CourierJson.GetInteger(document, "revision");
            if (revision != null) builder.AddNumber("revision", revision.Value);
            string? sessionId = CourierJson.GetString(document, "sessionId");
            if (sessionId != null) builder.AddString("sessionId", sessionId);

            if (document != null && document.TryGetValue("payload", out object? payload) && payload != null)
            {
                // payload 是任意 JSON（SPEC §14），原样嵌入而不是当成字符串再转义一次。
                builder.AddRaw("payload", CourierJson.Write(payload));
            }
            else
            {
                builder.AddRaw("payload", "null");
            }

            return builder.Build();
        }
    }
}
