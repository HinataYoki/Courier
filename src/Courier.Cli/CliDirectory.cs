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
    /// <para>
    /// 每个入口都带一个 <see cref="CourierLayout"/>：目录名是接入软件的自由，
    /// CLI 不能假设 endpoints/ 一定叫 endpoints。布局由 <see cref="TryLoadLayout"/> 统一决定。
    /// </para>
    /// </summary>
    internal static class CliDirectory
    {
        /// <summary>人类可读列表里 endpointId 列的最小宽度，保证 description 的括号能对齐。</summary>
        internal const int ENDPOINT_ID_COLUMN_WIDTH = 8;

        /// <summary>
        /// 读出 root 下生效的布局：优先 root 里的 courier.layout.json，没有就是默认布局。
        /// <para>
        /// 文件**存在但读不懂**时返回 false，绝不退回默认布局：默认布局意味着"endpoints/ 与 state/ 这些名字",
        /// 拿它去读一份按别的布局写成的目录，结论会是"没有端点"——这是次生错误，会把排查方向直接带偏。
        /// 所以这里把判断交回调用方，由它用独立的错误码退出（CourierLayout.Load 的注释同理）。
        /// </para>
        /// </summary>
        /// <param name="root">协议根目录。</param>
        /// <param name="layout">成功时的布局；失败时是默认布局，调用方不得使用。</param>
        /// <param name="detail">失败原因（含修复提示），供 --json 错误对象使用。</param>
        /// <returns>布局是否可用。</returns>
        internal static bool TryLoadLayout(string root, out CourierLayout layout, out string? detail)
        {
            if (CourierLayout.TryLoad(root, out layout, out detail)) return true;
            detail = CourierLayout.LAYOUT_FILE_NAME + " under " + root + " could not be read: " + detail
                + " (fix the file, or delete it to use the default layout name set)";
            return false;
        }

        /// <summary>root 下的端点目录。</summary>
        /// <param name="root">协议根目录。</param>
        /// <param name="layout">生效的目录布局。</param>
        /// <returns>绝对路径。</returns>
        internal static string EndpointsDirectory(string root, CourierLayout layout)
        {
            return CourierLayout.Combine(root, layout.EndpointsDirectory);
        }

        /// <summary>单个端点的目录；<paramref name="endpointId"/> 必须已通过 SPEC §4.1 校验。</summary>
        /// <param name="root">协议根目录。</param>
        /// <param name="layout">生效的目录布局。</param>
        /// <param name="endpointId">已校验的端点标识。</param>
        /// <returns>绝对路径。</returns>
        internal static string EndpointDirectory(string root, CourierLayout layout, string endpointId)
        {
            return Path.Combine(EndpointsDirectory(root, layout), endpointId);
        }

        /// <summary>
        /// 判断一个端点是否"注册在册"：目录存在且 endpoint.json 可读。
        /// <para>
        /// 投递前必须先问这一句。目录根本不存在时，命令文件写得进去也没有任何进程会读它，
        /// 调用方只能干等到超时——而 describe/gc/cancel 早就直接报 endpoint-not-found 了，
        /// 同一个"端点不存在"在不同命令上不该有两种代价。
        /// </para>
        /// </summary>
        /// <param name="root">协议根目录。</param>
        /// <param name="layout">生效的目录布局。</param>
        /// <param name="endpointId">已校验的端点标识。</param>
        /// <returns>endpoint.json 是否存在。</returns>
        internal static bool HasRegisteredEndpoint(string root, CourierLayout layout, string endpointId)
        {
            string directory = EndpointDirectory(root, layout, endpointId);
            if (!Directory.Exists(directory)) return false;
            return File.Exists(Path.Combine(directory, layout.EndpointFileName));
        }

        /// <summary>扫描全部端点，按 endpointId 升序返回。</summary>
        /// <param name="root">协议根目录。</param>
        /// <param name="layout">生效的目录布局。</param>
        /// <returns>能成功解析的端点；损坏或不合法的目录被跳过，而不是让整条命令失败。</returns>
        internal static IReadOnlyList<CourierEndpointInfo> ReadEndpoints(string root, CourierLayout layout)
        {
            return CourierEndpointReader.ReadAll(EndpointsDirectory(root, layout));
        }

        /// <summary>读单个端点；不存在或损坏时返回 null。</summary>
        /// <param name="root">协议根目录。</param>
        /// <param name="layout">生效的目录布局。</param>
        /// <param name="endpointId">已通过合法性校验的端点标识。</param>
        /// <returns>端点视图，或 null。</returns>
        internal static CourierEndpointInfo? ReadEndpoint(string root, CourierLayout layout, string endpointId)
        {
            return CourierEndpointReader.TryRead(EndpointDirectory(root, layout, endpointId));
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

        /// <summary>租约还剩多少毫秒（SPEC §10.1）；已过期返回 0。</summary>
        /// <param name="expiresAtUtc">claim 里记录的租约到期时刻。</param>
        /// <param name="nowUtc">当前 UTC 时刻。</param>
        /// <returns>非负毫秒数，过期即 0。</returns>
        internal static long LeaseRemainingMs(DateTime expiresAtUtc, DateTime nowUtc)
        {
            return AgeMs(nowUtc, expiresAtUtc);
        }

        /// <summary>读一个端点发布的状态原文（SPEC §14）；文件不存在或读不到时返回 null。</summary>
        /// <param name="root">协议根目录。</param>
        /// <param name="layout">生效的目录布局。</param>
        /// <param name="endpointId">已通过合法性校验的端点标识。</param>
        /// <param name="name">已通过合法性校验的状态名。</param>
        /// <param name="text">状态文件的原始文本。</param>
        /// <returns>文件是否存在且可读。</returns>
        internal static bool TryReadState(string root, CourierLayout layout, string endpointId, string name, out string? text)
        {
            string path = Path.Combine(
                EndpointDirectory(root, layout, endpointId),
                layout.StateDirectory,
                name + CourierProtocol.JSON_EXTENSION);
            return CourierIO.TryReadAllText(path, out text);
        }

        /// <summary>
        /// 扫描全部端点 claims/ 下正在处理的请求（SPEC §10）。
        /// 先按 endpointId、再按认领时刻排序，让 `courier ps` 的两次输出可以直接 diff。
        /// </summary>
        /// <param name="root">协议根目录。</param>
        /// <param name="layout">生效的目录布局。</param>
        /// <returns>认领记录；损坏的 claim 文件被跳过。</returns>
        internal static IReadOnlyList<CliClaimRecord> ReadClaims(string root, CourierLayout layout)
        {
            var claims = new List<CliClaimRecord>();
            IReadOnlyList<CourierEndpointInfo> endpoints = ReadEndpoints(root, layout);
            for (int index = 0; index < endpoints.Count; index++)
            {
                string endpointId = endpoints[index].EndpointId;
                string claimsDirectory = Path.Combine(EndpointDirectory(root, layout, endpointId), layout.ClaimsDirectory);
                string[] files;
                try
                {
                    files = Directory.GetFiles(claimsDirectory, "*" + CourierProtocol.JSON_EXTENSION);
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
