using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Courier
{
    /// <summary>
    /// 流数据面的会合描述。它写进端点目录下的实现私有 <c>streams/&lt;name&gt;/descriptor.json</c>，
    /// 不进入 <c>endpoint.json</c>，也不承载任何事件正文。
    /// </summary>
    internal sealed class CourierStreamDescriptor
    {
        /// <summary>流目录的实现私有名称。</summary>
        internal const string STREAM_DIRECTORY = "streams";

        /// <summary>描述文件名。</summary>
        internal const string FILE_NAME = "descriptor.json";

        /// <summary>Unix Domain Socket 传输名。</summary>
        internal const string TRANSPORT_UNIX_SOCKET = "unix-socket";

        /// <summary>Windows 命名共享内存 + 命名事件的传输名。</summary>
        internal const string TRANSPORT_MMAP_RING = "mmap-ring";

        /// <summary>描述格式版本。</summary>
        internal const int VERSION = 1;

        /// <summary>创建一份描述。</summary>
        internal CourierStreamDescriptor(
            string streamName,
            string sessionId,
            int hostPid,
            int capacity,
            int maxRecordBytes,
            string transport,
            string? socketFile,
            string? socketDirectory,
            string? mapName,
            string? dataEventName)
        {
            StreamName = streamName;
            SessionId = sessionId;
            HostPid = hostPid;
            Capacity = capacity;
            MaxRecordBytes = maxRecordBytes;
            Transport = transport;
            SocketFile = socketFile;
            SocketDirectory = socketDirectory;
            MapName = mapName;
            DataEventName = dataEventName;
        }

        /// <summary>流名称。</summary>
        internal string StreamName { get; }

        /// <summary>端点会话标识；域重载或重启后变化。</summary>
        internal string SessionId { get; }

        /// <summary>宿主进程号。</summary>
        internal int HostPid { get; }

        /// <summary>环容量。</summary>
        internal int Capacity { get; }

        /// <summary>单条记录正文上限。</summary>
        internal int MaxRecordBytes { get; }

        /// <summary>传输名：<c>unix-socket</c> 或 <c>mmap-ring</c>。</summary>
        internal string Transport { get; }

        /// <summary>Unix Domain Socket 传输使用的文件名；其他传输为 null。</summary>
        internal string? SocketFile { get; }

        /// <summary>socket 无法放进流目录时使用的短路径目录；正常情况为 null。</summary>
        internal string? SocketDirectory { get; }

        /// <summary>Windows 共享内存对象名；其他传输为 null。</summary>
        internal string? MapName { get; }

        /// <summary>Windows 有数据门铃对象名；其他传输为 null。</summary>
        internal string? DataEventName { get; }

        /// <summary>
        /// 判断流名称是否合法：小写字母、数字和连字符，长度 1 到 32，不能以连字符开头或结尾。
        /// </summary>
        internal static bool IsValidStreamName(string? name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 32) return false;
            if (name[0] == '-' || name[name.Length - 1] == '-') return false;

            for (int index = 0; index < name.Length; index++)
            {
                char current = name[index];
                bool valid = (current >= 'a' && current <= 'z')
                    || (current >= '0' && current <= '9')
                    || current == '-';
                if (!valid) return false;
            }

            return true;
        }

        /// <summary>流目录。</summary>
        internal static string DirectoryFor(string endpointDirectory, string streamName)
        {
            return Path.Combine(endpointDirectory, STREAM_DIRECTORY, streamName);
        }

        /// <summary>描述文件路径。</summary>
        internal static string PathFor(string endpointDirectory, string streamName)
        {
            return Path.Combine(DirectoryFor(endpointDirectory, streamName), FILE_NAME);
        }

        /// <summary>解析后的 socket 绝对路径。</summary>
        internal string ResolveSocketPath(string endpointDirectory)
        {
            if (!string.Equals(Transport, TRANSPORT_UNIX_SOCKET, StringComparison.Ordinal) || SocketFile == null)
            {
                throw new InvalidOperationException("courier stream does not use a unix socket");
            }

            string directory = string.IsNullOrEmpty(SocketDirectory)
                ? DirectoryFor(endpointDirectory, StreamName)
                : SocketDirectory!;
            return Path.Combine(directory, SocketFile);
        }

        /// <summary>把描述写成 JSON。</summary>
        internal string ToJson()
        {
            CourierJsonObject document = new CourierJsonObject()
                .AddNumber("version", VERSION)
                .AddString("transport", Transport)
                .AddString("stream", StreamName)
                .AddString("sessionId", SessionId)
                .AddNumber("hostPid", HostPid)
                .AddNumber("capacity", Capacity)
                .AddNumber("maxRecordBytes", MaxRecordBytes);

            if (string.Equals(Transport, TRANSPORT_UNIX_SOCKET, StringComparison.Ordinal))
            {
                document.AddString("socketFile", SocketFile);
                if (!string.IsNullOrEmpty(SocketDirectory))
                {
                    document.AddString("socketDirectory", SocketDirectory);
                }
            }
            else
            {
                document.AddString("mapName", MapName);
                document.AddString("dataEventName", DataEventName);
            }

            return document.Build();
        }

        /// <summary>解析描述；版本、传输、几何或路径不安全时返回 null。</summary>
        internal static CourierStreamDescriptor? TryParse(string? json)
        {
            Dictionary<string, object?>? block;
            if (!CourierJson.TryParseObject(json, out block) || block == null) return null;

            long? version = CourierJson.GetInteger(block, "version");
            if (version == null || version.Value != VERSION) return null;

            string? transport = CourierJson.GetString(block, "transport");
            bool unixSocket = string.Equals(transport, TRANSPORT_UNIX_SOCKET, StringComparison.Ordinal);
            bool mmapRing = string.Equals(transport, TRANSPORT_MMAP_RING, StringComparison.Ordinal);
            if (!unixSocket && !mmapRing) return null;

            string? streamName = CourierJson.GetString(block, "stream");
            if (!IsValidStreamName(streamName)) return null;

            string? sessionId = CourierJson.GetString(block, "sessionId");
            if (string.IsNullOrEmpty(sessionId)) return null;

            long? hostPid = CourierJson.GetInteger(block, "hostPid");
            if (hostPid == null || hostPid.Value <= 0 || hostPid.Value > int.MaxValue) return null;

            long? capacity = CourierJson.GetInteger(block, "capacity");
            if (capacity == null || capacity.Value <= 0 || capacity.Value > 65536) return null;

            long? maxRecordBytes = CourierJson.GetInteger(block, "maxRecordBytes");
            if (maxRecordBytes == null || maxRecordBytes.Value <= 0 || maxRecordBytes.Value > 1024 * 1024) return null;
            if (capacity.Value * maxRecordBytes.Value > CourierStreamOptions.MAX_BUFFER_BYTES) return null;

            string? socketFile = null;
            string? socketDirectory = null;
            string? mapName = null;
            string? dataEventName = null;
            if (unixSocket)
            {
                socketFile = CourierJson.GetString(block, "socketFile");
                if (!IsSafeFileName(socketFile)) return null;
                socketDirectory = CourierJson.GetString(block, "socketDirectory");
                if (!string.IsNullOrEmpty(socketDirectory) && !IsSafeSocketDirectory(socketDirectory))
                {
                    return null;
                }
            }
            else
            {
                mapName = CourierJson.GetString(block, "mapName");
                dataEventName = CourierJson.GetString(block, "dataEventName");
                if (!IsSafeObjectName(mapName) || !IsSafeObjectName(dataEventName)) return null;
            }

            return new CourierStreamDescriptor(
                streamName!,
                sessionId!,
                (int)hostPid.Value,
                (int)capacity.Value,
                (int)maxRecordBytes.Value,
                transport!,
                socketFile,
                socketDirectory,
                mapName,
                dataEventName);
        }

        /// <summary>
        /// 写入描述；已有另一个宿主或会话占用同一流时拒绝覆盖，返回 <c>false</c>。
        /// </summary>
        internal bool TryWrite(string endpointDirectory)
        {
            try
            {
                string directory = DirectoryFor(endpointDirectory, StreamName);
                string path = PathFor(endpointDirectory, StreamName);
                CourierStreamDescriptor? existing = ReadAt(path);
                if (existing != null
                    && (existing.HostPid != HostPid
                        || !string.Equals(existing.SessionId, SessionId, StringComparison.Ordinal))
                    && IsProcessAlive(existing.HostPid))
                {
                    return false;
                }

                CourierIO.EnsureDirectory(directory);
                CourierIO.WriteAtomic(path, ToJson(), true);
                return true;
            }
            catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException || caught is NotSupportedException)
            {
                return false;
            }
        }

        /// <summary>只在描述确实属于本会话时删除。</summary>
        internal bool TryDeleteOwned(string endpointDirectory)
        {
            string path = PathFor(endpointDirectory, StreamName);
            CourierStreamDescriptor? existing = ReadAt(path);
            if (existing != null
                && (existing.HostPid != HostPid
                    || !string.Equals(existing.SessionId, SessionId, StringComparison.Ordinal)))
            {
                return false;
            }

            CourierIO.TryDelete(path);
            return true;
        }

        /// <summary>从 root 读取指定端点的流描述。</summary>
        internal static bool TryRead(
            string rootDirectory,
            CourierLayout layout,
            string endpointId,
            string streamName,
            out CourierStreamDescriptor? descriptor)
        {
            descriptor = null;
            string endpointDirectory = Path.Combine(
                CourierLayout.Combine(rootDirectory, layout.EndpointsDirectory), endpointId);
            descriptor = ReadAt(PathFor(endpointDirectory, streamName));
            return descriptor != null;
        }

        /// <summary>读取任意路径的描述；失败时返回 null。</summary>
        private static CourierStreamDescriptor? ReadAt(string path)
        {
            string? text;
            if (!CourierIO.TryReadAllText(path, out text)) return null;
            return TryParse(text);
        }

        /// <summary>socket 文件名必须是单段相对文件名，不能逃出流目录。</summary>
        private static bool IsSafeFileName(string? value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            if (Path.IsPathRooted(value)) return false;
            if (string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) == false) return false;
            return value != "." && value != ".." && value.IndexOf('\0') < 0;
        }

        /// <summary>
        /// Windows 命名对象必须是本实现生成的本地命名空间名称，避免描述被篡改为任意内核对象名。
        /// </summary>
        /// <param name="value">对象名。</param>
        /// <returns>名称符合实现私有前缀与长度约束时为 <c>true</c>。</returns>
        private static bool IsSafeObjectName(string? value)
        {
            const string prefix = "Local\\CourierStream.";
            if (string.IsNullOrEmpty(value) || value.Length > 200) return false;
            if (!value.StartsWith(prefix, StringComparison.Ordinal)) return false;
            return value.IndexOf('\0') < 0;
        }

        /// <summary>短 socket 目录必须是绝对路径，且不能包含 NUL。</summary>
        /// <param name="value">描述中的目录。</param>
        /// <returns>可用时为 <c>true</c>。</returns>
        private static bool IsSafeSocketDirectory(string value)
        {
            if (value.IndexOf('\0') >= 0) return false;
            return Path.IsPathRooted(value);
        }

        /// <summary>
        /// 判断旧描述指向的宿主是否仍活着。崩溃后 PID 已消失时允许新会话接管描述；
        /// 进程存在但无权限查询时按存活处理，宁可拒绝也不覆盖可能仍在服务的流。
        /// </summary>
        /// <param name="processId">旧描述记录的宿主进程号。</param>
        /// <returns>进程仍存在时为 <c>true</c>。</returns>
        private static bool IsProcessAlive(int processId)
        {
            if (processId <= 0) return false;
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    return !process.HasExited;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (Win32Exception)
            {
                return true;
            }
        }
    }
}
