namespace Courier.Cli
{
    /// <summary>
    /// 五档退出码。0–3 与 SPEC §11 的四种 status 一一对应，调用方靠它决定"改参数"、"重试"还是"等软件启动"；
    /// 4 是 CLI 自己的"这次调用根本没发出去"。
    /// <para>
    /// 用法/环境错误单独占一档，是为了不和 SPEC §11.1 的 failed 混同：前者重试无意义、也不需要读对端日志，
    /// 后者意味着命令真的被执行过。两类都报 2 会让调用方永远不知道自己该改参数还是该重试。
    /// </para>
    /// </summary>
    internal static class CliExitCode
    {
        /// <summary>成功。</summary>
        internal const int OK = 0;

        /// <summary>请求本身有问题，重试无意义（SPEC §11.1）。</summary>
        internal const int REJECTED = 1;

        /// <summary>执行失败（SPEC §11.1 的 failed），以及 CLI 自身的意外故障（internal-error）。</summary>
        internal const int FAILED = 2;

        /// <summary>等到超时仍无 response（SPEC §11.2）。</summary>
        internal const int UNREACHABLE = 3;

        /// <summary>
        /// 用法或环境错误：参数/选项不合法、root 找不到、布局损坏、目标端点根本不存在。
        /// 这一类请求没有投出去，也没有对端参与，因此不能和 <see cref="FAILED"/> 混用——
        /// 拿到 2 的人会去翻对端日志，而这里根本没有对端。
        /// </summary>
        internal const int USAGE = 4;

        /// <summary>把 SPEC §11 的 status 映射成退出码；未知 status 一律按失败处理。</summary>
        /// <param name="status">response 的 status 字段。</param>
        /// <returns>0 / 1 / 2 / 3。</returns>
        internal static int FromStatus(string status)
        {
            switch (status)
            {
                case CourierProtocol.STATUS_OK: return OK;
                case CourierProtocol.STATUS_REJECTED: return REJECTED;
                case CourierProtocol.STATUS_FAILED: return FAILED;
                case CourierProtocol.STATUS_UNREACHABLE: return UNREACHABLE;
                default: return FAILED;
            }
        }
    }
}
