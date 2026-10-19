using System.Collections.Generic;
using System.Globalization;

namespace Courier.Cli
{
    /// <summary>
    /// CLI 的 JSON 序列化。协议文档的落盘与解析全部复用产品库的 CourierJson / CourierJsonObject
    /// （不复制一份 JSON 实现）；这里只补 CLI 自己需要、而协议不需要的东西：
    /// 把 payload 原样嵌入（而不是当成字符串再转义一次）。
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

        /// <summary>把 endpoint.json 里的 actions 序列化成 <c>[{"channel":..,"action":..,"summary":..}]</c>。</summary>
        /// <param name="actions">已注册的能力，按 endpoint.json 中的顺序。</param>
        /// <returns>JSON 数组文本。</returns>
        internal static string Actions(IReadOnlyList<CourierActionInfo> actions)
        {
            var items = new List<string>(actions.Count);
            for (int index = 0; index < actions.Count; index++)
            {
                CourierActionInfo action = actions[index];
                items.Add(new CourierJsonObject()
                    .AddString("channel", action.Channel)
                    .AddString("action", action.Action)
                    .AddString("summary", action.Summary)
                    .Build());
            }
            return CourierJson.Array(items);
        }

        /// <summary>
        /// payload 是"不透明字符串"（SPEC §7）。如果它本身就是一段合法 JSON，就直接嵌入，
        /// 让 `courier call ... --json` 的输出可直接被机器消费；否则当成字符串转义。
        /// 这一步只影响显示，不影响协议：落盘与解析始终按字符串处理。
        /// </summary>
        /// <param name="payload">handler 返回的原始文本。</param>
        /// <returns>可直接拼进 JSON 对象的值文本。</returns>
        internal static string PayloadValue(string? payload)
        {
            if (string.IsNullOrEmpty(payload)) return "\"\"";
            // 只关心"能不能解析成对象"，解析结果本身被丢弃：CLI 不解释 payload 的语义。
            if (!CourierJson.TryParseObject(payload, out _)) return String(payload);
            return payload;
        }
    }
}
