using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Courier
{
    /// <summary>
    /// 热通道的终态证据账本（见 docs/HOT-CHANNEL.md 的 T1）。
    /// <para>
    /// 热通道把消息走在共享内存里，但"每一条调用都必须留下可离线读取的终态证据"这条不放弃——
    /// 只是把它从每次调用一次 fsync 改成**追加写 + 组提交**：条目先落在 64 KiB 缓冲里，
    /// 攒够 64 条或超过 250 ms 才 write 一次。写出去之后**不做 fsync**：账本是尽力而为的旁证，
    /// 断电丢掉最近若干条不影响任何协议语义，<c>results/</c> 才是强证据。
    /// </para>
    /// <para>
    /// 账本是一行一个 JSON 对象，只追加不重写。它记录的是**结论**（谁、调了什么、成没成、多大、
    /// 多久），不记录正文原文——正文在热通道里只存在于共享内存帧中，帧被读走后就没地方再找它了。
    /// 需要正文可回溯的调用方应改用文件通道（<see cref="CourierNode.CallAsync"/>），
    /// 那是逐请求一个 <c>results/&lt;requestId&gt;.json</c> 的强证据。
    /// </para>
    /// <para>
    /// 磁盘占用**有界**：单个账本涨到 <see cref="DEFAULT_MAX_BYTES"/> 就换代（<c>hot-&lt;sid&gt;-1.log</c>…），
    /// 换代时把上一代删掉，因此一个会话任何时刻最多留一份、不超过上限多一点。
    /// 打开时还会顺手删掉本目录里不属于本次运行、且超过 <see cref="DEFAULT_STALE_AGE"/> 没动过的账本，
    /// 否则每重启一次就多一个永远不会有人再写的文件。
    /// </para>
    /// </summary>
    internal sealed class CourierHotJournal : IDisposable
    {
        /// <summary>攒够这么多条就写一次。</summary>
        private const int FLUSH_ENTRIES = 64;

        /// <summary>
        /// 距离上次写出去超过这么多毫秒也写一次，给低频调用方一个上界。
        /// 它只决定"证据什么时候从内存落到页缓存"，不承担持久性责任，所以不必是毫秒级。
        /// </summary>
        private const int FLUSH_INTERVAL_MS = 250;

        /// <summary>单份账本的上限；到顶就换代，磁盘占用因此有界。</summary>
        internal const long DEFAULT_MAX_BYTES = 4L * 1024 * 1024;

        /// <summary>不属于本次运行、且超过这么久没动过的账本，在打开时删掉。</summary>
        internal static readonly TimeSpan DEFAULT_STALE_AGE = TimeSpan.FromHours(24);

        private const string FILE_STEM = "hot-";

        private readonly object mGate = new object();
        private readonly string mDirectory;
        private readonly string mSessionId;
        private readonly long mMaxBytes;
        private readonly byte[] mBuffer = new byte[64 * 1024];
        private readonly Thread? mFlusher;

        /// <summary>当前这一代的文件；轮转失败后为 null，此时账本降级成"什么都不写"。</summary>
        private FileStream? mStream;

        /// <summary>已经写出去（离开缓冲）的字节数，用来判断何时换代。不每次去问文件长度，那是一次系统调用。</summary>
        private long mFlushedBytes;

        private int mGeneration;
        private int mUsed;
        private int mPending;
        private long mLastFlushTicks;
        private volatile bool mStop;
        private bool mDisposed;

        /// <summary>由 <c>Open</c> 在拿到独占的文件流之后调用；<paramref name="startFlusher"/> 为 false 时只写不刷（测试用）。</summary>
        private CourierHotJournal(string directory, string sessionId, long maxBytes, FileStream stream, bool startFlusher)
        {
            mDirectory = directory;
            mSessionId = sessionId;
            mMaxBytes = maxBytes;
            mStream = stream;
            mLastFlushTicks = Environment.TickCount;
            if (startFlusher)
            {
                mFlusher = new Thread(FlusherLoop);
                mFlusher.IsBackground = true;
                mFlusher.Name = "courier-hot-journal";
                mFlusher.Start();
            }
        }

        /// <summary>打开（必要时创建）账本文件；失败返回 <c>null</c>，调用方据此关闭热通道而不是丢掉证据。</summary>
        /// <param name="directory">账本目录（绝对路径）。</param>
        /// <param name="sessionId">本次运行的会话标识，用作文件名的一部分。</param>
        /// <param name="detail">失败原因。</param>
        /// <returns>账本，或 <c>null</c>。</returns>
        internal static CourierHotJournal? Open(string directory, string sessionId, out string? detail)
        {
            return Open(directory, sessionId, DEFAULT_MAX_BYTES, DEFAULT_STALE_AGE, out detail);
        }

        /// <summary>带上限与陈旧判定的重载；测试用它把轮转与清理压到很小的规模。</summary>
        /// <param name="directory">账本目录（绝对路径）。</param>
        /// <param name="sessionId">本次运行的会话标识，用作文件名的一部分。</param>
        /// <param name="maxBytes">单份账本的上限；非正数表示用默认值。</param>
        /// <param name="staleAge">旧账本的陈旧阈值；非正数表示不在打开时清理。</param>
        /// <param name="detail">失败原因。</param>
        /// <returns>账本，或 <c>null</c>。</returns>
        internal static CourierHotJournal? Open(string directory, string sessionId, long maxBytes, TimeSpan staleAge, out string? detail)
        {
            detail = null;
            if (maxBytes <= 0) maxBytes = DEFAULT_MAX_BYTES;
            try
            {
                Directory.CreateDirectory(directory);
                PruneStale(directory, sessionId, staleAge);
                string path = GenerationPath(directory, sessionId, 0);
                // FileShare.ReadWrite | Delete：允许 tail 型观察工具同时读，也让删除不会因为本进程持有句柄而失败。
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
                var journal = new CourierHotJournal(directory, sessionId, maxBytes, stream, true);
                // 同一个会话标识的文件可能本来就有内容（旧实现留下的），换代判断必须从真实长度起步。
                journal.mFlushedBytes = stream.Length;
                return journal;
            }
            catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException || caught is NotSupportedException)
            {
                detail = caught.Message;
                return null;
            }
        }

        /// <summary>追加一条证据（调用方传入的是不含换行的单行 JSON）。</summary>
        /// <param name="line">单行 JSON。</param>
        internal void Append(string line)
        {
            if (line == null || line.Length == 0) return;
            if (mDisposed) return;

            lock (mGate)
            {
                if (mDisposed) return;
                if (mFlushedBytes >= mMaxBytes) RotateLocked();

                int needed = Encoding.UTF8.GetByteCount(line) + 1;
                if (needed > mBuffer.Length)
                {
                    // 超长条目绕过缓冲区直接写：宁可慢一次，也不要截断证据。
                    FlushLocked();
                    FileStream? stream = mStream;
                    if (stream == null) return;
                    try
                    {
                        byte[] payload = Encoding.UTF8.GetBytes(line);
                        stream.Write(payload, 0, payload.Length);
                        stream.WriteByte((byte)'\n');
                        stream.Flush();
                        mFlushedBytes += payload.Length + 1;
                    }
                    catch (Exception caught) when (caught is IOException || caught is ObjectDisposedException)
                    {
                        DropStreamLocked(stream);
                    }
                    return;
                }

                if (mUsed + needed > mBuffer.Length) FlushLocked();
                if (mStream == null) return;
                mUsed += Encoding.UTF8.GetBytes(line, 0, line.Length, mBuffer, mUsed);
                mBuffer[mUsed] = (byte)'\n';
                mUsed++;
                mPending++;
                if (mPending >= FLUSH_ENTRIES) FlushLocked();
            }
        }

        /// <summary>某一代账本文件的路径：<c>hot-&lt;sessionId&gt;.&lt;generation&gt;.log</c>。</summary>
        private static string GenerationPath(string directory, string sessionId, int generation)
        {
            // 第 0 代保持旧名字 hot-<sid>.log，其后是 hot-<sid>-<n>.log：观察工具按前缀就能找到同一代宿主的全部账本。
            string name = generation == 0
                ? FILE_STEM + sessionId + ".log"
                : FILE_STEM + sessionId + "-" + generation.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".log";
            return Path.Combine(directory, name);
        }

        /// <summary>文件名是不是本次运行的账本（含它的旧代号）。会话标识定长，所以前缀匹配不会撞到别的会话。</summary>
        private static bool BelongsToSession(string fileName, string sessionId)
        {
            string stem = FILE_STEM + sessionId;
            if (!fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) return false;
            if (fileName.Length == stem.Length + 4) return fileName.StartsWith(stem, StringComparison.Ordinal);
            return fileName.Length > stem.Length + 4 && fileName.StartsWith(stem + "-", StringComparison.Ordinal);
        }

        /// <summary>删掉不属于本次运行、且超过阈值没动过的账本。失败一律忽略：清理不该妨碍开账本。</summary>
        private static void PruneStale(string directory, string sessionId, TimeSpan staleAge)
        {
            if (staleAge <= TimeSpan.Zero) return;
            string[] files;
            try
            {
                files = Directory.GetFiles(directory, FILE_STEM + "*.log");
            }
            catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException || caught is DirectoryNotFoundException)
            {
                return;
            }

            DateTime cutoffUtc = DateTime.UtcNow - staleAge;
            for (int index = 0; index < files.Length; index++)
            {
                if (BelongsToSession(Path.GetFileName(files[index]), sessionId)) continue;
                try
                {
                    if (File.GetLastWriteTimeUtc(files[index]) >= cutoffUtc) continue;
                    File.Delete(files[index]);
                }
                catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException)
                {
                    // 别的进程正握着它：留着，下次再说。
                }
            }
        }

        /// <summary>换一代文件并把上一代删掉，于是磁盘占用始终是"一份、不超过上限"。调用方必须持有 <see cref="mGate"/>。</summary>
        private void RotateLocked()
        {
            FlushLocked();
            FileStream? previous = mStream;
            mStream = null;
            if (previous != null)
            {
                try { previous.Dispose(); }
                catch (IOException) { }
            }

            mGeneration++;
            string path = GenerationPath(mDirectory, mSessionId, mGeneration);
            try
            {
                mStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
                mFlushedBytes = 0;
            }
            catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException || caught is NotSupportedException || caught is ArgumentException)
            {
                // 换代失败就停止记账：账本是旁证，不能让热通道因为写不出证据而停摆。
                mStream = null;
                return;
            }

            DeleteOlderGenerations(path);
        }

        /// <summary>删掉本次运行更早的代号，只留下刚落盘的那一份。失败忽略。</summary>
        private void DeleteOlderGenerations(string keepPath)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(mDirectory, FILE_STEM + "*.log");
            }
            catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException || caught is DirectoryNotFoundException)
            {
                return;
            }

            for (int index = 0; index < files.Length; index++)
            {
                if (string.Equals(files[index], keepPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (!BelongsToSession(Path.GetFileName(files[index]), mSessionId)) continue;
                try { File.Delete(files[index]); }
                catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException) { }
            }
        }

        /// <summary>刷盘线程的主循环：按 <c>FLUSH_INTERVAL_MS</c> 醒一次，把缓冲区里的行落到当前代文件。</summary>
        private void FlusherLoop()
        {
            while (!mStop)
            {
                Thread.Sleep(FLUSH_INTERVAL_MS);
                if (mStop) return;
                lock (mGate)
                {
                    if (mDisposed || mPending == 0) continue;
                    if (Environment.TickCount - mLastFlushTicks < FLUSH_INTERVAL_MS) continue;
                    FlushLocked();
                }
            }
        }

        /// <summary>把缓冲写进文件。只 Flush 不 fsync：账本不承担断电后的持久性。</summary>
        private void FlushLocked()
        {
            FileStream? stream = mStream;
            if (stream == null)
            {
                mUsed = 0;
                mPending = 0;
                return;
            }

            try
            {
                if (mUsed > 0)
                {
                    stream.Write(mBuffer, 0, mUsed);
                    mFlushedBytes += mUsed;
                    mUsed = 0;
                }

                stream.Flush();
                mPending = 0;
                mLastFlushTicks = Environment.TickCount;
            }
            catch (Exception caught) when (caught is IOException || caught is ObjectDisposedException)
            {
                // 证据丢失已经是既成事实：这里再抛只会把调用方的热通道一起打断。
                // 但流必须真的关掉：只把字段置空会让文件被本进程永久占写，轮转也拿不到那个引用。
                mUsed = 0;
                mPending = 0;
                DropStreamLocked(stream);
            }
        }

        /// <summary>
        /// 丢弃一个已经写不动的流：先把字段置空（后续 Append 立刻走"没有账本"的分支），再尝试关闭。
        /// </summary>
        /// <param name="stream">已经失败的那个流；调用方必须持有 <see cref="mGate"/>。</param>
        /// <remarks>
        /// 顺序不能反：<see cref="FlushLocked"/> 可能在持有 <see cref="mGate"/> 的任意路径上被调用，
        /// 先 Dispose 再置空会留出一个"字段指向已关闭流"的窗口，而那个窗口里 Append 会抛异常。
        /// </remarks>
        private void DropStreamLocked(FileStream? stream)
        {
            if (stream != null && ReferenceEquals(mStream, stream)) mStream = null;
            if (stream == null) return;
            mFlushedBytes = 0;
            try { stream.Dispose(); }
            catch (Exception caught) when (caught is IOException || caught is ObjectDisposedException)
            {
                // 关不掉只可能是句柄已经被别人关了：没有补救动作，也不该影响数据面。
            }
        }

        /// <summary>把缓冲写进文件并关闭；幂等。</summary>
        public void Dispose()
        {
            lock (mGate)
            {
                if (mDisposed) return;
                mDisposed = true;
                mStop = true;
                FlushLocked();
                FileStream? stream = mStream;
                mStream = null;
                if (stream != null)
                {
                    try { stream.Dispose(); }
                    catch (IOException) { }
                }
            }

            Thread? flusher = mFlusher;
            if (flusher != null && flusher.IsAlive)
            {
                try { flusher.Join(500); }
                catch (ThreadStateException) { }
            }
        }
    }
}
