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
        /// <summary>扫描 endpoints/ 下全部端点，按 endpointId 升序返回。</summary>
        /// <param name="endpointsRoot">endpoints/ 的绝对路径。</param>
        /// <returns>能成功解析的端点；损坏或不合法的目录被跳过，而不是让整次扫描失败。</returns>
        internal static List<CourierEndpointInfo> ReadAll(string endpointsRoot)
        {
            var result = new List<CourierEndpointInfo>();
            if (!Directory.Exists(endpointsRoot)) return result;

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
                CourierEndpointInfo? info = TryRead(directories[index]);
                if (info != null) result.Add(info);
            }

            return result;
        }

        /// <summary>读取一个端点目录；endpoint.json 缺失或不可解析时返回 null，由调用方跳过而不是抛出。</summary>
        internal static CourierEndpointInfo? TryRead(string directory)
        {
            string? text;
            if (!CourierIO.TryReadAllText(Path.Combine(directory, CourierProtocol.EndpointFileName), out text)) return null;

            Dictionary<string, object?>? document;
            if (!CourierJson.TryParseObject(text, out document)) return null;
            string? endpointId = CourierJson.GetString(document, "endpointId");
            // SPEC §5：endpointId MUST 等于目录名，否则一个端点可以冒充另一个，能力目录就失去可信度。
            if (endpointId == null || !string.Equals(endpointId, Path.GetFileName(directory), StringComparison.Ordinal)) return null;

            var actions = new List<CourierActionInfo>();
            List<object?>? rawActions = CourierJson.GetArray(document, "actions");
            if (rawActions != null)
            {
                for (int index = 0; index < rawActions.Count; index++)
                {
                    Dictionary<string, object?>? actionDocument = rawActions[index] as Dictionary<string, object?>;
                    if (actionDocument == null) continue;
                    string? channel = CourierJson.GetString(actionDocument, "channel");
                    string? action = CourierJson.GetString(actionDocument, "action");
                    if (channel == null || action == null) continue;
                    actions.Add(new CourierActionInfo(channel, action, CourierJson.GetString(actionDocument, "summary") ?? string.Empty));
                }
            }

            // SPEC §14：states 只是"这个端点会发布哪些状态"的声明，值在 state/ 下按需读取。
            // 名字非法的条目直接跳过——一条坏声明不该让整个端点的能力目录变得不可读。
            var states = new List<CourierStateInfo>();
            List<object?>? rawStates = CourierJson.GetArray(document, "states");
            if (rawStates != null)
            {
                for (int index = 0; index < rawStates.Count; index++)
                {
                    Dictionary<string, object?>? stateDocument = rawStates[index] as Dictionary<string, object?>;
                    if (stateDocument == null) continue;
                    string? name = CourierJson.GetString(stateDocument, "name");
                    if (name == null || !CourierProtocol.IsValidName(name)) continue;
                    states.Add(new CourierStateInfo(name, CourierJson.GetString(stateDocument, "summary") ?? string.Empty));
                }
            }

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
                ReadHeartbeatAgeMs(directory));
        }

        /// <summary>把协议里的 long 字段夹进 int 范围：对端写来一个超大值时应退化成 0，而不是强转成负数。</summary>
        private static int Clamp(long? value)
        {
            if (value == null) return 0;
            if (value.Value < int.MinValue || value.Value > int.MaxValue) return 0;
            return (int)value.Value;
        }

        /// <summary>按 SPEC §6 由心跳年龄推算活性：≤5 s 为 alive，≤15 s 为 stale，其余 dead。读不到心跳即 dead。</summary>
        internal static string ReadState(string directory)
        {
            return CourierProtocol.StateFromHeartbeatAge(ReadHeartbeatAgeMs(directory));
        }

        /// <summary>心跳年龄（毫秒）；读不到心跳时返回 null，由调用方决定写成 dead 还是 unknown。</summary>
        internal static long? ReadHeartbeatAgeMs(string directory)
        {
            DateTime? heartbeat = ReadHeartbeatUtc(directory);
            if (heartbeat == null) return null;
            double milliseconds = (DateTime.UtcNow - heartbeat.Value).TotalMilliseconds;
            // 时钟回拨时年龄为负：夹到 0，否则一个刚刚写下的心跳会被算成"未来"，比 dead 更难排查。
            if (milliseconds <= 0) return 0;
            return milliseconds >= long.MaxValue ? long.MaxValue : (long)milliseconds;
        }

        /// <summary>心跳时间戳；文件缺失或不可解析时返回 null。供 CLI 显示"多少秒之前"。</summary>
        internal static DateTime? ReadHeartbeatUtc(string directory)
        {
            string? text;
            if (!CourierIO.TryReadAllText(Path.Combine(directory, CourierProtocol.HeartbeatFileName), out text)) return null;
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
