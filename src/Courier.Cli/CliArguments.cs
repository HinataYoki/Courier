using System.Collections.Generic;

namespace Courier.Cli
{
    /// <summary>
    /// 一次 `courier` 调用的原始参数。解析只做词法层面的拆分与选项归类，
    /// 语义校验留给各命令，这样"未知命令"与"参数错误"能共用同一套退出码与 --json 错误对象。
    /// </summary>
    internal sealed class CliArguments
    {
        private CliArguments()
        {
            Command = string.Empty;
            Positionals = new List<string>();
        }

        /// <summary>子命令名；没有给出时为 <see cref="string.Empty"/>。</summary>
        internal string Command { get; private set; }

        /// <summary>命令名之后的位置参数，按出现顺序。</summary>
        internal List<string> Positionals { get; }

        /// <summary>是否要求机器可读输出。</summary>
        internal bool Json { get; private set; }

        /// <summary>是否请求帮助（-h / --help）。</summary>
        internal bool HelpRequested { get; private set; }

        /// <summary>--root 的原始值；未给出时为 null，由 <see cref="CliRoot"/> 走零配置发现。</summary>
        internal string? Root { get; private set; }

        /// <summary>--payload 的原始值；未给出时为 null（命令里落成空串）。</summary>
        internal string? Payload { get; private set; }

        /// <summary>--timeout 的原始文本；未给出时为 null（缺省 10s）。</summary>
        internal string? TimeoutText { get; private set; }

        /// <summary>
        /// 拆分命令行。返回值即使为 false 也带有已经解析到的选项，
        /// 这样出错时仍能按 --json 的约定输出结构化错误对象。
        /// </summary>
        /// <param name="args">进程参数。</param>
        /// <param name="error">解析失败时的人类可读原因，成功时为 null。</param>
        /// <returns>是否解析成功。</returns>
        internal static CliArguments Parse(string[] args, out string? error)
        {
            error = null;
            var result = new CliArguments();

            for (int index = 0; index < args.Length; index++)
            {
                string token = args[index];
                if (token.Length == 0) continue;

                if (token[0] == '-' && token.Length > 1)
                {
                    string name = token;
                    string? inlineValue = null;
                    int separator = token.IndexOf('=');
                    if (separator > 0)
                    {
                        name = token.Substring(0, separator);
                        inlineValue = token.Substring(separator + 1);
                    }

                    switch (name)
                    {
                        case "--json":
                            if (inlineValue != null)
                            {
                                error = "--json does not take a value";
                                return result;
                            }
                            result.Json = true;
                            break;
                        case "-h":
                        case "--help":
                            result.HelpRequested = true;
                            break;
                        case "--root":
                        case "--payload":
                        case "--timeout":
                            // 三个选项的取值规则相同（--opt value 或 --opt=value），只有落点不同。
                            if (inlineValue == null)
                            {
                                if (index + 1 >= args.Length)
                                {
                                    error = name + " requires a value";
                                    return result;
                                }
                                index++;
                                inlineValue = args[index];
                            }

                            if (name == "--root") result.Root = inlineValue;
                            else if (name == "--payload") result.Payload = inlineValue;
                            else result.TimeoutText = inlineValue;
                            break;
                        default:
                            error = "unknown option \"" + token + "\"";
                            return result;
                    }
                    continue;
                }

                if (result.Command.Length == 0) result.Command = token;
                else result.Positionals.Add(token);
            }

            return result;
        }
    }
}
