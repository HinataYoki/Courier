namespace Courier.Cli
{
    /// <summary>
    /// `courier ps` 的一行：某个端点 claims/ 下的一条认领记录（SPEC §10）。
    /// 单独成类型是为了让排序与输出共用同一份数据，而不是各自再扫一次目录。
    /// </summary>
    internal sealed class CliClaimRecord
    {
        /// <summary>构造一条认领记录。</summary>
        /// <param name="endpointId">认领所属端点。</param>
        /// <param name="requestId">请求标识。</param>
        /// <param name="claim">已解析的 claim 内容。</param>
        internal CliClaimRecord(string endpointId, string requestId, CourierClaim claim)
        {
            EndpointId = endpointId;
            RequestId = requestId;
            Claim = claim;
        }

        /// <summary>认领所属端点。</summary>
        internal string EndpointId { get; }

        /// <summary>请求标识，等于 claims/ 下的文件名（去掉扩展名）。</summary>
        internal string RequestId { get; }

        /// <summary>解析后的认领内容。</summary>
        internal CourierClaim Claim { get; }
    }
}
