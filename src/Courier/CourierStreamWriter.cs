using System;
using System.IO;
using System.Threading;

namespace Courier
{
    /// <summary>
    /// CourierStream 的宿主写入端。一个端点可以打开多个不同名称的流。
    /// </summary>
    public sealed class CourierStreamWriter : IDisposable
    {
        private readonly CourierStreamDescriptor mDescriptor;
        private readonly ICourierStreamHost mHost;
        private readonly string mEndpointDirectory;
        private bool mDisposed;

        /// <summary>创建流并写出 descriptor；失败时不留下可用的描述。</summary>
        internal CourierStreamWriter(
            string endpointDirectory,
            string streamName,
            string sessionId,
            CourierStreamOptions? options)
        {
            CourierStreamOptions settings = (options ?? new CourierStreamOptions()).Validate();
            if (!CourierStreamDescriptor.IsValidStreamName(streamName))
            {
                throw new ArgumentException("invalid stream name", nameof(streamName));
            }

            mEndpointDirectory = endpointDirectory;
            int hostPid = CourierProtocol.CurrentProcessId();
            ICourierStreamHost host;
            CourierStreamDescriptor descriptor;
            if (TryCreateMmapHost(
                endpointDirectory,
                streamName,
                sessionId,
                hostPid,
                settings,
                out host,
                out descriptor))
            {
                mHost = host;
                mDescriptor = descriptor;
            }
            else
            {
                string socketFile = sessionId + ".sock";
                string streamDirectory = CourierStreamDescriptor.DirectoryFor(endpointDirectory, streamName);
                var unixHost = new CourierStreamUnixHost(
                    streamDirectory,
                    socketFile,
                    settings.Capacity,
                    settings.MaxRecordBytes);
                descriptor = new CourierStreamDescriptor(
                    streamName,
                    sessionId,
                    hostPid,
                    settings.Capacity,
                    settings.MaxRecordBytes,
                    CourierStreamDescriptor.TRANSPORT_UNIX_SOCKET,
                    unixHost.SocketFile,
                    string.Equals(unixHost.SocketDirectory, streamDirectory, StringComparison.Ordinal)
                        ? null
                        : unixHost.SocketDirectory,
                    null,
                    null);
                host = unixHost;
                mHost = host;
                mDescriptor = descriptor;
            }

            if (!mDescriptor.TryWrite(endpointDirectory))
            {
                mHost.Dispose();
                throw new IOException("courier stream descriptor could not be written: " + streamName);
            }
        }

        /// <summary>流名称。</summary>
        public string StreamName
        {
            get { return mDescriptor.StreamName; }
        }

        /// <summary>当前已写入的最后一条记录序号；没有记录时为 0。</summary>
        public long WrittenSequence
        {
            get { return mHost.WrittenSequence; }
        }

        /// <summary>因环满丢弃的累计记录数。</summary>
        public long DroppedCount
        {
            get { return mHost.DroppedCount; }
        }

        /// <summary>
        /// 追加一条记录；正文超过配置上限时返回 <c>false</c>。环满时会丢弃最旧记录，
        /// 但当前记录仍会写入并返回 <c>true</c>。
        /// </summary>
        public bool TryWrite(ReadOnlySpan<byte> payload)
        {
            if (mDisposed) return false;
            return mHost.TryWrite(payload);
        }

        /// <summary>关闭发送线程、socket 和 descriptor。</summary>
        public void Dispose()
        {
            if (mDisposed) return;
            mDisposed = true;
            mHost.Dispose();
            mDescriptor.TryDeleteOwned(mEndpointDirectory);
        }

        /// <summary>
        /// 尝试创建 Windows mmap-ring 宿主；平台不支持或内核对象创建失败时返回 <c>false</c>，
        /// 调用方随后回退 UDS。描述冲突不在这里处理，由统一的 <see cref="CourierStreamDescriptor.TryWrite"/> 拒绝。
        /// </summary>
        /// <param name="endpointDirectory">端点目录。</param>
        /// <param name="streamName">流名称。</param>
        /// <param name="sessionId">端点会话标识。</param>
        /// <param name="hostPid">宿主进程号。</param>
        /// <param name="settings">已校验的容量配置。</param>
        /// <param name="host">创建成功的宿主。</param>
        /// <param name="descriptor">与宿主匹配的描述。</param>
        /// <returns>成功创建 mmap-ring 宿主时为 <c>true</c>。</returns>
        private static bool TryCreateMmapHost(
            string endpointDirectory,
            string streamName,
            string sessionId,
            int hostPid,
            CourierStreamOptions settings,
            out ICourierStreamHost host,
            out CourierStreamDescriptor descriptor)
        {
            host = null!;
            descriptor = null!;
            if (!CourierStreamWindowsHost.IsSupported) return false;

            string mapName = CourierStreamWindowsHost.MapNameFor(sessionId, streamName);
            string eventName = CourierStreamWindowsHost.DataEventNameFor(sessionId, streamName);
            try
            {
                var created = new CourierStreamWindowsHost(
                    mapName,
                    eventName,
                    settings.Capacity,
                    settings.MaxRecordBytes);
                host = created;
                descriptor = new CourierStreamDescriptor(
                    streamName,
                    sessionId,
                    hostPid,
                    settings.Capacity,
                    settings.MaxRecordBytes,
                    CourierStreamDescriptor.TRANSPORT_MMAP_RING,
                    null,
                    null,
                    mapName,
                    eventName);
                return true;
            }
            catch (Exception caught) when (IsMmapUnavailable(caught))
            {
                CourierHotFaults.Record("stream mmap unavailable: " + caught.GetType().Name);
                return false;
            }
        }

        /// <summary>判断异常是否只表示 mmap-ring 在当前机器上不可用。</summary>
        /// <param name="caught">创建共享内存或门铃时的异常。</param>
        /// <returns>可以安全回退 UDS 时为 <c>true</c>。</returns>
        private static bool IsMmapUnavailable(Exception caught)
        {
            return caught is IOException
                || caught is UnauthorizedAccessException
                || caught is NotSupportedException
                || caught is PlatformNotSupportedException
                || caught is WaitHandleCannotBeOpenedException
                || caught is ArgumentException;
        }
    }
}
