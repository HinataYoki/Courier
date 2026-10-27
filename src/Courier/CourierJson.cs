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
    internal static partial class CourierJson
    {
        /// <summary>解析一个顶层 JSON 对象。解析失败返回 false，调用方必须转成 rejected/invalid-command 或 deadletter。</summary>
        /// <remarks>
        /// 同一次解析里，整数既可能是 <see cref="long"/>（超出 double 精确范围）也可能是 <see cref="double"/>，
        /// 读字段一律走 <see cref="GetString"/> / <see cref="GetInteger"/> / <see cref="TryGetNumber"/> 这组访问器。
        /// </remarks>
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

        /// <summary>读取整数字段；缺失、类型不符或越界时返回 null（SPEC §4.4 的版本号等字段都走这里）。</summary>
        /// <remarks>
        /// 解析结果里的数字可能是 <see cref="double"/>（绝大多数）或 <see cref="long"/>
        /// （超过 2^53 的整数字面量，见 <see cref="ParseNumberAt"/>），两种都要认。
        /// 强转前 MUST 先判有限性与 long 值域：<c>(long)</c> 作用在 Infinity 或 1e19 上是未定义转换，
        /// 会把垃圾值当成合法版本号一路传下去，比直接判缺失更难排查。
        /// </remarks>
        /// <param name="source">解析结果，可为 null。</param>
        /// <param name="name">字段名，区分大小写（SPEC §3.1）。</param>
        /// <returns>整数；缺失、非整数、非有限或超出 long 范围时为 null。</returns>
        internal static long? GetInteger(Dictionary<string, object?>? source, string name)
        {
            object? raw;
            if (source == null || !source.TryGetValue(name, out raw)) return null;
            if (raw is long whole) return whole;
            if (raw is double number)
            {
                if (double.IsNaN(number) || double.IsInfinity(number)) return null;
                if (number != Math.Floor(number)) return null;
                // 上界用 2^63 而不是 long.MaxValue：long.MaxValue 转成 double 会舍入成 2^63，
                // 于是 9223372036854775808 能通过检查，再被强转成 long.MinValue。
                if (number < DOUBLE_LONG_MIN || number >= DOUBLE_LONG_LIMIT) return null;
                return (long)number;
            }

            return null;
        }

        /// <summary>
        /// 解析任意 JSON 值（对象、数组、字符串、数字、布尔、null）。state/ 的 payload 是不透明的任意值，
        /// 只接受对象会让"发布一个数字"这种合法用法被拒（SPEC §14）。
        /// </summary>
        internal static bool TryParseValue(string? text, out object? value)
        {
            value = null;
            if (string.IsNullOrEmpty(text)) return false;
            try
            {
                int index = 0;
                value = ParseValueAt(text, ref index, 0);
                SkipWhitespace(text, ref index);
                return index == text.Length;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        /// <summary>
        /// 判断字段值是不是 JSON 数字，并给出它的 <see cref="double"/> 形式（CLI 的显示层用）。
        /// </summary>
        /// <remarks>
        /// 解析器对超过 2^53 的整数保留 <see cref="long"/>（见 <see cref="ParseNumberAt"/>），
        /// 所以"是数字"不再等于 <c>raw is double</c>。显示层只需要数值本身，转换精度损失对显示无影响。
        /// </remarks>
        /// <param name="source">解析结果，可为 null。</param>
        /// <param name="name">字段名，区分大小写。</param>
        /// <param name="number">数值；不是数字时为 0。</param>
        /// <returns>字段存在且是 JSON 数字时为 true。</returns>
        internal static bool TryGetNumber(Dictionary<string, object?>? source, string name, out double number)
        {
            number = 0;
            object? raw;
            if (source == null || !source.TryGetValue(name, out raw)) return false;
            if (raw is double floating) { number = floating; return true; }
            if (raw is long whole) { number = whole; return true; }
            return false;
        }

        /// <summary>读取数组字段；缺失或类型不符时返回 null。</summary>
        internal static List<object?>? GetArray(Dictionary<string, object?>? source, string name)
        {
            object? raw;
            if (source == null || !source.TryGetValue(name, out raw)) return null;
            return raw as List<object?>;
        }

        /// <summary>按 SPEC §3.1 转义：只处理必须转义的字符，其余原样保留，保证文件可被 cat/grep 直读。</summary>
        /// <remarks>
        /// 没有需要转义的字符时直接返回原串：endpoint.json、心跳与绝大多数 payload 都走这条快路径。
        /// 孤立代理项（<c>\uD800</c> 这类不成对的 UTF-16 码元）也必须转义：原样写进 UTF-8 流会被
        /// 编码器的替换回退变成 U+FFFD，往返一次数据就静默丢了。
        /// </remarks>
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
                        if (current < ' ' || IsLoneSurrogate(value, index))
                        {
                            AppendUnicodeEscape(builder, current);
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

        /// <summary>把一个 UTF-16 码元写成 <c>\uXXXX</c> 转义。</summary>
        /// <param name="builder">目标缓冲。</param>
        /// <param name="value">要转义的码元。</param>
        private static void AppendUnicodeEscape(StringBuilder builder, char value)
        {
            builder.Append("\\u");
            builder.Append(((int)value).ToString("x4", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// 判断 <paramref name="value"/> 在 <paramref name="index"/> 处的码元是不是孤立代理项。
        /// 高位代理后必须紧跟低位代理、低位代理前必须有高位代理，否则它不是合法的 Unicode 文本，
        /// 无法编码成 UTF-8（SPEC §3.1 的落盘产物必须是无损文本）。
        /// </summary>
        /// <param name="value">待检查的字符串。</param>
        /// <param name="index">码元下标，调用方保证在范围内。</param>
        /// <returns>是孤立代理项时为 true。</returns>
        private static bool IsLoneSurrogate(string value, int index)
        {
            char current = value[index];
            if (current < '\uD800' || current > '\uDFFF') return false;
            if (current <= '\uDBFF')
            {
                if (index + 1 >= value.Length) return true;
                char low = value[index + 1];
                return low < '\uDC00' || low > '\uDFFF';
            }

            if (index == 0) return true;
            char high = value[index - 1];
            return high < '\uD800' || high > '\uDBFF';
        }

        /// <summary>
        /// 把解析出来的任意 JSON 值重新序列化成单行紧凑文本（SPEC §3.1）。
        /// <para>
        /// 用于把"协议不解释的 JSON"原样搬来搬去：endpoint.json 里的 metadata、以及任何需要
        /// 回写一份已解析文档的场合。整数值原样输出整数，超过 2^53 的整数在解析阶段被保留成
        /// <see cref="long"/>，所以往返不会再被 <c>double</c> 的精度截断；小数用 <c>R</c> 往返格式，
        /// 因为 <c>1.0</c> 与 <c>1</c> 解析出来是同一个值，原始字面量已经无从恢复。
        /// </para>
        /// <para>
        /// 非有限数字（NaN/Infinity）不是合法 JSON，直接抛 <see cref="FormatException"/>：
        /// 调用方在端口扫描这条只读路径上必须自己兜住，不能让一份坏文件变成整次扫描失败（SPEC §3.7）。
        /// </para>
        /// </summary>
        /// <param name="value">解析结果：Dictionary、List、string、long、double、bool 或 null。</param>
        /// <exception cref="FormatException">遇到无法序列化的值或非有限数字。</exception>
        /// <returns>单行紧凑 JSON 文本。</returns>
        internal static string Write(object? value)
        {
            var builder = new StringBuilder(64);
            WriteValue(builder, value, 0);
            return builder.ToString();
        }

        /// <summary>
        /// 递归写出一个已解析的值。<see cref="Write"/> 只负责开一个缓冲；分派全部在这里。
        /// </summary>
        /// <param name="builder">目标缓冲。</param>
        /// <param name="value">Dictionary、List、string、long、double、bool 或 null。</param>
        /// <param name="depth">当前嵌套深度，用于在栈被打穿之前拒绝。</param>
        /// <exception cref="FormatException">嵌套过深、非有限数字，或出现解析侧不会产出、本类也不认识的类型。</exception>
        private static void WriteValue(StringBuilder builder, object? value, int depth)
        {
            if (depth > MAX_PARSE_DEPTH) throw Malformed("nesting too deep");

            // 标量先走掉一半以上的调用：endpoint.json 与本协议每一份文档的绝大部分字段都是标量。
            if (value == null) { builder.Append("null"); return; }
            if (value is string text) { WriteString(builder, text); return; }
            if (value is bool flag) { builder.Append(flag ? "true" : "false"); return; }
            if (value is long integer) { builder.Append(integer.ToString(CultureInfo.InvariantCulture)); return; }
            if (value is double number) { WriteNumber(builder, number); return; }

            if (value is List<object?> items) { WriteArray(builder, items, depth); return; }
            if (value is Dictionary<string, object?> document) { WriteDocument(builder, document, depth); return; }

            // 解析侧只会产出上面几种类型；走到这里说明有人往里塞了别的对象。
            throw Malformed("unsupported value type " + value.GetType().FullName);
        }

        /// <summary>写出一个 JSON 字符串（加引号并转义）。</summary>
        /// <param name="builder">目标缓冲。</param>
        /// <param name="text">原串。</param>
        private static void WriteString(StringBuilder builder, string text)
        {
            builder.Append('"').Append(Escape(text)).Append('"');
        }

        /// <summary>
        /// 写出一个 JSON 数字。整数值按整数写，其余用 <c>R</c> 往返格式。
        /// </summary>
        /// <param name="builder">目标缓冲。</param>
        /// <param name="number">解析结果里的浮点值。</param>
        /// <exception cref="FormatException">NaN 或 Infinity——它们不是合法 JSON。</exception>
        private static void WriteNumber(StringBuilder builder, double number)
        {
            // NaN/Infinity 不是合法 JSON：解析侧永远产生不了它们，出现了就是内部错误。
            if (double.IsNaN(number) || double.IsInfinity(number)) throw Malformed("non-finite number");

            // 整数值按整数写：metadata 里的 "1.0" 变回 "1" 不影响语义，只影响可读性。
            // 上界同样用 2^63 而不是 long.MaxValue，理由见 DOUBLE_LONG_LIMIT。
            if (number == Math.Floor(number) && number >= DOUBLE_LONG_MIN && number < DOUBLE_LONG_LIMIT)
            {
                builder.Append(((long)number).ToString(CultureInfo.InvariantCulture));
                return;
            }

            builder.Append(number.ToString("R", CultureInfo.InvariantCulture));
        }

        /// <summary>写出一个 JSON 数组。</summary>
        /// <param name="builder">目标缓冲。</param>
        /// <param name="items">数组元素。</param>
        /// <param name="depth">当前嵌套深度。</param>
        private static void WriteArray(StringBuilder builder, List<object?> items, int depth)
        {
            builder.Append('[');
            for (int index = 0; index < items.Count; index++)
            {
                if (index > 0) builder.Append(',');
                WriteValue(builder, items[index], depth + 1);
            }
            builder.Append(']');
        }

        /// <summary>写出一个 JSON 对象；成员顺序沿用字典的插入顺序，因此字段序稳定（SPEC §3.1）。</summary>
        /// <param name="builder">目标缓冲。</param>
        /// <param name="document">已解析的对象。</param>
        /// <param name="depth">当前嵌套深度。</param>
        private static void WriteDocument(StringBuilder builder, Dictionary<string, object?> document, int depth)
        {
            builder.Append('{');
            bool first = true;
            foreach (KeyValuePair<string, object?> member in document)
            {
                if (!first) builder.Append(',');
                first = false;
                builder.Append('"').Append(Escape(member.Key)).Append("\":");
                WriteValue(builder, member.Value, depth + 1);
            }
            builder.Append('}');
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
        /// <remarks>
        /// 快路径的判定必须和 <see cref="Escape"/> 的分支一致，否则一个只含孤立代理项的字符串
        /// 会被这里判成"无需转义"而原样返回，又绕回数据丢失那条路。
        /// </remarks>
        private static int IndexOfEscape(string value)
        {
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                if (current == '"' || current == '\\' || current < ' ') return index;
                // 代理项只有孤立时才需要转义；成对的高位代理直接跳过它的低位伙伴。
                if (current >= '\uD800' && current <= '\uDFFF' && IsLoneSurrogate(value, index)) return index;
            }
            return -1;
        }

        /// <summary>解析深度上限。没有它，1 MiB 的 <c>[[[[…</c> 会把栈打穿成 StackOverflowException——在 .NET 上不可捕获，宿主进程直接终止（SPEC §3.1 要求解析失败只能产出 rejected/deadletter）。</summary>
        private const int MAX_PARSE_DEPTH = 64;

        /// <summary>long 的最小值的 double 近似（精确值，-2^63 可被 double 精确表示）。</summary>
        private const double DOUBLE_LONG_MIN = -9223372036854775808.0;

        /// <summary>
        /// long 上界的 double 形式：2^63，比 <see cref="long.MaxValue"/> 大 1。
        /// 用它做上界比较才能拒绝 9223372036854775808——<c>long.MaxValue</c> 转成 double 会被舍入成同一个 2^63，
        /// 于是那个越界值反而"不大于上界"，强转后变成 <see cref="long.MinValue"/>。
        /// </summary>
        private const double DOUBLE_LONG_LIMIT = 9223372036854775808.0;
    }

    /// <summary>
    /// 紧凑 JSON 对象构建器。协议要求单行、无缩进、字段顺序稳定，便于 cat 与逐行处理（SPEC §3.1）。
    /// </summary>
    internal sealed class CourierJsonObject
    {
        private readonly StringBuilder mText;
        private bool mFirst = true;
        private bool mBuilt;

        /// <summary>建一个空构建器，缓冲区预分配 256 字符（协议对象都是短行）。</summary>
        internal CourierJsonObject()
        {
            mText = new StringBuilder(256);
            mText.Append('{');
        }

        /// <summary>追加一个字符串成员；<paramref name="value"/> 为 null 时按空串写（协议字段非可选）。</summary>
        internal CourierJsonObject AddString(string name, string? value)
        {
            BeginMember(name);
            mText.Append('"').Append(CourierJson.Escape(value)).Append('"');
            return this;
        }

        /// <summary>追加一个整数成员，用不变区域性格式，避免千分位或本地化小数点。</summary>
        internal CourierJsonObject AddNumber(string name, long value)
        {
            BeginMember(name);
            mText.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        /// <summary>追加一个布尔成员。</summary>
        internal CourierJsonObject AddBoolean(string name, bool value)
        {
            BeginMember(name);
            mText.Append(value ? "true" : "false");
            return this;
        }

        /// <summary>追加一段已经序列化好的 JSON 原文；调用方负责保证它是单个合法值（metadata 与 hot 描述走这里）。</summary>
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
