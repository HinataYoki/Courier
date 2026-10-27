using System;
using System.Globalization;

namespace Courier.Cli
{
    /// <summary>
    /// 解析 --timeout / --older-than：接受 <c>500ms</c>、<c>10s</c>、<c>1m</c>、<c>2h</c>、<c>7d</c> 写法，
    /// 也接受纯数字（按秒）。结果必须是正数，否则由调用方转成参数错误而不是异常。
    /// <para>
    /// <c>h</c> 与 <c>d</c> 是给 <c>courier gc --older-than</c> 用的：保留期天然以天/小时计，
    /// 而用法文本与 README 一直写的是 <c>--older-than 1h</c>，解析器却不认这个单位。
    /// </para>
    /// </summary>
    internal static class CliDuration
    {
        /// <summary>
        /// 时长参数不合法时的机器可判断错误码。--timeout 与 --older-than 共用同一个：
        /// 两者用的是同一个解析器、同一套写法，报出两个码只会让调用方以为它们规则不同。
        /// </summary>
        internal const string ERROR_CODE = "invalid-duration";

        /// <summary>
        /// 拼出「这个时长参数不合法」的完整错误上下文：选项名、期望的写法、实际拿到的文本。
        /// 两个命令共用一份文案，避免同一种错误在两条命令上说法不一致。
        /// </summary>
        /// <param name="optionName">选项名，形如 <c>--timeout</c>。</param>
        /// <param name="text">用户给出的原始文本。</param>
        /// <returns>可直接交给 <see cref="CliOutput.WriteError"/> 的说明。</returns>
        internal static string DescribeFailure(string optionName, string? text)
        {
            return optionName + " expects a positive duration such as 500ms, 10s, 1m, 1h or 7d (got \"" + text + "\")";
        }

        /// <summary>解析时长文本。</summary>
        /// <param name="text">形如 <c>500ms</c> / <c>10s</c> / <c>1m</c> / <c>2h</c> / <c>7d</c> / <c>2</c> 的文本。</param>
        /// <param name="value">解析成功时的时长。</param>
        /// <returns>是否解析成功且为正数。</returns>
        internal static bool TryParse(string? text, out TimeSpan value)
        {
            value = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;

            // IsNullOrWhiteSpace 带 [NotNullWhen(false)]，走到这里 text 必非 null。
            string token = text.Trim();
            string unit;
            SplitUnit(token, out token, out unit);

            double amount;
            if (!double.TryParse(token.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out amount)) return false;
            if (double.IsNaN(amount) || double.IsInfinity(amount) || amount <= 0) return false;

            try
            {
                switch (unit)
                {
                    case "ms": value = TimeSpan.FromMilliseconds(amount); break;
                    case "m": value = TimeSpan.FromMinutes(amount); break;
                    case "h": value = TimeSpan.FromHours(amount); break;
                    case "d": value = TimeSpan.FromDays(amount); break;
                    default: value = TimeSpan.FromSeconds(amount); break;
                }
            }
            catch (OverflowException)
            {
                // 形如 1e300s 的输入会溢出 TimeSpan；这是参数错误，不是崩溃。
                value = TimeSpan.Zero;
                return false;
            }

            return value > TimeSpan.Zero;
        }

        /// <summary>
        /// 把 <c>500ms</c> 拆成数字部分与单位部分。没有单位时按秒（<c>--timeout 10</c> 与
        /// <c>--timeout 10s</c> 等价，这是 SPEC §19 的默认值写法）。
        /// <para>
        /// 单位只在**末尾**识别，且 <c>ms</c> 必须先于 <c>s</c> 判断：反过来会把 <c>500ms</c> 拆成
        /// 数字 <c>500m</c> 加单位 <c>s</c>，得到一个荒唐但不会报错的时长。
        /// </para>
        /// </summary>
        /// <param name="token">已去掉首尾空白的原文。</param>
        /// <param name="number">数字部分。</param>
        /// <param name="unit">单位部分，<c>ms</c> / <c>s</c> / <c>m</c> / <c>h</c> / <c>d</c>；无单位时为 <c>s</c>。</param>
        private static void SplitUnit(string token, out string number, out string unit)
        {
            number = token;
            unit = "s";
            if (token.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            {
                unit = "ms";
                number = token.Substring(0, token.Length - 2);
            }
            else if (token.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            {
                number = token.Substring(0, token.Length - 1);
            }
            else if (token.EndsWith("m", StringComparison.OrdinalIgnoreCase))
            {
                unit = "m";
                number = token.Substring(0, token.Length - 1);
            }
            else if (token.EndsWith("h", StringComparison.OrdinalIgnoreCase))
            {
                unit = "h";
                number = token.Substring(0, token.Length - 1);
            }
            else if (token.EndsWith("d", StringComparison.OrdinalIgnoreCase))
            {
                unit = "d";
                number = token.Substring(0, token.Length - 1);
            }
        }
    }
}
