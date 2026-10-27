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
    /// <para>
    /// root 与布局都在这里解析一次，然后一路传给命令：命令层拿到的永远是
    /// "已校验的绝对根目录 + 已生效的布局"，不再各自猜路径。
    /// </para>
    /// <para>
    /// 退出码把"这次调用根本没发出去"和"对端执行失败"分开：参数、root、布局、端点缺失一律
    /// <see cref="CliExitCode.USAGE"/>，SPEC §11.1 的 <c>failed</c> 才是 <see cref="CliExitCode.FAILED"/>。
    /// </para>
    /// </summary>
    internal static class Program
    {
        /// <summary>
        /// 交给 <see cref="WithRoot"/> 的特殊期望值：位置参数个数由命令自己校验。
        /// 只给 gc 用（它的端点是可选的 0 或 1 个，静态数字表达不了）。
        /// </summary>
        private const int POSITIONALS_CHECKED_BY_COMMAND = -2;

        /// <summary>进程入口：固定输出编码，然后交给解析与分发。</summary>
        /// <param name="args">进程参数。</param>
        /// <returns>进程退出码，见 <see cref="CliExitCode"/>。</returns>
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
                return CliExitCode.USAGE;
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
                return CliExitCode.FAILED;
            }
        }

        /// <summary>按子命令分发；未知/空命令在这里收口。</summary>
        /// <param name="arguments">已解析并校验过选项的参数集。</param>
        /// <returns>进程退出码。</returns>
        private static int Dispatch(CliArguments arguments)
        {
            if (arguments.HelpRequested)
            {
                CliUsage.Write(Console.Out);
                return CliExitCode.OK;
            }

            switch (arguments.Command)
            {
                case "help":
                    CliUsage.Write(Console.Out);
                    return CliExitCode.OK;
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
                case "state":
                    return WithRoot(arguments, 2, "<endpointId> and <name>", CliStateCommand.Run);
                case "progress":
                    return WithRoot(arguments, 2, "<endpointId> and <requestId>", CliProgressCommand.Run);
                case "cancel":
                    return WithRoot(arguments, 2, "<endpointId> and <requestId>", CliCancelCommand.Run);
                case "gc":
                    // gc 的位置参数是可选的一个端点：个数由 CliGcCommand 自己校验。
                    // 这里必须传"由命令校验"而不是 Positionals.Count——后者让比较恒真，校验形同虚设。
                    return WithRoot(arguments, POSITIONALS_CHECKED_BY_COMMAND, "at most one <endpointId>", CliGcCommand.Run);
                case "":
                    return EmptyCommand(arguments);
                default:
                    CliOutput.WriteError(arguments.Json, "unknown-command", "unknown command \"" + arguments.Command + "\"");
                    if (!arguments.Json) CliUsage.Write(Console.Error);
                    return CliExitCode.USAGE;
            }
        }

        /// <summary>
        /// 没给子命令：和参数错误走同一条错误通道。
        /// 以前这里直接往 stderr 打 usage 就退 2，--json 的调用方拿不到错误对象，也分不清"用法错"与"执行失败"。
        /// </summary>
        /// <param name="arguments">已解析的参数集（此时 Command 为空串）。</param>
        /// <returns>恒为 <see cref="CliExitCode.USAGE"/>。</returns>
        private static int EmptyCommand(CliArguments arguments)
        {
            CliOutput.WriteError(arguments.Json, "invalid-argument", "no command given (run \"courier --help\" for the list of commands)");
            if (!arguments.Json) CliUsage.Write(Console.Error);
            return CliExitCode.USAGE;
        }

        /// <summary>校验位置参数个数、解析 root、装载布局，然后执行命令。</summary>
        /// <param name="arguments">已解析的命令行。</param>
        /// <param name="expectedPositionals">该命令要求的位置参数个数；<see cref="POSITIONALS_CHECKED_BY_COMMAND"/> 表示跳过。</param>
        /// <param name="expectedDescription">写进报错文案的位置参数说明。</param>
        /// <param name="run">真正执行命令的方法。</param>
        /// <returns>退出码。</returns>
        private static int WithRoot(
            CliArguments arguments,
            int expectedPositionals,
            string expectedDescription,
            Func<CliArguments, string, CourierLayout, int> run)
        {
            if (expectedPositionals != POSITIONALS_CHECKED_BY_COMMAND && arguments.Positionals.Count != expectedPositionals)
            {
                CliOutput.WriteError(
                    arguments.Json,
                    "invalid-argument",
                    arguments.Command + " expects " + expectedDescription + " (got " + arguments.Positionals.Count + " argument(s))");
                return CliExitCode.USAGE;
            }

            string? detail;
            string rootError;
            if (!CliRoot.TryResolve(arguments.Root, arguments.RootName, out string root, out detail, out rootError))
            {
                CliOutput.WriteError(arguments.Json, rootError, detail ?? "no protocol root could be resolved");
                return CliExitCode.USAGE;
            }

            CourierLayout layout;
            if (!CliDirectory.TryLoadLayout(root, out layout, out detail))
            {
                // 布局损坏时绝不用默认布局"假装没事"：那会把"布局写错了"表现成"这里是空目录"，
                // 使用者随后看到的"没有端点"是次生错误，排查方向直接跑偏（CourierLayout.Load 的注释同理）。
                CliOutput.WriteError(arguments.Json, "layout-invalid", detail ?? "courier.layout.json could not be read");
                return CliExitCode.USAGE;
            }

            return run(arguments, root, layout);
        }

        /// <summary>打印版本号；--json 时是一行对象。</summary>
        /// <param name="arguments">已解析的命令行。</param>
        /// <returns>退出码。</returns>
        private static int Version(CliArguments arguments)
        {
            if (arguments.Positionals.Count != 0)
            {
                CliOutput.WriteError(arguments.Json, "invalid-argument", "version takes no arguments");
                return CliExitCode.USAGE;
            }

            if (arguments.Json)
            {
                CliOutput.WriteLine(new CourierJsonObject()
                    .AddString("name", "courier")
                    .AddString("version", CliUsage.VERSION)
                    .AddNumber("protocolVersion", CourierProtocol.PROTOCOL_VERSION)
                    .Build());
            }
            else
            {
                CliOutput.WriteLine("courier " + CliUsage.VERSION + " (protocol " + CourierProtocol.PROTOCOL_VERSION + ")");
            }

            return CliExitCode.OK;
        }
    }
}
