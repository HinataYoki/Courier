using System;

namespace Courier.Cli
{
    /// <summary>
    /// `courier state <endpointId> <name>`：读端点发布的状态最新值（SPEC §14）。
    /// <para>
    /// 这条命令没有副作用、不等待、不投递任何东西。文件不存在就是"还没发布过"，
    /// 按 SPEC §3.7 那不是错误，而是这一状态还没有值。
    /// </para>
    /// </summary>
    internal static class CliStateCommand
    {
        /// <summary>执行命令。</summary>
        /// <param name="arguments">已解析的命令行。</param>
        /// <param name="root">已解析的协议根目录。</param>
        /// <returns>退出码。</returns>
        internal static int Run(CliArguments arguments, string root)
        {
            string endpointId = arguments.Positionals[0];
            string name = arguments.Positionals[1];
            if (!CourierProtocol.IsValidEndpointId(endpointId))
            {
                // SPEC §4.1：endpointId 是唯一的路径拼接入口，非法值绝不能拼进路径。
                CliOutput.WriteError(arguments.Json, "invalid-endpoint-id", "\"" + endpointId + "\" is not a valid endpoint id");
                return CliExitCode.Failed;
            }

            if (!CourierProtocol.IsValidName(name))
            {
                // SPEC §4.3：状态名同样是路径片段，先校验再拼。
                CliOutput.WriteError(arguments.Json, "invalid-state-name", "\"" + name + "\" is not a valid state name (must match ^[A-Za-z0-9_.-]{1,64}$)");
                return CliExitCode.Failed;
            }

            string? text;
            if (!CliDirectory.TryReadState(root, endpointId, name, out text))
            {
                // SPEC §3.7：读不到不是错误。区分"端点不存在"与"端点还没发布这个状态"对使用者有用，
                // 但前者只能靠 endpoint.json 判断，这里不做二次读盘，统一报未发布。
                if (arguments.Json)
                {
                    CliOutput.WriteLine(new CourierJsonObject()
                        .AddString("endpointId", endpointId)
                        .AddString("name", name)
                        .AddBoolean("published", false)
                        .Build());
                }
                else
                {
                    CliOutput.WriteLine(endpointId + " has not published state \"" + name + "\"");
                }

                return CliExitCode.Ok;
            }

            if (arguments.Json)
            {
                CliOutput.WriteLine(text ?? string.Empty);
                return CliExitCode.Ok;
            }

            CliOutput.WriteLine(text ?? string.Empty);
            return CliExitCode.Ok;
        }
    }
}
