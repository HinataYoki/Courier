using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace Courier
{
    /// <summary>
    /// Unix Domain Socket 的 CourierStream 读取来源。握手后按帧读取，等待阶段不消费字节，
    /// 一旦开始读某一帧就要求完整读完，避免超时把后续帧边界打乱。
    /// </summary>
    internal sealed class CourierStreamSocketSource : ICourierStreamSource
    {
        private const int CONNECT_TIMEOUT_MS = 5000;
        private const int RECONNECT_DELAY_MS = 20;
        private const int FULL_FRAME_TIMEOUT_MS = 5000;
        private const int WAIT_SLICE_MS = 1000;

        private readonly NetworkStream mStream;
        private readonly Socket mSocket;
        private readonly int mMaxRecordBytes;
        private bool mConnected;
        private bool mDisposed;

        /// <summary>接管已完成握手的连接。</summary>
        private CourierStreamSocketSource(
            NetworkStream stream,
            Socket socket,
            int maxRecordBytes,
            long oldestSequence,
            long nextSequence,
            long droppedBefore)
        {
            mStream = stream;
            mSocket = socket;
            mMaxRecordBytes = maxRecordBytes;
            OldestSequence = oldestSequence;
            NextSequence = nextSequence;
            DroppedBefore = droppedBefore;
            mConnected = true;
        }

        /// <summary>连接时宿主环里最旧的记录序号。</summary>
        internal long OldestSequence { get; }

        /// <summary>连接时宿主下一次写入将使用的序号。</summary>
        internal long NextSequence { get; }

        /// <summary>宿主在连接前因环满丢弃的累计记录数。</summary>
        internal long DroppedBefore { get; }

        /// <summary>读取端是否仍持有连接。</summary>
        public bool IsConnected
        {
            get
            {
                if (mDisposed || !mConnected) return false;
                try
                {
                    return !(mSocket.Poll(0, SelectMode.SelectRead) && mSocket.Available == 0);
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
                catch (SocketException)
                {
                    return false;
                }
            }
        }

        /// <summary>连接指定 UDS，并完成序号握手。</summary>
        /// <param name="socketPath">socket 文件路径。</param>
        /// <param name="maxRecordBytes">单条正文上限。</param>
        /// <param name="sinceSequence">请求的起始序号；小于等于 0 表示从当前最旧记录开始。</param>
        /// <returns>已连接的读取来源。</returns>
        internal static CourierStreamSocketSource Open(string socketPath, int maxRecordBytes, long sinceSequence)
        {
            Socket? socket = null;
            NetworkStream? stream = null;
            try
            {
                socket = ConnectWithRetry(socketPath);
                stream = new NetworkStream(socket, true);
                socket.ReceiveTimeout = CONNECT_TIMEOUT_MS;
                socket.SendTimeout = CONNECT_TIMEOUT_MS;
                WriteRequest(stream, sinceSequence);

                byte[] response = new byte[CourierStreamProtocol.RESPONSE_BYTES];
                ReadExact(stream, response, 0, response.Length);
                if (BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(0, 4)) != CourierStreamProtocol.MAGIC
                    || BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(4, 4)) != CourierStreamProtocol.VERSION)
                {
                    throw new InvalidDataException("courier stream handshake returned an invalid response");
                }

                long oldest = BinaryPrimitives.ReadInt64LittleEndian(response.AsSpan(8, 8));
                long next = BinaryPrimitives.ReadInt64LittleEndian(response.AsSpan(16, 8));
                long dropped = BinaryPrimitives.ReadInt64LittleEndian(response.AsSpan(24, 8));
                socket.ReceiveTimeout = Timeout.Infinite;
                return new CourierStreamSocketSource(stream, socket, maxRecordBytes, oldest, next, dropped);
            }
            catch
            {
                stream?.Dispose();
                socket?.Dispose();
                throw;
            }
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
            if (mDisposed || !mConnected) return false;
            if (!WaitForReadable(timeoutMs)) return false;

            var header = new byte[CourierStreamProtocol.FRAME_HEADER_BYTES];
            mStream.ReadTimeout = FULL_FRAME_TIMEOUT_MS;
            ReadFrameExact(header, 0, header.Length);

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4, 2));
            long sequence = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8, 8));
            long timestampUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16, 8));
            int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(24, 4));
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28, 4));
            if (magic != CourierStreamProtocol.MAGIC || version != CourierStreamProtocol.VERSION)
            {
                throw new InvalidDataException("courier stream frame header is invalid");
            }

            if (length < 0 || length > mMaxRecordBytes)
            {
                throw new InvalidDataException("courier stream frame length is invalid");
            }

            byte[] payload = new byte[length];
            ReadFrameExact(payload, 0, payload.Length);
            if (Crc32.Compute(payload, 0, payload.Length) != crc)
            {
                throw new InvalidDataException("courier stream frame crc mismatch");
            }

            record = new CourierStreamRecord(sequence, timestampUtcTicks, payload);
            return true;
        }

        /// <summary>关闭连接。</summary>
        public void Dispose()
        {
            if (mDisposed) return;
            mDisposed = true;
            try { mStream.Dispose(); }
            catch (ObjectDisposedException) { }
            try { mSocket.Dispose(); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>等到 socket 可读，或到达调用方给的超时。等待阶段不读取任何字节。</summary>
        private bool WaitForReadable(int timeoutMs)
        {
            // timeoutMs == 0 表示“只检查当前已有数据”。Socket.Poll(0) 会立即返回，
            // 不能先走 deadline 判断，否则任何已到达的数据都会被误判为超时。
            if (timeoutMs == 0)
            {
                try
                {
                    return mSocket.Poll(0, SelectMode.SelectRead);
                }
                catch (ObjectDisposedException)
                {
                    mConnected = false;
                    return false;
                }
                catch (SocketException)
                {
                    mConnected = false;
                    return false;
                }
            }

            DateTime deadline = timeoutMs < 0
                ? DateTime.MaxValue
                : DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                int sliceMs;
                if (timeoutMs < 0)
                {
                    sliceMs = WAIT_SLICE_MS;
                }
                else
                {
                    double remaining = (deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (remaining <= 0) return false;
                    sliceMs = remaining >= WAIT_SLICE_MS ? WAIT_SLICE_MS : (int)Math.Ceiling(remaining);
                }

                try
                {
                    if (mSocket.Poll(sliceMs * 1000, SelectMode.SelectRead)) return true;
                }
                catch (ObjectDisposedException)
                {
                    mConnected = false;
                    return false;
                }
                catch (SocketException)
                {
                    mConnected = false;
                    return false;
                }
            }
        }

        /// <summary>读满一帧的指定字节；中途超时或断开时让连接失效并抛出异常。</summary>
        private void ReadFrameExact(byte[] buffer, int offset, int count)
        {
            try
            {
                ReadExact(mStream, buffer, offset, count);
            }
            catch (Exception caught) when (caught is IOException || caught is ObjectDisposedException)
            {
                mConnected = false;
                throw new IOException("courier stream connection was lost in the middle of a frame", caught);
            }
        }

        /// <summary>带重试连接 socket；descriptor 可能比监听线程早一个瞬时窗口。</summary>
        private static Socket ConnectWithRetry(string socketPath)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(CONNECT_TIMEOUT_MS);
            Exception? last = null;
            while (DateTime.UtcNow < deadline)
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    socket.Connect(new UnixDomainSocketEndPoint(socketPath));
                    return socket;
                }
                catch (Exception caught) when (caught is SocketException || caught is IOException)
                {
                    last = caught;
                    socket.Dispose();
                    Thread.Sleep(RECONNECT_DELAY_MS);
                }
            }

            throw new IOException("courier stream socket could not be connected: " + socketPath, last);
        }

        /// <summary>写握手请求。</summary>
        private static void WriteRequest(NetworkStream stream, long sinceSequence)
        {
            var request = new byte[CourierStreamProtocol.REQUEST_BYTES];
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(0, 4), CourierStreamProtocol.MAGIC);
            BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(4, 4), CourierStreamProtocol.VERSION);
            BinaryPrimitives.WriteInt64LittleEndian(request.AsSpan(8, 8), sinceSequence);
            stream.Write(request, 0, request.Length);
        }

        /// <summary>读满指定字节；EOF 抛异常。</summary>
        private static void ReadExact(NetworkStream stream, byte[] buffer, int offset, int count)
        {
            int received = 0;
            while (received < count)
            {
                int read = stream.Read(buffer, offset + received, count - received);
                if (read <= 0) throw new EndOfStreamException("courier stream closed");
                received += read;
            }
        }
    }
}
