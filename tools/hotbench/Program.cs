using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;

namespace HotBench
{
    internal static unsafe class Program
    {
        private const int Capacity = 64;
        private const int SlotSize = 256;
        private const int HeaderSize = 64;
        private const int RingBytes = Capacity * SlotSize;
        private const int FileBytes = HeaderSize + RingBytes * 2;
        private const int PayloadBytes = 200;

        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

        private enum Mode { Futex, Spin, Hybrid }

        private static int Main(string[] args)
        {
            if (args.Length == 0) { Usage(); return 2; }
            string cmd = args[0];
            bool child = Has(args, "child");
            bool cold = Has(args, "cold");
            int count = IntArg(args, 3000);

            if (cmd == "granularity") { Granularity(); return 0; }
            if (cmd == "addr") return Addr(child);
            if (cmd == "spawn") return SpawnBench(args[1], IntArg(args, 30));

            Mode mode = Has(args, "spin") ? Mode.Spin : Has(args, "hybrid") ? Mode.Hybrid : Mode.Futex;
            switch (cmd)
            {
                case "ring": return child ? RingChild(mode, count, cold) : RingParent(mode, count, cold);
                case "pipe": return child ? PipeChild(count, cold) : PipeParent(count, cold);
                case "watch": return child ? WatchChild(count) : WatchParent(count);
                case "poll": return child ? PollChild(count) : PollParent(count);
            }
            Usage();
            return 2;
        }

        private static void Usage()
        {
            Console.WriteLine("hotbench granularity");
            Console.WriteLine("hotbench addr [child]");
            Console.WriteLine("hotbench spawn <exe> [N]");
            Console.WriteLine("hotbench ring  [spin|hybrid] [cold] [N] [child]");
            Console.WriteLine("hotbench pipe  [cold] [N] [child]");
            Console.WriteLine("hotbench watch [N] [child]");
            Console.WriteLine("hotbench poll  [N] [child]");
        }

        private static bool Has(string[] a, string f) { foreach (var s in a) if (s == f) return true; return false; }

        private static int IntArg(string[] a, int fallback)
        {
            foreach (var s in a) { int v; if (int.TryParse(s, out v)) return v; }
            return fallback;
        }

        private static string Self()
        {
            string? p = Environment.ProcessPath;
            if (p == null) throw new InvalidOperationException("no process path");
            return p;
        }

        private static Process Spawn(string extra)
        {
            return Process.Start(new ProcessStartInfo(Self(), extra) { UseShellExecute = false })!;
        }

        private static string WorkDir()
        {
            string root = Path.Combine(Path.GetTempPath(), "courier-hotbench");
            Directory.CreateDirectory(root);
            return root;
        }

        private static string RingPath() { return Path.Combine(WorkDir(), "ring.bin"); }
        private static string EvA2B() { return "courier-hotbench-a2b"; }
        private static string EvB2A() { return "courier-hotbench-b2a"; }

        // ---------------- 关键验证：跨进程 WaitOnAddress 必须两端映射到同一虚拟地址 ----------------

        private static int Addr(bool child)
        {
            string path = RingPath();
            byte* p;
            if (child)
            {
                var sw = Stopwatch.StartNew();
                while (!File.Exists(path)) { if (sw.ElapsedMilliseconds > 20000) return 3; Thread.Sleep(1); }
                p = OpenRing(path);
                Console.WriteLine("child  base=0x" + ((ulong)p).ToString("X"));
            }
            else
            {
                if (File.Exists(path)) File.Delete(path);
                using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite)) fs.SetLength(FileBytes);
                p = OpenRing(path);
                Console.WriteLine("parent base=0x" + ((ulong)p).ToString("X"));
                var c = Spawn("addr child");
                c.WaitForExit();
            }
            return 0;
        }

        // ---------------- 进程启动 ----------------

        private static int SpawnBench(string exe, int n)
        {
            var samples = new double[n];
            for (int i = 0; i < n; i++)
            {
                var psi = new ProcessStartInfo(exe, "") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                long t0 = Stopwatch.GetTimestamp();
                using (var p = Process.Start(psi)!)
                {
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                }
                samples[i] = (Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency / 1000.0;
            }
            Array.Sort(samples);
            double sum = 0; foreach (var s in samples) sum += s;
            Console.WriteLine(string.Format("spawn {0,-40} n={1,4} avg={2,7:F1}ms p50={3,7:F1}ms p99={4,7:F1}ms min={5,6:F1}ms",
                Path.GetFileName(exe), n, sum / n, samples[n / 2], samples[Math.Min(n - 1, (int)(n * 0.99))], samples[0]));
            return 0;
        }

        // ---------------- ring ----------------

        private static byte* OpenRing(string path)
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            var mmf = MemoryMappedFile.CreateFromFile(fs, null, FileBytes, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
            var acc = mmf.CreateViewAccessor(0, FileBytes, MemoryMappedFileAccess.ReadWrite);
            byte* p = null;
            acc.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            return p + acc.PointerOffset; // 故意不释放：随进程退出
        }

        private static void Push(byte* ring, long index, byte* payload, int length)
        {
            byte* slotBase = ring + (int)(index % Capacity) * SlotSize;
            Buffer.MemoryCopy(payload, slotBase + 16, SlotSize - 16, length);
            *(int*)(slotBase + 8) = length;
            Volatile.Write(ref *(long*)slotBase, index + 1);
        }

        private static void SpinUntil(byte* ring, long index)
        {
            long* seq = (long*)(ring + (int)(index % Capacity) * SlotSize);
            long want = index + 1;
            int spins = 0;
            while (Volatile.Read(ref *seq) != want)
            {
                if (++spins > 4000) Thread.Yield();
                else Thread.SpinWait(20);
            }
        }

        private static void CopyOut(byte* ring, long index, byte* into)
        {
            byte* slotBase = ring + (int)(index % Capacity) * SlotSize;
            Buffer.MemoryCopy(slotBase + 16, into, PayloadBytes, *(int*)(slotBase + 8));
        }

        private static int RingChild(Mode mode, int count, bool cold)
        {
            string path = RingPath();
            var sw = Stopwatch.StartNew();
            while (!File.Exists(path)) { if (sw.ElapsedMilliseconds > 30000) throw new TimeoutException("ring.bin never appeared"); Thread.Sleep(1); }

            byte* basePtr = OpenRing(path);
            byte* a2b = basePtr + HeaderSize;
            byte* b2a = basePtr + HeaderSize + RingBytes;
            byte* buf = stackalloc byte[PayloadBytes];

            EventWaitHandle? evA = null, evB = null;
            if (mode == Mode.Hybrid)
            {
                evA = EventWaitHandle.OpenExisting(EvA2B());
                evB = EventWaitHandle.OpenExisting(EvB2A());
            }

            int warmup = Math.Min(200, count);
            for (int i = 0; i < warmup + count; i++)
            {
                if (mode == Mode.Hybrid) evA!.WaitOne();
                else SpinUntil(a2b, i);
                CopyOut(a2b, i, buf);
                Push(b2a, i, buf, PayloadBytes);
                if (mode == Mode.Hybrid) evB!.Set();
            }
            Thread.Sleep(2000);
            return 0;
        }

        private static int RingParent(Mode mode, int count, bool cold)
        {
            string path = RingPath();
            if (File.Exists(path)) File.Delete(path);
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite)) fs.SetLength(FileBytes);

            byte* basePtr = OpenRing(path);
            byte* a2b = basePtr + HeaderSize;
            byte* b2a = basePtr + HeaderSize + RingBytes;

            EventWaitHandle? evA = null, evB = null;
            if (mode == Mode.Hybrid)
            {
                evA = new EventWaitHandle(false, EventResetMode.AutoReset, EvA2B(), out _);
                evB = new EventWaitHandle(false, EventResetMode.AutoReset, EvB2A(), out _);
            }

            var child = Spawn("ring " + (mode == Mode.Spin ? "spin " : mode == Mode.Hybrid ? "hybrid " : "") + (cold ? "cold " : "") + count + " child");
            if (mode == Mode.Hybrid) { evA!.Reset(); evB!.Reset(); }

            byte* payload = stackalloc byte[PayloadBytes];
            for (int i = 0; i < PayloadBytes; i++) payload[i] = (byte)(i * 7 & 0xFF);
            byte* reply = stackalloc byte[PayloadBytes];

            int warmup = Math.Min(200, count);
            for (int i = 0; i < warmup; i++)
            {
                Push(a2b, i, payload, PayloadBytes);
                if (mode == Mode.Hybrid) { evA!.Set(); evB!.WaitOne(); } else SpinUntil(b2a, i);
                CopyOut(b2a, i, reply);
            }

            var samples = new double[count];
            for (int i = 0; i < count; i++)
            {
                long index = warmup + i;
                if (cold) Thread.Sleep(50);
                long t0 = Stopwatch.GetTimestamp();
                Push(a2b, index, payload, PayloadBytes);
                if (mode == Mode.Hybrid) { evA!.Set(); evB!.WaitOne(); } else SpinUntil(b2a, index);
                CopyOut(b2a, index, reply);
                samples[i] = (Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency;
            }

            child.Refresh();
            TimeSpan cpu0 = child.TotalProcessorTime;
            Thread.Sleep(2200);
            child.Refresh();
            double idleCpu = (child.TotalProcessorTime - cpu0).TotalMilliseconds / 2200.0 * 100.0;

            Report("ring/" + mode + (cold ? "/cold" : ""), samples, idleCpu);
            child.Kill(); child.WaitForExit();
            return 0;
        }

        // ---------------- named pipe ----------------

        private static string PipeName() { return "courier-hotbench-pipe"; }

        private static void ReadFull(Stream s, byte[] buf, int n)
        {
            int off = 0;
            while (off < n)
            {
                int r = s.Read(buf, off, n - off);
                if (r <= 0) throw new EndOfStreamException();
                off += r;
            }
        }

        private static int PipeChild(int count, bool cold)
        {
            using var client = new NamedPipeClientStream(".", PipeName(), PipeDirection.InOut, PipeOptions.None);
            client.Connect(30000);
            var buf = new byte[PayloadBytes];
            var seq = new byte[4];
            int warmup = Math.Min(200, count);
            for (int i = 0; i < warmup + count; i++)
            {
                if (cold && i >= warmup) Thread.Sleep(50);
                ReadFull(client, buf, PayloadBytes);
                ReadFull(client, seq, 4);
                client.Write(seq, 0, 4);
                client.Flush();
            }
            Thread.Sleep(2000);
            return 0;
        }

        private static int PipeParent(int count, bool cold)
        {
            using var server = new NamedPipeServerStream(PipeName(), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);
            var child = Spawn("pipe " + (cold ? "cold " : "") + count + " child");
            server.WaitForConnection();

            var payload = new byte[PayloadBytes];
            for (int i = 0; i < PayloadBytes; i++) payload[i] = (byte)(i * 7 & 0xFF);
            var ack = new byte[4];
            int warmup = Math.Min(200, count);
            for (int i = 0; i < warmup; i++)
            {
                server.Write(payload, 0, PayloadBytes);
                server.Write(BitConverter.GetBytes(i), 0, 4);
                server.Flush();
                ReadFull(server, ack, 4);
            }

            var samples = new double[count];
            for (int i = 0; i < count; i++)
            {
                if (cold) Thread.Sleep(50);
                long t0 = Stopwatch.GetTimestamp();
                server.Write(payload, 0, PayloadBytes);
                server.Write(BitConverter.GetBytes(warmup + i), 0, 4);
                server.Flush();
                ReadFull(server, ack, 4);
                samples[i] = (Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency;
            }

            child.Refresh();
            TimeSpan cpu0 = child.TotalProcessorTime;
            Thread.Sleep(2200);
            child.Refresh();
            double idleCpu = (child.TotalProcessorTime - cpu0).TotalMilliseconds / 2200.0 * 100.0;

            Report("pipe" + (cold ? "/cold" : ""), samples, idleCpu);
            child.Kill(); child.WaitForExit();
            return 0;
        }

        // ---------------- file + FileSystemWatcher ----------------

        private static string WatchDir()
        {
            string d = Path.Combine(WorkDir(), "watch");
            Directory.CreateDirectory(d);
            return d;
        }

        private static int WatchChild(int count)
        {
            string dir = WatchDir();
            using var watcher = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.EnableRaisingEvents = true;   // 先装监听，再宣告就绪，避免漏掉第一批事件
            File.WriteAllText(Path.Combine(dir, "ready"), "1");

            int served = 0;
            while (served < count)
            {
                // 事件只是提示不是事实：醒来必须重新枚举，且要能被超时兜底
                var change = watcher.WaitForChanged(WatcherChangeTypes.All, 1000);
                for (int i = served; i < count; i++)
                {
                    string cmdPath = Path.Combine(dir, "cmd" + i + ".json");
                    if (!File.Exists(cmdPath)) break;
                    string payload;
                    try { payload = File.ReadAllText(cmdPath); } catch (IOException) { break; }
                    File.WriteAllText(Path.Combine(dir, "res" + i + ".json"), payload);
                    served = i + 1;
                }
                if (change.TimedOut && served < count && !File.Exists(Path.Combine(dir, "cmd" + served + ".json"))) continue;
            }
            Thread.Sleep(2000);
            return 0;
        }

        private static int WatchParent(int count)
        {
            string dir = WatchDir();
            foreach (var f in Directory.GetFiles(dir)) File.Delete(f);

            var child = Spawn("watch " + count + " child");
            var sw = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(dir, "ready")))
            {
                if (sw.ElapsedMilliseconds > 30000) throw new TimeoutException("child never became ready");
                Thread.Sleep(2);
            }

            string payload = new string('x', PayloadBytes);
            var samples = new double[count];
            for (int i = 0; i < count; i++)
            {
                string cmdPath = Path.Combine(dir, "cmd" + i + ".json");
                string resPath = Path.Combine(dir, "res" + i + ".json");
                long t0 = Stopwatch.GetTimestamp();
                File.WriteAllText(cmdPath, payload);
                while (!File.Exists(resPath)) Thread.SpinWait(50);
                samples[i] = (Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency;
            }

            long misses = 0;
            foreach (var s in samples) if (s > 100_000) misses++;
            Report("file+fsw", samples, 0);
            Console.WriteLine("          其中超过 100ms 的次数 = " + misses + " / " + count);

            child.Kill(); child.WaitForExit();
            foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
            return 0;
        }

        // ---------------- 纯轮询文件通道（对照组） ----------------

        private static int PollChild(int count)
        {
            string dir = WatchDir();
            File.WriteAllText(Path.Combine(dir, "ready"), "1");
            int served = 0;
            while (served < count)
            {
                for (int i = served; i < count; i++)
                {
                    string cmdPath = Path.Combine(dir, "cmd" + i + ".json");
                    if (!File.Exists(cmdPath)) break;
                    string payload;
                    try { payload = File.ReadAllText(cmdPath); } catch (IOException) { break; }
                    File.WriteAllText(Path.Combine(dir, "res" + i + ".json"), payload);
                    served = i + 1;
                }
                Thread.Sleep(1);
            }
            Thread.Sleep(2000);
            return 0;
        }

        private static int PollParent(int count)
        {
            string dir = WatchDir();
            foreach (var f in Directory.GetFiles(dir)) File.Delete(f);

            var child = Spawn("poll " + count + " child");
            var sw = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(dir, "ready")))
            {
                if (sw.ElapsedMilliseconds > 30000) throw new TimeoutException("child never became ready");
                Thread.Sleep(2);
            }

            string payload = new string('x', PayloadBytes);
            var samples = new double[count];
            for (int i = 0; i < count; i++)
            {
                string cmdPath = Path.Combine(dir, "cmd" + i + ".json");
                string resPath = Path.Combine(dir, "res" + i + ".json");
                long t0 = Stopwatch.GetTimestamp();
                File.WriteAllText(cmdPath, payload);
                while (!File.Exists(resPath)) Thread.SpinWait(50);
                samples[i] = (Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency;
            }

            Report("file+poll1ms", samples, 0);
            child.Kill(); child.WaitForExit();
            foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
            return 0;
        }

        // ---------------- 汇总输出 ----------------

        private static void Report(string label, double[] samples, double idleCpu)
        {
            Array.Sort(samples);
            double sum = 0; foreach (var s in samples) sum += s;
            int n = samples.Length;
            Console.WriteLine(string.Format(
                "{0,-18} n={1,6}  avg={2,9:F1}us  p50={3,9:F1}us  p99={4,9:F1}us  min={5,8:F1}us  idle_cpu={6,6:F2}%",
                label, n, sum / n, samples[n / 2], samples[Math.Min(n - 1, (int)(n * 0.99))], samples[0], idleCpu));
        }

        private static void Granularity()
        {
            void Measure(string label, Action wait, int n)
            {
                wait();
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < n; i++) wait();
                sw.Stop();
                Console.WriteLine(string.Format("{0,-30} {1,10:F1} us/op", label, sw.Elapsed.TotalMilliseconds / n * 1000.0));
            }

            Measure("Thread.Sleep(1)", () => Thread.Sleep(1), 100);
            Measure("Thread.Sleep(2)", () => Thread.Sleep(2), 100);
            Measure("Thread.Sleep(5)", () => Thread.Sleep(5), 100);
            Measure("Thread.Sleep(0)", () => Thread.Sleep(0), 300);
            Measure("SpinWait(20)", () => Thread.SpinWait(20), 300);

            timeBeginPeriod(1);
            Measure("Sleep(1) + timeBeginPeriod(1)", () => Thread.Sleep(1), 100);
            timeEndPeriod(1);
        }
    }
}
