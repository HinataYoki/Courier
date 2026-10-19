using System;
using System.IO;
using System.Text;

namespace Courier.Cli
{
    /// <summary>
    /// `courier` 的入口：参数分发、root 解析、退出码映射。
    /// <para>
    /// CLI 不含业务逻辑，只做三件事：读写目录、序列化、格式化输出。
    /// CLI 也绝不开端点：短命进程开端点会在 endpoints/ 下留下"能力齐全、心跳已死"的僵尸目录，
    /// 之后每个调用方都要等到超时（SPEC §6）。所以这里没有 CourierNode，只有目录读写。
    /// </para>
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // endpoint.json 里的 summary 可以是中文。输出编码必须固定成无 BOM 的 UTF-8，
            // 否则重定向到文件或管道时会跟随控制台代码页，机器读到的字节不可预测。
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch (IOException)
            {
                // 没有控制台（服务、管道）时保持宿主默认编码，不影响协议读写。
            }

            CliArguments arguments = CliArguments.Parse(args, out string? parseError);
            if (parseError != null)
            {
                CliOutput.WriteError(arguments.Json, "invalid-argument", parseError);
                return CliExitCode.Failed;
            }

            try
            {
                return Dispatch(arguments);
            }
            catch (Exception exception)
            {
                // 协议层已经把异常挡在库内；这里兜住的是路径、权限一类的宿主异常。
                // 不打印堆栈：CLI 的输出是给人读的，堆栈对使用者没有信息量。
                CliOutput.WriteError(arguments.Json, "internal-error", "courier failed: " + exception.Message);
                return CliExitCode.Failed;
            }
        }

        private static int Dispatch(CliArguments arguments)
        {
            if (arguments.HelpRequested)
            {
                CliUsage.Write(Console.Out);
                return CliExitCode.Ok;
            }

            switch (arguments.Command)
            {
                case "help":
                    CliUsage.Write(Console.Out);
                    return CliExitCode.Ok;
                case "version":
                    return Version(arguments);
                case "endpoints":
                    return WithRoot(arguments, 0, "no arguments", CliEndpointsCommand.Run);
                case "describe":
                    return WithRoot(arguments, 1, "exactly one <endpointId>", CliDescribeCommand.Run);
                case "call":
                    return WithRoot(arguments, 2, "<endpointId> and <channel>/<action>", CliCallCommand.Run);
                case "ps":
                    return WithRoot(arguments, 0, "no arguments", CliPsCommand.Run);
                case "":
                    CliUsage.Write(Console.Error);
                    return CliExitCode.Failed;
                default:
                    CliOutput.WriteError(arguments.Json, "unknown-command", "unknown command \"" + arguments.Command + "\"");
                    if (!arguments.Json)
                    {
                        CliUsage.Write(Console.Error);
                    }
                    return CliExitCode.Failed;
            }
        }

        /// <summary>校验位置参数个数并解析 root，然后执行命令。</summary>
        private static int WithRoot(CliArguments arguments, int expectedPositionals, string expectedDescription, Func<CliArguments, string, int> run)
        {
            if (arguments.Positionals.Count != expectedPositionals)
            {
                CliOutput.WriteError(
                    arguments.Json,
                    "invalid-argument",
                    arguments.Command + " expects " + expectedDescription + " (got " + arguments.Positionals.Count + " argument(s))");
                return CliExitCode.Failed;
            }

            string? detail;
            if (!CliRoot.TryResolve(arguments.Root, out string root, out detail))
            {
                CliOutput.WriteError(arguments.Json, "root-not-found", detail ?? "no protocol root could be resolved");
                return CliExitCode.Failed;
            }

            return run(arguments, root);
        }

        private static int Version(CliArguments arguments)
        {
            if (arguments.Positionals.Count != 0)
            {
                CliOutput.WriteError(arguments.Json, "invalid-argument", "version takes no arguments");
                return CliExitCode.Failed;
            }

            if (arguments.Json)
            {
                CliOutput.WriteLine(new CourierJsonObject()
                    .AddString("name", "courier")
                    .AddString("version", CliUsage.Version)
                    .AddNumber("protocolVersion", CourierProtocol.ProtocolVersion)
                    .Build());
            }
            else
            {
                CliOutput.WriteLine("courier " + CliUsage.Version + " (protocol " + CourierProtocol.ProtocolVersion + ")");
            }
            return CliExitCode.Ok;
        }
    }
}
