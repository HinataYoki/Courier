using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

internal static class Program
{
    static readonly byte[] Payload = MakePayload(200);

    static byte[] MakePayload(int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)('a' + (i % 26));
        return b;
    }

    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "atomic";
        int n = args.Length > 1 ? int.Parse(args[1]) : 500;
        string dir = args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "courier-durabench");
        Directory.CreateDirectory(dir);

        var sw = new List<double>(n);
        var total = Stopwatch.StartNew();

        switch (mode)
        {
            case "atomic": RunAtomic(dir, n, sw, durable: true); break;
            case "atomic-nosync": RunAtomic(dir, n, sw, durable: false); break;
            case "append": RunAppend(dir, n, sw, durable: true); break;
            case "append-nosync": RunAppend(dir, n, sw, durable: false); break;
            case "append-batch": RunAppendBatched(dir, n, sw); break;
            default:
                Console.Error.WriteLine("unknown mode: " + mode);
                return 2;
        }

        total.Stop();
        Report(mode, sw);
        Console.WriteLine($"total_ms={total.Elapsed.TotalMilliseconds:F1}");
        return 0;
    }

    // 复刻 CourierIO.WriteAtomic：同目录临时文件 -> flush(可选 FlushFileBuffers) -> rename
    static void RunAtomic(string dir, int n, List<double> sw, bool durable)
    {
        string target = Path.Combine(dir, "cmd.json");
        string temp = Path.Combine(dir, "cmd.tmp");
        for (int i = 0; i < n; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(Payload, 0, Payload.Length);
                stream.Flush();
                if (durable) stream.Flush(true);
            }
            if (File.Exists(target)) File.Delete(target);
            File.Move(temp, target);
            long t1 = Stopwatch.GetTimestamp();
            sw.Add((t1 - t0) * 1e6 / Stopwatch.Frequency);
        }
    }

    // 追加写单一日志：group commit 的基础形态
    static void RunAppend(string dir, int n, List<double> sw, bool durable)
    {
        string log = Path.Combine(dir, "journal.log");
        if (File.Exists(log)) File.Delete(log);
        using var stream = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096);
        for (int i = 0; i < n; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            stream.Write(Payload, 0, Payload.Length);
            stream.Flush();
            if (durable) stream.Flush(true);
            long t1 = Stopwatch.GetTimestamp();
            sw.Add((t1 - t0) * 1e6 / Stopwatch.Frequency);
        }
    }

    // 每 64 条或每 2ms 才 FlushFileBuffers 一次（组提交）
    static void RunAppendBatched(string dir, int n, List<double> sw)
    {
        string log = Path.Combine(dir, "journal-batch.log");
        if (File.Exists(log)) File.Delete(log);
        using var stream = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096);
        int pending = 0;
        long lastSync = Stopwatch.GetTimestamp();
        for (int i = 0; i < n; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            stream.Write(Payload, 0, Payload.Length);
            stream.Flush();
            pending++;
            long now = Stopwatch.GetTimestamp();
            if (pending >= 64 || (now - lastSync) * 1e3 / Stopwatch.Frequency >= 2.0)
            {
                stream.Flush(true);
                pending = 0;
                lastSync = now;
            }
            long t1 = Stopwatch.GetTimestamp();
            sw.Add((t1 - t0) * 1e6 / Stopwatch.Frequency);
        }
        stream.Flush(true);
    }

    static void Report(string label, List<double> s)
    {
        s.Sort();
        double sum = 0;
        foreach (var v in s) sum += v;
        double avg = sum / s.Count;
        double p50 = s[s.Count / 2];
        double p99 = s[Math.Min(s.Count - 1, (int)(s.Count * 0.99))];
        Console.WriteLine($"{label,-16} n={s.Count,-6} avg={avg,10:F1}us p50={p50,10:F1}us p99={p99,10:F1}us min={s[0],8:F1}us max={s[s.Count-1],10:F1}us");
    }
}
