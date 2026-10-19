namespace Courier.Cli
{
    /// <summary>
    /// 四档退出码。CLI 的每个返回值都必须是这四个之一，
    /// 调用方靠它决定"改参数"、"重试"还是"等软件启动"。
    /// </summary>
    internal static class CliExitCode
    {
        /// <summary>成功。</summary>
        internal const int Ok = 0;

        /// <summary>请求本身有问题，重试无意义（SPEC §9.1）。</summary>
        internal const int Rejected = 1;

        /// <summary>执行失败，以及参数、root、权限一类的 CLI 级错误。</summary>
        internal const int Failed = 2;

        /// <summary>等到超时仍无 response（SPEC §9.2）。</summary>
        internal const int Unreachable = 3;

        /// <summary>把 SPEC §9 的 status 映射成退出码；未知 status 一律按失败处理。</summary>
        /// <param name="status">response 的 status 字段。</param>
        /// <returns>0 / 1 / 2 / 3。</returns>
        internal static int FromStatus(string status)
        {
            switch (status)
            {
                case CourierProtocol.StatusOk: return Ok;
                case CourierProtocol.StatusRejected: return Rejected;
                case CourierProtocol.StatusFailed: return Failed;
                case CourierProtocol.StatusUnreachable: return Unreachable;
                default: return Failed;
            }
        }
    }
}
