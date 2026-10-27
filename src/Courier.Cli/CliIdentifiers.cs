namespace Courier.Cli
{
    /// <summary>
    /// <c>endpointId</c> 与 <c>requestId</c> 的入口校验（SPEC §4.1、§4.2）。
    /// <para>
    /// 这两个标识符都是**路径片段**：它们的取值会直接拼进 <c>endpoints/&lt;id&gt;/</c>、
    /// <c>cancels/&lt;requestId&gt;.json</c> 这类路径，所以非法值必须在拼路径之前就被拒掉，
    /// 而不是交给文件系统去解释 <c>..</c> 或分隔符。
    /// </para>
    /// <para>
    /// CLI 里有六条命令各自要校验一次。共用一份实现不是为了让代码短，
    /// 而是为了让同一个写错的 id 在所有命令上得到同一句解释——否则调用方会以为它们的规则不同。
    /// </para>
    /// </summary>
    internal static class CliIdentifiers
    {
        /// <summary>SPEC §4.1 允许的 endpointId 形状，写进报错供使用者自查。</summary>
        private const string ENDPOINT_ID_PATTERN = "^[a-z0-9]([a-z0-9-]{0,62}[a-z0-9])?$";

        /// <summary>SPEC §4.2 允许的 requestId 形状，写进报错供使用者自查。</summary>
        private const string REQUEST_ID_PATTERN = "^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8,32}$";

        /// <summary>
        /// 校验一个已经解析出来的 endpointId；不合法时直接写出错误（--json 时是错误对象）。
        /// </summary>
        /// <param name="arguments">已解析的命令行，用来决定错误输出的格式。</param>
        /// <param name="endpointId">位置参数里的端点标识原文。</param>
        /// <returns>合法为 true；false 时调用方应返回 <see cref="CliExitCode.USAGE"/>。</returns>
        internal static bool RequireEndpointId(CliArguments arguments, string endpointId)
        {
            if (CourierProtocol.IsValidEndpointId(endpointId)) return true;
            CliOutput.WriteError(
                arguments.Json,
                "invalid-endpoint-id",
                "\"" + endpointId + "\" is not a valid endpoint id (must match " + ENDPOINT_ID_PATTERN + ")");
            return false;
        }

        /// <summary>
        /// 校验一个已经解析出来的 requestId；不合法时直接写出错误（--json 时是错误对象）。
        /// </summary>
        /// <param name="arguments">已解析的命令行，用来决定错误输出的格式。</param>
        /// <param name="requestId">位置参数里的请求标识原文。</param>
        /// <returns>合法为 true；false 时调用方应返回 <see cref="CliExitCode.USAGE"/>。</returns>
        internal static bool RequireRequestId(CliArguments arguments, string requestId)
        {
            if (CourierProtocol.IsValidRequestId(requestId)) return true;
            CliOutput.WriteError(
                arguments.Json,
                "invalid-request-id",
                "\"" + requestId + "\" is not a valid request id (must match " + REQUEST_ID_PATTERN + ")");
            return false;
        }
    }
}
