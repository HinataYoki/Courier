using System;
using System.Collections.Generic;
using System.IO;

namespace Courier
{
    /// <summary>
    /// 只读扫描 endpoints/（SPEC §5、§6）。库的 <see cref="CourierNode.Endpoints"/> 与 CLI 的 endpoints/ps 命令
    /// 共用这一份实现，保证"能力目录离线可读"、扫描顺序与活性判定都只有一个版本。
    /// </summary>
    internal static class CourierEndpointReader
    {
        /// <summary>向上找 <c>courier.layout.json</c> 时最多回溯几层（root 到 endpoints/ 通常只有 1–2 层）。</summary>
        private const int MAX_LAYOUT_SEARCH_DEPTH = 8;

        /// <summary>扫描 endpoints/ 下全部端点，按 endpointId 升序返回。</summary>
        /// <param name="endpointsRoot">endpoints/ 的绝对路径。</param>
        /// <returns>能成功解析的端点；损坏或不合法的目录被跳过，而不是让整次扫描失败。</returns>
        internal static List<CourierEndpointInfo> ReadAll(string endpointsRoot)
        {
            var result = new List<CourierEndpointInfo>();
            if (!Directory.Exists(endpointsRoot)) return result;

            // 布局只解析一次：文件名叫什么由它决定，逐个端点再找一遍纯属重复 I/O。
            CourierLayout layout = FindLayout(endpointsRoot);

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(endpointsRoot);
            }
            catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException)
            {
                // 目录不可读：返回空快照，把"读不到"与"没有端点"一起交给调用方显示。
                return result;
            }

            Array.Sort(directories, StringComparer.Ordinal);
            for (int index = 0; index < directories.Length; index++)
            {
                // SPEC §4.1：目录名来自磁盘，先校验再打开，垃圾目录连 endpoint.json 都不读。
                if (!CourierProtocol.IsValidEndpointId(Path.GetFileName(directories[index]))) continue;
                CourierEndpointInfo? info = TryRead(directories[index], layout);
                if (info != null) result.Add(info);
            }

            return result;
        }

        /// <summary>
        /// 定位某个协议目录所属的布局：从 <paramref name="directory"/> 逐级向上找 <c>courier.layout.json</c>。
        /// </summary>
        /// <remarks>
        /// 读取侧只拿得到一个绝对路径（<see cref="CourierEndpointInfo"/> 的扫描接口没有 root 参数，CLI 与
        /// <see cref="CourierNode"/> 传进来的都是拼好的目录），而文件名由布局决定，所以文件名只能这样反查。
        /// 找不到布局文件、或布局文件不可解析时一律用默认布局：这是只读扫描路径，§3.7 要求跳过而不是抛异常，
        /// 而默认布局的文件名与协议常量一致，恰好是"没有自定义布局"的正确解读。
        /// </remarks>
        /// <param name="directory">协议目录（endpoints/、端点目录或它们下面任意一层）。</param>
        /// <returns>可用的布局，永不为 null。</returns>
        internal static CourierLayout FindLayout(string directory)
        {
            string? current = directory;
            for (int depth = 0; depth < MAX_LAYOUT_SEARCH_DEPTH && !string.IsNullOrEmpty(current); depth++)
            {
                if (File.Exists(Path.Combine(current, CourierLayout.LAYOUT_FILE_NAME)))
                {
                    // 找到了布局文件就以它为准：解析失败也回默认，不再往上找——再往上的布局文件属于别的协议根。
                    return CourierLayout.TryLoad(current, out CourierLayout loaded, out _) ? loaded : CourierLayout.Default;
                }

                current = Path.GetDirectoryName(current);
            }

            return CourierLayout.Default;
        }

        /// <summary>读取一个端点目录；endpoint.json 缺失或不可解析时返回 null，由调用方跳过而不是抛出。</summary>
        /// <param name="directory">端点目录。</param>
        /// <returns>端点视图，或 null。</returns>
        internal static CourierEndpointInfo? TryRead(string directory)
        {
            return TryRead(directory, FindLayout(directory));
        }

        /// <summary>
        /// 按给定布局读取一个端点目录；<c>endpoint.json</c> 与心跳的文件名都取自布局（SPEC §5、§6）。
        /// </summary>
        /// <param name="directory">端点目录。</param>
        /// <param name="layout">生效的布局，提供 endpoint.json 与心跳的文件名。</param>
        /// <returns>端点视图，或 null。</returns>
        internal static CourierEndpointInfo? TryRead(string directory, CourierLayout layout)
        {
            string? text;
            if (!CourierIO.TryReadAllText(Path.Combine(directory, layout.EndpointFileName), out text)) return null;

            Dictionary<string, object?>? document;
            if (!CourierJson.TryParseObject(text, out document)) return null;
            string? endpointId = CourierJson.GetString(document, "endpointId");
            // SPEC §5：endpointId MUST 等于目录名，否则一个端点可以冒充另一个，能力目录就失去可信度。
            if (endpointId == null || !string.Equals(endpointId, Path.GetFileName(directory), StringComparison.Ordinal)) return null;

            List<CourierActionInfo> actions = ReadActions(document);
            List<CourierStateInfo> states = ReadStates(document);
            List<string> capabilities = ReadCapabilities(document);

            // SPEC §5：startedAtUtc 是 MAY 且不得用于判活；缺失时回落 MinValue 而不是"现在"，否则观察工具会看到一个永远刚启动的端点。
            DateTime startedAtUtc;
            if (!CourierProtocol.TryParseUtc(CourierJson.GetString(document, "startedAtUtc"), out startedAtUtc)) startedAtUtc = DateTime.MinValue;
            return new CourierEndpointInfo(
                endpointId,
                CourierJson.GetString(document, "description") ?? string.Empty,
                CourierJson.GetString(document, "sessionId") ?? string.Empty,
                Clamp(CourierJson.GetInteger(document, "processId")),
                startedAtUtc,
                Clamp(CourierJson.GetInteger(document, "protocolVersion")),
                actions,
                states,
                ReadHeartbeatAgeMs(directory, layout),
                ReadMetadata(document),
                capabilities);
        }

        /// <summary>
        /// 解析 <c>actions</c> 数组（SPEC §5.1）。名字非法的条目直接跳过：一条坏声明不该让整个能力目录不可读。
        /// </summary>
        /// <param name="document">已解析的 endpoint.json。</param>
        /// <returns>按文件顺序排列的动作表；字段缺失时为空表。</returns>
        private static List<CourierActionInfo> ReadActions(Dictionary<string, object?> document)
        {
            var actions = new List<CourierActionInfo>();
            List<object?>? rawActions = CourierJson.GetArray(document, "actions");
            if (rawActions == null) return actions;

            for (int index = 0; index < rawActions.Count; index++)
            {
                Dictionary<string, object?>? actionDocument = rawActions[index] as Dictionary<string, object?>;
                if (actionDocument == null) continue;
                string? channel = CourierJson.GetString(actionDocument, "channel");
                string? action = CourierJson.GetString(actionDocument, "action");
                // SPEC §4.3：channel/action 与 states、capabilities 用同一条名字正则，而且都必须是单段路径片段。
                // 非法名字（含 "../"、首尾点）若进了能力表，投递侧拼出来的路径就会逃出端点目录（§3.6）。
                if (channel == null || action == null) continue;
                if (!CourierProtocol.IsValidName(channel) || !CourierProtocol.IsValidName(action)) continue;
                actions.Add(new CourierActionInfo(
                    channel,
                    action,
                    CourierJson.GetString(actionDocument, "summary") ?? string.Empty,
                    ReadMetadata(actionDocument)));
            }

            return actions;
        }

        /// <summary>
        /// 解析 <c>states</c> 数组（SPEC §14）。它只是"这个端点会发布哪些状态"的声明，值在 <c>state/</c> 下按需读取。
        /// </summary>
        /// <param name="document">已解析的 endpoint.json。</param>
        /// <returns>按文件顺序排列的状态名表；字段缺失时为空表。</returns>
        private static List<CourierStateInfo> ReadStates(Dictionary<string, object?> document)
        {
            var states = new List<CourierStateInfo>();
            List<object?>? rawStates = CourierJson.GetArray(document, "states");
            if (rawStates == null) return states;

            for (int index = 0; index < rawStates.Count; index++)
            {
                Dictionary<string, object?>? stateDocument = rawStates[index] as Dictionary<string, object?>;
                if (stateDocument == null) continue;
                string? name = CourierJson.GetString(stateDocument, "name");
                if (name == null || !CourierProtocol.IsValidName(name)) continue;
                states.Add(new CourierStateInfo(
                    name,
                    CourierJson.GetString(stateDocument, "summary") ?? string.Empty,
                    ReadMetadata(stateDocument)));
            }

            return states;
        }

        /// <summary>
        /// 解析 <c>capabilities</c> 数组（SPEC §5）。名字非法的条目跳过，重复的名字只留第一次出现的。
        /// </summary>
        /// <param name="document">已解析的 endpoint.json。</param>
        /// <returns>能力名表；字段缺失时为空表。</returns>
        private static List<string> ReadCapabilities(Dictionary<string, object?> document)
        {
            var capabilities = new List<string>();
            List<object?>? rawCapabilities = CourierJson.GetArray(document, "capabilities");
            if (rawCapabilities == null) return capabilities;

            for (int index = 0; index < rawCapabilities.Count; index++)
            {
                string? capability = rawCapabilities[index] as string;
                if (capability == null || !CourierProtocol.IsValidName(capability)) continue;
                if (capabilities.Contains(capability)) continue;
                capabilities.Add(capability);
            }

            return capabilities;
        }

        /// <summary>
        /// 取条目上的 <c>metadata</c> 字段（SPEC §5.1、§14）。
        /// <para>
        /// 只接受 JSON 对象：协议说它是"对象"，写成一个数组或字符串多半是宿主写错了字段名。
        /// 类型不对按缺失处理（SPEC §3.7）——一条坏 metadata 不该让整个端点的能力目录变得不可读。
        /// </para>
        /// </summary>
        /// <param name="source">entry 或 endpoint.json 的解析结果。</param>
        /// <returns>单行紧凑 JSON 原文；缺失或类型不符时为 null。</returns>
        internal static string? ReadMetadata(Dictionary<string, object?>? source)
        {
            if (source == null) return null;
            object? raw;
            if (!source.TryGetValue("metadata", out raw)) return null;
            var document = raw as Dictionary<string, object?>;
            if (document == null) return null;
            try
            {
                // 空对象与"没有 metadata"是两件事：前者是宿主明确写了 {}，照样输出。
                return CourierJson.Write(document);
            }
            catch (FormatException caught)
            {
                // 写不回去的 metadata（例如解析留下 1e400 → Infinity 这种非有限数）按"没有 metadata"处理：
                // 这是只读扫描路径，§3.7 要求跳过坏条目，一份坏 endpoint.json 不能让整次扫描或 CLI 抛异常。
                _ = caught;
                return null;
            }
        }

        /// <summary>把协议里的 long 字段夹进 int 范围：对端写来一个超大值时应退化成 0，而不是强转成负数。</summary>
        private static int Clamp(long? value)
        {
            if (value == null) return 0;
            if (value.Value < int.MinValue || value.Value > int.MaxValue) return 0;
            return (int)value.Value;
        }

        /// <summary>按 SPEC §6 由心跳年龄推算活性：≤5 s 为 alive，≤15 s 为 stale，其余 dead。读不到心跳即 dead。</summary>
        /// <param name="directory">端点目录。</param>
        /// <returns>活性字符串。</returns>
        internal static string ReadState(string directory)
        {
            return CourierProtocol.StateFromHeartbeatAge(ReadHeartbeatAgeMs(directory));
        }

        /// <summary>按给定布局推算活性，心跳文件名取自布局（见 <see cref="TryRead(string, CourierLayout)"/>）。</summary>
        /// <param name="directory">端点目录。</param>
        /// <param name="layout">生效的布局。</param>
        /// <returns>活性字符串。</returns>
        internal static string ReadState(string directory, CourierLayout layout)
        {
            return CourierProtocol.StateFromHeartbeatAge(ReadHeartbeatAgeMs(directory, layout));
        }

        /// <summary>心跳年龄（毫秒）；读不到心跳时返回 null，由调用方决定写成 dead 还是 unknown。</summary>
        /// <param name="directory">端点目录。</param>
        /// <returns>非负毫秒数，读不到时为 null。</returns>
        internal static long? ReadHeartbeatAgeMs(string directory)
        {
            return ReadHeartbeatAgeMs(directory, FindLayout(directory));
        }

        /// <summary>按给定布局读取心跳年龄；心跳文件名取自布局，与写方（<see cref="CourierNode"/>）保持一致。</summary>
        /// <param name="directory">端点目录。</param>
        /// <param name="layout">生效的布局。</param>
        /// <returns>非负毫秒数，读不到时为 null。</returns>
        internal static long? ReadHeartbeatAgeMs(string directory, CourierLayout layout)
        {
            DateTime? heartbeat = ReadHeartbeatUtc(directory, layout);
            if (heartbeat == null) return null;
            double milliseconds = (DateTime.UtcNow - heartbeat.Value).TotalMilliseconds;
            // 时钟回拨时年龄为负：夹到 0，否则一个刚刚写下的心跳会被算成"未来"，比 dead 更难排查。
            if (milliseconds <= 0) return 0;
            return milliseconds >= long.MaxValue ? long.MaxValue : (long)milliseconds;
        }

        /// <summary>心跳时间戳；文件缺失或不可解析时返回 null。供 CLI 显示"多少秒之前"。</summary>
        /// <param name="directory">端点目录。</param>
        /// <returns>心跳时刻，读不到时为 null。</returns>
        internal static DateTime? ReadHeartbeatUtc(string directory)
        {
            return ReadHeartbeatUtc(directory, FindLayout(directory));
        }

        /// <summary>
        /// 按给定布局读取心跳时间戳（SPEC §6）。文件名 MUST 与写方一致：写方用
        /// <see cref="CourierLayout.HeartbeatFileName"/>，读方读协议常量时，配了 <c>heartbeatFile</c>
        /// 的布署下每个端点都会被判成 dead——投递正常、活性全错，最难排查的那一类不对称。
        /// </summary>
        /// <param name="directory">端点目录。</param>
        /// <param name="layout">生效的布局。</param>
        /// <returns>心跳时刻，读不到时为 null。</returns>
        internal static DateTime? ReadHeartbeatUtc(string directory, CourierLayout layout)
        {
            string? text;
            if (!CourierIO.TryReadAllText(Path.Combine(directory, layout.HeartbeatFileName), out text)) return null;
            DateTime heartbeat;
            return CourierProtocol.TryParseUtc(Unquote(text), out heartbeat) ? heartbeat : (DateTime?)null;
        }

        /// <summary>
        /// 心跳按 SPEC §6 是裸时间戳，但读取侧容忍带 JSON 引号的写法：判活是纯只读操作，
        /// 因为对方实现多写了一对引号就把一个活着的端点判成 dead，代价远大于多剥一次引号。
        /// </summary>
        private static string Unquote(string text)
        {
            string trimmed = text.Trim();
            if (trimmed.Length < 2 || trimmed[0] != '"' || trimmed[trimmed.Length - 1] != '"') return trimmed;
            return trimmed.Substring(1, trimmed.Length - 2);
        }
    }
}
