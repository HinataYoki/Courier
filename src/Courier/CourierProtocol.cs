using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Courier
{
    /// <summary>
    /// 协议常量、标识符校验与时间格式化。所有数值都来自 SPEC §19，改动任何一项都必须先改 SPEC。
    /// </summary>
    internal static class CourierProtocol
    {
        /// <summary>SPEC §4.4：唯一的版本字段，命令与响应里不得重复携带。</summary>
        internal const int PROTOCOL_VERSION = 2;

        /// <summary>SPEC §19：命令文件字节上限，1 MiB + 4 KiB，MUST NOT 配置。</summary>
        internal const int MAX_COMMAND_FILE_BYTES = (1024 * 1024) + 4096;

        /// <summary>SPEC §19：投递方判定背压的 commands/ 文件数阈值。</summary>
        internal const int MAX_COMMANDS_BACKPRESSURE = 4096;

        /// <summary>SPEC §19：单次 Tick 最多**启动**多少条命令，防止积压拖死宿主。</summary>
        internal const int MAX_TICK_BATCH = 16;

        /// <summary>SPEC §16.1：单个端点同时在跑的 handler 上限，防止积压时把线程池占满。</summary>
        internal const int MAX_INFLIGHT = 4;

        /// <summary>SPEC §19：同一条命令最多尝试几次。</summary>
        internal const int MAX_ATTEMPTS = 3;

        /// <summary>SPEC §19：单个状态文件的字节上限，超出 MUST 拒写而不是截断（§14）。</summary>
        internal const int MAX_STATE_BYTES = 1024 * 1024;

        /// <summary>SPEC §19：单个端点可声明的状态名上限。</summary>
        internal const int MAX_STATE_NAMES = 256;

        /// <summary>
        /// 复用的 CSPRNG 句柄。临时文件名、备份名与 requestId 随机段每次落盘都要取随机数，
        /// 而 Create/Dispose 在 Windows 上会反复申请 BCrypt 算法对象，是纯可避免的开销。
        /// </summary>
        private static readonly RandomNumberGenerator sRandomSource = RandomNumberGenerator.Create();

        /// <summary>保护 <see cref="sRandomSource"/>：CSPRNG 的实例方法不保证线程安全。</summary>
        private static readonly object sRandomGate = new object();

        /// <summary>保护 <see cref="sProcessId"/>，见 <see cref="CurrentProcessId"/>。</summary>
        private static readonly object sProcessIdGate = new object();

        /// <summary>缓存的进程号；-1 表示尚未查询。</summary>
        private static int sProcessId = -1;

        /// <summary>
        /// 协议时间的可接受写法：无小数秒，或 1–7 位小数秒。两个格式串都以字面量 Z 结尾，
        /// 因此带偏移量（+08:00）与不带时区的文本都会被 TryParseExact 拒绝。
        /// </summary>
        private static readonly string[] sUtcFormats =
        {
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm:ss'Z'"
        };

        /// <summary>
        /// SPEC §10.1：claim 的租约长度。认领时写 expiresAtUtc = now + CLAIM_WINDOW，
        /// 执行期间由 Tick 按 <see cref="CLAIM_RENEW_INTERVAL"/> 续约。慢 handler 靠续约维持租约，
        /// 不靠把窗口调大——窗口只需要大于一个续约周期，不需要大于最长执行时间。
        /// </summary>
        internal static readonly TimeSpan CLAIM_WINDOW = TimeSpan.FromSeconds(10);

        /// <summary>SPEC §19：续约间隔，MUST 小于 <see cref="CLAIM_WINDOW"/>。</summary>
        internal static readonly TimeSpan CLAIM_RENEW_INTERVAL = TimeSpan.FromSeconds(2);

        /// <summary>SPEC §19：投递时默认的 deadline 长度，可由调用方按次覆盖。</summary>
        internal static readonly TimeSpan DEFAULT_DEADLINE = TimeSpan.FromSeconds(10);

        /// <summary>SPEC §19：调用方给的 deadline 上限，超过即截断，防止命令在盘上躺太久。</summary>
        internal static readonly TimeSpan MAX_DEADLINE = TimeSpan.FromSeconds(300);

        /// <summary>SPEC §6：age 小于等于该值即 alive。</summary>
        internal static readonly TimeSpan HEARTBEAT_ALIVE = TimeSpan.FromSeconds(5);

        /// <summary>SPEC §6：age 大于该值即 dead。</summary>
        internal static readonly TimeSpan HEARTBEAT_DEAD = TimeSpan.FromSeconds(15);

        /// <summary>SPEC §11：handler 正常返回。</summary>
        internal const string STATUS_OK = "ok";

        /// <summary>SPEC §11.1：请求本身不合法，未被执行业务逻辑，重试通常无意义。</summary>
        internal const string STATUS_REJECTED = "rejected";

        /// <summary>SPEC §11.1：业务逻辑执行了但失败，与 rejected 严格区分。</summary>
        internal const string STATUS_FAILED = "failed";

        /// <summary>SPEC §11.2：调用方在超时后本地合成的结果，不落盘、没有对应文件。</summary>
        internal const string STATUS_UNREACHABLE = "unreachable";

        internal const string REASON_STALE = "stale";
        internal const string REASON_UNKNOWN_ACTION = "unknown-action";
        internal const string REASON_INVALID_COMMAND = "invalid-command";
        internal const string REASON_CANCELLED_BEFORE_START = "cancelled-before-start";
        internal const string REASON_BACKPRESSURE = "backpressure";
        internal const string REASON_HANDLER_ERROR = "handler-error";
        internal const string REASON_HANDLER_TIMEOUT = "handler-timeout";
        internal const string REASON_CANCELLED = "cancelled";

        /// <summary>SPEC §6：心跳年龄在阈值内。</summary>
        internal const string STATE_ALIVE = "alive";

        /// <summary>SPEC §6：心跳偏旧，可能是宿主卡顿。</summary>
        internal const string STATE_STALE = "stale";

        /// <summary>SPEC §6：心跳超过死亡阈值。</summary>
        internal const string STATE_DEAD = "dead";

        internal const string ENDPOINTS_DIRECTORY = "endpoints";
        internal const string COMMANDS_DIRECTORY = "commands";
        internal const string CLAIMS_DIRECTORY = "claims";
        internal const string RESULTS_DIRECTORY = "results";
        internal const string DEADLETTER_DIRECTORY = "deadletter";
        internal const string CANCELS_DIRECTORY = "cancels";
        internal const string PROGRESS_DIRECTORY = "progress";
        internal const string STATE_DIRECTORY = "state";
        internal const string ARCHIVE_DIRECTORY = "archive";
        internal const string ENDPOINT_FILE_NAME = "endpoint.json";
        internal const string HEARTBEAT_FILE_NAME = "heartbeat";

        /// <summary>SPEC §8：单实例互斥锁文件名。内容无意义，语义完全来自共享模式。</summary>
        internal const string HOST_LOCK_FILE_NAME = "host.lock";

        /// <summary>
        /// SPEC §5：endpoint.json 里能力声明的名字长度上限。与 §4.3 的 channel/action/state 同一个正则，
        /// 因为它们最终都落进同一份能力目录，没有任何理由给第三个名字空间另定一套规则。
        /// </summary>
        internal const int MAX_CAPABILITY_NAME_LENGTH = 64;

        /// <summary>SPEC §5：单个端点可声明的能力上限，与 MAX_STATE_NAMES 同源取自 §19。</summary>
        internal const int MAX_CAPABILITIES = 256;

        // ---- 可选能力的自述名（SPEC §5、§21）----
        // 只用 §5 已经写明的这几个：未定义的名字读方无法解释，写上去只会让"读方正确退化"这条更模糊。

        /// <summary>SPEC §14：实现了 state/ 状态发布。</summary>
        internal const string CAPABILITY_STATE = "state";

        /// <summary>SPEC §13.1：实现了 cancels/ 取消。</summary>
        internal const string CAPABILITY_CANCELS = "cancels";

        /// <summary>SPEC §13.2：实现了 progress/ 进度发布。</summary>
        internal const string CAPABILITY_PROGRESS = "progress";

        /// <summary>SPEC §8：启用并实现了 host.lock 单实例互斥。</summary>
        internal const string CAPABILITY_HOST_LOCK = "host-lock";

        /// <summary>SPEC §7：实现了 root 级注册表。</summary>
        internal const string CAPABILITY_REGISTRY = "registry";

        /// <summary>时间戳的日期目录名格式。<c>archive/</c> 的保留策略与死信保留都按它比较（SPEC §15.1、§15.3）。</summary>
        internal const string DATE_FORMAT = "yyyy-MM-dd";
        internal const string JSON_EXTENSION = ".json";

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

        /// <summary>
        /// SPEC §4.3：channel、action 与 state 名共用同一套命名规则，区分大小写；为 true 时参数必非 null。
        /// 正则只是第一道闸，"."、".." 与首尾点这些在路径语义上有特殊含义的名字在这里一并拒掉。
        /// </summary>
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

            // 名字要直接拼成文件名，"." 与 ".." 会分别落到当前目录与父目录上。
            if (value[0] == '.' || value[value.Length - 1] == '.') return false;
            return true;
        }

        /// <summary>SPEC §4.2：requestId 必须是时间戳前缀加 8–32 位小写 hex，随机段 MUST 存在。</summary>
        internal static bool IsValidRequestId(string? value)
        {
            // 前缀 "yyyyMMddTHHmmssZ-" 定长 17，随机段 8–32 位，故总长 25–49。
            if (value == null || value.Length < 25 || value.Length > 49) return false;
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
            for (int index = 17; index < value.Length; index++)
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

        /// <summary>
        /// 生成新的会话标识（SPEC §5）。每次启动换新，让读方分辨「同一端点的新一轮运行」——
        /// processId 会被 OS 复用、startedAtUtc 可能被复制，只有它是这个实例独有的。
        /// </summary>
        internal static string NewSessionId()
        {
            return RandomHex(16);
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
            if (heartbeatAgeMs == null) return STATE_DEAD;
            if (heartbeatAgeMs.Value <= (long)HEARTBEAT_ALIVE.TotalMilliseconds) return STATE_ALIVE;
            if (heartbeatAgeMs.Value <= (long)HEARTBEAT_DEAD.TotalMilliseconds) return STATE_STALE;
            return STATE_DEAD;
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
                sUtcFormats,
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
            const string ALPHABET = "0123456789abcdef";
            byte[] buffer = new byte[(digits + 1) / 2];
            // 用 GetBytes 而不是 RandomNumberGenerator.Fill：后者不在 netstandard2.1 里，
            // 而本实现必须能作为源码直接拖进 .NET Standard 2.1 宿主。
            lock (sRandomGate)
            {
                sRandomSource.GetBytes(buffer);
            }

            char[] chars = new char[digits];
            for (int index = 0; index < digits; index++)
            {
                byte value = buffer[index / 2];
                chars[index] = ALPHABET[index % 2 == 0 ? value >> 4 : value & 0x0F];
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
            lock (sProcessIdGate)
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
