using System;
using System.Collections.Generic;
using System.IO;

namespace Courier.Cli
{
    /// <summary>
    /// 只读目录访问的共用实现：CLI 只读写目录、序列化、格式化输出，不复制协议逻辑。
    /// <para>
    /// 所有"扫描端点"的细节集中在这里：路径拼接一律先校验标识符（SPEC §4.1），
    /// 目录不存在、权限不足等宿主异常一律折算成空结果，由命令层决定怎么报告。
    /// </para>
    /// </summary>
    internal static class CliDirectory
    {
        /// <summary>人类可读列表里 endpointId 列的最小宽度，保证 description 的括号能对齐。</summary>
        internal const int EndpointIdColumnWidth = 8;

        /// <summary>root 下的 endpoints/ 目录。</summary>
        /// <param name="root">协议根目录。</param>
        /// <returns>绝对路径。</returns>
        internal static string EndpointsDirectory(string root)
        {
            return Path.Combine(root, CourierProtocol.EndpointsDirectory);
        }

        /// <summary>扫描全部端点，按 endpointId 升序返回。</summary>
        /// <param name="root">协议根目录。</param>
        /// <returns>能成功解析的端点；损坏或不合法的目录被跳过，而不是让整条命令失败。</returns>
        internal static IReadOnlyList<CourierEndpointInfo> ReadEndpoints(string root)
        {
            return CourierEndpointReader.ReadAll(EndpointsDirectory(root));
        }

        /// <summary>读单个端点；不存在或损坏时返回 null。</summary>
        /// <param name="root">协议根目录。</param>
        /// <param name="endpointId">已通过合法性校验的端点标识。</param>
        /// <returns>端点视图，或 null。</returns>
        internal static CourierEndpointInfo? ReadEndpoint(string root, string endpointId)
        {
            return CourierEndpointReader.TryRead(Path.Combine(EndpointsDirectory(root), endpointId));
        }

        /// <summary>把"过去某刻"换算成毫秒年龄，负数（时钟回拨）夹到 0。</summary>
        /// <param name="thenUtc">过去的 UTC 时刻。</param>
        /// <param name="nowUtc">当前 UTC 时刻。</param>
        /// <returns>非负毫秒数。</returns>
        internal static long AgeMs(DateTime thenUtc, DateTime nowUtc)
        {
            double milliseconds = (nowUtc - thenUtc).TotalMilliseconds;
            if (milliseconds <= 0) return 0;
            if (milliseconds >= long.MaxValue) return long.MaxValue;
            return (long)milliseconds;
        }

        /// <summary>租约还剩多少毫秒（SPEC §8.1）；已过期返回 0。</summary>
        /// <param name="expiresAtUtc">claim 里记录的租约到期时刻。</param>
        /// <param name="nowUtc">当前 UTC 时刻。</param>
        /// <returns>非负毫秒数，过期即 0。</returns>
        internal static long LeaseRemainingMs(DateTime expiresAtUtc, DateTime nowUtc)
        {
            return AgeMs(nowUtc, expiresAtUtc);
        }

        /// <summary>
        /// 扫描全部端点 claims/ 下正在处理的请求（SPEC §8）。
        /// 先按 endpointId、再按认领时刻排序，让 `courier ps` 的两次输出可以直接 diff。
        /// </summary>
        /// <param name="root">协议根目录。</param>
        /// <returns>认领记录；损坏的 claim 文件被跳过。</returns>
        internal static IReadOnlyList<CliClaimRecord> ReadClaims(string root)
        {
            var claims = new List<CliClaimRecord>();
            IReadOnlyList<CourierEndpointInfo> endpoints = ReadEndpoints(root);
            for (int index = 0; index < endpoints.Count; index++)
            {
                string endpointId = endpoints[index].EndpointId;
                string claimsDirectory = Path.Combine(EndpointsDirectory(root), endpointId, CourierProtocol.ClaimsDirectory);
                string[] files;
                try
                {
                    files = Directory.GetFiles(claimsDirectory, "*" + CourierProtocol.JsonExtension);
                }
                catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException)
                {
                    // 某个端点的 claims/ 读不到：跳过它，不影响其余端点。
                    continue;
                }

                Array.Sort(files, StringComparer.Ordinal);
                for (int fileIndex = 0; fileIndex < files.Length; fileIndex++)
                {
                    string requestId = Path.GetFileNameWithoutExtension(files[fileIndex]);
                    if (!CourierProtocol.IsValidRequestId(requestId)) continue;
                    string? text;
                    if (!CourierIO.TryReadAllText(files[fileIndex], out text)) continue;
                    CourierClaim? claim = CourierClaim.TryParse(text);
                    if (claim == null) continue;
                    claims.Add(new CliClaimRecord(endpointId, requestId, claim));
                }
            }

            return claims;
        }
    }
}
