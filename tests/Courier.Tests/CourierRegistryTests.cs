using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Courier.Tests;

/// <summary>
/// root 级注册表 <c>&lt;root&gt;/endpoint.json</c>（SPEC §7）。
/// <para>
/// 注册表是可选的加速视图：它让不能列目录的宿主一次读到端点名单，但权威永远是
/// <c>endpoints/</c> 目录与每个端点自己的 <c>heartbeat</c>。这一类测试因此既验证"写对了"，
/// 也验证"读不到时不装作没有端点"。
/// </para>
/// </summary>
public sealed class CourierRegistryTests : TestRoot
{
    private static CourierNodeOptions Registered()
    {
        return new CourierNodeOptions { PublishRootRegistry = true };
    }

    private string RegistryPath
    {
        get { return Path.Combine(Root, CourierRootRegistry.REGISTRY_FILE_NAME); }
    }

    private string RegistryLockPath
    {
        get { return Path.Combine(Root, CourierRootRegistry.LOCK_FILE_NAME); }
    }

    /// <summary>SPEC §7：字段严格是三种，多一个少一个都不是协议定义的。</summary>
    [Fact]
    public void Publish_单个端点_注册表字段齐全且不含多余键()
    {
        using (CourierNode node = CourierNode.Open(Root, "worker", "测试端点", null, Registered()))
        {
            Dictionary<string, object?> document = ReadJsonObject(RegistryPath);

            Assert.Equal(3, document.Count);
            Assert.Equal(CourierProtocol.PROTOCOL_VERSION, CourierJson.GetInteger(document, "protocolVersion"));
            Assert.NotNull(CourierJson.GetString(document, "generatedAtUtc"));

            List<object?>? entries = CourierJson.GetArray(document, "endpoints");
            Assert.NotNull(entries);
            Assert.Single(entries!);

            var entry = Assert.IsType<Dictionary<string, object?>>(entries![0]);
            Assert.Equal(4, entry.Count);
            Assert.Equal("worker", CourierJson.GetString(entry, "endpointId"));
            Assert.Equal("测试端点", CourierJson.GetString(entry, "description"));
            Assert.Equal(node.Endpoints()[0].SessionId, CourierJson.GetString(entry, "sessionId"));
            Assert.Contains(CourierJson.GetString(entry, "state"), new[] { "alive", "stale", "dead" });
        }
    }

    /// <summary>SPEC §7：条目 MUST 按 endpointId 序数升序，与打开顺序相反时也要排好。</summary>
    [Fact]
    public void Publish_两个端点_条目按endpointId序数升序()
    {
        using (CourierNode zebra = CourierNode.Open(Root, "zebra", "后开的端点", null, Registered()))
        using (CourierNode alpha = CourierNode.Open(Root, "alpha", "先开但名字在前", null, Registered()))
        {
            List<object?>? entries = CourierJson.GetArray(ReadJsonObject(RegistryPath), "endpoints");
            Assert.NotNull(entries);
            Assert.Equal(2, entries!.Count);
            Assert.Equal("alpha", CourierJson.GetString(Assert.IsType<Dictionary<string, object?>>(entries[0]), "endpointId"));
            Assert.Equal("zebra", CourierJson.GetString(Assert.IsType<Dictionary<string, object?>>(entries[1]), "endpointId"));
        }
    }

    /// <summary>SPEC §7：Dispose 把条目摘掉，且不删端点目录（目录是"这个端点存在过"的证据）。</summary>
    [Fact]
    public void Dispose_第二个端点退出_条目消失且目录保留()
    {
        using (CourierNode alpha = CourierNode.Open(Root, "alpha", null, null, Registered()))
        {
            CourierNode beta = CourierNode.Open(Root, "beta", null, null, Registered());
            Assert.Equal(2, CourierJson.GetArray(ReadJsonObject(RegistryPath), "endpoints")!.Count);

            beta.Dispose();

            List<object?>? entries = CourierJson.GetArray(ReadJsonObject(RegistryPath), "endpoints");
            Assert.NotNull(entries);
            Assert.Single(entries!);
            Assert.Equal("alpha", CourierJson.GetString(Assert.IsType<Dictionary<string, object?>>(entries![0]), "endpointId"));
            Assert.True(Directory.Exists(EndpointDirectory("beta")));
        }
    }

    /// <summary>SPEC §7：注册表损坏时读方 MUST 退化为列 <c>endpoints/</c> 目录，而不是报告"没有端点"。</summary>
    [Fact]
    public void 注册表损坏_读方退化为列目录()
    {
        using (CourierNode alpha = CourierNode.Open(Root, "alpha", "端点甲", null, Registered()))
        using (CourierNode beta = CourierNode.Open(Root, "beta", "端点乙", null, Registered()))
        {
            // 半截 JSON：模拟"上一个写者写到一半就被杀掉了"。原子写本不该留下这种文件，
            // 但读方不能假设所有写者都守规矩。
            CourierIO.WriteAtomic(RegistryPath, "{\"protocolVersion\":2,\"endpoints\":[{\"endpointId\"", true);

            List<CourierRegistryEntry> listing = CourierRootRegistry.ReadListing(Root, CourierLayout.Default);
            Assert.Equal(2, listing.Count);
            Assert.Equal("alpha", listing[0].EndpointId);
            Assert.Equal("beta", listing[1].EndpointId);
            Assert.Equal("端点甲", listing[0].Description);
        }
    }

    /// <summary>SPEC §7：注册表写坏了之后，下一次 Publish 必须重建，而不是把别人的条目一起丢掉。</summary>
    [Fact]
    public void 注册表损坏_下一次发布从目录重建且不丢条目()
    {
        using (CourierNode alpha = CourierNode.Open(Root, "alpha", null, null, Registered()))
        using (CourierNode beta = CourierNode.Open(Root, "beta", null, null, Registered()))
        {
            CourierIO.WriteAtomic(RegistryPath, "not json at all", true);

            Assert.True(CourierRootRegistry.Publish(
                Root, CourierLayout.Default, new CourierRegistryEntry("beta", string.Empty, "session", "alive"), false));

            List<object?>? entries = CourierJson.GetArray(ReadJsonObject(RegistryPath), "endpoints");
            Assert.NotNull(entries);
            Assert.Equal(2, entries!.Count);
            Assert.Equal("alpha", CourierJson.GetString(Assert.IsType<Dictionary<string, object?>>(entries[0]), "endpointId"));
            Assert.Equal("beta", CourierJson.GetString(Assert.IsType<Dictionary<string, object?>>(entries[1]), "endpointId"));
        }
    }

    /// <summary>
    /// SPEC §7：并发写不得产出半截 JSON——任何时刻读到的要么是完整旧文件，要么是完整新文件。
    /// <para>
    /// 这里必须是**真并发**：两个写者线程各自反复"重读-合并-整体原子替换"，同时第三个线程不断读，
    /// 才能覆盖"读到别人正在替换的那一瞬间"这条不变式。串行地写两次再读，永远不可能触发竞态。
    /// 抢不到锁返回 false 是正常结果（写者之间会互相让路），要守的是读者侧：读到的每个字节都能解析，
    /// 且 endpoints 数组始终存在。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 并发写_任何时刻读到的注册表都是完整JSON()
    {
        using (CourierNode alpha = CourierNode.Open(Root, "alpha", null, null, Registered()))
        using (CourierNode beta = CourierNode.Open(Root, "beta", null, null, Registered()))
        {
            // 先落下初值，保证读者一开始就有文件可读（否则它会在"文件还不存在"里空转）。
            PublishRounds("alpha", "s-alpha", 1);
            PublishRounds("beta", "s-beta", 1);

            const int Rounds = 200;
            var probe = new RegistryReaderProbe(RegistryPath);
            using var stop = new CancellationTokenSource();
            using var readerReady = new ManualResetEventSlim(false);
            using var readerInWriteWindow = new ManualResetEventSlim(false);

            Task reader = Task.Run(() => probe.Run(stop, readerReady, readerInWriteWindow));

            // 读者必须先就位，否则 200 轮发布可能在读者线程被调度起来之前就跑完了。
            Assert.True(readerReady.Wait(TimeSpan.FromSeconds(10)), "读者没有在 10 秒内读到注册表");

            probe.WritersRunning = 1;
            Task[] writers =
            {
                Task.Run(() => PublishInTwoHalves("alpha", "s-alpha", Rounds, readerInWriteWindow)),
                Task.Run(() => PublishInTwoHalves("beta", "s-beta", Rounds, readerInWriteWindow))
            };

            await Task.WhenAll(writers);
            probe.WritersRunning = 0;
            stop.Cancel();
            await reader;

            Assert.Empty(probe.Failures);
            Assert.True(probe.SuccessfulReads > 0, "读者一次都没读到注册表，并发窗口没有被覆盖");
            Assert.True(probe.ReadsDuringWrite > 0, "写者运行期间读者一次都没读到注册表，并发窗口没有被覆盖");

            List<object?>? entries = CourierJson.GetArray(ReadJsonObject(RegistryPath), "endpoints");
            Assert.NotNull(entries);
            Assert.Equal(2, entries!.Count);
        }
    }

    /// <summary>
    /// 反复发布同一端点的注册表条目，供并发用例的写者线程调用。
    /// 抢不到注册表锁时 <see cref="CourierRootRegistry.Publish"/> 返回 false，这是设计内结果，直接进入下一轮。
    /// </summary>
    /// <param name="endpointId">要发布的端点。</param>
    /// <param name="sessionId">该端点的会话标识，只用于填充条目字段。</param>
    /// <param name="rounds">发布轮数。</param>
    private void PublishRounds(string endpointId, string sessionId, int rounds)
    {
        for (int round = 0; round < rounds; round++)
        {
            CourierRootRegistry.Publish(
                Root, CourierLayout.Default,
                new CourierRegistryEntry(endpointId, "第" + round + "轮", sessionId, "alive"), false);
        }
    }

    /// <summary>
    /// 分两半发布，中间插一个同步点：等读者确实在"写者运行期间"读到过一次再继续。
    /// <para>
    /// 没有这个同步点时，两个写者可能在读者线程被调度起来之前就把所有轮次跑完
    /// （写者是文件 IO，读者只是循环读一个小文件，饥饿时完全可能），
    /// 于是"并发窗口被覆盖"这条覆盖度断言会偶发判负——那不是被测不变式失败，而是用例没造出并发。
    /// </para>
    /// </summary>
    /// <param name="endpointId">要发布的端点。</param>
    /// <param name="sessionId">该端点的会话标识。</param>
    /// <param name="rounds">发布轮数。</param>
    /// <param name="readerInWriteWindow">读者在写窗口内完成过一次读取后置位的事件。</param>
    private void PublishInTwoHalves(
        string endpointId,
        string sessionId,
        int rounds,
        ManualResetEventSlim readerInWriteWindow)
    {
        int half = rounds / 2;
        PublishRounds(endpointId, sessionId, half);
        // 同步点（不是断言）：等读者进入写窗口，超时也继续，由调用方的覆盖度断言给出结论。
        readerInWriteWindow.Wait(TimeSpan.FromSeconds(10));
        PublishRounds(endpointId, sessionId, rounds - half);
    }

    /// <summary>SPEC §7：注册表默认关闭——没开开关的端点一个字节都不写。</summary>
    [Fact]
    public void 未开启开关_不写注册表()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            node.Tick();
            Assert.False(File.Exists(RegistryPath));
        }
    }

    /// <summary>SPEC §7：生成的注册表不能留下临时文件或锁文件（锁文件允许存在，但必须是空的）。</summary>
    [Fact]
    public void Publish_不留临时文件_锁文件为空()
    {
        using (CourierNode node = CourierNode.Open(Root, "worker", null, null, Registered()))
        {
            string[] leftovers = Directory.GetFiles(Root, "*.tmp");
            Assert.Empty(leftovers);
            if (File.Exists(RegistryLockPath))
            {
                Assert.Equal(0, new FileInfo(RegistryLockPath).Length);
            }
        }
    }

    /// <summary>SPEC §7：条目字段严格是四个；多余字段不该被写出来。</summary>
    [Fact]
    public void 条目字段_严格是四个()
    {
        using (CourierNode node = CourierNode.Open(Root, "worker", "说明", null, Registered()))
        {
            List<object?>? entries = CourierJson.GetArray(ReadJsonObject(RegistryPath), "endpoints");
            var entry = Assert.IsType<Dictionary<string, object?>>(entries![0]);
            Assert.Equal(new[] { "description", "endpointId", "sessionId", "state" }, GetKeys(entry));
        }
    }

    /// <summary>取一份 JSON 对象的键名并排序，用来断言"字段严格是这几个、不多不少"。</summary>
    /// <param name="document">待检查的对象。</param>
    /// <returns>按序数升序排列的键名。</returns>
    private static string[] GetKeys(Dictionary<string, object?> document)
    {
        var keys = new string[document.Count];
        document.Keys.CopyTo(keys, 0);
        Array.Sort(keys, StringComparer.Ordinal);
        return keys;
    }

    /// <summary>
    /// 并发用例里的读者：不断读注册表并解析，记录"读到半截"的现场。
    /// </summary>
    /// <remarks>
    /// 单独成类而不是内联 lambda，是因为它要在读者线程与断言之间共享四个量
    /// （成功次数、写者窗口内读到的次数、故障清单、写者是否在跑）；用 ref 参数传进 lambda
    /// 只会绕得更远。计数器走 <see cref="Interlocked"/>，两个线程都能安全读写。
    /// </remarks>
    private sealed class RegistryReaderProbe
    {
        private readonly string mRegistryPath;

        /// <summary>读者成功解析过注册表的次数。</summary>
        internal int SuccessfulReads;

        /// <summary>读者在写者运行期间读到注册表的次数——为 0 说明并发窗口根本没被覆盖。</summary>
        internal int ReadsDuringWrite;

        /// <summary>写者是否在跑；读者用它判断这次读到的是不是并发窗口内的快照。</summary>
        internal int WritersRunning;

        /// <summary>读到的半截 JSON 等故障现场，由断言侧检查为空。</summary>
        internal readonly List<string> Failures = new List<string>();

        /// <summary>绑定要读的注册表路径。</summary>
        /// <param name="registryPath">root 级注册表的绝对路径。</param>
        internal RegistryReaderProbe(string registryPath)
        {
            mRegistryPath = registryPath;
        }

        /// <summary>读者主循环：一直读到 <paramref name="stop"/> 被取消，或第一次读到坏数据为止。</summary>
        /// <param name="stop">停止信号，由用例在写者收敛后触发。</param>
        /// <param name="readerReady">首次读成功时置位，让用例知道读者已经就位。</param>
        /// <param name="readerInWriteWindow">在写者运行期间读到注册表时置位，作为并发覆盖度的证据。</param>
        internal void Run(CancellationTokenSource stop, ManualResetEventSlim readerReady, ManualResetEventSlim readerInWriteWindow)
        {
            bool announced = false;
            while (!stop.IsCancellationRequested)
            {
                string? text;
                if (!CourierIO.TryReadAllText(mRegistryPath, out text)) continue;
                Interlocked.Increment(ref SuccessfulReads);
                if (Volatile.Read(ref WritersRunning) != 0)
                {
                    Interlocked.Increment(ref ReadsDuringWrite);
                    readerInWriteWindow.Set();
                }

                if (!announced)
                {
                    announced = true;
                    readerReady.Set();
                }

                if (!Inspect(text)) return;

                // 退避：让写者拿到窗口推进，也避免读者把核打满放大其他并行用例的时序抖动。
                Thread.Yield();
            }
        }

        /// <summary>解析一次读到的文本，坏数据记进 <see cref="Failures"/>。</summary>
        /// <param name="text">刚读到的注册表原文。</param>
        /// <returns>这份文本是完整注册表时为 true。</returns>
        private bool Inspect(string text)
        {
            Dictionary<string, object?>? document;
            if (!CourierJson.TryParseObject(text, out document))
            {
                lock (Failures) Failures.Add("读到了半截注册表（" + text.Length + " 字符）: " + text);
                return false;
            }

            if (CourierJson.GetArray(document!, "endpoints") == null)
            {
                lock (Failures) Failures.Add("注册表缺少 endpoints 数组: " + text);
                return false;
            }

            return true;
        }
    }
}