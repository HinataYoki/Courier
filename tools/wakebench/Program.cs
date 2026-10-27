using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

internal static unsafe class Program
{
    private const int FileBytes = 4096;
    private const int OffMagic = 0;
    private const int OffWord = 8;
    private const int OffTs = 16;
    private const long Magic = 0x31454B4157;
    private static readonly IntPtr Base = new IntPtr(0x3000000000L);
    private static IntPtr KeepAlive = IntPtr.Zero;

    [DllImport("kernelbase.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateFileW(string n, uint a, uint s, IntPtr sa, uint d, uint f, IntPtr t);
    [DllImport("kernelbase.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateFileMappingW(IntPtr f, IntPtr sa, uint p, uint hi, uint lo, string? n);
    [DllImport("kernelbase.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
    [DllImport("kernelbase.dll", SetLastError = true)] private static extern IntPtr MapViewOfFileEx(IntPtr m, uint a, uint hi, uint lo, UIntPtr b, IntPtr bas);
    [DllImport("kernelbase.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernelbase.dll")] private static extern bool WaitOnAddress(void* a, void* c, UIntPtr s, uint ms);
    [DllImport("kernelbase.dll")] private static extern void WakeByAddressSingle(void* a);

    private static string PathFor(string tag) => Path.Combine(Path.GetTempPath(), "courier-wakebench", "w-" + tag + ".bin");

    private static byte* Map(string carrier, bool creator, string tag)
    {
        IntPtr h = IntPtr.Zero;
        if (carrier == "named")
        {
            string name = "courier-wakebench-" + tag;
            if (creator) { h = CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, 0x04, 0, FileBytes, name); KeepAlive = h; }
            else
            {
                var sw = Stopwatch.StartNew();
                int err = 0;
                while (true)
                {
                    h = OpenFileMappingW(0xF001F, false, name);
                    if (h != IntPtr.Zero) break;
                    err = Marshal.GetLastWin32Error();
                    if (sw.ElapsedMilliseconds > 8000) throw new IOException("OpenFileMapping '" + name + "' err=" + err);
                    Thread.Sleep(2);
                }
                KeepAlive = h;
            }
            if (h == IntPtr.Zero) throw new IOException("CreateFileMapping '" + name + "' err=" + Marshal.GetLastWin32Error());
        }
        else
        {
            string path = PathFor(tag);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (creator)
            {
                if (File.Exists(path)) File.Delete(path);
                using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite)) { fs.SetLength(FileBytes); fs.Flush(true); }
            }
            else
            {
                var sw = Stopwatch.StartNew();
                while (!File.Exists(path) || new FileInfo(path).Length < FileBytes)
                {
                    if (sw.ElapsedMilliseconds > 8000) throw new TimeoutException("file not ready: " + path);
                    Thread.Sleep(2);
                }
            }
            IntPtr f = CreateFileW(path, 0xC0000000, 0x7, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (f == new IntPtr(-1)) throw new IOException("CreateFileW err=" + Marshal.GetLastWin32Error());
            h = CreateFileMappingW(f, IntPtr.Zero, 0x04, 0, FileBytes, null);
            int e = Marshal.GetLastWin32Error();
            CloseHandle(f);
            if (h == IntPtr.Zero) throw new IOException("CreateFileMapping(file) err=" + e);
        }
        IntPtr want = Environment.GetEnvironmentVariable("WB_ANYBASE") == "1" ? IntPtr.Zero : Base;
        IntPtr v = MapViewOfFileEx(h, 0xF001F, 0, 0, (UIntPtr)FileBytes, want);
        int err2 = Marshal.GetLastWin32Error();
        if (carrier != "named" || !creator) CloseHandle(h);
        if (v == IntPtr.Zero) throw new IOException("MapViewOfFileEx err=" + err2);
        Console.Error.WriteLine("[" + carrier + "/" + (creator ? "parent" : "child") + "] base=0x" + v.ToString("X"));
        return (byte*)v;
    }

    private static int Main(string[] args)
    {
        string carrier = args.Length > 0 ? args[0] : "file";
        string role = args.Length > 1 ? args[1] : "parent";
        int n = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 20000;
        string tag = args.Length > 3 ? args[3] : "solo";
        if (Environment.GetEnvironmentVariable("WB_ANYBASE") == "1") Console.Error.WriteLine("[mode] any-base");

        bool isParent = role == "parent";
        byte* p = Map(carrier, isParent, tag);
        long* magic = (long*)(p + OffMagic);
        long* word = (long*)(p + OffWord);
        long* ts = (long*)(p + OffTs);

        if (isParent)
        {
            string self = Environment.ProcessPath!;
            var psi = new ProcessStartInfo(self, carrier + " child " + n + " " + tag) { UseShellExecute = false };
            using var c = Process.Start(psi)!;
            var hs = Stopwatch.StartNew();
            while (Volatile.Read(ref *magic) != Magic)
            {
                if (c.HasExited) { Console.WriteLine("CHILD EXITED code=" + c.ExitCode + " during handshake"); return 5; }
                if (hs.ElapsedMilliseconds > 20000) { Console.WriteLine("HANDSHAKE TIMEOUT"); return 3; }
                Thread.Sleep(1);
            }

            var rnd = new Random(12345);
            for (int i = 1; i <= n; i++)
            {
                // 先随机忙等，制造「消费者尚未进入等待 / 正在被抢占」的竞态，
                // 再把时间戳写入并立刻唤醒，这样测到的才是纯唤醒延迟。
                int spins = rnd.Next(0, 400);
                for (int s = 0; s < spins; s++) Thread.SpinWait(20);
                Volatile.Write(ref *ts, Stopwatch.GetTimestamp());
                Volatile.Write(ref *word, i);
                WakeByAddressSingle(word);
            }
            c.WaitForExit();
            return c.ExitCode;
        }

        Volatile.Write(ref *magic, Magic);
        long seen = 0;
        int timeouts = 0, idlePolls = 0, slowWaits = 0;
        var lat = new double[n];
        int got = 0;
        var guard = Stopwatch.StartNew();
        while (got < n)
        {
            long cur = Volatile.Read(ref *word);
            if (cur == seen)
            {
                long before = cur;
                var w0 = Stopwatch.GetTimestamp();
                WaitOnAddress(word, &before, (UIntPtr)8, 50);
                double waitedMs = (Stopwatch.GetTimestamp() - w0) * 1000.0 / Stopwatch.Frequency;
                if (waitedMs > 45) timeouts++;
                else if (waitedMs > 1) slowWaits++;
                cur = Volatile.Read(ref *word);
                if (cur == seen) { idlePolls++; continue; }
            }
            while (seen < cur && got < n)
            {
                lat[got++] = (Stopwatch.GetTimestamp() - Volatile.Read(ref *ts)) * 1_000_000.0 / Stopwatch.Frequency;
                seen++;
            }
            if (guard.ElapsedMilliseconds > 120000) { Console.WriteLine("GUARD seen=" + seen + "/" + n); return 2; }
        }
        Array.Sort(lat, 0, got);
        double sum = 0; for (int i = 0; i < got; i++) sum += lat[i];
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0,-6} n={1} got={2} total={3:F0}ms full_timeouts={4} slow_waits(>1ms)={5}  wake_lat avg={6:F1}us p50={7:F1}us p99={8:F1}us p999={9:F1}us max={10:F1}us",
            carrier, n, got, guard.Elapsed.TotalMilliseconds, timeouts, slowWaits, sum / got, lat[got / 2], lat[(int)(got * 0.99)], lat[(int)(got * 0.999)], lat[got - 1]));
        return 0;
    }
}
