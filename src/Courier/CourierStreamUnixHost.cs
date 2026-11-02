using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Courier
{
    /// <summary>
    /// CourierStream 的 Unix Domain Socket 宿主端。
    /// <para>
    /// 写入方把记录放进进程内环；后台线程把环里的新记录顺序送给唯一的活动 reader。
    /// reader 断开后宿主继续保留环内容，下一次连接可以从当前最旧记录重新追赶。
    /// 这个类型不写 commands/results，也不改变 Courier 文件协议。
    /// </para>
    /// </summary>
    internal sealed class CourierStreamUnixHost : ICourierStreamHost
    {
        private const int HANDSHAKE_TIMEOUT_MS = 5000;
        private const int WAIT_SLICE_MS = 100;
        private const int STOP_JOIN_MS = 1000;
        private const int MAX_SOCKET_PATH_BYTES = 100;
        private const string SHORT_SOCKET_FILE = "stream.sock";

        private readonly CourierStreamRing mRing;
        private readonly string mSocketPath;
        private readonly string mSocketDirectory;
        private readonly string mSocketFile;
        private readonly bool mOwnsSocketDirectory;
        private readonly int mMaxRecordBytes;
        private readonly Socket mListener;
        private readonly Thread mServerThread;
        private readonly ManualResetEvent mStop = new ManualResetEvent(false);
        private readonly byte[] mResponse = new byte[CourierStreamProtocol.RESPONSE_BYTES];
        private readonly byte[] mFrameHeader = new byte[CourierStreamProtocol.FRAME_HEADER_BYTES];
        private readonly byte[] mSendBuffer;
        private bool mDisposed;

        /// <summary>创建监听 socket 并启动发送线程。</summary>
        internal CourierStreamUnixHost(string streamDirectory, string socketFile, int capacity, int maxRecordBytes)
        {
            if (string.IsNullOrEmpty(streamDirectory)) throw new ArgumentException("stream directory is required", nameof(streamDirectory));
            if (string.IsNullOrEmpty(socketFile)) throw new ArgumentException("socket file is required", nameof(socketFile));

            mMaxRecordBytes = maxRecordBytes;
            mRing = new CourierStreamRing(capacity, maxRecordBytes);
            mSendBuffer = new byte[maxRecordBytes];
            string requestedPath = Path.Combine(streamDirectory, socketFile);
            if (FitsSocketPath(requestedPath))
            {
                mSocketDirectory = streamDirectory;
                mSocketFile = socketFile;
                mOwnsSocketDirectory = false;
            }
            else
            {
                ResolveShortLocation(socketFile, out mSocketDirectory, out mSocketFile);
                mOwnsSocketDirectory = true;
            }

            mSocketPath = Path.Combine(mSocketDirectory, mSocketFile);
            CourierIO.EnsureDirectory(mSocketDirectory);

            // socket 文件可能来自上一个崩溃的会话；本次 socket 名含 sessionId，不会误删活跃会话。
            CourierIO.TryDelete(mSocketPath);

            mListener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                mListener.Bind(new UnixDomainSocketEndPoint(mSocketPath));
                mListener.Listen(1);
            }
            catch
            {
                mListener.Dispose();
                mRing.Dispose();
                throw;
            }

            mServerThread = new Thread(ServerLoop);
            mServerThread.IsBackground = true;
            mServerThread.Name = "courier-stream-" + Path.GetFileNameWithoutExtension(socketFile);
            mServerThread.Start();
        }

        /// <summary>当前已写入的最后一条记录序号；没有记录时为 0。</summary>
        public long WrittenSequence
        {
            get { return mRing.NextSequence - 1; }
        }

        /// <summary>因环满被丢弃的记录数。</summary>
        public long DroppedCount
        {
            get { return mRing.DroppedCount; }
        }

        /// <summary>socket 绝对路径；仅供诊断。</summary>
        internal string SocketPath
        {
            get { return mSocketPath; }
        }

        /// <summary>实际使用的 socket 目录；路径过长时会是私有短目录。</summary>
        internal string SocketDirectory
        {
            get { return mSocketDirectory; }
        }

        /// <summary>实际使用的 socket 文件名。</summary>
        internal string SocketFile
        {
            get { return mSocketFile; }
        }

        /// <summary>追加一条记录；超过单条上限时返回 <c>false</c>。</summary>
        public bool TryWrite(ReadOnlySpan<byte> payload)
        {
            return mRing.TryAppend(payload, out _);
        }

        /// <summary>接受 reader 连接并持续发送，直到宿主释放或 reader 断开。</summary>
        private void ServerLoop()
        {
            while (!mStop.WaitOne(0))
            {
                Socket client;
                try
                {
                    client = mListener.Accept();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }

                try
                {
                    ServeClient(client);
                }
                catch (Exception caught) when (IsDisconnect(caught))
                {
                    // reader 断开是正常生命周期；换下一个连接即可。
                }
                finally
                {
                    client.Dispose();
                }
            }
        }

        /// <summary>处理一个 reader：握手、追赶历史记录、然后持续发送新记录。</summary>
        private void ServeClient(Socket client)
        {
            client.ReceiveTimeout = HANDSHAKE_TIMEOUT_MS;
            client.SendTimeout = HANDSHAKE_TIMEOUT_MS;
            using (var stream = new NetworkStream(client, false))
            {
                var request = new byte[CourierStreamProtocol.REQUEST_BYTES];
                if (!ReadExact(stream, request, 0, request.Length)) return;

                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(0, 4));
                int version = BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(4, 4));
                long sinceSequence = BinaryPrimitives.ReadInt64LittleEndian(request.AsSpan(8, 8));
                if (magic != CourierStreamProtocol.MAGIC || version != CourierStreamProtocol.VERSION) return;

                mRing.GetBounds(out long oldestSequence, out long nextSequence, out long droppedCount);
                long sendSequence = sinceSequence <= 0 ? oldestSequence : sinceSequence;
                if (sendSequence < oldestSequence) sendSequence = oldestSequence;

                WriteResponse(oldestSequence, nextSequence, droppedCount);
                if (!WriteExact(stream, mResponse, 0, mResponse.Length)) return;

                while (!mStop.WaitOne(0))
                {
                    mRing.GetBounds(out long currentOldest, out long currentNext, out _);
                    if (sendSequence < currentOldest) sendSequence = currentOldest;

                    bool sentAny = false;
                    while (sendSequence < currentNext)
                    {
                        if (!mRing.TryCopy(sendSequence, mSendBuffer, out int length, out long timestampUtcTicks))
                        {
                            sendSequence++;
                            continue;
                        }

                        if (!WriteFrame(stream, sendSequence, timestampUtcTicks, mSendBuffer, length)) return;
                        sendSequence++;
                        sentAny = true;
                    }

                    if (sentAny) continue;
                    mRing.WaitForData(WAIT_SLICE_MS);
                }
            }
        }

        /// <summary>写握手响应。</summary>
        private void WriteResponse(long oldestSequence, long nextSequence, long droppedCount)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(mResponse.AsSpan(0, 4), CourierStreamProtocol.MAGIC);
            BinaryPrimitives.WriteInt32LittleEndian(mResponse.AsSpan(4, 4), CourierStreamProtocol.VERSION);
            BinaryPrimitives.WriteInt64LittleEndian(mResponse.AsSpan(8, 8), oldestSequence);
            BinaryPrimitives.WriteInt64LittleEndian(mResponse.AsSpan(16, 8), nextSequence);
            BinaryPrimitives.WriteInt64LittleEndian(mResponse.AsSpan(24, 8), droppedCount);
        }

        /// <summary>写一条记录帧。</summary>
        private bool WriteFrame(NetworkStream stream, long sequence, long timestampUtcTicks, byte[] payload, int length)
        {
            if (length < 0 || length > mMaxRecordBytes) return false;
            BinaryPrimitives.WriteUInt32LittleEndian(mFrameHeader.AsSpan(0, 4), CourierStreamProtocol.MAGIC);
            BinaryPrimitives.WriteUInt16LittleEndian(mFrameHeader.AsSpan(4, 2), (ushort)CourierStreamProtocol.VERSION);
            BinaryPrimitives.WriteUInt16LittleEndian(mFrameHeader.AsSpan(6, 2), 0);
            BinaryPrimitives.WriteInt64LittleEndian(mFrameHeader.AsSpan(8, 8), sequence);
            BinaryPrimitives.WriteInt64LittleEndian(mFrameHeader.AsSpan(16, 8), timestampUtcTicks);
            BinaryPrimitives.WriteInt32LittleEndian(mFrameHeader.AsSpan(24, 4), length);
            BinaryPrimitives.WriteUInt32LittleEndian(mFrameHeader.AsSpan(28, 4), Crc32.Compute(payload, 0, length));
            return WriteExact(stream, mFrameHeader, 0, mFrameHeader.Length)
                && WriteExact(stream, payload, 0, length);
        }

        /// <summary>从网络流读满指定字节数；超时或 EOF 返回 <c>false</c>。</summary>
        private static bool ReadExact(NetworkStream stream, byte[] buffer, int offset, int count)
        {
            int received = 0;
            while (received < count)
            {
                int read;
                try
                {
                    read = stream.Read(buffer, offset + received, count - received);
                }
                catch (IOException caught) when (IsTimeout(caught) || caught.InnerException is SocketException)
                {
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }

                if (read <= 0) return false;
                received += read;
            }

            return true;
        }

        /// <summary>把一段字节完整写入网络流。</summary>
        private static bool WriteExact(NetworkStream stream, byte[] buffer, int offset, int count)
        {
            try
            {
                stream.Write(buffer, offset, count);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        /// <summary>判断异常是否由 reader 断开或超时导致。</summary>
        private static bool IsDisconnect(Exception caught)
        {
            return caught is IOException
                || caught is SocketException
                || caught is ObjectDisposedException
                || caught is InvalidOperationException;
        }

        /// <summary>判断 IO 异常是否由读取超时导致。</summary>
        private static bool IsTimeout(IOException caught)
        {
            return caught.InnerException is SocketException socket
                && socket.SocketErrorCode == SocketError.TimedOut;
        }

        /// <summary>停止发送线程、关闭监听 socket 并清理 socket 文件。</summary>
        public void Dispose()
        {
            if (mDisposed) return;
            mDisposed = true;
            mStop.Set();
            mRing.Dispose();

            try
            {
                mListener.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                if (!ReferenceEquals(Thread.CurrentThread, mServerThread))
                {
                    mServerThread.Join(STOP_JOIN_MS);
                }
            }
            catch (ThreadStateException)
            {
            }

            CourierIO.TryDelete(mSocketPath);
            if (mOwnsSocketDirectory)
            {
                try { Directory.Delete(mSocketDirectory, false); }
                catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException || caught is NotSupportedException) { }
            }
            mStop.Dispose();
        }

        /// <summary>判断路径是否满足 Unix Domain Socket 的保守长度上限。</summary>
        /// <param name="path">候选绝对路径。</param>
        /// <returns>UTF-8 字节数不超过上限时为 <c>true</c>。</returns>
        private static bool FitsSocketPath(string path)
        {
            return Encoding.UTF8.GetByteCount(path) <= MAX_SOCKET_PATH_BYTES;
        }

        /// <summary>
        /// 项目路径过深时，把 socket 放到用户临时目录下的会话私有短目录。描述文件记录实际目录，
        /// 读取端因此不需要知道项目路径长度。
        /// </summary>
        /// <param name="requestedSocketFile">原 socket 文件名，用于派生短目录名。</param>
        /// <param name="directory">实际目录。</param>
        /// <param name="file">实际文件名。</param>
        private static void ResolveShortLocation(string requestedSocketFile, out string directory, out string file)
        {
            string seed = Path.GetFileNameWithoutExtension(requestedSocketFile);
            if (string.IsNullOrEmpty(seed)) seed = "stream";
            string directoryName = "courier-stream-" + seed;
            file = SHORT_SOCKET_FILE;

            string[] roots = Path.DirectorySeparatorChar == '/'
                ? new[] { Path.GetTempPath(), "/tmp", "/var/tmp" }
                : new[] { Path.GetTempPath() };
            for (int index = 0; index < roots.Length; index++)
            {
                string root = roots[index];
                if (string.IsNullOrEmpty(root)) continue;
                string candidate = Path.Combine(root, directoryName);
                if (!FitsSocketPath(Path.Combine(candidate, file))) continue;
                directory = candidate;
                return;
            }

            throw new IOException("courier stream socket path is too long and no short temporary directory is available");
        }
    }
}
