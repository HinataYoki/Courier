using System;
using System.IO;
using System.Threading;

namespace Courier.Cli
{
    /// <summary>
    /// `courier call &lt;endpointId&gt; &lt;channel&gt;/&lt;action&gt;`：投递一条命令并等待终态（SPEC §9.3、§17）。
    /// <para>
    /// 投递复用产品库的 <see cref="CourierDispatcher.Deliver"/>，前置检查（版本、背压、体积）与库内
    /// <c>CallAsync</c> 完全同源；CLI 只补三件它必须自己做的事：读环境变量决定 issuedBy、投递前确认端点真的在册，
    /// 以及同步轮询结果文件。短命进程用同步等待是安全的（SPEC §17.3 明确允许），因为它没有 Tick 循环，不会和另一端互等。
    /// </para>
    /// </summary>
    internal static class CliCallCommand
    {
        /// <summary>未设置 COURIER_ISSUED_BY 时写进命令的投递方标识。</summary>
        internal const string DEFAULT_ISSUED_BY = "cli";

        /// <summary>执行命令。</summary>
        /// <param name="arguments">已解析的命令行。</param>
        /// <param name="root">已解析的协议根目录。</param>
        /// <param name="layout">root 下生效的目录布局。</param>
        /// <returns>退出码，由 response 的 status 决定。</returns>
        internal static int Run(CliArguments arguments, string root, CourierLayout layout)
        {
            string endpointId = arguments.Positionals[0];
            string target = arguments.Positionals[1];

            // SPEC §4.1：endpointId 是唯一的路径拼接入口，非法值绝不能拼进路径。
            if (!CliIdentifiers.RequireEndpointId(arguments, endpointId)) return CliExitCode.USAGE;

            string channel;
            string action;
            if (!TrySplitTarget(arguments, target, out channel, out action)) return CliExitCode.USAGE;

            TimeSpan deadline;
            if (!TryResolveDeadline(arguments, out deadline)) return CliExitCode.USAGE;

            // 端点目录都不存在时，命令文件写得进去也没有任何进程会读它：
            // 旧行为是照投递、再干等到 --timeout 才报 unreachable，而 describe/gc/cancel 早已直接报 endpoint-not-found。
            // 同一个"端点不存在"在四条命令上不该有两种代价（P1-13）。
            if (!CliDirectory.HasRegisteredEndpoint(root, layout, endpointId))
            {
                CliOutput.WriteError(
                    arguments.Json,
                    "endpoint-not-found",
                    "no endpoint named \"" + endpointId + "\" under " + CliDirectory.EndpointsDirectory(root, layout)
                        + " (it has never started, or its endpoint.json is gone)");
                return CliExitCode.USAGE;
            }

            string issuedBy = IssuedBy();
            string requestId;
            // durationMs 量的是"从投递到拿到终态"，也就是调用方实际等待的那一段（SPEC §17）。
            // Stopwatch 用单调时钟：墙上时间会被对时/夏令时改写，量耗时会得到负数。
            long startedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            // SPEC §9.1：timeout 同时是"调用方等多久"和"服务者最晚什么时候还允许开始执行"，
            // 两者必须是同一个数，否则调用方已经放弃、命令却还能被执行。
            CourierResponse? early = CourierDispatcher.Deliver(
                root, layout, issuedBy, endpointId, channel, action, arguments.Payload, deadline, out requestId);

            // 热通道快路径：命令已经落盘，这里只是再叫醒一次宿主，让它不必等到下一次 Tick 才看见它。
            // 命中就省掉整个轮询（本机实测：轮询里 Thread.Sleep 的粒度是 15.5 ms，热回铃是 µs 级）；
            // 连不上、对端没开热通道、帧装不下，一律当没有这条通道，退回下面的文件等待（docs/HOT-CHANNEL.md §4）。
            // 两条路径拿到的都是同一份 results/<requestId>.json 的语义，输出与退出码因此不需要分叉。
            CourierResponse response = early ?? WaitHot(root, layout, issuedBy, endpointId, channel, action, arguments.Payload, requestId, deadline)
                ?? Wait(root, layout, endpointId, requestId, deadline);

            long durationMs = ElapsedMs(startedTicks);
            WriteResponse(arguments, endpointId, channel, action, requestId, durationMs, deadline, response);
            return CliExitCode.FromStatus(response.Status);
        }

        /// <summary>把 <c>&lt;channel&gt;/&lt;action&gt;</c> 拆成两段并各自按 SPEC §4.3 校验；失败时自己写出错误。</summary>
        /// <param name="arguments">已解析的命令行（只用来决定错误输出格式）。</param>
        /// <param name="target">原始目标文本。</param>
        /// <param name="channel">拆分出的信道名。</param>
        /// <param name="action">拆分出的动作名。</param>
        /// <returns>是否是一个合法的 channel/action 组合。</returns>
        private static bool TrySplitTarget(CliArguments arguments, string target, out string channel, out string action)
        {
            channel = string.Empty;
            action = string.Empty;
            int separator = target.IndexOf('/');
            if (separator <= 0 || separator == target.Length - 1)
            {
                CliOutput.WriteError(arguments.Json, "invalid-argument", "expected <channel>/<action>, got \"" + target + "\"");
                return false;
            }

            channel = target.Substring(0, separator);
            action = target.Substring(separator + 1);
            if (CourierProtocol.IsValidName(channel) && CourierProtocol.IsValidName(action)) return true;

            CliOutput.WriteError(arguments.Json, "invalid-argument", "\"" + target + "\" is not a valid channel/action pair");
            channel = string.Empty;
            action = string.Empty;
            return false;
        }

        /// <summary>
        /// 算出这次调用**实际生效**的等待上限：--timeout（缺省 <see cref="CourierProtocol.DEFAULT_DEADLINE"/>）
        /// 再按 <see cref="CourierProtocol.MAX_DEADLINE"/> 截断。
        /// <para>
        /// 截断在库里做（<see cref="CourierDispatcher.Deliver"/> 内部），所以 CLI 必须自己先截一次，
        /// 再把同一个值交给投递与等待：否则 `--timeout 1h` 会按 300s 投递、却按 3600s 等，
        /// 对端没启动时空等一小时，最后报的"超时"也和命令真正允许的时间对不上。
        /// </para>
        /// </summary>
        /// <param name="arguments">已解析的命令行。</param>
        /// <param name="deadline">生效的等待上限。</param>
        /// <returns>--timeout 是否合法。</returns>
        private static bool TryResolveDeadline(CliArguments arguments, out TimeSpan deadline)
        {
            deadline = CourierProtocol.DEFAULT_DEADLINE;
            if (arguments.TimeoutText != null && !CliDuration.TryParse(arguments.TimeoutText, out deadline))
            {
                CliOutput.WriteError(
                    arguments.Json,
                    CliDuration.ERROR_CODE,
                    CliDuration.DescribeFailure("--timeout", arguments.TimeoutText));
                return false;
            }

            if (deadline > CourierProtocol.MAX_DEADLINE) deadline = CourierProtocol.MAX_DEADLINE;
            return true;
        }

        /// <summary>投递方标识：优先环境变量，空值回落到 <see cref="DEFAULT_ISSUED_BY"/>。</summary>
        /// <returns>非空的 issuedBy。</returns>
        private static string IssuedBy()
        {
            string? value = Environment.GetEnvironmentVariable("COURIER_ISSUED_BY");
            // IsNullOrWhiteSpace 带 [NotNullWhen(false)]，编译器已把 value 窄化为非空。
            if (string.IsNullOrWhiteSpace(value)) return DEFAULT_ISSUED_BY;
            return value.Trim();
        }

        /// <summary>
        /// 热通道快路径：叫醒宿主并等它回一条响应通知。任何一步不成立（端点没开热通道、命名空间打不开、
        /// 槽全被占、等超时）都返回 null，调用方据此退回文件轮询。
        /// <para>
        /// 返回的 <see cref="CourierResponse"/> 是从通知帧里反序列化出来的，与 <c>results/&lt;requestId&gt;.json</c>
        /// 是同一份内容——热通道只负责叫醒，不负责记账（docs/HOT-CHANNEL.md §4）。
        /// </para>
        /// </summary>
        /// <param name="root">协议根目录。</param>
        /// <param name="layout">root 下生效的目录布局。</param>
        /// <param name="askedBy">投递方标识，写进通知帧。</param>
        /// <param name="endpointId">目标端点。</param>
        /// <param name="channel">信道名。</param>
        /// <param name="action">动作名。</param>
        /// <param name="payload">不透明字符串。</param>
        /// <param name="requestId">投递时生成的请求标识。</param>
        /// <param name="deadline">生效的等待上限（已按 MAX_DEADLINE 截断）。</param>
        /// <returns>命中时的响应；未命中返回 null。</returns>
        private static CourierResponse? WaitHot(
            string root,
            CourierLayout layout,
            string askedBy,
            string endpointId,
            string channel,
            string action,
            string? payload,
            string requestId,
            TimeSpan deadline)
        {
            CourierHotDescriptor? descriptor;
            if (!CourierHotDescriptor.TryRead(root, layout, endpointId, out descriptor)) return null;

            CourierHotChannel? channelHandle = CourierHotChannel.TryOpen(root, endpointId, descriptor, 200);
            if (channelHandle == null) return null;

            using (channelHandle)
            {
                if (!channelHandle.Announce(requestId, askedBy, channel, action, payload, DateTime.UtcNow + deadline)) return null;
                int milliseconds = (int)Math.Min(int.MaxValue, Math.Max(1, deadline.TotalMilliseconds));
                return channelHandle.WaitResponse(requestId, milliseconds);
            }
        }

        /// <summary>
        /// 轮询 results/ 等待终态（SPEC §17.2）：10 ms 起指数退避到 200 ms 上限，到点合成 unreachable。
        /// unreachable 不落盘，它是调用方的判断而不是对端的答复。
        /// </summary>
        /// <param name="root">协议根目录。</param>
        /// <param name="layout">root 下生效的目录布局。</param>
        /// <param name="endpointId">目标端点。</param>
        /// <param name="requestId">投递时生成的请求标识。</param>
        /// <param name="deadline">生效的等待上限（与投递用的是同一个值）。</param>
        /// <returns>四种 status 之一，永不返回 null。</returns>
        private static CourierResponse Wait(string root, CourierLayout layout, string endpointId, string requestId, TimeSpan deadline)
        {
            string endpointDirectory = CliDirectory.EndpointDirectory(root, layout, endpointId);
            string resultPath = Path.Combine(
                endpointDirectory,
                layout.ResultsDirectory,
                requestId + CourierProtocol.JSON_EXTENSION);

            DateTime expireAtUtc = DateTime.UtcNow + deadline;
            int delayMilliseconds = 10;
            while (true)
            {
                string? text;
                if (CourierIO.TryReadAllText(resultPath, out text))
                {
                    CourierResponse? response = CourierResponse.TryParse(text);
                    if (response != null) return response;
                }

                if (DateTime.UtcNow >= expireAtUtc) break;
                int remaining = (int)(expireAtUtc - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0) break;
                Thread.Sleep(delayMilliseconds < remaining ? delayMilliseconds : remaining);
                delayMilliseconds *= 2;
                if (delayMilliseconds > 200) delayMilliseconds = 200;
            }

            // SPEC §15.2 的读者条款：结果文件缺失时，墓碑能把"已被回收"与"从没处理过"分开。
            // CLI 与库走同一条判断，避免两条等待路径各自漂移。
            bool reclaimed = CourierResultsGc.IsPossiblyReclaimed(
                Path.Combine(endpointDirectory, layout.ResultsDirectory),
                requestId);
            return new CourierResponse(requestId, CourierProtocol.STATUS_UNREACHABLE, string.Empty, null, false, DateTime.UtcNow, reclaimed);
        }

        /// <summary>把 <see cref="System.Diagnostics.Stopwatch"/> 的起始刻度换算成毫秒（向下取整）。</summary>
        /// <param name="startedTicks">起始刻度。</param>
        /// <returns>非负毫秒数。</returns>
        private static long ElapsedMs(long startedTicks)
        {
            long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - startedTicks;
            if (elapsed <= 0) return 0;
            return (long)(elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }

        /// <summary>按 --json 与否输出结果：一行紧凑 JSON 或一句人类可读的话。</summary>
        /// <param name="arguments">已解析的命令行。</param>
        /// <param name="endpointId">目标端点。</param>
        /// <param name="channel">信道名。</param>
        /// <param name="action">动作名。</param>
        /// <param name="requestId">投递时生成的请求标识；为空表示命令根本没写出去。</param>
        /// <param name="durationMs">从投递到拿到终态的毫秒数。</param>
        /// <param name="deadline">生效的等待上限。</param>
        /// <param name="response">终态或本地合成的 unreachable。</param>
        private static void WriteResponse(
            CliArguments arguments,
            string endpointId,
            string channel,
            string action,
            string requestId,
            long durationMs,
            TimeSpan deadline,
            CourierResponse response)
        {
            if (arguments.Json)
            {
                var builder = new CourierJsonObject()
                    .AddString("status", response.Status);
                if (response.Reason != null) builder.AddString("reason", response.Reason);
                // requestId 就是 results/<requestId>.json 的文件名（README 承诺过）——命令没写出去时它是空串，
                // 这时输出 "requestId":"" 会让读方去 cat 一个名字为空的文件，所以整个字段缺席。
                if (requestId.Length > 0) builder.AddString("requestId", requestId);
                // 这几个字段是"这次调用是谁、投给了谁、花了多久"，让 CLI 的输出能直接与
                // results/<requestId>.json、服务端日志对齐；它们只影响显示，不是协议字段。
                builder
                    .AddString("endpointId", endpointId)
                    .AddString("channel", channel)
                    .AddString("action", action)
                    .AddNumber("durationMs", durationMs)
                    .AddRaw("payload", CliJson.PayloadValue(response.Payload));
                // SPEC §15.2：结果缺失但墓碑说明它已被回收。只在真出现时加这个字段——它不是原因码，
                // 不改变 status 与退出码，纯粹是让"没等到"与"等的东西被删了"能被区分开。
                if (response.Reclaimed) builder.AddBoolean("reclaimed", true);
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
            if (string.Equals(response.Status, CourierProtocol.STATUS_UNREACHABLE, StringComparison.Ordinal))
            {
                // 报的是实际等过的时间（截断后的 deadline），不是用户写下的 --timeout。
                Console.Error.Write("courier: " + endpointId + " did not answer " + channel + "/" + action
                    + " within " + CliOutput.FormatSeconds((long)deadline.TotalMilliseconds)
                    + (response.Reclaimed ? " (its results may already have been reclaimed; see results/gc.json)" : string.Empty)
                    + "\n");
                return;
            }

            Console.Error.Write("courier: " + endpointId + " " + response.Status + " " + channel + "/" + action + reason + detail + "\n");
        }
    }
}
