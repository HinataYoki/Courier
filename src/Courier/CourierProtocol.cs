using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Courier
{
    /// <summary>
    /// 协议常量、标识符校验与时间格式化。所有数值都来自 SPEC §16，改动任何一项都必须先改 SPEC。
    /// </summary>
    internal static class CourierProtocol
    {
        /// <summary>SPEC §4.4：唯一的版本字段，命令与响应里不得重复携带。</summary>
        internal const int ProtocolVersion = 1;

        /// <summary>SPEC §16：命令文件字节上限，1 MiB + 4 KiB，MUST NOT 配置。</summary>
        internal const int MaxCommandFileBytes = (1024 * 1024) + 4096;

        /// <summary>SPEC §16：投递方判定背压的 commands/ 文件数阈值。</summary>
        internal const int MaxCommandsBackpressure = 4096;

        /// <summary>SPEC §16：单次 Tick 最多**启动**多少条命令，防止积压拖死宿主。</summary>
        internal const int MaxTickBatch = 16;

        /// <summary>SPEC §13.1：单个端点同时在跑的 handler 上限，防止积压时把线程池占满。</summary>
        internal const int MaxInflight = 4;

        /// <summary>SPEC §16：同一条命令最多尝试几次。</summary>
        internal const int MaxAttempts = 3;

        /// <summary>
        /// 复用的 CSPRNG 句柄。临时文件名、备份名与 requestId 随机段每次落盘都要取随机数，
        /// 而 Create/Dispose 在 Windows 上会反复申请 BCrypt 算法对象，是纯可避免的开销。
        /// </summary>
        private static readonly RandomNumberGenerator RandomSource = RandomNumberGenerator.Create();

        /// <summary>保护 <see cref="RandomSource"/>：CSPRNG 的实例方法不保证线程安全。</summary>
        private static readonly object RandomGate = new object();

        /// <summary>保护 <see cref="sProcessId"/>，见 <see cref="CurrentProcessId"/>。</summary>
        private static readonly object ProcessIdGate = new object();

        /// <summary>缓存的进程号；-1 表示尚未查询。</summary>
        private static int sProcessId = -1;

        /// <summary>
        /// 协议时间的可接受写法：无小数秒，或 1–7 位小数秒。两个格式串都以字面量 Z 结尾，
        /// 因此带偏移量（+08:00）与不带时区的文本都会被 TryParseExact 拒绝。
        /// </summary>
        private static readonly string[] UtcFormats =
        {
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm:ss'Z'"
        };

        /// <summary>
        /// SPEC §8.1：claim 的租约长度。认领时写 expiresAtUtc = now + ClaimWindow，
        /// 执行期间由 Tick 按 <see cref="ClaimRenewInterval"/> 续约。慢 handler 靠续约维持租约，
        /// 不靠把窗口调大——窗口只需要大于一个续约周期，不需要大于最长执行时间。
        /// </summary>
        internal static readonly TimeSpan ClaimWindow = TimeSpan.FromSeconds(10);

        /// <summary>SPEC §16：续约间隔，MUST 小于 <see cref="ClaimWindow"/>。</summary>
        internal static readonly TimeSpan ClaimRenewInterval = TimeSpan.FromSeconds(2);

        /// <summary>SPEC §16：投递时默认的 deadline 长度，可由调用方按次覆盖。</summary>
        internal static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(10);

        /// <summary>SPEC §16：调用方给的 deadline 上限，超过即截断，防止命令在盘上躺太久。</summary>
        internal static readonly TimeSpan MaxDeadline = TimeSpan.FromSeconds(300);

        /// <summary>SPEC §6：age 小于等于该值即 alive。</summary>
        internal static readonly TimeSpan HeartbeatAlive = TimeSpan.FromSeconds(5);

        /// <summary>SPEC §6：age 大于该值即 dead。</summary>
        internal static readonly TimeSpan HeartbeatDead = TimeSpan.FromSeconds(15);

        /// <summary>SPEC §9：handler 正常返回。</summary>
        internal const string StatusOk = "ok";

        /// <summary>SPEC §9.1：请求本身不合法，未被执行业务逻辑，重试通常无意义。</summary>
        internal const string StatusRejected = "rejected";

        /// <summary>SPEC §9.1：业务逻辑执行了但失败，与 rejected 严格区分。</summary>
        internal const string StatusFailed = "failed";

        /// <summary>SPEC §9.2：调用方在超时后本地合成的结果，不落盘、没有对应文件。</summary>
        internal const string StatusUnreachable = "unreachable";

        internal const string ReasonStale = "stale";
        internal const string ReasonUnknownAction = "unknown-action";
        internal const string ReasonInvalidCommand = "invalid-command";
        internal const string ReasonCancelledBeforeStart = "cancelled-before-start";
        internal const string ReasonBackpressure = "backpressure";
        internal const string ReasonHandlerError = "handler-error";
        internal const string ReasonHandlerTimeout = "handler-timeout";
        internal const string ReasonCancelled = "cancelled";

        /// <summary>SPEC §6：心跳年龄在阈值内。</summary>
        internal const string StateAlive = "alive";

        /// <summary>SPEC §6：心跳偏旧，可能是宿主卡顿。</summary>
        internal const string StateStale = "stale";

        /// <summary>SPEC §6：心跳超过死亡阈值。</summary>
        internal const string StateDead = "dead";

        internal const string EndpointsDirectory = "endpoints";
        internal const string CommandsDirectory = "commands";
        internal const string ClaimsDirectory = "claims";
        internal const string ResultsDirectory = "results";
        internal const string DeadletterDirectory = "deadletter";
        internal const string CancelsDirectory = "cancels";
        internal const string ProgressDirectory = "progress";
        internal const string ArchiveDirectory = "archive";
        internal const string EndpointFileName = "endpoint.json";
        internal const string HeartbeatFileName = "heartbeat";
        internal const string JsonExtension = ".json";

        /// <summary>SPEC §4.1：endpointId 是唯一的路径拼接入口，不校验等于允许 ../ 打穿 root。</summary>
        internal static bool IsValidEndpointId(string? value)
        {
            if (value == null || value.Length == 0 || value.Length > 64) return false;
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                bool alphanumeric = (current >= 'a' && current <= 'z') || (current >= '0' && current <= '9');
                // 连字符只允许出现在中间：首尾必须是字母或数字，否则 "-" 与 ".." 能拼出穿越 root 的路径。
                if (!alphanumeric && (current != '-' || index == 0 || index == value.Length - 1)) return false;
            }

            return true;
        }

        /// <summary>SPEC §4.3：channel 与 action 共用同一套命名规则，区分大小写；为 true 时参数必非 null。</summary>
        internal static bool IsValidName([NotNullWhen(true)] string? value)
        {
            if (value == null || value.Length == 0 || value.Length > 64) return false;
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                bool valid = (current >= 'a' && current <= 'z')
                    || (current >= 'A' && current <= 'Z')
                    || (current >= '0' && current <= '9')
                    || current == '_' || current == '.' || current == '-';
                if (!valid) return false;
            }
            return true;
        }

        /// <summary>SPEC §4.2：requestId 必须是时间戳前缀加 8 位小写 hex，随机段 MUST 存在。</summary>
        internal static bool IsValidRequestId(string? value)
        {
            if (value == null || value.Length != 25) return false;
            for (int index = 0; index < 8; index++)
            {
                if (value[index] < '0' || value[index] > '9') return false;
            }
            if (value[8] != 'T') return false;
            for (int index = 9; index < 15; index++)
            {
                if (value[index] < '0' || value[index] > '9') return false;
            }
            if (value[15] != 'Z' || value[16] != '-') return false;
            for (int index = 17; index < 25; index++)
            {
                char current = value[index];
                bool valid = (current >= '0' && current <= '9') || (current >= 'a' && current <= 'f');
                if (!valid) return false;
            }
            return true;
        }

        /// <summary>生成新的 requestId。随机段用加密随机数，避免同秒内两个进程撞名（SPEC §4.2）。</summary>
        internal static string NewRequestId()
        {
            DateTime now = DateTime.UtcNow;
            return now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" + RandomHex(8);
        }

        /// <summary>SPEC §3.2：UTC、RFC 3339、以 Z 结尾、7 位小数秒。</summary>
        internal static string FormatUtc(DateTime value)
        {
            return value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
        }

        /// <summary>SPEC §6：心跳年龄换算成活性的唯一实现，库与 CLI 共用。</summary>
        /// <param name="heartbeatAgeMs">心跳年龄（毫秒）；null 表示读不到心跳。</param>
        /// <returns><c>alive</c>、<c>stale</c> 或 <c>dead</c>。</returns>
        internal static string StateFromHeartbeatAge(long? heartbeatAgeMs)
        {
            if (heartbeatAgeMs == null) return StateDead;
            if (heartbeatAgeMs.Value <= (long)HeartbeatAlive.TotalMilliseconds) return StateAlive;
            if (heartbeatAgeMs.Value <= (long)HeartbeatDead.TotalMilliseconds) return StateStale;
            return StateDead;
        }

        /// <summary>
        /// 解析协议时间（SPEC §3.2）。只接受 RFC 3339 的 UTC 形式：<c>yyyy-MM-ddTHH:mm:ss</c> 后跟可选的
        /// 1–7 位小数秒，并以大写 <c>Z</c> 结尾。偏移量、本地时间与更宽的小数秒一律拒绝——
        /// 宽松解析会让另一个实现写出的非协议时间被悄悄当成合法值。
        /// </summary>
        /// <param name="text">待解析文本；首尾空白被忽略。</param>
        /// <param name="value">解析成功时的 UTC 时刻。</param>
        /// <returns>是否为合法的协议时间。</returns>
        internal static bool TryParseUtc(string? text, out DateTime value)
        {
            value = default;
            if (text == null) return false;
            return DateTime.TryParseExact(
                text.Trim(),
                UtcFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out value);
        }

        /// <summary>
        /// 生成指定长度的随机小写 hex 串，用于临时文件名、备份名与 requestId 随机段。
        /// 每次原子写都要取一次随机数，因此 CSPRNG 句柄复用而不是每次新建。
        /// </summary>
        internal static string RandomHex(int digits)
        {
            const string Alphabet = "0123456789abcdef";
            byte[] buffer = new byte[(digits + 1) / 2];
            // 用 GetBytes 而不是 RandomNumberGenerator.Fill：后者不在 netstandard2.1 里，
            // 而本实现必须能作为源码直接拖进 .NET Standard 2.1 宿主。
            lock (RandomGate)
            {
                RandomSource.GetBytes(buffer);
            }

            char[] chars = new char[digits];
            for (int index = 0; index < digits; index++)
            {
                byte value = buffer[index / 2];
                chars[index] = Alphabet[index % 2 == 0 ? value >> 4 : value & 0x0F];
            }
            return new string(chars);
        }

        /// <summary>
        /// 当前进程号，仅用于写入 endpoint.json 作诊断（SPEC §5：processId 是 MAY，不得用于判活）。
        /// 不用 Environment.ProcessId（不在 netstandard2.1 里）；受限宿主拿不到进程号时退化为 0。
        /// </summary>
        internal static int CurrentProcessId()
        {
            // 进程号在进程生命周期内不变：endpoint.json 每次注册能力都会重写，不必每次去查系统。
            lock (ProcessIdGate)
            {
                if (sProcessId < 0)
                {
                    try
                    {
                        using (Process process = Process.GetCurrentProcess()) sProcessId = process.Id;
                    }
                    catch (Exception)
                    {
                        sProcessId = 0;
                    }
                }

                return sProcessId;
            }
        }

    }
}
