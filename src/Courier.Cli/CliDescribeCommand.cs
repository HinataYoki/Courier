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
        /// <returns>退出码。</returns>
        internal static int Run(CliArguments arguments, string root)
        {
            string endpointId = arguments.Positionals[0];
            if (!CourierProtocol.IsValidEndpointId(endpointId))
            {
                // SPEC §4.1：endpointId 是唯一的路径拼接入口，非法值绝不能拼进路径。
                CliOutput.WriteError(arguments.Json, "invalid-endpoint-id", "\"" + endpointId + "\" is not a valid endpoint id (must match ^[a-z0-9]([a-z0-9-]{0,62}[a-z0-9])?$)");
                return CliExitCode.Failed;
            }

            CourierEndpointInfo? endpoint = CliDirectory.ReadEndpoint(root, endpointId);
            if (endpoint == null)
            {
                CliOutput.WriteError(arguments.Json, "endpoint-not-found", "no endpoint named \"" + endpointId + "\" under " + CliDirectory.EndpointsDirectory(root));
                return CliExitCode.Failed;
            }

            if (arguments.Json)
            {
                CliOutput.WriteLine(new CourierJsonObject()
                    .AddString("endpointId", endpoint.EndpointId)
                    .AddRaw("actions", CliJson.Actions(endpoint.Actions))
                    .Build());
                return CliExitCode.Ok;
            }

            for (int index = 0; index < endpoint.Actions.Count; index++)
            {
                CourierActionInfo action = endpoint.Actions[index];
                CliOutput.WriteLine(action.Channel + "/" + action.Action + "  " + action.Summary);
            }

            return CliExitCode.Ok;
        }
    }
}
