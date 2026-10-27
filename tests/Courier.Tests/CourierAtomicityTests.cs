using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Xunit;

namespace Courier.Tests;

/// <summary>
/// 原子写与幂等（SPEC §3.3 与 §16）。
/// </summary>
public sealed class CourierAtomicityTests : TestRoot
{
    private const int PayloadLength = 200 * 1024;

    /// <summary>
    /// SPEC §3.3：读者要么看不到目标文件，要么看到一个完整可解析的文件，绝无中间态。
    /// <para>
    /// 读者带退避（<see cref="Thread.Yield"/> / 短睡眠）而不是空转：无退避的热循环会把所有核打满，
    /// 让同一进程里并行跑的其他用例出现无关的时序抖动。写者也不再"立刻开写"，
    /// 而是等两个读者都完成第一遍扫描——否则写者可能在没有读者参与时就把 200 轮写完，
    /// 并发窗口形同虚设。
    /// </para>
    /// </summary>
    [Fact]
    public void WriteAtomic_并发读写_读者永不见半写文件()
    {
        string directory = Path.Combine(Root, "atomic-probe");
        Directory.CreateDirectory(directory);

        const int Rounds = 200;
        string payload = new string('x', PayloadLength);

        string? failureA;
        string? failureB;
        int successfulReads;

        // 读者的生命周期包在块里：先停线程并等待退出，再断言，
        // 否则最后一次写入引发的失败可能落在断言之后，断言就形同虚设。
        using (var readerA = new ConcurrentFileReader(
            () => Directory.GetFiles(directory, "*.json"),
            text => InspectAtomicPayload(text)))
        using (var readerB = new ConcurrentFileReader(
            () => Directory.GetFiles(directory, "*.json"),
            text => InspectAtomicPayload(text)))
        {
            // 读者先就位再开写：否则写者可能在没有读者参与时就把 200 轮写完。
            Assert.True(readerA.WaitUntilReady(TimeSpan.FromSeconds(10)), "读者 A 没有在 10 秒内读到文件");
            Assert.True(readerB.WaitUntilReady(TimeSpan.FromSeconds(10)), "读者 B 没有在 10 秒内读到文件");

            // 每一轮写一个全新的目标：写者的临时文件与最终文件都必须对读者不可见。
            for (int round = 0; round < Rounds; round++)
            {
                string json = new CourierJsonObject()
                    .AddString("kind", "atomic-probe")
                    .AddNumber("round", round)
                    .AddString("payload", payload)
                    .Build();
                CourierIO.WriteAtomic(Path.Combine(directory, "payload-" + round + ".json"), json, true);
            }

            failureA = readerA.FirstFailure;
            failureB = readerB.FirstFailure;
            successfulReads = readerA.SuccessfulReads + readerB.SuccessfulReads;
        }

        Assert.Null(failureA);
        Assert.Null(failureB);
        Assert.True(successfulReads > 0, "读者一次都没读到文件，并发窗口没有被覆盖");
        Assert.Equal(Rounds, Directory.GetFiles(directory, "*.json").Length);

        // 临时文件不得残留（SPEC §3.3 的 rename 必须把临时名搬走）。
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    /// <summary>
    /// 校验一份并发读写中的原子写产物：必须是可解析的 JSON，且 payload 完整。
    /// </summary>
    /// <param name="text">读到的文件原文。</param>
    /// <returns>合格返回 null，否则返回写进失败列表的描述。</returns>
    private static string? InspectAtomicPayload(string text)
    {
        Dictionary<string, object?>? document;
        if (!CourierJson.TryParseObject(text, out document)) return "读到了无法解析的半写文件，长度 " + text.Length;

        string? readPayload = CourierJson.GetString(document, "payload");
        if (readPayload == null || readPayload.Length != PayloadLength) return "读到的 payload 不完整";
        return null;
    }

    /// <summary>
    /// SPEC §3.3 + §3.4：覆盖写同一路径时，正在读该文件的读者不得让写者失败。
    /// 心跳与 endpoint.json 就是这种"反复覆盖同一个文件、同时有人在读"的形态。
    /// <para>
    /// 读者每轮扫描后让出时间片（同样的退避理由见上一条用例），写者等读者扫完首轮才开始覆盖，
    /// 保证 100 轮覆盖确实发生在"有人持有读句柄"的窗口里。
    /// </para>
    /// </summary>
    [Fact]
    public void WriteAtomic_读者持有句柄时覆盖同一路径_写入不抛异常()
    {
        string directory = Path.Combine(Root, "atomic-probe");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "payload.json");

        string json = new CourierJsonObject()
            .AddString("kind", "atomic-probe")
            .AddString("payload", new string('x', PayloadLength))
            .Build();
        CourierIO.WriteAtomic(path, json, true);

        string[] singlePath = new string[] { path };
        string? readerFailure;

        using (var reader = new ConcurrentFileReader(() => singlePath, text => InspectAtomicPayload(text)))
        {
            // 读者先读到基线文件再开始覆盖，保证 100 轮覆盖确实落在"有人持有读句柄"的窗口里。
            Assert.True(reader.WaitUntilReady(TimeSpan.FromSeconds(10)), "读者没有在 10 秒内读到基线文件");

            for (int round = 0; round < 100; round++)
            {
                // 写者不得因为读者正持有该文件的读句柄而失败（SPEC §3.3/§3.4）。
                CourierIO.WriteAtomic(path, json, true);
            }

            readerFailure = reader.FirstFailure;
        }

        Assert.Null(readerFailure);
    }

    /// <summary>SPEC §3.3：覆盖写必须每次都产出完整文件，不得出现新旧内容拼接。</summary>
    [Fact]
    public void WriteAtomic_覆盖同一路径_每次读到的都是本次写入的完整内容()
    {
        string directory = Path.Combine(Root, "atomic-probe");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "payload.json");

        for (int round = 0; round < 20; round++)
        {
            string json = new CourierJsonObject()
                .AddNumber("round", round)
                .AddString("payload", new string((char)('a' + (round % 26)), PayloadLength))
                .Build();
            CourierIO.WriteAtomic(path, json, true);

            Dictionary<string, object?> document = ReadJsonObject(path);
            Assert.Equal((long)round, CourierJson.GetInteger(document, "round"));

            string? readPayload = CourierJson.GetString(document, "payload");
            Assert.NotNull(readPayload);
            Assert.Equal(PayloadLength, readPayload!.Length);
        }

        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    /// <summary>SPEC §3.3：不覆盖语义。目标已存在时 WriteAtomic(overwrite:false) 必须失败而不是静默覆盖。</summary>
    [Fact]
    public void WriteAtomic_目标已存在且不允许覆盖_抛出IOException()
    {
        string directory = Path.Combine(Root, "atomic-probe");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "command.json");

        CourierIO.WriteAtomic(path, "{\"v\":1}", false);
        Assert.Throws<IOException>(() => CourierIO.WriteAtomic(path, "{\"v\":2}", false));
        Assert.Equal("{\"v\":1}", ReadText(path));
    }

    /// <summary>SPEC §16：同一条命令无论 Tick 多少次都只执行一次，只产生一个终态证据。</summary>
    [Fact]
    public void Tick_同一命令重复Tick_只执行一次且只产生一个终态()
    {
        using CourierNode node = OpenNode("worker");
        int calls = 0;
        node.Handle("document", "open", "打开文档", handlerPayload =>
        {
            Interlocked.Increment(ref calls);
            return "opened:" + handlerPayload;
        });

        string requestId = DeliverCommand("worker", "document", "open", "main.doc");

        // 反复驱动泵：无论 Tick 多少轮，同一条命令只被启动一次、只产生一个终态（SPEC §16.1）。
        node.WaitForIdle(TimeSpan.FromSeconds(5));
        node.Tick();
        node.Tick();

        Assert.Equal(1, calls);

        IReadOnlyList<string> results = RequestIds(ResultsDirectory("worker"));
        Assert.Single(results);
        Assert.Equal(requestId, results[0]);

        CourierResponse? response = ReadResponse("worker", requestId);
        Assert.NotNull(response);
        Assert.Equal(CourierProtocol.STATUS_OK, response.Status);
        Assert.Equal("opened:main.doc", response.Payload);
        Assert.Null(response.Reason);

        Assert.False(File.Exists(CommandsPath("worker", requestId)));
        AssertArchivedCommand("worker", requestId);
    }
}
