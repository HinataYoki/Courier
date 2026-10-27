using System.Collections.Generic;
using System.Globalization;

namespace Courier.Cli
{
    /// <summary>
    /// CLI 的 JSON 序列化。协议文档的落盘与解析全部复用产品库的 CourierJson / CourierJsonObject
    /// （不复制一份 JSON 实现）；这里只补 CLI 自己需要、而协议不需要的东西：
    /// 把 payload 原样嵌入（而不是当成字符串再转义一次），以及把任意 JSON 值写成一行。
    /// </summary>
    internal static class CliJson
    {
        /// <summary>把值序列化成 JSON 字符串字面量，转义规则与 SPEC §3.1 一致。</summary>
        /// <param name="value">任意字符串，可为 null。</param>
        /// <returns>带引号的 JSON 字符串。</returns>
        internal static string String(string? value)
        {
            return "\"" + CourierJson.Escape(value) + "\"";
        }

        /// <summary>整数字面量。</summary>
        /// <param name="value">整数值。</param>
        /// <returns>十进制文本。</returns>
        internal static string Number(long value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>把 endpoint.json 里的 actions 序列化成 <c>[{"channel":..,"action":..,"summary":..[, "metadata": {..}]}]</c>。</summary>
        /// <param name="actions">已注册的能力，按 endpoint.json 中的顺序。</param>
        /// <returns>JSON 数组文本。</returns>
        internal static string Actions(IReadOnlyList<CourierActionInfo> actions)
        {
            var items = new List<string>(actions.Count);
            for (int index = 0; index < actions.Count; index++)
            {
                CourierActionInfo action = actions[index];
                var item = new CourierJsonObject()
                    .AddString("channel", action.Channel)
                    .AddString("action", action.Action)
                    .AddString("summary", action.Summary);
                // SPEC §5.1：metadata 没有就省略字段，不写 null——null 会被读方当成"有 metadata 但是空的"。
                if (action.Metadata != null) item.AddRaw("metadata", action.Metadata!);
                items.Add(item.Build());
            }

            return CourierJson.Array(items);
        }

        /// <summary>
        /// 把 endpoint.json 里的 capabilities 序列化成 <c>["state","cancels"]</c>（SPEC §5）。
        /// 没有声明时返回 <c>null</c>，由调用方省略整个字段——空数组会被读方当成"故意声明了零个能力"。
        /// </summary>
        /// <param name="capabilities">端点自述的可选能力。</param>
        /// <returns>JSON 数组文本；没有能力时为 null。</returns>
        internal static string? Capabilities(IReadOnlyList<string> capabilities)
        {
            if (capabilities.Count == 0) return null;
            var items = new List<string>(capabilities.Count);
            for (int index = 0; index < capabilities.Count; index++)
            {
                items.Add(String(capabilities[index]));
            }

            return CourierJson.Array(items);
        }

        /// <summary>把 endpoint.json 里的 states 序列化成 <c>[{"name":..,"summary":..[, "metadata": {..}]}]</c>（SPEC §14）。</summary>
        /// <param name="states">已声明的状态名。</param>
        /// <returns>JSON 数组文本。</returns>
        internal static string States(IReadOnlyList<CourierStateInfo> states)
        {
            var items = new List<string>(states.Count);
            for (int index = 0; index < states.Count; index++)
            {
                CourierStateInfo state = states[index];
                var item = new CourierJsonObject()
                    .AddString("name", state.Name)
                    .AddString("summary", state.Summary);
                if (state.Metadata != null) item.AddRaw("metadata", state.Metadata!);
                items.Add(item.Build());
            }

            return CourierJson.Array(items);
        }

        /// <summary>
        /// payload 是"不透明字符串"（SPEC §9）。如果它本身就是一段合法 JSON 值，就直接嵌入，
        /// 让 `courier call ... --json` 的输出可直接被机器消费；否则当成字符串转义。
        /// <para>
        /// 任何合法 JSON 值都算数，不只是对象：handler 返回 <c>[1,2]</c>、<c>42</c> 或 <c>"ok"</c> 时，
        /// 只认对象会让同一个字段在不同调用之间变成两种 JSON 类型（数组退化成字符串），
        /// 读方拿到的 schema 就不稳定了（SPEC §14 对 state 的 payload 有同样的自由度）。
        /// 这一步只影响显示，不影响协议：落盘与解析始终按字符串处理。
        /// </para>
        /// </summary>
        /// <param name="payload">handler 返回的原始文本。</param>
        /// <returns>可直接拼进 JSON 对象的值文本。</returns>
        internal static string PayloadValue(string? payload)
        {
            if (string.IsNullOrEmpty(payload)) return "\"\"";
            // 只关心"能不能解析成某个 JSON 值"，解析结果本身被丢弃：CLI 不解释 payload 的语义。
            if (!CourierJson.TryParseValue(payload, out _)) return String(payload);
            return payload;
        }

        /// <summary>
        /// 把一个已解析的 JSON 数字原样写成字面量。
        /// <para>
        /// 协议里的 percent 是 0..100 的整数，但读方没必要因为它写成 <c>0.5</c> 或 <c>42.0</c> 就把值丢掉：
        /// 数值本身是合法的 JSON 数字，退化是显示层的事，不该变成数据丢失（SPEC §13.2 的"读不懂就忽略"
        /// 针对的是类型不对的字段，不是"整数写成了小数"）。</para>
        /// </summary>
        /// <param name="value">已解析的 double（CourierJson 的数字表示）。</param>
        /// <returns>JSON 数字字面量；NaN/Infinity 这类非 JSON 值返回 null。</returns>
        internal static string? Decimal(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return null;
            if (value == System.Math.Floor(value) && value >= long.MinValue && value <= long.MaxValue)
            {
                return Number((long)value);
            }

            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
