using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Courier.E2E
{
    /// <summary>
    /// 端到端验证宿主：两个真实进程，一个 serve 一个 call，走磁盘上的协议互操作。
    /// <para>
    /// 它不是产品代码，不参与体积预算，也不追求优雅：目的是让"两个进程真的通过 .courier 目录说话"
    /// 这件事可以被一条命令重复验证，而不是靠单元测试推断。
    /// </para>
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch (IOException)
            {
                // 没有控制台时保持宿主默认编码；不影响协议读写。
            }

            if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
            {
                Usage(Console.Error);
                return 2;
            }

            try
            {
                switch (args[0])
                {
                    case "serve": return Serve(args);
                    case "call": return Call(args);
                    default:
                        Console.Error.WriteLine("e2e: unknown mode \"" + args[0] + "\"");
                        Usage(Console.Error);
                        return 2;
                }
            }
            catch (ArgumentException exception)
            {
                Console.Error.WriteLine("e2e: " + exception.Message);
                return 2;
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine("e2e: " + exception.Message);
                return 2;
            }
        }

        private static void Usage(TextWriter writer)
        {
            writer.Write(
                "usage:\n" +
                "  Courier.E2E serve <root> [endpointId] [--seconds N]\n" +
                "  Courier.E2E call <root> <endpointId> <channel> <action> <payload> [timeoutSeconds]\n");
        }

        /// <summary>
        /// serve 模式：打开一个端点，注册能力，打印 READY，然后按 20 ms 节奏 Tick 直到 stdin EOF、
        /// Ctrl+C 或 --seconds 到点。stdin EOF 是给"父进程结束"准备的退出信号，避免留下僵尸端点。
        /// </summary>
        private static int Serve(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("e2e: serve needs a root directory");
                return 2;
            }

            string root = args[1];
            string endpointId = "app-a";
            double seconds = 0;
            for (int index = 2; index < args.Length; index++)
            {
                if (args[index] == "--seconds")
                {
                    if (index + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("e2e: --seconds needs a value");
                        return 2;
                    }
                    if (!double.TryParse(args[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) || seconds <= 0)
                    {
                        Console.Error.WriteLine("e2e: --seconds needs a positive number");
                        return 2;
                    }
                    index++;
                    continue;
                }

                endpointId = args[index];
            }

            using (CourierNode node = CourierNode.Open(root, endpointId, "e2e host"))
            {
                node.Handle("document", "open", "打开文档", payload =>
                    "{\"opened\":true,\"payload\":" + Quote(payload) + "}");
                node.Handle("demo", "echo", "回显 payload", payload => payload);
                // SPEC §13.1 的活证据：这个 handler 会占住一个在途名额若干秒，
                // 期间服务循环必须继续 Tick（心跳、取消、续约都靠它）。
                node.Handle("demo", "slow", "慢任务：payload 是毫秒数", payload =>
                {
                    int milliseconds;
                    if (!int.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out milliseconds)) milliseconds = 0;
                    if (milliseconds < 0) milliseconds = 0;
                    if (milliseconds > 30000) milliseconds = 30000;
                    Thread.Sleep(milliseconds);
                    return "slept:" + milliseconds.ToString(CultureInfo.InvariantCulture);
                });

                // 父进程读这一行才知道可以开始 call；必须 flush，否则重定向到管道时会卡在缓冲区里。
                Console.Out.Write("READY " + root + "\n");
                Console.Out.Flush();

                ConsoleCancelEventHandler onCancel = (sender, eventArgs) =>
                {
                    // 自己处理 Ctrl+C：先退出循环让 using 里的 Dispose 跑完，再结束进程。
                    eventArgs.Cancel = true;
                    Interlocked.Exchange(ref s_cancelled, 1);
                };
                Console.CancelKeyPress += onCancel;
                try
                {
                    StartStdinWatcher();

                    // 启动瞬间就 EOF 说明 stdin 指向 NUL 或被关闭，根本没有监督者；
                    // 此时若仍把 EOF 当退出信号，serve 会打印完 READY 就自杀。
                    Thread.Sleep(150);
                    bool watchStdin = Volatile.Read(ref s_stdinEof) == 0;
                    if (!watchStdin)
                    {
                        Console.Error.WriteLine("e2e: stdin 启动即 EOF，忽略 stdin，改用 --seconds/Ctrl+C 退出");
                        Console.Error.Flush();
                    }

                    DateTime deadline = seconds > 0 ? DateTime.UtcNow.AddSeconds(seconds) : DateTime.MaxValue;
                    while (DateTime.UtcNow < deadline)
                    {
                        if (Volatile.Read(ref s_cancelled) != 0) break;
                        if (watchStdin && Volatile.Read(ref s_stdinEof) != 0) break;
                        node.Tick();
                        Thread.Sleep(20);
                    }
                }
                finally
                {
                    Console.CancelKeyPress -= onCancel;
                    // SPEC §13.1：Tick 只负责启动，退出前必须把在途 handler 跑完，
                    // 否则最后一条命令的终态会留到下一次启动才补上。
                    node.WaitForIdle(TimeSpan.FromSeconds(10));
                }
            }

            Console.Out.Write("STOPPED\n");
            Console.Out.Flush();
            return 0;
        }

        /// <summary>
        /// call 模式：用 <see cref="CourierNode.CallAsync"/> 投递并等待（长命宿主应当异步，
        /// 这里保持与库内推荐用法一致，而不是走同步重载）。
        /// </summary>
        private static int Call(string[] args)
        {
            if (args.Length < 6)
            {
                Console.Error.WriteLine("e2e: call needs <root> <endpointId> <channel> <action> <payload> [timeoutSeconds]");
                return 2;
            }

            string root = args[1];
            string endpointId = args[2];
            string channel = args[3];
            string action = args[4];
            string payload = args[5];
            TimeSpan timeout = TimeSpan.FromSeconds(10);
            if (args.Length > 6 && !double.TryParse(args[6], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            {
                Console.Error.WriteLine("e2e: timeoutSeconds must be a number");
                return 2;
            }
            else if (args.Length > 6)
            {
                timeout = TimeSpan.FromSeconds(double.Parse(args[6], CultureInfo.InvariantCulture));
            }

            using (CourierNode node = CourierNode.Open(root, "app-b", "e2e caller"))
            {
                CourierResponse response = node.CallAsync(endpointId, channel, action, payload, timeout).GetAwaiter().GetResult();
                Console.Out.Write(new StringBuilder()
                    .Append("{\"status\":").Append(Quote(response.Status))
                    .Append(",\"reason\":").Append(response.Reason == null ? "null" : Quote(response.Reason!))
                    .Append(",\"payload\":").Append(Quote(response.Payload))
                    .Append(",\"requestId\":").Append(Quote(response.RequestId))
                    .Append('}')
                    .ToString());
                Console.Out.Write('\n');
                Console.Out.Flush();
                return ExitCode(response.Status);
            }
        }

        // stdin 监视线程的状态：0 = 还没读到 EOF，1 = 已 EOF；s_cancelled 同理。
        private static int s_stdinEof;
        private static int s_cancelled;

        /// <summary>
        /// 后台线程读 stdin，读到 EOF 就置位。不能在 Tick 循环里直接 <c>Console.In.Peek()</c>：
        /// 管道连着但暂时没数据时 Peek 会阻塞，端点随之停止 Tick，call 端永远等不到结果。
        /// </summary>
        private static void StartStdinWatcher()
        {
            Thread watcher = new Thread(() =>
            {
                try
                {
                    while (Console.In.Read() >= 0)
                    {
                        // 只关心 EOF，读到的字节丢弃。
                    }
                }
                catch (IOException)
                {
                    // 管道被父进程打断，等价于 EOF。
                }
                catch (ObjectDisposedException)
                {
                    // 同上。
                }

                Interlocked.Exchange(ref s_stdinEof, 1);
            });
            watcher.IsBackground = true;
            watcher.Name = "stdin-watcher";
            watcher.Start();
        }

        private static int ExitCode(string status)
        {
            switch (status)
            {
                case "ok": return 0;
                case "rejected": return 1;
                case "failed": return 2;
                case "unreachable": return 3;
                default: return 2;
            }
        }

        private static string Quote(string? value)
        {
            if (value == null) return "null";
            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                switch (current)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (current < ' ')
                        {
                            builder.Append("\\u").Append(((int)current).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(current);
                        }
                        break;
                }
            }
            builder.Append('"');
            return builder.ToString();
        }
    }
}
