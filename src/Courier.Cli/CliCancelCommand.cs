using System;
using System.IO;

namespace Courier.Cli
{
    /// <summary>
    /// `courier cancel &lt;endpointId&gt; &lt;requestId&gt;`：向目标端点投递一次取消请求（SPEC §13.1）。
    /// <para>
    /// 取消是**尽力而为**：它只是往 `endpoints/&lt;id&gt;/cancels/` 放一个文件，目标端点下一次 Tick 才会读到；
    /// 命令已经在跑时只能把信号递给 handler 的 token，handler 完全可以忽略。
    /// 因此"写进去了"不等于"取消了"，这条命令的退出码只表示**请求已投递**。
    /// </para>
    /// <para>
    /// 目标端点目录不存在（从未启动过）时报 endpoint-not-found 并返回用法错误：往一个不存在的目录里投递
    /// 只会在磁盘上留下一片垃圾，而读方永远不会看到它。
    /// </para>
    /// </summary>
    internal static class CliCancelCommand
    {
        /// <summary>未给出 --reason 时写进取消文件的默认原因。</summary>
        private const string DEFAULT_REASON = "requested";

        /// <summary>
        /// --reason 的长度上限（按 UTF-16 码元计）。
        /// 取消文件是要被对端读进内存的一条小记录，不是日志：给一个 1 MiB 的 reason 既超出 SPEC §13.1 的用意
        /// （它只是"用于排查的自由文本"），也让写失败变成一种可以人为触发的失败模式。
        /// 512 个字符足够写清原因，同时又远小于任何体积上限。
        /// </summary>
        private const int MAX_REASON_LENGTH = 512;

        /// <summary>执行命令。</summary>
        /// <param name="arguments">已解析的命令行。</param>
        /// <param name="root">已解析的协议根目录。</param>
        /// <param name="layout">root 下生效的目录布局。</param>
        /// <returns>退出码：请求写进 cancels/ 时是 0。</returns>
        internal static int Run(CliArguments arguments, string root, CourierLayout layout)
        {
            string endpointId = arguments.Positionals[0];
            string requestId = arguments.Positionals[1];

            // SPEC §4.1 / §4.2：两个标识符都是路径片段，拼进 cancels/ 之前必须先校验。
            if (!CliIdentifiers.RequireEndpointId(arguments, endpointId)) return CliExitCode.USAGE;
            if (!CliIdentifiers.RequireRequestId(arguments, requestId)) return CliExitCode.USAGE;

            string reason;
            if (!TryResolveReason(arguments, out reason)) return CliExitCode.USAGE;

            string endpointDirectory;
            if (!TryResolveEndpoint(arguments, root, layout, endpointId, out endpointDirectory)) return CliExitCode.USAGE;

            string json = new CourierJsonObject()
                .AddString("requestId", requestId)
                .AddString("requestedAtUtc", CourierProtocol.FormatUtc(DateTime.UtcNow))
                .AddString("reason", reason)
                .Build();

            string cancelsDirectory = Path.Combine(endpointDirectory, layout.CancelsDirectory);
            string path = Path.Combine(cancelsDirectory, requestId + CourierProtocol.JSON_EXTENSION);
            if (!TryWriteCancel(arguments, cancelsDirectory, path, json)) return CliExitCode.FAILED;

            if (arguments.Json)
            {
                CliOutput.WriteLine(new CourierJsonObject()
                    .AddString("endpointId", endpointId)
                    .AddString("requestId", requestId)
                    .AddString("reason", reason)
                    .AddBoolean("delivered", true)
                    .Build());
            }
            else
            {
                CliOutput.WriteLine("cancel requested for " + requestId + " on " + endpointId + " (best effort; the endpoint reads it on its next tick)");
            }

            return CliExitCode.OK;
        }

        /// <summary>
        /// 确认目标端点目录存在，并给出它的绝对路径。
        /// <para>
        /// 往一个不存在的目录里投递只会在磁盘上留下一片没人会读的垃圾，所以这里提前判错：
        /// 错误码与其他命令的 "端点不在册" 保持同一个 <c>endpoint-not-found</c>。
        /// </para>
        /// </summary>
        /// <param name="arguments">已解析的命令行，用来决定错误输出格式。</param>
        /// <param name="root">协议根目录。</param>
        /// <param name="layout">root 下生效的目录布局。</param>
        /// <param name="endpointId">已校验的端点标识。</param>
        /// <param name="endpointDirectory">端点目录的绝对路径。</param>
        /// <returns>目录存在为 true。</returns>
        private static bool TryResolveEndpoint(
            CliArguments arguments,
            string root,
            CourierLayout layout,
            string endpointId,
            out string endpointDirectory)
        {
            endpointDirectory = CliDirectory.EndpointDirectory(root, layout, endpointId);
            if (Directory.Exists(endpointDirectory)) return true;
            CliOutput.WriteError(
                arguments.Json,
                "endpoint-not-found",
                "endpoint \"" + endpointId + "\" has no directory under " + CliDirectory.EndpointsDirectory(root, layout)
                    + ", so it cannot observe a cancel");
            return false;
        }

        /// <summary>
        /// 把取消文件原子写进 <c>cancels/</c>；宿主层的写失败折算成明确的错误码，而不是冒泡成 internal-error。
        /// </summary>
        /// <param name="arguments">已解析的命令行，用来决定错误输出格式。</param>
        /// <param name="cancelsDirectory">端点自己的 <c>cancels/</c>。</param>
        /// <param name="path">取消文件的完整路径。</param>
        /// <param name="json">取消文件的单行 JSON 内容。</param>
        /// <returns>写入成功为 true；失败时已写出错误。</returns>
        private static bool TryWriteCancel(CliArguments arguments, string cancelsDirectory, string path, string json)
        {
            try
            {
                // SPEC §3.3：覆盖写（同一个 requestId 重复请求取消是幂等的），不落 fsync——
                // 取消是意图而不是证据（§13.1），断电丢掉它最多让一条命令跑完。
                CourierIO.EnsureDirectory(cancelsDirectory);
                CourierIO.WriteAtomic(path, json, true, false);
                return true;
            }
            catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException || caught is NotSupportedException)
            {
                // 以前这里不捕获：写失败一路冒到 Program 的兜底，变成 internal-error——
                // 使用者看不出是"这个目录写不进去"，而这恰恰是最常见的失败（只读挂载、权限、路径过长）。
                CliOutput.WriteError(
                    arguments.Json,
                    "cancel-failed",
                    "could not write " + path + ": " + caught.Message
                        + " (check that the root directory is writable and the path is not too long)");
                return false;
            }
        }

        /// <summary>
        /// 决定写进取消文件的 reason：缺省 <see cref="DEFAULT_REASON"/>，给出时校验长度与字符。
        /// <para>
        /// SPEC §13.1 说 reason 是自由文本、读方不得依赖其取值，但"自由"不等于"任意字节"：
        /// 控制字符（换行、NUL）会被 JSON 转义成可读性极差的 \u 序列，长度也无上限。
        /// 这两点都由 CLI 在写盘之前挡掉，让磁盘上永远只有一条短、单行、可 grep 的记录。
        /// </para>
        /// </summary>
        /// <param name="arguments">已解析的命令行。</param>
        /// <param name="reason">最终写进文件的 reason。</param>
        /// <returns>是否可用。</returns>
        private static bool TryResolveReason(CliArguments arguments, out string reason)
        {
            reason = arguments.ReasonText ?? DEFAULT_REASON;
            if (reason.Length > MAX_REASON_LENGTH)
            {
                CliOutput.WriteError(
                    arguments.Json,
                    "invalid-argument",
                    "--reason must be at most " + MAX_REASON_LENGTH + " characters (got " + reason.Length + ")");
                reason = DEFAULT_REASON;
                return false;
            }

            for (int index = 0; index < reason.Length; index++)
            {
                if (!IsPrintable(reason[index]))
                {
                    CliOutput.WriteError(
                        arguments.Json,
                        "invalid-argument",
                        "--reason must be a single line of printable text (found a control character at offset " + index + ")");
                    reason = DEFAULT_REASON;
                    return false;
                }
            }

            return true;
        }

        /// <summary>该字符是否是可打印文本：制表符之外的控制字符一律不接受。</summary>
        /// <param name="value">待判定的字符。</param>
        /// <returns>可打印为 true。</returns>
        private static bool IsPrintable(char value)
        {
            if (value == '\t') return true;
            return value >= ' ' && value != '\u007f';
        }
    }
}
