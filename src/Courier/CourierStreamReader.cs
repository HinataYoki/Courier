using System;
using System.IO;

namespace Courier
{
    /// <summary>
    /// CourierStream 的读取端。打开时只读一次 descriptor，之后通过描述指定的传输直接读取记录，
    /// 不经过 commands/results 文件轮询。
    /// </summary>
    public sealed class CourierStreamReader : IDisposable
    {
        private readonly ICourierStreamSource mSource;
        private bool mDisposed;

        /// <summary>接管一个已经连接的读取来源。</summary>
        private CourierStreamReader(
            ICourierStreamSource source,
            string transport,
            long oldestSequence,
            long nextSequence,
            long skippedBefore,
            long droppedBefore)
        {
            mSource = source;
            Transport = transport;
            OldestSequence = oldestSequence;
            NextSequence = nextSequence;
            SkippedBefore = skippedBefore;
            DroppedBefore = droppedBefore;
        }

        /// <summary>实际使用的传输名：<c>unix-socket</c> 或 <c>mmap-ring</c>。</summary>
        public string Transport { get; }

        /// <summary>连接时宿主环里最旧的记录序号。</summary>
        public long OldestSequence { get; }

        /// <summary>连接时宿主下一次写入将使用的序号。</summary>
        public long NextSequence { get; }

        /// <summary>请求的起点早于宿主最旧记录时，确认已经错过的记录数。</summary>
        public long SkippedBefore { get; }

        /// <summary>宿主在连接前因环满丢弃的累计记录数。</summary>
        public long DroppedBefore { get; }

        /// <summary>读取端是否仍持有可用连接。</summary>
        public bool IsConnected
        {
            get { return !mDisposed && mSource.IsConnected; }
        }

        /// <summary>
        /// 打开指定端点的流；descriptor 不存在、传输不受支持或连接失败时抛出 <see cref="IOException"/>。
        /// </summary>
        /// <param name="rootDirectory">Courier 根目录。</param>
        /// <param name="endpointId">目标端点。</param>
        /// <param name="streamName">流名称。</param>
        /// <param name="layout">目录布局；为空时读取根目录布局。</param>
        /// <param name="sinceSequence">请求的起始序号；小于等于 0 表示从宿主当前最旧记录开始。</param>
        public static CourierStreamReader Open(
            string rootDirectory,
            string endpointId,
            string streamName,
            CourierLayout? layout = null,
            long sinceSequence = 0)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("root directory is required", nameof(rootDirectory));
            if (!CourierProtocol.IsValidEndpointId(endpointId))
            {
                throw new ArgumentException("invalid endpointId: must match ^[a-z0-9]([a-z0-9-]{0,62}[a-z0-9])?$", nameof(endpointId));
            }

            if (!CourierStreamDescriptor.IsValidStreamName(streamName))
            {
                throw new ArgumentException("invalid stream name", nameof(streamName));
            }

            string root = Path.GetFullPath(rootDirectory);
            CourierLayout resolved = layout ?? CourierLayout.Load(root);
            if (!CourierStreamDescriptor.TryRead(root, resolved, endpointId, streamName, out CourierStreamDescriptor? descriptor)
                || descriptor == null)
            {
                throw new IOException("courier stream descriptor was not found: " + streamName);
            }

            ICourierStreamSource source;
            long oldest;
            long next;
            long dropped;
            if (string.Equals(descriptor.Transport, CourierStreamDescriptor.TRANSPORT_UNIX_SOCKET, StringComparison.Ordinal))
            {
                string endpointDirectory = Path.Combine(
                    CourierLayout.Combine(root, resolved.EndpointsDirectory),
                    endpointId);
                string socketPath = descriptor.ResolveSocketPath(endpointDirectory);
                var socketSource = CourierStreamSocketSource.Open(socketPath, descriptor.MaxRecordBytes, sinceSequence);
                source = socketSource;
                oldest = socketSource.OldestSequence;
                next = socketSource.NextSequence;
                dropped = socketSource.DroppedBefore;
            }
            else if (string.Equals(descriptor.Transport, CourierStreamDescriptor.TRANSPORT_MMAP_RING, StringComparison.Ordinal))
            {
                var mmapSource = CourierStreamMmapSource.Open(descriptor, sinceSequence);
                source = mmapSource;
                oldest = mmapSource.OldestSequence;
                next = mmapSource.NextSequence;
                dropped = mmapSource.DroppedBefore;
            }
            else
            {
                throw new IOException("courier stream transport is unsupported: " + descriptor.Transport);
            }

            long skipped = sinceSequence > 0 && oldest > sinceSequence ? oldest - sinceSequence : 0;
            return new CourierStreamReader(source, descriptor.Transport, oldest, next, skipped, dropped);
        }

        /// <summary>
        /// 读取下一条记录。超时返回 <c>false</c>；连接已断开或帧损坏时抛出异常。
        /// </summary>
        /// <param name="timeoutMs">等待毫秒数；0 表示只检查当前已有数据，负数表示无限等待。</param>
        /// <param name="record">读到的记录；没有数据时为 <c>null</c>。</param>
        /// <returns>读到记录时为 <c>true</c>。</returns>
        public bool TryRead(int timeoutMs, out CourierStreamRecord? record)
        {
            record = null;
            if (mDisposed) return false;
            return mSource.TryRead(timeoutMs, out record);
        }

        /// <summary>关闭连接；幂等。</summary>
        public void Dispose()
        {
            if (mDisposed) return;
            mDisposed = true;
            mSource.Dispose();
        }
    }
}
