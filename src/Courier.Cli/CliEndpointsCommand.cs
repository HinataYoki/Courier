using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Courier.Cli
{
    /// <summary>
    /// `courier endpoints`：列出全部端点与活性（SPEC §5、§6）。
    /// 只读目录，不要求任何软件在运行，也不做任何连接尝试。
    /// </summary>
    internal static class CliEndpointsCommand
    {
        /// <summary>执行命令。</summary>
        /// <param name="arguments">已解析的命令行。</param>
        /// <param name="root">已解析的协议根目录。</param>
        /// <param name="layout">root 下生效的目录布局。</param>
        /// <returns>退出码。</returns>
        internal static int Run(CliArguments arguments, string root, CourierLayout layout)
        {
            IReadOnlyList<CourierEndpointInfo> endpoints = CliDirectory.ReadEndpoints(root, layout);
            if (arguments.Json) return WriteJson(endpoints);

            if (endpoints.Count == 0)
            {
                // 报错文案带的是"已解析的实际目录"，因为布局可自定义，不能硬编码 endpoints/。
                Console.Error.Write("courier: no endpoints are registered under " + CliDirectory.EndpointsDirectory(root, layout) + "\n");
                return CliExitCode.OK;
            }

            for (int index = 0; index < endpoints.Count; index++) CliOutput.WriteLine(FormatLine(endpoints[index]));
            return CliExitCode.OK;
        }

        /// <summary>按 CLI 的 JSON 契约输出端点清单（数组字段带上 actions/states/capabilities）。</summary>
        /// <param name="endpoints">已读出的端点。</param>
        /// <returns>退出码，恒为 <see cref="CliExitCode.OK"/>。</returns>
        private static int WriteJson(IReadOnlyList<CourierEndpointInfo> endpoints)
        {
            var items = new List<string>(endpoints.Count);
            for (int index = 0; index < endpoints.Count; index++)
            {
                CourierEndpointInfo endpoint = endpoints[index];
                var item = new CourierJsonObject()
                    .AddString("endpointId", endpoint.EndpointId)
                    .AddString("description", endpoint.Description)
                    .AddString("sessionId", endpoint.SessionId)
                    .AddNumber("processId", endpoint.ProcessId)
                    .AddString("startedAtUtc", CourierProtocol.FormatUtc(endpoint.StartedAtUtc))
                    .AddNumber("protocolVersion", endpoint.ProtocolVersion)
                    .AddString("state", endpoint.State)
                    .AddRaw("heartbeatAgeMs", endpoint.HeartbeatAgeMs == null ? "null" : CliJson.Number(endpoint.HeartbeatAgeMs.Value))
                    .AddRaw("actions", CliJson.Actions(endpoint.Actions))
                    .AddRaw("states", CliJson.States(endpoint.States));
                // SPEC §5：能力自述没有就省略，与 metadata 同一规则。
                string? capabilities = CliJson.Capabilities(endpoint.Capabilities);
                if (capabilities != null) item.AddRaw("capabilities", capabilities);
                items.Add(item.Build());
            }

            CliOutput.WriteLine(new CourierJsonObject().AddRaw("endpoints", CourierJson.Array(items)).Build());
            return CliExitCode.OK;
        }

        /// <summary>
        /// 拼一行人可读输出。版式：id 左对齐到固定列宽，再空一格写括号里的 description
        /// （description 为空时整段省略），后面是 state + 心跳年龄。固定列宽让两次输出可以直接 diff。
        /// </summary>
        /// <param name="endpoint">要渲染的端点。</param>
        /// <returns>不带换行的一行文本。</returns>
        private static string FormatLine(CourierEndpointInfo endpoint)
        {
            var line = new StringBuilder(64);
            line.Append(endpoint.EndpointId);
            if (!string.IsNullOrEmpty(endpoint.Description))
            {
                int padding = CliDirectory.ENDPOINT_ID_COLUMN_WIDTH - endpoint.EndpointId.Length;
                if (padding < 0) padding = 0;
                line.Append(' ', padding);
                line.Append(' ').Append('(').Append(endpoint.Description).Append(')');
            }

            line.Append("  ").Append(endpoint.State).Append(", heartbeat ");
            line.Append(endpoint.HeartbeatAgeMs == null ? "unknown" : CliOutput.FormatSeconds(endpoint.HeartbeatAgeMs.Value) + " ago");
            return line.ToString();
        }
    }
}
