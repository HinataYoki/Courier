using System;

namespace Courier.Cli
{
    /// <summary>
    /// `courier describe &lt;endpointId&gt;`：只读 endpoint.json 列出能力（SPEC §5）。
    /// <para>
    /// 这是"能力目录离线可读"的兑现点：软件没在运行、心跳已经 dead，describe 仍然必须列出能力。
    /// 因此本命令只读 endpoint.json，不读心跳、不判活、更不投递任何东西。
    /// </para>
    /// </summary>
    internal static class CliDescribeCommand
    {
        /// <summary>执行命令。</summary>
        /// <param name="arguments">已解析的命令行。</param>
        /// <param name="root">已解析的协议根目录。</param>
        /// <param name="layout">root 下生效的目录布局。</param>
        /// <returns>退出码。</returns>
        internal static int Run(CliArguments arguments, string root, CourierLayout layout)
        {
            string endpointId = arguments.Positionals[0];
            // SPEC §4.1：endpointId 是唯一的路径拼接入口，非法值绝不能拼进路径。
            if (!CliIdentifiers.RequireEndpointId(arguments, endpointId)) return CliExitCode.USAGE;

            CourierEndpointInfo? endpoint = CliDirectory.ReadEndpoint(root, layout, endpointId);
            if (endpoint == null)
            {
                CliOutput.WriteError(arguments.Json, "endpoint-not-found", "no endpoint named \"" + endpointId + "\" under " + CliDirectory.EndpointsDirectory(root, layout));
                return CliExitCode.USAGE;
            }

            if (arguments.Json)
            {
                var builder = new CourierJsonObject()
                    .AddString("endpointId", endpoint.EndpointId)
                    .AddString("sessionId", endpoint.SessionId)
                    .AddRaw("actions", CliJson.Actions(endpoint.Actions))
                    .AddRaw("states", CliJson.States(endpoint.States));
                // SPEC §5：capabilities 与 metadata 同一规则——没有就整个字段缺席。
                string? capabilities = CliJson.Capabilities(endpoint.Capabilities);
                if (capabilities != null) builder.AddRaw("capabilities", capabilities);
                // SPEC §5.1：metadata 是可选自由 JSON。没有就整个字段缺席，绝不输出 null。
                if (endpoint.Metadata != null) builder.AddRaw("metadata", endpoint.Metadata!);
                CliOutput.WriteLine(builder.Build());
                return CliExitCode.OK;
            }

            for (int index = 0; index < endpoint.Actions.Count; index++)
            {
                CourierActionInfo action = endpoint.Actions[index];
                CliOutput.WriteLine(action.Channel + "/" + action.Action + "  " + action.Summary);
            }

            // SPEC §14：状态名是声明的，值在 state/ 下按需读；describe 只报名字，不读值。
            for (int index = 0; index < endpoint.States.Count; index++)
            {
                CourierStateInfo state = endpoint.States[index];
                CliOutput.WriteLine("state " + state.Name + "  " + state.Summary);
            }

            return CliExitCode.OK;
        }
    }
}
