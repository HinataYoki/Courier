using System;
using System.Collections.Generic;

namespace Courier.Cli
{
    /// <summary>
    /// 一次 `courier` 调用的原始参数。解析做两件事：词法层面的拆分与选项归类，以及"这个命令认不认这个选项"的
    /// 白名单校验。语义校验（时长、标识符、取值）留给各命令，这样"未知命令"与"参数错误"能共用同一套
    /// 退出码与 --json 错误对象。
    /// <para>
    /// 选项白名单按命令声明：`endpoints --timeout 5s` 这类"给了但根本用不上"的写法以前会被静默接受，
    /// 使用者以为超时生效了，其实一次都没被读过。静默忽略比报错更危险，所以这里统一报 invalid-argument。
    /// </para>
    /// </summary>
    internal sealed class CliArguments
    {
        /// <summary>要求机器可读输出。</summary>
        private const string OPTION_JSON = "--json";

        /// <summary>state/progress：输出文件原文而不是包装后的 JSON。</summary>
        private const string OPTION_RAW = "--raw";

        /// <summary>长/短两种帮助写法；记录时统一成长写法，白名单只需要认一个名字。</summary>
        private const string OPTION_HELP = "--help";
        private const string OPTION_HELP_SHORT = "-h";

        /// <summary>显式指定协议根目录。</summary>
        private const string OPTION_ROOT = "--root";

        /// <summary>零配置向上查找时使用的目录名。</summary>
        private const string OPTION_ROOT_NAME = "--root-name";

        /// <summary>call：交给 handler 的不透明字符串。</summary>
        private const string OPTION_PAYLOAD = "--payload";

        /// <summary>call：等待上限。</summary>
        private const string OPTION_TIMEOUT = "--timeout";

        /// <summary>gc：多久之前完成的响应才算够旧。</summary>
        private const string OPTION_OLDER_THAN = "--older-than";

        /// <summary>cancel：写进取消文件的原因。</summary>
        private const string OPTION_REASON = "--reason";

        /// <summary>解析过程中出现过的选项名（已归一化），供白名单校验使用。</summary>
        private readonly List<string> mSeenOptions;

        /// <summary>构造一个空参数集：命令为空串、位置参数为空、所有选项未给出。</summary>
        private CliArguments()
        {
            Command = string.Empty;
            Positionals = new List<string>();
            mSeenOptions = new List<string>();
        }

        /// <summary>子命令名；没有给出时为 <see cref="string.Empty"/>。</summary>
        internal string Command { get; private set; }

        /// <summary>命令名之后的位置参数，按出现顺序。</summary>
        internal List<string> Positionals { get; }

        /// <summary>是否要求机器可读输出。解析一开始就按原始参数整体扫描决定，见 <see cref="RequestsJson"/>。</summary>
        internal bool Json { get; private set; }

        /// <summary>是否请求帮助（-h / --help）。</summary>
        internal bool HelpRequested { get; private set; }

        /// <summary>--root 的原始值；未给出时为 null，由 <see cref="CliRoot"/> 走零配置发现。</summary>
        internal string? Root { get; private set; }

        /// <summary>
        /// --root-name 的原始值：零配置向上查找时用的目录名。
        /// 未给出时为 null，由 <see cref="CliRoot"/> 回落到 COURIER_ROOT_NAME 或 <c>.courier</c>。
        /// </summary>
        internal string? RootName { get; private set; }

        /// <summary>--payload 的原始值；未给出时为 null（命令里落成空串）。</summary>
        internal string? Payload { get; private set; }

        /// <summary>--timeout 的原始文本；未给出时为 null（缺省 10s）。</summary>
        internal string? TimeoutText { get; private set; }

        /// <summary>--older-than 的原始文本（GC 用）；未给出时为 null。</summary>
        internal string? OlderThanText { get; private set; }

        /// <summary>--reason 的原始文本（cancel 用）；未给出时为 null，落成 <c>requested</c>。</summary>
        internal string? ReasonText { get; private set; }

        /// <summary>
        /// --raw：让 <c>state --json</c> 保持"只输出 payload"的旧行为（SPEC §15.2 之外的可观测性约定）。
        /// 只影响显示，不改变状态文件本身；与 --json 互斥。
        /// </summary>
        internal bool Raw { get; private set; }

        /// <summary>
        /// 拆分命令行并校验选项白名单。返回值即使带 error 也带有已经解析到的选项，
        /// 这样出错时仍能按 --json 的约定输出结构化错误对象。
        /// </summary>
        /// <param name="args">进程参数。</param>
        /// <param name="error">解析失败时的人类可读原因，成功时为 null。</param>
        /// <returns>参数集；是否成功必须看 <paramref name="error"/>。</returns>
        internal static CliArguments Parse(string[] args, out string? error)
        {
            error = null;
            var result = new CliArguments();
            // --json 必须在任何报错之前就确定：`courier --bogus --json` 解析到 --bogus 就失败了，
            // 若按"此刻已解析到的选项"判断，输出会退化成人类可读文本，管道里的读方拿到半截句子。
            result.Json = RequestsJson(args);

            for (int index = 0; index < args.Length; index++)
            {
                string token = args[index];
                if (token.Length == 0) continue;

                if (token[0] == '-' && token.Length > 1)
                {
                    if (!result.TryApplyOption(args, ref index, token, out error)) return result;
                    continue;
                }

                if (result.Command.Length == 0) result.Command = token;
                else result.Positionals.Add(token);
            }

            result.TryValidate(out error);
            return result;
        }

        /// <summary>
        /// 在整份原始参数里找 --json。只做词法判断，不看它出现在哪个位置、也不管其余参数是否合法——
        /// 它的用途正是"其余参数已经不合法时的输出格式"。
        /// </summary>
        /// <param name="args">进程参数。</param>
        /// <returns>是否要求 JSON 输出。</returns>
        private static bool RequestsJson(string[] args)
        {
            for (int index = 0; index < args.Length; index++)
            {
                string token = args[index];
                if (string.Equals(token, OPTION_JSON, StringComparison.Ordinal)) return true;
                // --json=1 之类是错误的用法，但它表达了 JSON 意图，报错本身也应当结构化。
                if (token.StartsWith(OPTION_JSON + "=", StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>
        /// 处理一个以 '-' 开头的 token：拆出选项名与内联取值，必要时取出下一个 token 作为取值，然后落进对应字段。
        /// <para>
        /// 取值规则是 `--opt value` 或 `--opt=value`。分隔写法下，下一个 token 以 '-' 开头时一律判为"缺少取值"：
        /// 否则 `--payload --json` 会把 --json 吞成 payload 的内容，JSON 模式被静默丢掉。
        /// 值本身确实以 '-' 开头时用等号写法（`--payload=-x`），这条约定写进了 --help。
        /// </para>
        /// </summary>
        /// <param name="args">进程参数。</param>
        /// <param name="index">当前 token 下标；取走下一个 token 时会被推进。</param>
        /// <param name="token">当前 token 原文。</param>
        /// <param name="error">失败原因。</param>
        /// <returns>是否成功。</returns>
        private bool TryApplyOption(string[] args, ref int index, string token, out string? error)
        {
            error = null;
            string name = token;
            string? inlineValue = null;
            int separator = token.IndexOf('=');
            if (separator > 0)
            {
                name = token.Substring(0, separator);
                inlineValue = token.Substring(separator + 1);
            }

            string normalized = name == OPTION_HELP_SHORT ? OPTION_HELP : name;
            if (!IsValueOption(name))
            {
                if (inlineValue != null)
                {
                    error = name + " does not take a value (got \"" + inlineValue + "\")";
                    return false;
                }

                return ApplyFlag(normalized, out error);
            }

            if (inlineValue == null)
            {
                if (index + 1 >= args.Length)
                {
                    error = name + " requires a value";
                    return false;
                }

                string next = args[index + 1];
                if (next.Length > 0 && next[0] == '-')
                {
                    error = name + " requires a value: the next argument (" + next + ") looks like another option (write " + name + "=<value> if the value really starts with '-')";
                    return false;
                }

                index++;
                inlineValue = next;
            }

            ApplyValue(name, inlineValue);
            mSeenOptions.Add(normalized);
            return true;
        }

        /// <summary>取值类选项：它们的第二个 token 是取值而不是新选项。</summary>
        /// <param name="name">选项名（已去掉内联取值）。</param>
        /// <returns>是否需要紧跟一个取值。</returns>
        private static bool IsValueOption(string name)
        {
            switch (name)
            {
                case OPTION_ROOT:
                case OPTION_ROOT_NAME:
                case OPTION_PAYLOAD:
                case OPTION_TIMEOUT:
                case OPTION_OLDER_THAN:
                case OPTION_REASON:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>处理不带取值的开关选项；未知选项在这里被拒。</summary>
        /// <param name="name">归一化后的选项名。</param>
        /// <param name="error">失败原因。</param>
        /// <returns>是否成功。</returns>
        private bool ApplyFlag(string name, out string? error)
        {
            error = null;
            switch (name)
            {
                case OPTION_JSON: Json = true; break;
                case OPTION_HELP: HelpRequested = true; break;
                case OPTION_RAW: Raw = true; break;
                default:
                    error = "unknown option \"" + name + "\"";
                    return false;
            }

            mSeenOptions.Add(name);
            return true;
        }

        /// <summary>把取值落进对应字段。只影响字段，不做语义校验。</summary>
        /// <param name="name">选项名（已去掉内联取值）。</param>
        /// <param name="value">取值原文。</param>
        private void ApplyValue(string name, string value)
        {
            switch (name)
            {
                case OPTION_ROOT: Root = value; break;
                case OPTION_ROOT_NAME: RootName = value; break;
                case OPTION_PAYLOAD: Payload = value; break;
                case OPTION_OLDER_THAN: OlderThanText = value; break;
                case OPTION_REASON: ReasonText = value; break;
                default: TimeoutText = value; break;
            }
        }

        /// <summary>
        /// 命令已知之后的两条全局校验：选项是否属于这个命令，以及 --json 与 --raw 是否同时出现。
        /// 未知命令不校验选项——`courier bogus --reason x` 里更该报的是"没有这个命令"。
        /// </summary>
        /// <param name="error">失败原因。</param>
        /// <returns>是否通过。</returns>
        private bool TryValidate(out string? error)
        {
            error = null;
            if (Json && Raw)
            {
                error = OPTION_JSON + " and " + OPTION_RAW + " cannot be combined: --json wraps the document, --raw prints it as stored";
                return false;
            }

            if (Command.Length == 0 || !IsKnownCommand(Command)) return true;
            for (int index = 0; index < mSeenOptions.Count; index++)
            {
                string option = mSeenOptions[index];
                if (IsAllowedOption(Command, option)) continue;
                error = option + " does not apply to \"" + Command + "\" (it accepts " + DescribeAllowedOptions(Command) + ")";
                return false;
            }

            return true;
        }

        /// <summary>该名字是否是本 CLI 认识的子命令。</summary>
        /// <param name="command">子命令名。</param>
        /// <returns>认识为 true。</returns>
        private static bool IsKnownCommand(string command)
        {
            switch (command)
            {
                case "endpoints":
                case "describe":
                case "call":
                case "ps":
                case "state":
                case "progress":
                case "cancel":
                case "gc":
                case "version":
                case "help":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 某个命令认哪些选项。--json 与 --help 对每个命令都成立；--root / --root-name 对所有要读目录的命令成立，
        /// version/help 不碰目录因此不接受它们。其余选项各自只属于一条命令。
        /// </summary>
        /// <param name="command">已确认存在的子命令名。</param>
        /// <param name="option">归一化后的选项名。</param>
        /// <returns>是否允许。</returns>
        private static bool IsAllowedOption(string command, string option)
        {
            if (option == OPTION_HELP) return true;
            switch (option)
            {
                case OPTION_JSON: return true;
                case OPTION_ROOT:
                case OPTION_ROOT_NAME:
                    return command != "version" && command != "help";
                case OPTION_RAW:
                    return command == "state" || command == "progress";
                case OPTION_PAYLOAD:
                case OPTION_TIMEOUT:
                    return command == "call";
                case OPTION_OLDER_THAN:
                    return command == "gc";
                case OPTION_REASON:
                    return command == "cancel";
                default:
                    return false;
            }
        }

        /// <summary>把某个命令允许的选项排成一句话，放进报错里，让使用者不用翻 --help 就知道该怎么改。</summary>
        /// <param name="command">已确认存在的子命令名。</param>
        /// <returns>形如 <c>--json, --payload, --timeout</c> 的列表。</returns>
        private static string DescribeAllowedOptions(string command)
        {
            string[] candidates =
            {
                OPTION_JSON, OPTION_ROOT, OPTION_ROOT_NAME, OPTION_RAW,
                OPTION_PAYLOAD, OPTION_TIMEOUT, OPTION_OLDER_THAN, OPTION_REASON
            };
            var allowed = new List<string>(candidates.Length);
            for (int index = 0; index < candidates.Length; index++)
            {
                if (IsAllowedOption(command, candidates[index])) allowed.Add(candidates[index]);
            }

            return string.Join(", ", allowed);
        }
    }
}
