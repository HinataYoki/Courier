using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Courier
{
    /// <summary>
    /// 协议用到的极小 JSON 读写器（SPEC §3.1）。手写而非引用第三方库，是零第三方依赖硬约束的要求。
    /// 只支持协议实际用到的子集：扁平对象、字符串、数字、布尔、null、数组与嵌套对象。
    /// </summary>
    internal static class CourierJson
    {
        /// <summary>解析一个顶层 JSON 对象。解析失败返回 false，调用方必须转成 rejected/invalid-command 或 deadletter。</summary>
        internal static bool TryParseObject(string? text, [NotNullWhen(true)] out Dictionary<string, object?>? value)
        {
            value = null;
            if (string.IsNullOrEmpty(text)) return false;
            try
            {
                int index = 0;
                Dictionary<string, object?> parsed = ParseObjectAt(text, ref index, 0);
                SkipWhitespace(text, ref index);
                if (index != text.Length) return false;
                value = parsed;
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        /// <summary>读取字符串字段；字段缺失、类型不符或为 null 时返回 null。未知字段一律忽略（SPEC §3.1）。</summary>
        internal static string? GetString(Dictionary<string, object?>? source, string name)
        {
            object? raw;
            if (source == null || !source.TryGetValue(name, out raw)) return null;
            return raw as string;
        }

        /// <summary>读取整数字段；缺失或类型不符时返回 null。</summary>
        internal static long? GetInteger(Dictionary<string, object?>? source, string name)
        {
            object? raw;
            if (source == null || !source.TryGetValue(name, out raw)) return null;
            if (raw is double number)
            {
                if (number != Math.Floor(number)) return null;
                return (long)number;
            }
            return null;
        }

        /// <summary>读取数组字段；缺失或类型不符时返回 null。</summary>
        internal static List<object?>? GetArray(Dictionary<string, object?>? source, string name)
        {
            object? raw;
            if (source == null || !source.TryGetValue(name, out raw)) return null;
            return raw as List<object?>;
        }

        /// <summary>按 SPEC §3.1 转义：只处理必须转义的字符，其余原样保留，保证文件可被 cat/grep 直读。</summary>
        /// <remarks>没有需要转义的字符时直接返回原串：endpoint.json、心跳与绝大多数 payload 都走这条快路径。</remarks>
        internal static string Escape(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            int first = IndexOfEscape(value);
            if (first < 0) return value;

            var builder = new StringBuilder(value.Length + 8);
            builder.Append(value, 0, first);
            for (int index = first; index < value.Length; index++)
            {
                char current = value[index];
                switch (current)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (current < ' ')
                        {
                            builder.Append("\\u");
                            builder.Append(((int)current).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(current);
                        }
                        break;
                }
            }
            return builder.ToString();
        }

        /// <summary>把已经序列化好的片段拼成单行数组，供 endpoint.json 与 CLI 输出共用。</summary>
        internal static string Array(IReadOnlyList<string> items)
        {
            var builder = new StringBuilder(64);
            builder.Append('[');
            for (int index = 0; index < items.Count; index++)
            {
                if (index > 0) builder.Append(',');
                builder.Append(items[index]);
            }
            builder.Append(']');
            return builder.ToString();
        }

        /// <summary>返回第一个必须转义的字符下标；整串都无需转义时返回 -1。</summary>
        private static int IndexOfEscape(string value)
        {
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                if (current == '"' || current == '\\' || current < ' ') return index;
            }
            return -1;
        }

        /// <summary>解析深度上限。没有它，1 MiB 的 <c>[[[[…</c> 会把栈打穿成 StackOverflowException——在 .NET 上不可捕获，宿主进程直接终止（SPEC §3.1 要求解析失败只能产出 rejected/deadletter）。</summary>
        private const int MaxParseDepth = 64;

        private static Dictionary<string, object?> ParseObjectAt(string text, ref int index, int depth)
        {
            if (depth > MaxParseDepth) throw Malformed("nesting too deep");
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

        private static List<object?> ParseArrayAt(string text, ref int index, int depth)
        {
            if (depth > MaxParseDepth) throw Malformed("nesting too deep");
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
                        if (index + 4 > text.Length) throw Malformed("truncated \\u escape");
                        int code = 0;
                        for (int offset = 0; offset < 4; offset++)
                        {
                            int digit = HexValue(text[index + offset]);
                            if (digit < 0) throw Malformed("invalid \\u escape");
                            code = (code << 4) | digit;
                        }
                        index += 4;
                        builder.Append((char)code);
                        break;
                    default:
                        throw Malformed("invalid escape");
                }
            }
        }

        private static double ParseNumberAt(string text, ref int index)
        {
            int start = index;
            if (index < text.Length && (text[index] == '-' || text[index] == '+')) index++;
            while (index < text.Length && text[index] >= '0' && text[index] <= '9') index++;
            if (index < text.Length && text[index] == '.')
            {
                index++;
                while (index < text.Length && text[index] >= '0' && text[index] <= '9') index++;
            }
            if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
            {
                index++;
                if (index < text.Length && (text[index] == '-' || text[index] == '+')) index++;
                while (index < text.Length && text[index] >= '0' && text[index] <= '9') index++;
            }
            if (index == start) throw Malformed("expected value");
            string token = text.Substring(start, index - start);
            double parsed;
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                throw Malformed("invalid number");
            }
            return parsed;
        }

        private static void ExpectWord(string text, ref int index, string word)
        {
            if (index + word.Length > text.Length) throw Malformed("expected " + word);
            if (string.CompareOrdinal(text, index, word, 0, word.Length) != 0) throw Malformed("expected " + word);
            index += word.Length;
        }

        private static void Expect(string text, ref int index, char expected)
        {
            if (index >= text.Length || text[index] != expected) throw Malformed("expected '" + expected + "'");
            index++;
        }

        private static char Peek(string text, int index)
        {
            if (index >= text.Length) throw Malformed("unexpected end of input");
            return text[index];
        }

        private static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length)
            {
                char current = text[index];
                if (current != ' ' && current != '\t' && current != '\r' && current != '\n') return;
                index++;
            }
        }

        private static int HexValue(char value)
        {
            if (value >= '0' && value <= '9') return value - '0';
            if (value >= 'a' && value <= 'f') return value - 'a' + 10;
            if (value >= 'A' && value <= 'F') return value - 'A' + 10;
            return -1;
        }

        private static FormatException Malformed(string message)
        {
            return new FormatException("malformed json: " + message);
        }
    }

    /// <summary>
    /// 紧凑 JSON 对象构建器。协议要求单行、无缩进、字段顺序稳定，便于 cat 与逐行处理（SPEC §3.1）。
    /// </summary>
    internal sealed class CourierJsonObject
    {
        private readonly StringBuilder mText;
        private bool mFirst = true;
        private bool mBuilt;

        internal CourierJsonObject()
        {
            mText = new StringBuilder(256);
            mText.Append('{');
        }

        internal CourierJsonObject AddString(string name, string? value)
        {
            BeginMember(name);
            mText.Append('"').Append(CourierJson.Escape(value)).Append('"');
            return this;
        }

        internal CourierJsonObject AddNumber(string name, long value)
        {
            BeginMember(name);
            mText.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        internal CourierJsonObject AddBoolean(string name, bool value)
        {
            BeginMember(name);
            mText.Append(value ? "true" : "false");
            return this;
        }

        internal CourierJsonObject AddRaw(string name, string rawJson)
        {
            BeginMember(name);
            mText.Append(rawJson);
            return this;
        }

        /// <summary>收尾并返回单行文本。可重复调用，只在第一次补上右花括号。</summary>
        internal string Build()
        {
            if (!mBuilt)
            {
                mText.Append('}');
                mBuilt = true;
            }
            return mText.ToString();
        }

        /// <summary>开一个新字段。<paramref name="name"/> 必须是代码里的字面量字段名，因此不做转义：一条 results 有 5–7 个字段，一次 Tick 批量 16 条就是上百次无谓的转义扫描。</summary>
        private void BeginMember(string name)
        {
            if (mFirst) mFirst = false;
            else mText.Append(',');
            mText.Append('"').Append(name).Append("\":");
        }
    }
}
