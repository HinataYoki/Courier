using System;
using System.Collections.Generic;

namespace Courier.Cli
{
    /// <summary>
    /// `courier ps`：列出所有端点 claims/ 里正在处理的请求（SPEC §8）。
    /// <para>
    /// 这条命令回答"现在卡在哪条命令上"，只读 claims/，不需要新字段、不需要端点配合。
    /// </para>
    /// </summary>
    internal static class CliPsCommand
    {
        /// <summary>执行命令。</summary>
        /// <param name="arguments">已解析的命令行。</param>
        /// <param name="root">已解析的协议根目录。</param>
        /// <returns>退出码。</returns>
        internal static int Run(CliArguments arguments, string root)
        {
            IReadOnlyList<CliClaimRecord> claims = CliDirectory.ReadClaims(root);
            DateTime now = DateTime.UtcNow;

            if (arguments.Json)
            {
                var items = new List<string>(claims.Count);
                for (int index = 0; index < claims.Count; index++)
                {
                    CliClaimRecord claim = claims[index];
                    items.Add(new CourierJsonObject()
                        .AddString("endpointId", claim.EndpointId)
                        .AddString("requestId", claim.RequestId)
                        .AddString("owner", claim.Claim.Owner)
                        .AddString("claimedAtUtc", CourierProtocol.FormatUtc(claim.Claim.ClaimedAtUtc))
                        .AddNumber("ageMs", CliDirectory.AgeMs(claim.Claim.ClaimedAtUtc, now))
                        .AddNumber("attempts", claim.Claim.Attempts)
                        .Build());
                }

                CliOutput.WriteLine(new CourierJsonObject().AddRaw("claims", CourierJson.Array(items)).Build());
                return CliExitCode.Ok;
            }

            if (claims.Count == 0)
            {
                Console.Error.Write("courier: no endpoint is currently processing a request\n");
                return CliExitCode.Ok;
            }

            for (int index = 0; index < claims.Count; index++)
            {
                CliClaimRecord claim = claims[index];
                long age = CliDirectory.AgeMs(claim.Claim.ClaimedAtUtc, now);
                CliOutput.WriteLine(
                    claim.EndpointId + "  " + claim.RequestId
                    + "  owner=" + claim.Claim.Owner
                    + "  claimed " + CliOutput.FormatSeconds(age) + " ago"
                    + "  attempt " + claim.Claim.Attempts);
            }

            return CliExitCode.Ok;
        }
    }
}
