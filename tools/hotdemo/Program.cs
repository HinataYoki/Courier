using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace HotDemo
{
    /// <summary>
    /// 双进程端到端演示：host（被调用的软件）与 client（AI 的 CLI）。
    ///
    /// 拓扑：**宿主建一个槽池，客户端 CAS 抢槽**。
    /// 之所以不是「每客户端建一个环文件」，是因为连接建立成本：
    ///   每客户端一环：宿主 accept→opened 实测 5–19 ms（枚举目录 + 打开文件 + mmap + 开进程句柄）
    ///   槽池：客户端 Claim 实测 1–2 µs，宿主完全不参与连接建立
    /// 对「一次 CLI 调用只发一条命令」的场景，前者把 5 µs 的通道优势全吃掉了。
    ///
    /// 数据面仍是纯 mmap；文件只负责命名、容量与生存期。
    /// </summary>
    internal static class Program
    {
        static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0] : "host";
            return mode switch
            {
                "host" => Host(args),
                "host-file" => HostFile(args),
                "client" => Client(args),
                "selftest" => SelfTest(),
                "echo" => Echo(args),
                "fileonce" => FileOnce(args),
                "probe" => Probe(args),
                "openprobe" => OpenProbe(args),
                "flushcost" => FlushCost(args),
                _ => Usage(),
            };
        }

        static int Usage()
        {
            Console.Error.WriteLine("usage: hotdemo host <root> <count> [throttleUs] [slots]");
            Console.Error.WriteLine("       hotdemo host-file <root> <count>");
            Console.Error.WriteLine("       hotdemo client <root> <count> [throttleUs]");
            Console.Error.WriteLine("       hotdemo selftest | echo <root> | fileonce <root>");
            Console.Error.WriteLine("       hotdemo probe <root> <rounds> | openprobe <path> [rounds] | flushcost [rounds]");
            Console.Error.WriteLine("       hotdemo version");
            return 2;
        }

        static string RootTag(string root)
        {
            byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToLowerInvariant()));
            var sb = new StringBuilder(16);
            for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
            return sb.ToString();
        }

        /// <summary>
        /// 池与门铃的名字**必须带 sessionId**。
        /// 只按 root 推导会让「上一代宿主的残留」与新宿主撞名：宿主被杀时如果还有客户端
        /// 持着那个节的映射，内核不会回收这个名字，新宿主 CreateNew 直接失败。
        /// 带上 sessionId 后每一代宿主都有自己的命名空间，旧一代无论多脏都不影响新一代。
        /// </summary>
        static string DoorPrefix(string root, string sessionId) => "courier-" + RootTag(root) + "-" + sessionId;

        /// <summary>
        /// 池的"位置"不再是路径，而是内核命名空间里的一个节名。
        /// 从 root + sessionId 确定性推导，客户端读完 endpoint.json 就能算出来。
        /// </summary>
        static string PoolName(string root, string sessionId) => "courier-" + RootTag(root) + "-" + sessionId + "-pool";

        static void WriteEndpoint(string root, int pid, int slots, int capacity, string sid)
        {
            string tmp = Path.Combine(root, "endpoint.json.tmp");
            string final = Path.Combine(root, "endpoint.json");
            // 手写 JSON：NativeAOT 关闭了反射序列化
            // （"Reflection-based serialization has been disabled for this application"）
            string json =
                "{\n" +
                "  \"protocolVersion\": 2,\n" +
                "  \"pid\": " + pid + ",\n" +
                "  \"sessionId\": \"" + sid + "\",\n" +
                "  \"hot\": {\n" +
                "    \"version\": 1,\n" +
                "    \"transport\": \"mmap-pool\",\n" +
                "    \"poolName\": \"" + PoolName(root, sid) + "\",\n" +
                "    \"doorPrefix\": \"" + DoorPrefix(root, sid) + "\",\n" +
                "    \"slots\": " + slots + ",\n" +
                "    \"capacity\": " + capacity + ",\n" +
                "    \"slotBytes\": 256,\n" +
                "    \"maxPayloadBytes\": 232,\n" +
                "    \"doorbell\": \"named-event\"\n" +
                "  }\n" +
                "}\n";
            File.WriteAllText(tmp, json);
            using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                fs.Flush(true);      // 发现入口必须 durable；数据面不必
            if (File.Exists(final)) File.Replace(tmp, final, null);
            else File.Move(tmp, final);
        }

        // ------------------------------------------------------------------
        // host
        // ------------------------------------------------------------------
        static int Host(string[] args)
        {
            string root = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "courier-hotdemo");
            int count = args.Length > 2 ? int.Parse(args[2]) : 2000;
            long throttleUs = args.Length > 3 ? long.Parse(args[3]) : 0;
            int slots = args.Length > 4 ? int.Parse(args[4]) : 32;
            const int capacity = 64;

            foreach (var d in new[] { "commands", "results", "state", "hot" })
                Directory.CreateDirectory(Path.Combine(root, d));

            string sid = Guid.NewGuid().ToString("N").Substring(0, 16);
            using var pool = HotPool.CreateHost(PoolName(root, sid), slots, capacity, DoorPrefix(root, sid));
            WriteEndpoint(root, Environment.ProcessId, slots, capacity, sid);

            var payload = new byte[200];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)('a' + (i % 26));
            var inbox = new byte[HotPool.MaxBodyBytes];

            Console.WriteLine("host ready pid=" + Environment.ProcessId + " root=" + root + " slots=" + slots + " session=" + sid);
            Console.Out.Flush();

            WaitHandle[] bells = pool.HostBells!;
            int handled = 0, served = 0;
            var idle = Stopwatch.StartNew();
            long sweepAt = Environment.TickCount64 + 1000;

            while (handled < count)
            {
                int idx = WaitHandle.WaitAny(bells, 100);

                if (idx != WaitHandle.WaitTimeout)
                {
                    // 门铃会被合并（AutoResetEvent 只清一个信号），必须排空整个环
                    while (pool.Receive(idx, true, out int msgId, out ushort kind, inbox, out int inLen))
                    {
                        if (kind == 'C')
                        {
                            if (throttleUs > 0)
                            {
                                // 自旋而不是 Sleep：本机 Sleep(1) 实测 15.6ms
                                long until = Stopwatch.GetTimestamp() + throttleUs * (Stopwatch.Frequency / 1_000_000);
                                while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(20);
                            }
                            pool.Send(idx, false, msgId, (ushort)'R', payload);
                        }
                        handled++;
                        served++;
                    }
                    idle.Restart();
                }

                // 回收死客户端占的槽：每秒最多一次，绝不放数据面路径上
                if (Environment.TickCount64 >= sweepAt)
                {
                    int freed = pool.SweepDeadOwners();
                    if (freed > 0) Console.Error.WriteLine("[host] reclaimed " + freed + " slot(s) from dead clients");
                    sweepAt = Environment.TickCount64 + 1000;
                }
                if (idle.ElapsedMilliseconds > 30000) break;
            }

            Console.WriteLine($"handled={handled} slots={slots} served={served}");
            return 0;
        }

        // ------------------------------------------------------------------
        // host-file：纯文件通道的服务端，作为对照基线
        // ------------------------------------------------------------------
        static int HostFile(string[] args)
        {
            string root = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "courier-hotdemo");
            int count = args.Length > 2 ? int.Parse(args[2]) : 2000;
            Directory.CreateDirectory(Path.Combine(root, "commands"));
            Directory.CreateDirectory(Path.Combine(root, "results"));
            WriteEndpoint(root, Environment.ProcessId, 0, 0, Guid.NewGuid().ToString("N").Substring(0, 16));

            var payload = new byte[200];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)('a' + (i % 26));
            string cmdDir = Path.Combine(root, "commands");
            string resDir = Path.Combine(root, "results");
            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int handled = 0;
            var idle = Stopwatch.StartNew();
            Console.WriteLine("host-file ready pid=" + Environment.ProcessId);
            Console.Out.Flush();
            while (handled < count)
            {
                bool did = false;
                foreach (var f in Directory.EnumerateFiles(cmdDir, "*.json"))
                {
                    string name = Path.GetFileName(f);
                    if (!processed.Add(name)) continue;
                    File.WriteAllBytes(Path.Combine(resDir, name), payload);
                    handled++; did = true;
                    if (handled >= count) break;
                }
                if (handled >= count) break;
                if (!did)
                {
                    Thread.Yield();
                    if (idle.ElapsedMilliseconds > 60000) break;
                }
                else idle.Restart();
            }
            Console.WriteLine("handled=" + handled);
            return 0;
        }

        // ------------------------------------------------------------------
        // client：AI 的 CLI。开池 → 抢槽 → 走内存；任何异常静默回退文件通道
        // ------------------------------------------------------------------
        static int Client(string[] args)
        {
            string root = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "courier-hotdemo");
            int count = args.Length > 2 ? int.Parse(args[2]) : 2000;
            long throttleUs = args.Length > 3 ? long.Parse(args[3]) : 0;

            var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "endpoint.json"))).RootElement;
            long hostPid = json.TryGetProperty("pid", out var pidEl) ? pidEl.GetInt64() : 0;
            string sid = json.TryGetProperty("sessionId", out var sidEl) ? sidEl.GetString() ?? "" : "";

            var payload = new byte[200];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)('a' + (i % 26));

            HotPool? pool = null;
            WaitHandle? bell = null;
            int slot = -1;
            string used = "file";
            double connectUs = -1;
            var tConnect = Stopwatch.GetTimestamp();

            if (json.TryGetProperty("hot", out var hot) && hot.ValueKind == JsonValueKind.Object && hostPid > 0)
            {
                try
                {
                    pool = HotPool.OpenClient(PoolName(root, sid), hostPid, DoorPrefix(root, sid));
                    slot = pool.Claim(50);
                    if (slot < 0)
                    {
                        pool.Dispose();
                        pool = null;
                    }
                    else
                    {
                        bell = pool.ClientBell(slot);
                        used = "hot";
                        connectUs = (Stopwatch.GetTimestamp() - tConnect) * 1e6 / Stopwatch.Frequency;
                    }
                }
                catch (Exception ex)
                {
                    // hot 建不起来就静默回退：这条路必须永远存在，且结果不可区分
                    Console.Error.WriteLine("[client] hot unavailable: " + ex.GetType().Name + ": " + ex.Message);
                    pool?.Dispose();
                    pool = null;
                    bell = null;
                    used = "file";
                }
            }

            var lat = new System.Collections.Generic.List<double>(count);
            var buf = new byte[HotPool.MaxBodyBytes];
            int ok = 0, fallback = 0, mismatch = 0;
            bool dead = false;
            var startAll = Stopwatch.StartNew();

            for (int i = 0; i < count; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                if (pool != null && pool.Send(slot, true, i, (ushort)'C', payload))
                {
                    // 一次内核等待同时盯「门铃」与「宿主存活」：崩溃检测延迟由内核决定，不是轮询周期
                    bool got = pool.WaitResponse(slot, bell!, out int mid, out _, buf, out _, out bool hostDead, 2000, AliveBudgetMs);
                    if (got)
                    {
                        if (mid != i) mismatch++;
                        ok++;
                        lat.Add((Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency);
                        continue;
                    }
                    if (hostDead || !pool.HostAlive())
                    {
                        double detectUs = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
                        Console.WriteLine($"[client] host died: pid={pool.HostPid}, detected at op={i} " +
                                          $"after {detectUs / 1000:F2}ms, elapsed={startAll.ElapsedMilliseconds}ms");
                        dead = true;
                        break;
                    }
                    // 还活着但不回：本会话永久降级，绝不重试这个槽（重试会重复投递）
                    pool.Release(slot);
                    pool.Dispose();
                    pool = null;
                    fallback++;
                }
                else
                {
                    fallback++;
                }

                string name = "c-" + i.ToString("D6") + ".json";
                File.WriteAllBytes(Path.Combine(root, "commands", name), payload);
                string resPath = Path.Combine(root, "results", name);
                var swF = Stopwatch.StartNew();
                while (!File.Exists(resPath))
                {
                    if (swF.ElapsedMilliseconds > 500) break;
                    Thread.Yield();
                }
                if (File.Exists(resPath)) ok++;
                lat.Add((Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency);
            }

            if (pool != null)
            {
                pool.Release(slot);
                pool.Dispose();
            }
            string connectTxt = connectUs >= 0 ? $"{connectUs / 1000:F2}ms" : "n/a";
            if (lat.Count == 0)
            {
                Console.WriteLine($"client transport={used} n={count} ok={ok} fallback={fallback} dead={dead} connect={connectTxt} (no ops)");
                return dead ? 2 : 1;
            }
            lat.Sort();
            double sum = 0; foreach (var v in lat) sum += v;
            Console.WriteLine($"client transport={used} n={count} ok={ok} fallback={fallback} dead={dead} mismatch={mismatch} " +
                              $"connect={connectTxt} avg={sum / lat.Count:F1}us p50={lat[lat.Count / 2]:F1}us " +
                              $"p99={lat[Math.Min(lat.Count - 1, (int)(lat.Count * 0.99))]:F1}us max={lat[lat.Count - 1]:F1}us");
            return dead ? 2 : 0;
        }

        /// <summary>一次 CLI 调用 = 一条命令，走 hot 池。用于单发端到端计时。</summary>
        static int Echo(string[] args)
        {
            string root = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "courier-hotdemo");
            var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "endpoint.json"))).RootElement;
            long hostPid = json.GetProperty("pid").GetInt64();
            string sid = json.TryGetProperty("sessionId", out var sidEl) ? sidEl.GetString() ?? "" : "";
            using var pool = HotPool.OpenClient(PoolName(root, sid), hostPid, DoorPrefix(root, sid));
            int slot = pool.Claim(50);
            if (slot < 0) return 3;
            using var bell = pool.ClientBell(slot);
            var body = new byte[200];
            var buf = new byte[HotPool.MaxBodyBytes];
            try
            {
                if (!pool.Send(slot, true, 1, (ushort)'C', body)) return 4;
                if (!pool.WaitResponse(slot, bell, out _, out _, buf, out _, out _, 2000, 2000)) return 5;
            }
            finally { pool.Release(slot); }
            return 0;
        }

        /// <summary>一次 CLI 调用 = 一条命令，走文件通道。与 Echo 对照。</summary>
        static int FileOnce(string[] args)
        {
            string root = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "courier-hotdemo");
            string name = Guid.NewGuid().ToString("N").Substring(0, 12) + ".json";
            string cmd = Path.Combine(root, "commands", name);
            string res = Path.Combine(root, "results", name);
            var body = new byte[200];
            using (var fs = new FileStream(cmd, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(body, 0, body.Length);
                fs.Flush(true);   // 与 Courier 的 WriteAtomic 一致：durable 是协议承诺
            }
            var sw = Stopwatch.StartNew();
            while (!File.Exists(res))
            {
                if (sw.ElapsedMilliseconds > 5000) return 5;
                Thread.Sleep(0);
            }
            return 0;
        }

        /// <summary>
        /// 对比两种 mmap 载体在"写过之后再关闭"上的成本：
        ///   file  —— 文件支撑的映射（<root>/hot/pool.bin 那种）
        ///   named —— 页文件支撑的命名节（CreateFileMapping(INVALID_HANDLE_VALUE)）
        /// 假设：file 载体在最后一次 unmap 时要把脏页写回磁盘，这笔钱等价于 FlushFileBuffers，
        /// 正好是我们想逃离的那个开销；named 载体没有后备文件，不该有这笔钱。
        /// </summary>
        static int FlushCost(string[] args)
        {
            int rounds = args.Length > 1 ? int.Parse(args[1]) : 20;
            const int size = 530688;
            string dir = Path.Combine(Path.GetTempPath(), "courier-flushcost");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "carrier.bin");

            double OpenClose(bool fileCarrier, bool write, int rounds2)
            {
                var opens = new List<double>(); var closes = new List<double>();
                for (int i = 0; i < rounds2; i++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    MemoryMappedFile mm; FileStream? fs = null; MemoryMappedViewAccessor v;
                    if (fileCarrier)
                    {
                        fs = new FileStream(path, i == 0 ? FileMode.Create : FileMode.Open,
                                            FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                        mm = MemoryMappedFile.CreateFromFile(fs, null, size, MemoryMappedFileAccess.ReadWrite,
                                                             HandleInheritability.None, false);
                    }
                    else
                    {
                        mm = MemoryMappedFile.CreateNew("courier-flushcost-" + Environment.ProcessId, size);
                    }
                    v = mm.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
                    long t1 = Stopwatch.GetTimestamp();
                    if (write) for (int k = 0; k < size; k += 64) v.Write(k, (byte)(i + k));
                    long t2 = Stopwatch.GetTimestamp();
                    v.Dispose(); mm.Dispose(); fs?.Dispose();
                    long t3 = Stopwatch.GetTimestamp();
                    opens.Add((t1 - t0) * 1e6 / Stopwatch.Frequency);
                    closes.Add((t3 - t2) * 1e6 / Stopwatch.Frequency);
                    _ = t2;
                }
                opens.Sort(); closes.Sort();
                Console.WriteLine($"  open  p50={opens[opens.Count / 2],9:F0}us p99={opens[Math.Min(opens.Count - 1, (int)(opens.Count * 0.9))],9:F0}us");
                Console.WriteLine($"  close p50={closes[closes.Count / 2],9:F0}us max={closes[closes.Count - 1],9:F0}us");
                return closes[closes.Count / 2];
            }

            Console.WriteLine("file-backed mapping, NO writes:");
            OpenClose(true, false, Math.Min(rounds, 10));
            Console.WriteLine("file-backed mapping, writes " + size + " bytes:");
            OpenClose(true, true, rounds);
            Console.WriteLine("named pagefile section, NO writes:");
            OpenClose(false, false, Math.Min(rounds, 10));
            Console.WriteLine("named pagefile section, writes " + size + " bytes:");
            OpenClose(false, true, rounds);
            return 0;
        }

        /// <summary>
        /// 拆开 FileStream open / CreateFromFile / CreateViewAccessor 三段，
        /// 判断 connect 的 3–4 ms 到底花在打开文件上还是建映射上。
        /// </summary>
        static int OpenProbe(string[] args)
        {
            string path = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "fmprobe.bin");
            int rounds = args.Length > 2 ? int.Parse(args[2]) : 20;
            if (!File.Exists(path))
            {
                using var c = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
                c.SetLength(530688);
            }
            var a = new List<double>(); var b = new List<double>(); var d = new List<double>();
            for (int i = 0; i < rounds; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                long t1 = Stopwatch.GetTimestamp();
                var mm = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, false);
                long t2 = Stopwatch.GetTimestamp();
                var v = mm.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
                long t3 = Stopwatch.GetTimestamp();
                v.Dispose(); mm.Dispose(); fs.Dispose();
                a.Add((t1 - t0) * 1e6 / Stopwatch.Frequency);
                b.Add((t2 - t1) * 1e6 / Stopwatch.Frequency);
                d.Add((t3 - t2) * 1e6 / Stopwatch.Frequency);
                if (i < 4) Console.WriteLine($"  round {i}: file={a[i],8:F0}us map={b[i],8:F0}us view={d[i],8:F0}us");
            }
            a.Sort(); b.Sort(); d.Sort();
            Console.WriteLine($"fileopen p50={a[a.Count / 2],8:F0}us   createmap p50={b[b.Count / 2],8:F0}us   view p50={d[d.Count / 2],8:F0}us");
            return 0;
        }

        /// <summary>
        /// 连接建立成本探针：同一个进程里反复 open/close，区分「首次冷启动」与「每次都要付」。
        /// 这决定了单发 CLI 调用要不要为 connect 做预热/常驻。
        /// </summary>
        static int Probe(string[] args)
        {
            string root = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "courier-hotdemo");
            int rounds = args.Length > 2 ? int.Parse(args[2]) : 20;
            var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "endpoint.json"))).RootElement;
            long hostPid = json.GetProperty("pid").GetInt64();
            string sid = json.TryGetProperty("sessionId", out var sidEl) ? sidEl.GetString() ?? "" : "";
            var lat = new List<double>(rounds);
            for (int i = 0; i < rounds; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                var pool = HotPool.OpenClient(PoolName(root, sid), hostPid, DoorPrefix(root, sid));
                long t1 = Stopwatch.GetTimestamp();
                int slot = pool.Claim(1000);
                long t2 = Stopwatch.GetTimestamp();
                if (slot >= 0) pool.Release(slot);
                pool.Dispose();
                double openUs = (t1 - t0) * 1e6 / Stopwatch.Frequency;
                double claimUs = (t2 - t1) * 1e6 / Stopwatch.Frequency;
                lat.Add(openUs + claimUs);
                if (i < 5 || i == rounds - 1)
                    Console.WriteLine($"  round {i,3}: open={openUs,8:F0}us claim={claimUs,7:F1}us total={openUs + claimUs,8:F0}us");
            }
            lat.Sort();
            double sum = 0; foreach (var v in lat) sum += v;
            Console.WriteLine($"connect: avg={sum / lat.Count:F0}us p50={lat[lat.Count / 2]:F0}us max={lat[lat.Count - 1]:F0}us");
            return 0;
        }

        // ------------------------------------------------------------------
        // selftest
        // ------------------------------------------------------------------
        static int SelfTest()
        {
            string dir = Path.Combine(Path.GetTempPath(), "courier-hotdemo-selftest");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            string poolName = "st-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            string prefix = "st-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            using var host = HotPool.CreateHost(poolName, 4, 8, prefix);
            using var a = HotPool.OpenClient(poolName, Environment.ProcessId, prefix);
            using var b = HotPool.OpenClient(poolName, Environment.ProcessId, prefix);

            var body = new byte[200];
            var buf = new byte[HotPool.MaxBodyBytes];
            int failures = 0;
            void Check(bool cond, string what) { if (!cond) { Console.WriteLine("FAIL: " + what); failures++; } }

            // 1) 抢槽互斥：a 和 b 拿到的必须是不同槽
            int sa = a.Claim(50), sb = b.Claim(50);
            Check(sa >= 0 && sb >= 0 && sa != sb, "claim must hand out distinct slots");

            // 2) 同槽发收 + 内容完整 + CRC
            if (sa >= 0)
            {
                for (int i = 0; i < 200; i++)
                {
                    for (int k = 0; k < body.Length; k++) body[k] = (byte)(i + k);
                    if (!a.Send(sa, true, i, 1, body)) { failures++; break; }
                    if (!host.Receive(sa, true, out int id, out _, buf, out int len)) { failures++; break; }
                    if (id != i || len != body.Length) failures++;
                    for (int k = 0; k < body.Length; k++) if (buf[k] != body[k]) { failures++; break; }
                }
            }

            // 3) 满环必须返回 false，不阻塞不覆盖
            int pushed = 0;
            while (a.Send(sa, true, 999, 1, body)) pushed++;
            Check(pushed == 8, "ring-full must reject after capacity=8, got " + pushed);

            // 4) 排空后恢复
            while (host.Receive(sa, true, out _, out _, buf, out _)) { }
            Check(a.Send(sa, true, 1000, 1, body), "ring must recover after drain");
            while (host.Receive(sa, true, out _, out _, buf, out _)) { }

            // 5) 反向通道独立
            Check(host.Send(sa, false, 7, 2, body), "reverse send failed");
            Check(a.Receive(sa, false, out int rid, out _, buf, out _) && rid == 7, "reverse recv failed");

            // 6) 越界 body 必须抛
            try { a.Send(sa, true, 1, 1, new byte[400]); Console.WriteLine("FAIL: oversized body not rejected"); failures++; }
            catch (ArgumentOutOfRangeException) { }

            // 7) 释放后可被再次抢到
            a.Release(sa); b.Release(sb);
            int s3 = b.Claim(50);
            Check(s3 >= 0, "released slot must be claimable again");
            if (s3 >= 0) b.Release(s3);

            // 8) 池满返回 -1
            int[] held = new int[4];
            for (int i = 0; i < 4; i++) held[i] = host.Claim(50);
            Check(held[3] >= 0, "host must be able to claim all 4 slots");
            using (var extra = HotPool.OpenClient(poolName, Environment.ProcessId, prefix))
                Check(extra.Claim(10) == -1, "full pool must return -1 rather than block");
            for (int i = 0; i < 4; i++) if (held[i] >= 0) host.Release(held[i]);

            Console.WriteLine(failures == 0 ? "SELFTEST OK" : "SELFTEST FAILED failures=" + failures);
            return failures == 0 ? 0 : 1;
        }

        const int AliveBudgetMs = 2000;
    }
}
