using System.Collections.Generic;
using System.IO;

namespace Courier.Cli
{
    /// <summary>
    /// `courier progress &lt;endpointId&gt; &lt;requestId&gt;`：读一条在途命令的最新进度快照（SPEC §13.2）。
    /// <para>
    /// 进度是**服务者**覆盖写的单份快照，不是日志：只保留最近一次，命令一旦产生终态就被服务者删掉
    /// （§13.2）。所以这里的三种结果含义不同——文件在、文件不在、文件在但读不懂：
    /// </para>
    /// <list type="bullet">
    /// <item>文件在：输出它，缺哪个字段就省哪个字段；</item>
    /// <item>文件不在：输出 `published:false`，**退出码 0**——SPEC §13.2 明写不写进度的端点
    /// MUST 被调用方当作"没有进度信息"，那不是错误；</item>
    /// <item>文件在但解析不出：照原文输出 `raw`，仍然退出 0。</item>
    /// </list>
    /// <para>
    /// 这条命令没有副作用、不等待、不投递任何东西。要盯着进度的人自己按所需频率重复调用。
    /// "文件不在"与"端点不在"仍然要分开：前者是正常的没有进度信息，后者是 endpointId 写错了。
    /// </para>
    /// </summary>
    internal static class CliProgressCommand
    {
        /// <summary>执行命令。</summary>
        /// <param name="arguments">已解析的命令行。</param>
        /// <param name="root">已解析的协议根目录。</param>
        /// <param name="layout">root 下生效的目录布局。</param>
        /// <returns>退出码。</returns>
        internal static int Run(CliArguments arguments, string root, CourierLayout layout)
        {
            string endpointId = arguments.Positionals[0];
            string requestId = arguments.Positionals[1];
            if (!TryValidateTarget(arguments, root, layout, endpointId, requestId)) return CliExitCode.USAGE;

            string path = Path.Combine(
                CliDirectory.EndpointDirectory(root, layout, endpointId),
                layout.ProgressDirectory,
                requestId + CourierProtocol.JSON_EXTENSION);

            string? text;
            if (!CourierIO.TryReadAllText(path, out text) || text == null) return ReportMissing(arguments, endpointId, requestId);

            // --raw 与 state 命令一致：stdout 就是进度文件的原文，不再包一层。
            if (arguments.Raw) CliOutput.WriteLine(text);
            else CliOutput.WriteLine(arguments.Json ? Wrap(endpointId, requestId, text) : text);
            return CliExitCode.OK;
        }

        /// <summary>
        /// 校验两个标识并确认端点确实登记过。
        /// </summary>
        /// <remarks>
        /// "文件不在"与"端点不在"必须分开：前者是正常的没有进度信息，后者是 endpointId 写错了。
        /// 把后者伪装成"这条命令还没有进度"会让调用方一直等一个根本没被受理的请求。
        /// </remarks>
        /// <param name="arguments">已解析的命令行（用于 <c>--json</c> 分支与错误文案）。</param>
        /// <param name="root">协议根目录。</param>
        /// <param name="layout">布局。</param>
        /// <param name="endpointId">端点标识。</param>
        /// <param name="requestId">命令标识。</param>
        /// <returns>两个标识合法且端点已登记时为 true。</returns>
        private static bool TryValidateTarget(CliArguments arguments, string root, CourierLayout layout, string endpointId, string requestId)
        {
            // SPEC §4.1 / §4.2：与 state、cancel 走同一份校验，同一个写错的 id 得到同一句解释。
            if (!CliIdentifiers.RequireEndpointId(arguments, endpointId)) return false;
            if (!CliIdentifiers.RequireRequestId(arguments, requestId)) return false;

            if (CliDirectory.HasRegisteredEndpoint(root, layout, endpointId)) return true;

            CliOutput.WriteError(
                arguments.Json,
                "endpoint-not-found",
                "no endpoint named \"" + endpointId + "\" under " + CliDirectory.EndpointsDirectory(root, layout)
                    + " (it has never started, or its endpoint.json is gone)");
            return false;
        }

        /// <summary>
        /// 没有进度文件时的输出。"没有进度"是合法状态（SPEC §13.2：progress 是可选的），所以退出码仍是 0。
        /// </summary>
        /// <param name="arguments">已解析的命令行。</param>
        /// <param name="endpointId">端点标识。</param>
        /// <param name="requestId">命令标识。</param>
        /// <returns>退出码，恒为 <see cref="CliExitCode.OK"/>。</returns>
        private static int ReportMissing(CliArguments arguments, string endpointId, string requestId)
        {
            if (arguments.Json)
            {
                CliOutput.WriteLine(new CourierJsonObject()
                    .AddString("endpointId", endpointId)
                    .AddString("requestId", requestId)
                    .AddBoolean("published", false)
                    .Build());
            }
            else
            {
                CliOutput.WriteLine(endpointId + " reports no progress for " + requestId);
            }

            return CliExitCode.OK;
        }

        /// <summary>
        /// 把 `progress/&lt;requestId&gt;.json` 的原文包成一条带元信息的 JSON（SPEC §13.2）。
        /// <para>
        /// 与 state 的包装同一套原则：能读出来的字段照原样搬，读不出来的一律省略，
        /// 不因为一个字段不合规就丢掉整份快照。解析不出整份对象时退回 `raw` 原文，
        /// 让人仍能看到磁盘上到底写了什么。
        /// </para>
        /// </summary>
        /// <param name="endpointId">端点标识（已校验）。</param>
        /// <param name="requestId">命令标识（已校验）。</param>
        /// <param name="text">进度文件原文。</param>
        /// <returns>一行紧凑 JSON。</returns>
        private static string Wrap(string endpointId, string requestId, string text)
        {
            Dictionary<string, object?>? document = null;
            if (!CourierJson.TryParseObject(text, out document) || document == null)
            {
                return new CourierJsonObject()
                    .AddString("endpointId", endpointId)
                    .AddString("requestId", requestId)
                    .AddBoolean("published", true)
                    .AddString("raw", text)
                    .Build();
            }

            var builder = new CourierJsonObject()
                .AddString("endpointId", endpointId)
                .AddString("requestId", requestId)
                .AddBoolean("published", true);

            string? percent = PercentValue(document);
            if (percent != null) builder.AddRaw("percent", percent);

            string? message = CourierJson.GetString(document, "message");
            if (message != null) builder.AddString("message", message);

            string? updatedAtUtc = CourierJson.GetString(document, "updatedAtUtc");
            if (updatedAtUtc != null) builder.AddString("updatedAtUtc", updatedAtUtc);

            return builder.Build();
        }

        /// <summary>
        /// 取出快照里的 <c>percent</c> 并写成 JSON 数字字面量。
        /// <para>
        /// SPEC §13.2 说它是 0..100 的整数，但读方按整数读会让 <c>0.5</c> 这类小数被**静默丢弃**：
        /// 输出里直接没有 percent 字段，调用方看到的是"这条命令没有进度"，而不是"取到 0.5"。
        /// 只要字段是 JSON 数字就照原样输出（不替写方纠值域：显示层的职责是如实转述，
        /// 判合规是读方自己的事），不是数字才按 SPEC §3.1 忽略这个字段。
        /// </para>
        /// </summary>
        /// <param name="document">进度文件的解析结果。</param>
        /// <returns>数字字面量；字段缺失、不是数字或不是有限值时返回 null。</returns>
        private static string? PercentValue(Dictionary<string, object?> document)
        {
            object? raw;
            if (!document.TryGetValue("percent", out raw)) return null;
            if (!(raw is double number)) return null;
            return CliJson.Decimal(number);
        }
    }
}
