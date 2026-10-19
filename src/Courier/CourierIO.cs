using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Threading;

namespace Courier
{
    /// <summary>
    /// 文件系统原语。原子写与有界重试读是 SPEC §3.3 / §3.4 的强制要求，任何写入都不得绕过这里。
    /// </summary>
    internal static class CourierIO
    {
        /// <summary>SPEC §3.4：读取失败的重试上限，200 次 × 5 ms ≈ 1 s。</summary>
        private const int ReadRetryCount = 200;

        /// <summary>SPEC §3.4：每次重试之间的等待。</summary>
        private const int ReadRetryDelayMilliseconds = 5;

        /// <summary>
        /// 原子写（SPEC §3.3）：同目录临时文件 → 写入 → flush → close → rename。
        /// 临时文件必须与目标同目录同卷，否则 rename 退化为拷贝+删除，不再原子。
        /// </summary>
        /// <param name="path">目标路径，必须带目录。</param>
        /// <param name="content">完整文件内容。</param>
        /// <param name="overwrite">false 时目标已存在即失败，即 SPEC §3.3 的不覆盖创建。</param>
        /// <param name="durable">
        /// true 时把数据刷到物理磁盘。心跳这类每 Tick 重写、丢了下轮就补上的文件传 false：
        /// SPEC §3.3 明确允许省略，而 Windows 上每次 fsync 是毫秒级开销。
        /// </param>
        internal static void WriteAtomic(string path, string content, bool overwrite, bool durable = true)
        {
            string? parent = Path.GetDirectoryName(path);
            if (parent == null || parent.Length == 0) throw new ArgumentException("path has no directory", nameof(path));
            string tempPath = Path.Combine(parent, "." + Path.GetFileName(path) + "." + CourierProtocol.RandomHex(8) + ".tmp");
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(content);
                    writer.Flush();
                    // durable 才刷到物理磁盘：否则掉电后 rename 已生效、内容还在 OS 缓存里，磁盘上是个空文件。
                    if (durable) stream.Flush(true);
                }

                // overwrite=false 时 rename 在目标已存在时失败，这正是 SPEC §3.3 要求的
                // “不覆盖”创建：命令与 claim 靠它保证 requestId 不被静默覆盖。
                if (!overwrite)
                {
                    // 两参 Move：目标已存在则抛 IOException。不用三参重载，它不在 netstandard2.1 里。
                    File.Move(tempPath, path);
                    return;
                }

                MoveOverwrite(tempPath, path);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }

        /// <summary>
        /// 把 <paramref name="source"/> 移动到 <paramref name="destination"/>，目标已存在时覆盖。
        /// 覆盖写与归档共用（SPEC §3.3 第 4 步、§12.1）。
        /// </summary>
        /// <remarks>
        /// 优先 File.Replace：Windows 上 File.Move(..., overwrite: true) 在目标正被读者打开时抛
        /// UnauthorizedAccessException——哪怕读者已授予 FileShare.Delete；实测同一个读者下
        /// File.Replace（ReplaceFile 语义）可以成功，这是并发读写下唯一稳定的覆盖路径。
        /// 不用 File.Move 的三参重载，也不用 OperatingSystem.IsWindows()：两者都不在 netstandard2.1 里，
        /// 而本实现必须能作为源码直接拖进 .NET Standard 2.1 宿主。
        /// </remarks>
        internal static void MoveOverwrite(string source, string destination)
        {
            // 先 Replace 而不是先判存：目标不存在时 Replace 抛 FileNotFoundException，代价只是一次异常；
            // 反过来先判存再 Move 会在“判存之后被别人创建”的竞态里丢掉覆盖语义。
            try
            {
                File.Replace(source, destination, null);
            }
            catch (FileNotFoundException)
            {
                // 目标不存在，或它在判存与替换之间被删掉（例如服务者刚归档完命令）。退回普通 rename。
                File.Move(source, destination);
            }
            catch (Exception caught) when (caught is IOException || caught is NotSupportedException)
            {
                // 没有 ReplaceFile 语义时的退路（Mono、部分网络盘）：目标先挪到备份名，新内容到位后才删备份。
                // 这一步不是原子的，只在 File.Replace 不可用时走到；崩溃窗口里可能留下 .bak，但不会留下半写的目标。
                string backupPath = destination + "." + CourierProtocol.RandomHex(8) + ".bak";
                File.Move(destination, backupPath);
                try
                {
                    File.Move(source, destination);
                }
                catch
                {
                    try
                    {
                        File.Move(backupPath, destination);
                    }
                    catch (IOException)
                    {
                        // 恢复失败时保留 .bak，内容没丢。
                    }
                    throw;
                }
                TryDelete(backupPath);
            }
        }

        /// <summary>
        /// 有界重试读取（SPEC §3.4）。Windows 上杀软与索引器会在 rename 后短暂持有句柄，
        /// 刚写完的文件瞬间读不到是常态，不是异常。
        /// </summary>
        internal static bool TryReadAllText(string path, [NotNullWhen(true)] out string? text)
        {
            text = null;
            for (int attempt = 0; attempt < ReadRetryCount; attempt++)
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        text = reader.ReadToEnd();
                        return true;
                    }
                }
                catch (Exception caught) when (caught is FileNotFoundException || caught is DirectoryNotFoundException)
                {
                    // 文件不在（还没写完或已被归档）：重试没有意义，立刻放弃。
                    return false;
                }
                catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException)
                {
                    // 被占用：留到下一轮重试，最后一轮仍失败才算读不到。
                    if (attempt == ReadRetryCount - 1) return false;
                }

                Thread.Sleep(ReadRetryDelayMilliseconds);
            }

            return false;
        }

        /// <summary>删除文件，文件不存在或正被占用时静默忽略。</summary>
        internal static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException)
            {
                // 文件不存在或正被占用：删除只是清理，失败留给下一轮。
            }
        }

        /// <summary>创建目录并容忍已存在（SPEC §2：实现 SHOULD 在启动时创建缺失目录）。</summary>
        internal static void EnsureDirectory(string path)
        {
            Directory.CreateDirectory(path);
        }

        /// <summary>统计目录下 .json 文件数，用于背压判定（SPEC §7.3）。</summary>
        internal static int CountJsonFiles(string directory)
        {
            if (!Directory.Exists(directory)) return 0;
            int count = 0;
            // 用 EnumerateFiles 而不是 GetFiles().Length：背压判定在每次投递上跑，
            // commands/ 满时是 4096 个路径，不该为了一次计数分配整个字符串数组。
            foreach (string unused in Directory.EnumerateFiles(directory, "*" + CourierProtocol.JsonExtension))
            {
                count++;
            }

            return count;
        }
    }
}
