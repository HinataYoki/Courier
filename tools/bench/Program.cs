
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;

internal static class Program
{
    private static void Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "file";
        int n = args.Length > 1 ? int.Parse(args[1]) : DefaultCount(mode);
        bool child = args.Length > 2 && args[2] == "child";
        string root = Path.Combine(Path.GetTempPath(), "courier-rtt");
        Directory.CreateDirectory(root);

        switch (mode)
        {
            case "file": RunFile(n, child, root, mode); break;
            case "mmap": RunRing(n, child, root, mode); break;
            case "pipe": RunPipe(n, child, args); break;
            case "hybrid": RunHybrid(n, child, root, mode, args); break;
            default: Console.WriteLine("mode: file|mmap|pipe"); break;
        }
    }

    // 每种载体的默认往返次数：文件模式慢，少跑几次；映射与管道快，多跑几次让平均值稳定
    private static int DefaultCount(string mode)
    {
        return mode switch
        {
            "file" => 1000,
            "mmap" => 20000,
            "pipe" => 20000,
            "hybrid" => 20000,
            _ => 1000,
        };
    }

    // 纯自旋等待，不能用 SpinWait（长时间自旋后会退化成 Sleep(1)，把 RTT 污染成毫秒级）
    private static void BusyWait(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("busy wait timeout");
            Thread.SpinWait(20);
        }
    }

    private static void RunFile(int n, bool child, string root, string mode)
    {
        string a2b = Path.Combine(root, "a2b"), b2a = Path.Combine(root, "b2a");
        if (child)
        {
            // 就绪信号：让父进程排除子进程启动与 JIT 时间，只测稳态往返
            File.WriteAllText(Path.Combine(b2a, "ready"), "1");
            for (int i = 0; i < n; i++)
            {
                string want = Path.Combine(a2b, i.ToString("D8") + ".json");
                BusyWait(() => File.Exists(want), 20000);
                string body = ReadTextRetry(want);
                try { File.Delete(want); } catch (IOException) { }
                string tmp = Path.Combine(b2a, i.ToString("D8") + ".tmp");
                File.WriteAllText(tmp, body);
                File.Move(tmp, Path.Combine(b2a, i.ToString("D8") + ".json"), true);
            }
            return;
        }
        if (Directory.Exists(a2b)) Directory.Delete(a2b, true);
        if (Directory.Exists(b2a)) Directory.Delete(b2a, true);
        Directory.CreateDirectory(a2b); Directory.CreateDirectory(b2a);
        var proc = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, mode + " " + n + " child") { UseShellExecute = false })!;
        string body200 = new string('y', 200);
        BusyWait(() => File.Exists(Path.Combine(b2a, "ready")), 20000);
        // 杀软/索引器可能短暂持有句柄，删不掉不算失败
        try { File.Delete(Path.Combine(b2a, "ready")); } catch (IOException) { }
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++)
        {
            string tmp = Path.Combine(a2b, i.ToString("D8") + ".tmp");
            File.WriteAllText(tmp, body200);
            File.Move(tmp, Path.Combine(a2b, i.ToString("D8") + ".json"), true);
            string back = Path.Combine(b2a, i.ToString("D8") + ".json");
            BusyWait(() => File.Exists(back), 20000);
            File.Delete(back);
        }
        sw.Stop();
        proc.WaitForExit();
        Report("文件: 临时文件+rename+轮询+delete", n, sw);
    }

    // 环形缓冲：offset0 = A 写游标，offset16 = B 写游标；游标必须 volatile 读写，
    // 否则 JIT 会把循环内的映射读取提到循环外，测出"8 百万次/秒"的假数据。
    private static void RunRing(int n, bool child, string root, string mode)
    {
        const int HDR = 64;
        const int CAP = 64 * 1024;
        long fileLen = HDR + CAP * 2;
        string path = Path.Combine(root, "ring.bin");

        if (child)
        {
            MemoryMappedFile? mmf = null;
            BusyWait(() => { try { mmf = OpenRing(path, fileLen); return true; } catch (IOException) { return false; } }, 20000);
            using var _ = mmf!;
            using var acc = mmf!.CreateViewAccessor(0, fileLen, MemoryMappedFileAccess.ReadWrite);
            unsafe
            {
                byte* p = null;
                acc.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                long* hdr = (long*)(p + acc.PointerOffset);
                long consumed = 0, produced = 0;
                for (int i = 0; i < n; i++)
                {
                    BusyWait(() => Volatile.Read(ref hdr[0]) != consumed, 20000);
                    consumed = Volatile.Read(ref hdr[0]);
                    produced = produced + 1;
                    Volatile.Write(ref hdr[2], produced);
                }
                acc.SafeMemoryMappedViewHandle.ReleasePointer();
            }
            return;
        }

        File.Delete(path);
        using (var f = new FileStream(path, FileMode.Create, FileAccess.ReadWrite)) f.SetLength(fileLen);
        var proc = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, mode + " " + n + " child") { UseShellExecute = false })!;
        MemoryMappedFile? mmf2 = null;
        BusyWait(() => { try { mmf2 = OpenRing(path, fileLen); return true; } catch (IOException) { return false; } }, 20000);
        using var __ = mmf2!;
        using var acc2 = mmf2!.CreateViewAccessor(0, fileLen, MemoryMappedFileAccess.ReadWrite);
        unsafe
        {
            byte* p = null;
            acc2.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            long* hdr = (long*)(p + acc2.PointerOffset);
            long produced2 = 0, consumed2 = 0;
            // 预热一次，排除子进程映射建立
            Volatile.Write(ref hdr[0], 1);
            BusyWait(() => Volatile.Read(ref hdr[2]) == 1, 20000);
            produced2 = 1; consumed2 = Volatile.Read(ref hdr[2]);
            var sw = Stopwatch.StartNew();
            for (int i = 1; i < n; i++)
            {
                produced2 = produced2 + 1;
                Volatile.Write(ref hdr[0], produced2);
                BusyWait(() => Volatile.Read(ref hdr[2]) == produced2, 20000);
                consumed2 = Volatile.Read(ref hdr[2]);
            }
            sw.Stop();
            acc2.SafeMemoryMappedViewHandle.ReleasePointer();
            proc.WaitForExit();
            Report("文件映射: 环形缓冲 + 双游标自旋", n - 1, sw);
        }
    }

    private static void RunPipe(int n, bool child, string[] args)
    {
        // 管道名必须由父进程算好传给子进程：子进程的 ProcessId 与父进程不同
        string name = args.Length > 3 ? args[3] : "courier-bench-" + Environment.ProcessId;
        if (child)
        {
            using var c = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None);
            c.Connect(20000);
            int size0 = args.Length > 4 ? int.Parse(args[4]) : 200;
            var buf = new byte[size0];
            for (int i = 0; i < n; i++)
            {
                ReadExact(c, buf, size0);
                c.Write(buf, 0, size0);
                c.Flush();
            }
            return;
        }
        int size = args.Length > 4 ? int.Parse(args[4]) : 200;
        using var s = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);
        var proc = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "pipe " + n + " child " + name + " " + size) { UseShellExecute = false })!;
        s.WaitForConnection();
        var buf2 = new byte[size];
        Array.Fill(buf2, (byte)'y');
        s.Write(buf2, 0, size); s.Flush(); ReadExact(s, buf2, size);
        var sw = Stopwatch.StartNew();
        for (int i = 1; i < n; i++)
        {
            s.Write(buf2, 0, size);
            s.Flush();
            ReadExact(s, buf2, size);
        }
        sw.Stop();
        proc.WaitForExit();
        Report($"命名管道({size}B)", n - 1, sw);
    }

    // 两端必须都用 FileShare.ReadWrite 打开同一个文件，否则第二个进程会撞共享冲突；
    // 共享内存语义靠同一物理文件的映射，不靠命名 map（命名 map 在 Unix 上不可用）。
    private static MemoryMappedFile OpenRing(string path, long fileLen)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        return MemoryMappedFile.CreateFromFile(fs, null, fileLen, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
    }

    // Windows 上忙轮询的文件协议必须容忍共享冲突：杀软/索引器会在 rename 后短暂打开文件，
    // 裸 File.ReadAllText 会抛 IOException "used by another process"。这是文件式协议的真实代价。
    private static string ReadTextRetry(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (attempt < 200) { Thread.SpinWait(200); }
        }
    }

    // 混合模式：数据走文件映射环形缓冲（零拷贝、可覆盖写最新快照），
    // 唤醒走命名管道（阻塞等待，不烧 CPU）。这是推荐形态。
    private static void RunHybrid(int n, bool child, string root, string mode, string[] args)
    {
        const int HDR = 64;
        const int CAP = 64 * 1024;
        long fileLen = HDR + CAP * 2;
        string path = Path.Combine(root, "hybrid.bin");
        string pipeName = "courier-hybrid-" + Environment.ProcessId;
        int payload = args.Length > 3 ? int.Parse(args[3]) : 200;

        if (child)
        {
            string name = Environment.GetEnvironmentVariable("COURIER_PIPE") ?? pipeName;
            using var c = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None);
            c.Connect(20000);
            MemoryMappedFile? mmf = null;
            BusyWait(() => { try { mmf = OpenRing(path, fileLen); return true; } catch (IOException) { return false; } }, 20000);
            using var _ = mmf!;
            using var acc = mmf!.CreateViewAccessor(0, fileLen, MemoryMappedFileAccess.ReadWrite);
            unsafe
            {
                byte* p = null;
                acc.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                long* hdr = (long*)(p + acc.PointerOffset);
                var door = new byte[1];
                for (int i = 0; i < n; i++)
                {
                    ReadExact(c, door, 1);          // 阻塞等门铃，零 CPU
                    for (int b = 0; b < payload; b += 8) Volatile.Read(ref hdr[4 + (b >> 3)]);   // 模拟读载荷
                    long seq = Volatile.Read(ref hdr[0]);   // 读最新序号，顺带确认
                    Volatile.Write(ref hdr[2], seq);        // 回写 ack 游标
                    c.Write(door, 0, 1);            // 敲回门铃
                    c.Flush();
                }
                acc.SafeMemoryMappedViewHandle.ReleasePointer();
            }
            return;
        }

        File.Delete(path);
        using (var f = new FileStream(path, FileMode.Create, FileAccess.ReadWrite)) f.SetLength(fileLen);
        using var s = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);
        var psi = new ProcessStartInfo(Environment.ProcessPath!, "hybrid " + n + " child " + payload) { UseShellExecute = false };
        psi.Environment["COURIER_PIPE"] = pipeName;
        var proc = Process.Start(psi)!;
        s.WaitForConnection();
        MemoryMappedFile? mmf2 = null;
        BusyWait(() => { try { mmf2 = OpenRing(path, fileLen); return true; } catch (IOException) { return false; } }, 20000);
        using var __ = mmf2!;
        using var acc2 = mmf2!.CreateViewAccessor(0, fileLen, MemoryMappedFileAccess.ReadWrite);
        unsafe
        {
            byte* p = null;
            acc2.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            long* hdr = (long*)(p + acc2.PointerOffset);
            var door = new byte[1];
            // 预热
            Volatile.Write(ref hdr[0], 1);
            s.Write(door, 0, 1); s.Flush();
            ReadExact(s, door, 1);
            var sw = Stopwatch.StartNew();
            for (int i = 1; i < n; i++)
            {
                Volatile.Write(ref hdr[0], i + 1);   // 数据直接落映射，不经过管道
                s.Write(door, 0, 1);                 // 只敲 1 字节门铃
                s.Flush();
                ReadExact(s, door, 1);
            }
            sw.Stop();
            acc2.SafeMemoryMappedViewHandle.ReleasePointer();
            proc.WaitForExit();
            Report($"混合: 映射载荷({payload}B) + 管道门铃", n - 1, sw);
        }
    }

    private static void ReadExact(Stream s, byte[] buf, int count)
    {
        int got = 0;
        while (got < count)
        {
            int r = s.Read(buf, got, count - got);
            if (r <= 0) throw new IOException("eof");
            got += r;
        }
    }

    private static void Report(string label, int n, Stopwatch sw)
    {
        Console.WriteLine($"{label,-44} {n,6} 次往返 / {sw.Elapsed.TotalMilliseconds,8:F0} ms = {sw.Elapsed.TotalMilliseconds * 1000 / n,8:F1} us/往返 = {n * 1000.0 / sw.Elapsed.TotalMilliseconds,9:F0} 往返/s");
    }
}
