using System;
using System.IO;
using System.Threading;

namespace Courier.Cli
{
    /// <summary>
    /// `courier call &lt;endpointId&gt; &lt;channel&gt;/&lt;action&gt;`：投递一条命令并等待终态（SPEC §7.3、§14）。
    /// <para>
    /// 投递复用产品库的 <see cref="CourierDispatcher.Deliver"/>，前置检查（版本、背压、体积）与库内
    /// <c>CallAsync</c> 完全同源；CLI 只补两件它必须自己做的事：读环境变量决定 issuedBy，以及同步轮询结果文件。
    /// 短命进程用同步等待是安全的（SPEC §14.3 明确允许），因为它没有 Tick 循环，不会和另一端互等。
    /// </para>
    /// </summary>
    internal static class CliCallCommand
    {
        /// <summary>未设置 COURIER_ISSUED_BY 时写进命令的投递方标识。</summary>
        internal const string DefaultIssuedBy = "cli";

        /// <summary>执行命令。</summary>
        /// <param name="arguments">已解析的命令行。</param>
        /// <param name="root">已解析的协议根目录。</param>
        /// <returns>退出码，由 response 的 status 决定。</returns>
        internal static int Run(CliArguments arguments, string root)
        {
            string endpointId = arguments.Positionals[0];
            string target = arguments.Positionals[1];

            // SPEC §4.1：endpointId 是唯一的路径拼接入口，非法值绝不能拼进路径。
            if (!CourierProtocol.IsValidEndpointId(endpointId))
            {
                CliOutput.WriteError(arguments.Json, "invalid-endpoint-id", "\"" + endpointId + "\" is not a valid endpoint id");
                return CliExitCode.Failed;
            }

            int separator = target.IndexOf('/');
            if (separator <= 0 || separator == target.Length - 1)
            {
                CliOutput.WriteError(arguments.Json, "invalid-argument", "expected <channel>/<action>, got \"" + target + "\"");
                return CliExitCode.Failed;
            }

            string channel = target.Substring(0, separator);
            string action = target.Substring(separator + 1);
            if (!CourierProtocol.IsValidName(channel) || !CourierProtocol.IsValidName(action))
            {
                CliOutput.WriteError(arguments.Json, "invalid-argument", "\"" + target + "\" is not a valid channel/action pair");
                return CliExitCode.Failed;
            }

            TimeSpan timeout = CourierProtocol.DefaultDeadline;
            if (arguments.TimeoutText != null && !CliDuration.TryParse(arguments.TimeoutText, out timeout))
            {
                CliOutput.WriteError(arguments.Json, "invalid-timeout", "--timeout expects a positive duration such as 500ms, 10s or 1m (got \"" + arguments.TimeoutText + "\")");
                return CliExitCode.Failed;
            }

            string issuedBy = IssuedBy();
            string requestId;
            // SPEC §7.1：timeout 同时是"调用方等多久"和"服务者最晚什么时候还允许开始执行"，
            // 两者必须是同一个数，否则调用方已经放弃、命令却还能被执行。
            CourierResponse? early = CourierDispatcher.Deliver(root, issuedBy, endpointId, channel, action, arguments.Payload, timeout, out requestId);
            CourierResponse response = early ?? Wait(root, endpointId, requestId, timeout);

            WriteResponse(arguments, endpointId, channel, action, timeout, response);
            return CliExitCode.FromStatus(response.Status);
        }

        /// <summary>投递方标识：优先环境变量，空值回落到 <see cref="DefaultIssuedBy"/>。</summary>
        /// <returns>非空的 issuedBy。</returns>
        private static string IssuedBy()
        {
            string? value = Environment.GetEnvironmentVariable("COURIER_ISSUED_BY");
            // IsNullOrWhiteSpace 带 [NotNullWhen(false)]，编译器已把 value 窄化为非空。
            if (string.IsNullOrWhiteSpace(value)) return DefaultIssuedBy;
            return value.Trim();
        }

        /// <summary>
        /// 轮询 results/ 等待终态（SPEC §14.2）：10 ms 起指数退避到 200 ms 上限，到点合成 unreachable。
        /// unreachable 不落盘，它是调用方的判断而不是对端的答复。
        /// </summary>
        /// <param name="root">协议根目录。</param>
        /// <param name="endpointId">目标端点。</param>
        /// <param name="requestId">投递时生成的请求标识。</param>
        /// <param name="timeout">等待上限。</param>
        /// <returns>四种 status 之一，永不返回 null。</returns>
        private static CourierResponse Wait(string root, string endpointId, string requestId, TimeSpan timeout)
        {
            string resultPath = Path.Combine(
                CliDirectory.EndpointsDirectory(root),
                endpointId,
                CourierProtocol.ResultsDirectory,
                requestId + CourierProtocol.JsonExtension);

            DateTime deadline = DateTime.UtcNow + timeout;
            int delayMilliseconds = 10;
            while (true)
            {
                string? text;
                if (CourierIO.TryReadAllText(resultPath, out text))
                {
                    CourierResponse? response = CourierResponse.TryParse(text);
                    if (response != null) return response;
                }

                if (DateTime.UtcNow >= deadline) break;
                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0) break;
                Thread.Sleep(delayMilliseconds < remaining ? delayMilliseconds : remaining);
                delayMilliseconds *= 2;
                if (delayMilliseconds > 200) delayMilliseconds = 200;
            }

            return new CourierResponse(requestId, CourierProtocol.StatusUnreachable, string.Empty, null, false, DateTime.UtcNow);
        }

        /// <summary>按 --json 与否输出结果：一行紧凑 JSON 或一句人类可读的话。</summary>
        private static void WriteResponse(CliArguments arguments, string endpointId, string channel, string action, TimeSpan timeout, CourierResponse response)
        {
            if (arguments.Json)
            {
                var builder = new CourierJsonObject().AddString("status", response.Status);
                if (response.Reason != null) builder.AddString("reason", response.Reason);
                builder.AddRaw("payload", CliJson.PayloadValue(response.Payload));
                CliOutput.WriteLine(builder.Build());
                return;
            }

            if (response.IsOk)
            {
                // ok 时 payload 是 handler 的原文，不再包一层：让 `courier call` 能直接进管道。
                Console.Out.Write(response.Payload);
                Console.Out.Write('\n');
                return;
            }

            string reason = response.Reason == null ? string.Empty : " (" + response.Reason + ")";
            string detail = string.IsNullOrEmpty(response.Payload) ? string.Empty : ": " + response.Payload;
            if (string.Equals(response.Status, CourierProtocol.StatusUnreachable, StringComparison.Ordinal))
            {
                Console.Error.Write("courier: " + endpointId + " did not answer " + channel + "/" + action
                    + " within " + CliOutput.FormatSeconds((long)timeout.TotalMilliseconds) + "\n");
                return;
            }

            Console.Error.Write("courier: " + endpointId + " " + response.Status + " " + channel + "/" + action + reason + detail + "\n");
        }
    }
}
