using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Courier
{
    /// <summary>
    /// <see cref="CourierJson"/> 的解析半边：递归下降的 JSON 读取器（SPEC §3.1）。
    /// <para>
    /// 与写入半边分开成两个文件，是为了让两边各自的职责单独可读：这里只回答"这段文本是不是合法 JSON、
    /// 里面是什么值"，从不产生输出。解析深度上限、数字文法、代理对规则都在这一半。
    /// </para>
    /// </summary>
    internal static partial class CourierJson
    {
        /// <summary>解析一个 JSON 对象；<paramref name="index"/> 停在右花括号之后。</summary>
        /// <param name="text">源文本。</param>
        /// <param name="index">当前下标，进出都按值前进。</param>
        /// <param name="depth">当前嵌套深度。</param>
        /// <returns>成员按出现顺序保存的字典（查表用序数比较，SPEC §3.1 区分大小写）。</returns>
        /// <exception cref="FormatException">文法不合法或嵌套过深。</exception>
        private static Dictionary<string, object?> ParseObjectAt(string text, ref int index, int depth)
        {
            if (depth > MAX_PARSE_DEPTH) throw Malformed("nesting too deep");
            SkipWhitespace(text, ref index);
            Expect(text, ref index, '{');
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            SkipWhitespace(text, ref index);
            if (Peek(text, index) == '}')
            {
                index++;
                return result;
            }

            while (true)
            {
                SkipWhitespace(text, ref index);
                string name = ParseStringAt(text, ref index);
                SkipWhitespace(text, ref index);
                Expect(text, ref index, ':');
                result[name] = ParseValueAt(text, ref index, depth + 1);
                SkipWhitespace(text, ref index);
                char next = Peek(text, index);
                if (next == ',')
                {
                    index++;
                    continue;
                }
                if (next == '}')
                {
                    index++;
                    return result;
                }
                throw Malformed("expected ',' or '}'");
            }
        }

        /// <summary>解析一个 JSON 数组；<paramref name="index"/> 停在右方括号之后。</summary>
        /// <param name="text">源文本。</param>
        /// <param name="index">当前下标，进出都按值前进。</param>
        /// <param name="depth">当前嵌套深度。</param>
        /// <returns>按出现顺序排列的元素。</returns>
        /// <exception cref="FormatException">文法不合法或嵌套过深。</exception>
        private static List<object?> ParseArrayAt(string text, ref int index, int depth)
        {
            if (depth > MAX_PARSE_DEPTH) throw Malformed("nesting too deep");
            SkipWhitespace(text, ref index);
            Expect(text, ref index, '[');
            var result = new List<object?>();
            SkipWhitespace(text, ref index);
            if (Peek(text, index) == ']')
            {
                index++;
                return result;
            }

            while (true)
            {
                result.Add(ParseValueAt(text, ref index, depth + 1));
                SkipWhitespace(text, ref index);
                char next = Peek(text, index);
                if (next == ',')
                {
                    index++;
                    continue;
                }
                if (next == ']')
                {
                    index++;
                    return result;
                }
                throw Malformed("expected ',' or ']'");
            }
        }

        /// <summary>
        /// 按首字符分派一个 JSON 值：对象、数组、字符串、true/false/null，其余一律当数字。
        /// </summary>
        /// <param name="text">源文本。</param>
        /// <param name="index">当前下标，进出都按值前进。</param>
        /// <param name="depth">当前嵌套深度。</param>
        /// <returns>Dictionary、List、string、long、double、bool 或 null。</returns>
        /// <exception cref="FormatException">文法不合法。</exception>
        private static object? ParseValueAt(string text, ref int index, int depth)
        {
            SkipWhitespace(text, ref index);
            char current = Peek(text, index);
            switch (current)
            {
                case '{': return ParseObjectAt(text, ref index, depth);
                case '[': return ParseArrayAt(text, ref index, depth);
                case '"': return ParseStringAt(text, ref index);
                case 't': ExpectWord(text, ref index, "true"); return true;
                case 'f': ExpectWord(text, ref index, "false"); return false;
                case 'n': ExpectWord(text, ref index, "null"); return null;
                default: return ParseNumberAt(text, ref index);
            }
        }

        /// <summary>解析一个 JSON 字符串（含转义与 <c>\\uXXXX</c> 代理对）。</summary>
        /// <param name="text">源文本。</param>
        /// <param name="index">指向开引号；返回时停在闭引号之后。</param>
        /// <returns>还原后的字符串。</returns>
        /// <exception cref="FormatException">未闭合、转义非法或代理项不成对。</exception>
        private static string ParseStringAt(string text, ref int index)
        {
            Expect(text, ref index, '"');
            var builder = new StringBuilder(32);
            while (true)
            {
                if (index >= text.Length) throw Malformed("unterminated string");
                char current = text[index++];
                if (current == '"') return builder.ToString();
                if (current != '\\')
                {
                    builder.Append(current);
                    continue;
                }
                if (index >= text.Length) throw Malformed("unterminated escape");
                char escaped = text[index++];
                switch (escaped)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        builder.Append(ParseUnicodeEscape(text, ref index));
                        break;
                    default:
                        throw Malformed("invalid escape");
                }
            }
        }

        /// <summary>
        /// 解析一个 JSON 数字（RFC 8259 §6 的文法）。整数字面量在 double 装不下时保留成 <see cref="long"/>，
        /// 其余一律是 <see cref="double"/>。
        /// </summary>
        /// <remarks>
        /// 数字文法 MUST 严格：<c>+1</c> 与 <c>007</c> 都不是合法 JSON，宽松接受只会让本实现写出的文本
        /// 与读到的文本悄悄不同（写侧固定在 R 格式与去零形式，读侧却接受另一套写法）。
        /// 超出 double 表示范围的字面量（<c>1e400</c>）按解析失败处理——放它过去会在回写时变成
        /// <c>Infinity</c> 再抛 <see cref="FormatException"/>，不如在这里就拒绝。
        /// </remarks>
        /// <param name="text">源文本。</param>
        /// <param name="index">当前下标；返回时指向数字之后的第一个字符。</param>
        /// <returns>long（整数且超出 double 精确范围）或 double。</returns>
        /// <exception cref="FormatException">文法不合法或数值超出可表示范围。</exception>
        private static object ParseNumberAt(string text, ref int index)
        {
            bool isInteger;
            string token = ScanNumberToken(text, ref index, out isInteger);
            return isInteger ? ConvertIntegerToken(token) : ConvertFloatingToken(token);
        }

        /// <summary>
        /// 按 RFC 8259 §6 的文法扫出一个数字字面量。前导 <c>+</c>、前导零、<c>.</c> 与 <c>e</c> 后缺数字都拒绝。
        /// </summary>
        /// <param name="text">源文本。</param>
        /// <param name="index">当前下标；返回时指向数字之后的第一个字符。</param>
        /// <param name="isInteger">字面量里没有小数点也没有指数部分时为 true。</param>
        /// <returns>字面量原文。</returns>
        /// <exception cref="FormatException">文法不合法。</exception>
        private static string ScanNumberToken(string text, ref int index, out bool isInteger)
        {
            int start = index;
            if (index < text.Length && text[index] == '-') index++;

            // 整数部分：单个 0，或 1–9 开头的数字串。前导零与前导 '+' 都不允许。
            if (index >= text.Length || text[index] < '0' || text[index] > '9') throw Malformed("invalid number");
            if (text[index] == '0')
            {
                index++;
                if (index < text.Length && text[index] >= '0' && text[index] <= '9') throw Malformed("leading zero in number");
            }
            else
            {
                while (index < text.Length && text[index] >= '0' && text[index] <= '9') index++;
            }

            isInteger = true;
            if (index < text.Length && text[index] == '.')
            {
                isInteger = false;
                index++;
                if (index >= text.Length || text[index] < '0' || text[index] > '9') throw Malformed("invalid number");
                while (index < text.Length && text[index] >= '0' && text[index] <= '9') index++;
            }

            if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
            {
                isInteger = false;
                index++;
                if (index < text.Length && (text[index] == '-' || text[index] == '+')) index++;
                if (index >= text.Length || text[index] < '0' || text[index] > '9') throw Malformed("invalid number");
                while (index < text.Length && text[index] >= '0' && text[index] <= '9') index++;
            }

            return text.Substring(start, index - start);
        }

        /// <summary>
        /// 把整数字面量转成解析结果：2^53 以内继续吐 <see cref="double"/>，装不下才保留 <see cref="long"/>。
        /// </summary>
        /// <remarks>
        /// 继续吐 double 是全仓读数字的既有约定，换成 long 会让所有 <c>raw is double</c> 的读方突然失手。
        /// 只有装不下的整数才保留 long——那些值在旧实现里本来就已经被静默改掉了
        /// （9223372036854775807 变成 9223372036854775808）。
        /// </remarks>
        /// <param name="token">整数字面量原文。</param>
        /// <returns>double 或 long。</returns>
        private static object ConvertIntegerToken(string token)
        {
            long whole;
            if (long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out whole))
            {
                double approximate = whole;
                if (approximate >= DOUBLE_LONG_LIMIT || approximate < DOUBLE_LONG_MIN) return whole;
                if ((long)approximate != whole) return whole;
                return approximate;
            }

            // 超出 long 的整数字面量退回落浮点解析：能表示就表示，超出 double 范围则失败。
            return ConvertFloatingToken(token);
        }

        /// <summary>
        /// 把带小数点或指数的字面量转成 <see cref="double"/>；NaN、Infinity 与越界一律按解析失败处理。
        /// </summary>
        /// <param name="token">数字字面量原文。</param>
        /// <returns>double。</returns>
        /// <exception cref="FormatException">数值超出可表示范围。</exception>
        private static double ConvertFloatingToken(string token)
        {
            double parsed;
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                || double.IsNaN(parsed) || double.IsInfinity(parsed))
            {
                throw Malformed("number out of range");
            }

            return parsed;
        }

        /// <summary>
        /// 解析 <c>\uXXXX</c> 转义（不含前导的反斜杠与 <c>u</c>），并按 UTF-16 代理对规则还原成码元。
        /// </summary>
        /// <param name="text">源文本。</param>
        /// <param name="index">指向四位十六进制数的第一个字符；返回时指向其后的字符。</param>
        /// <returns>要追加到字符串上的字符；代理对合并时返回高位代理，由调用方补低位。</returns>
        /// <exception cref="FormatException">十六进制数不完整或非法，或代理项不成对。</exception>
        private static string ParseUnicodeEscape(string text, ref int index)
        {
            int code = ParseHex4(text, ref index);
            if (code >= 0xDC00 && code <= 0xDFFF)
            {
                // 低位代理必须紧跟在它的高位伙伴之后；单独出现说明这段文本不是合法的 Unicode。
                throw Malformed("unpaired low surrogate \\u" + code.ToString("x4", CultureInfo.InvariantCulture));
            }

            if (code < 0xD800 || code > 0xDBFF) return ((char)code).ToString();

            if (index + 2 > text.Length || text[index] != '\\' || text[index + 1] != 'u')
            {
                throw Malformed("unpaired high surrogate \\u" + code.ToString("x4", CultureInfo.InvariantCulture));
            }

            index += 2;
            int low = ParseHex4(text, ref index);
            if (low < 0xDC00 || low > 0xDFFF)
            {
                throw Malformed("unpaired high surrogate \\u" + code.ToString("x4", CultureInfo.InvariantCulture));
            }

            return new string(new[] { (char)code, (char)low });
        }

        /// <summary>读取四位十六进制数并前移下标。</summary>
        /// <param name="text">源文本。</param>
        /// <param name="index">指向四位十六进制数的第一个字符；成功时前移 4。</param>
        /// <returns>读到的码元值。</returns>
        /// <exception cref="FormatException">位数不足或含非十六进制字符。</exception>
        private static int ParseHex4(string text, ref int index)
        {
            if (index + 4 > text.Length) throw Malformed("truncated \\u escape");
            int code = 0;
            for (int offset = 0; offset < 4; offset++)
            {
                int digit = HexValue(text[index + offset]);
                if (digit < 0) throw Malformed("invalid \\u escape");
                code = (code << 4) | digit;
            }

            index += 4;
            return code;
        }

        /// <summary>要求接下来出现的正是 <paramref name="word"/>（<c>true</c>/<c>false</c>/<c>null</c>）。</summary>
        /// <param name="text">源文本。</param>
        /// <param name="index">当前下标；匹配成功时前移一个词长。</param>
        /// <param name="word">期望的字面量。</param>
        /// <exception cref="FormatException">长度不够或内容不符。</exception>
        private static void ExpectWord(string text, ref int index, string word)
        {
            if (index + word.Length > text.Length) throw Malformed("expected " + word);
            if (string.CompareOrdinal(text, index, word, 0, word.Length) != 0) throw Malformed("expected " + word);
            index += word.Length;
        }

        /// <summary>要求接下来出现的正是 <paramref name="expected"/>。</summary>
        /// <param name="text">源文本。</param>
        /// <param name="index">当前下标；匹配成功时前移一位。</param>
        /// <param name="expected">期望的字符。</param>
        /// <exception cref="FormatException">已到末尾或字符不符。</exception>
        private static void Expect(string text, ref int index, char expected)
        {
            if (index >= text.Length || text[index] != expected) throw Malformed("expected '" + expected + "'");
            index++;
        }

        /// <summary>读一个字符但不前进；已到末尾时按解析失败处理，而不是返回 NUL。</summary>
        /// <param name="text">源文本。</param>
        /// <param name="index">要读的下标。</param>
        /// <returns>该位置的字符。</returns>
        /// <exception cref="FormatException"><paramref name="index"/> 已越界。</exception>
        private static char Peek(string text, int index)
        {
            if (index >= text.Length) throw Malformed("unexpected end of input");
            return text[index];
        }

        /// <summary>跳过 JSON 允许的四种空白（空格、制表、回车、换行）。</summary>
        /// <param name="text">源文本。</param>
        /// <param name="index">当前下标；返回时指向第一个非空白字符或末尾。</param>
        private static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length)
            {
                char current = text[index];
                if (current != ' ' && current != '\t' && current != '\r' && current != '\n') return;
                index++;
            }
        }

        /// <summary>把一位十六进制字符转成数值。</summary>
        /// <param name="value">待转换的字符。</param>
        /// <returns>0–15；不是十六进制字符时为 -1。</returns>
        private static int HexValue(char value)
        {
            if (value >= '0' && value <= '9') return value - '0';
            if (value >= 'a' && value <= 'f') return value - 'a' + 10;
            if (value >= 'A' && value <= 'F') return value - 'A' + 10;
            return -1;
        }

        /// <summary>造一条统一的解析失败异常（消息前缀固定为 <c>malformed json:</c>，便于日志里筛）。</summary>
        /// <param name="message">具体原因。</param>
        /// <returns>待抛出的异常。</returns>
        private static FormatException Malformed(string message)
        {
            return new FormatException("malformed json: " + message);
        }
    }
}
