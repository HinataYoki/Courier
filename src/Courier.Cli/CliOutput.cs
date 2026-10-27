using System;
using System.Globalization;

namespace Courier.Cli
{
    /// <summary>
    /// 输出约定：人类可读走 stdout，机器可读是一行紧凑 JSON；
    /// 错误走 stderr，--json 时是一条结构化的错误对象，永远不掺日志、不掺杂色。
    /// </summary>
    internal static class CliOutput
    {
        /// <summary>写一行人类可读文本到 stdout。</summary>
        /// <param name="text">已经格式化好的整行，不带换行。</param>
        internal static void WriteLine(string text)
        {
            Console.Out.Write(text);
            Console.Out.Write('\n');
        }

        /// <summary>
        /// 输出一次失败。--json 时输出 <c>{"error":...,"detail":...}</c> 到 stderr，
        /// 否则输出一句完整句子。
        /// </summary>
        /// <param name="json">是否要求机器可读输出。</param>
        /// <param name="error">稳定的机器可判别代码。</param>
        /// <param name="detail">人类可读的补充说明。</param>
        internal static void WriteError(bool json, string error, string detail)
        {
            if (json)
            {
                Console.Error.Write(new CourierJsonObject()
                    .AddString("error", error)
                    .AddString("detail", detail)
                    .Build());
                Console.Error.Write('\n');
                return;
            }

            Console.Error.Write("courier: " + detail);
            Console.Error.Write('\n');
        }

        /// <summary>把毫秒渲染成"1.2s"形式：一位小数，十进制点固定。</summary>
        /// <param name="milliseconds">心跳年龄或 claim 年龄。</param>
        /// <returns>例如 <c>1.2s</c>、<c>0.0s</c>。</returns>
        internal static string FormatSeconds(long milliseconds)
        {
            long clamped = milliseconds < 0 ? 0 : milliseconds;
            return (clamped / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s";
        }
    }
}
