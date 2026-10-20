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
        /// <returns>退出码。</returns>
        internal static int Run(CliArguments arguments, string root)
        {
            IReadOnlyList<CourierEndpointInfo> endpoints = CliDirectory.ReadEndpoints(root);

            if (arguments.Json)
            {
                var items = new List<string>(endpoints.Count);
                for (int index = 0; index < endpoints.Count; index++)
                {
                    CourierEndpointInfo endpoint = endpoints[index];
                    items.Add(new CourierJsonObject()
                        .AddString("endpointId", endpoint.EndpointId)
                        .AddString("description", endpoint.Description)
                        .AddString("sessionId", endpoint.SessionId)
                        .AddNumber("processId", endpoint.ProcessId)
                        .AddString("startedAtUtc", CourierProtocol.FormatUtc(endpoint.StartedAtUtc))
                        .AddNumber("protocolVersion", endpoint.ProtocolVersion)
                        .AddString("state", endpoint.State)
                        .AddRaw("heartbeatAgeMs", endpoint.HeartbeatAgeMs == null ? "null" : CliJson.Number(endpoint.HeartbeatAgeMs.Value))
                        .AddRaw("actions", CliJson.Actions(endpoint.Actions))
                        .AddRaw("states", CliJson.States(endpoint.States))
                        .Build());
                }

                CliOutput.WriteLine(new CourierJsonObject().AddRaw("endpoints", CourierJson.Array(items)).Build());
                return CliExitCode.Ok;
            }

            if (endpoints.Count == 0)
            {
                Console.Error.Write("courier: no endpoints are registered under " + Path.Combine(root, CourierProtocol.EndpointsDirectory) + "\n");
                return CliExitCode.Ok;
            }

            for (int index = 0; index < endpoints.Count; index++)
            {
                CourierEndpointInfo endpoint = endpoints[index];

                // 版式：id 左对齐到固定列宽，再空一格写括号里的 description
                // （description 为空时整段省略），后面是 state + 心跳年龄。
                // 列宽 8 + 分隔空格 = "app-a" 后面 4 个空格，固定列宽让两次输出可以直接 diff。
                var line = new StringBuilder(64);
                line.Append(endpoint.EndpointId);
                if (!string.IsNullOrEmpty(endpoint.Description))
                {
                    int padding = CliDirectory.EndpointIdColumnWidth - endpoint.EndpointId.Length;
                    if (padding < 0) padding = 0;
                    line.Append(' ', padding);
                    line.Append(' ').Append('(').Append(endpoint.Description).Append(')');
                }

                line.Append("  ").Append(endpoint.State).Append(", heartbeat ");
                line.Append(endpoint.HeartbeatAgeMs == null ? "unknown" : CliOutput.FormatSeconds(endpoint.HeartbeatAgeMs.Value) + " ago");
                CliOutput.WriteLine(line.ToString());
            }

            return CliExitCode.Ok;
        }
    }
}
