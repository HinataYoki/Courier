using System;
using System.Globalization;

namespace Courier.Cli
{
    /// <summary>
    /// 解析 --timeout：接受 <c>500ms</c>、<c>10s</c>、<c>1m</c> 写法，也接受纯数字（按秒）。
    /// 结果必须是正数，否则由调用方转成参数错误而不是异常。
    /// </summary>
    internal static class CliDuration
    {
        /// <summary>解析时长文本。</summary>
        /// <param name="text">形如 <c>500ms</c> / <c>10s</c> / <c>1m</c> / <c>2</c> 的文本。</param>
        /// <param name="value">解析成功时的时长。</param>
        /// <returns>是否解析成功且为正数。</returns>
        internal static bool TryParse(string? text, out TimeSpan value)
        {
            value = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;

            // IsNullOrWhiteSpace 带 [NotNullWhen(false)]，走到这里 text 必非 null。
            string token = text.Trim();
            string unit = "s";
            if (token.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            {
                unit = "ms";
                token = token.Substring(0, token.Length - 2);
            }
            else if (token.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            {
                unit = "s";
                token = token.Substring(0, token.Length - 1);
            }
            else if (token.EndsWith("m", StringComparison.OrdinalIgnoreCase))
            {
                unit = "m";
                token = token.Substring(0, token.Length - 1);
            }

            double amount;
            if (!double.TryParse(token.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out amount)) return false;
            if (double.IsNaN(amount) || double.IsInfinity(amount) || amount <= 0) return false;

            try
            {
                switch (unit)
                {
                    case "ms": value = TimeSpan.FromMilliseconds(amount); break;
                    case "m": value = TimeSpan.FromMinutes(amount); break;
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
    }
}
