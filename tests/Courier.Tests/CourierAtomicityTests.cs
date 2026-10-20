using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Courier.Tests;

/// <summary>
/// 原子写与幂等（SPEC §3.3 与 §16）。
/// </summary>
public sealed class CourierAtomicityTests : TestRoot
{
    private const int PayloadLength = 200 * 1024;

    /// <summary>SPEC §3.3：读者要么看不到目标文件，要么看到一个完整可解析的文件，绝无中间态。</summary>
    [Fact]
    public async Task WriteAtomic_并发读写_读者永不见半写文件()
    {
        string directory = Path.Combine(Root, "atomic-probe");
        Directory.CreateDirectory(directory);

        const int Rounds = 200;
        string payload = new string('x', PayloadLength);

        var failures = new List<string>();
        int successfulReads = 0;
        using var stop = new CancellationTokenSource();

        Task[] readers = new Task[2];
        for (int index = 0; index < readers.Length; index++)
        {
            readers[index] = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    string[] files;
                    try
                    {
                        files = Directory.GetFiles(directory, "*.json");
                    }
                    catch (IOException)
                    {
                        continue;
                    }

                    for (int file = 0; file < files.Length; file++)
                    {
                        string? text;
                        if (!CourierIO.TryReadAllText(files[file], out text)) continue;
                        Interlocked.Increment(ref successfulReads);

                        Dictionary<string, object?>? document;
                        if (!CourierJson.TryParseObject(text, out document))
                        {
                            lock (failures) failures.Add("读到了无法解析的半写文件 " + files[file] + "，长度 " + text.Length);
                            return;
                        }

                        string? readPayload = CourierJson.GetString(document, "payload");
                        if (readPayload == null || readPayload.Length != PayloadLength)
                        {
                            lock (failures) failures.Add("读到的 payload 不完整: " + files[file]);
                            return;
                        }
                    }
                }
            });
        }

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

        stop.Cancel();
        await Task.WhenAll(readers);

        Assert.Empty(failures);
        Assert.True(successfulReads > 0, "读者一次都没读到文件，并发窗口没有被覆盖");
        Assert.Equal(Rounds, Directory.GetFiles(directory, "*.json").Length);

        // 临时文件不得残留（SPEC §3.3 的 rename 必须把临时名搬走）。
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    /// <summary>
    /// SPEC §3.3 + §3.4：覆盖写同一路径时，正在读该文件的读者不得让写者失败。
    /// 心跳与 endpoint.json 就是这种"反复覆盖同一个文件、同时有人在读"的形态。
    /// </summary>
    [Fact]
    public async Task WriteAtomic_读者持有句柄时覆盖同一路径_写入不抛异常()
    {
        string directory = Path.Combine(Root, "atomic-probe");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "payload.json");

        string json = new CourierJsonObject()
            .AddString("kind", "atomic-probe")
            .AddString("payload", new string('x', PayloadLength))
            .Build();
        CourierIO.WriteAtomic(path, json, true);

        using var stop = new CancellationTokenSource();
        var readerFailures = new List<string>();

        Task reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                string? text;
                if (!CourierIO.TryReadAllText(path, out text)) continue;

                Dictionary<string, object?>? document;
                if (!CourierJson.TryParseObject(text, out document))
                {
                    lock (readerFailures) readerFailures.Add("读到了无法解析的半写文件");
                    return;
                }
            }
        });

        var writerFailures = new List<string>();
        for (int round = 0; round < 100; round++)
        {
            try
            {
                CourierIO.WriteAtomic(path, json, true);
            }
            catch (Exception exception)
            {
                lock (writerFailures) writerFailures.Add(exception.GetType().Name + ": " + exception.Message);
            }
        }

        stop.Cancel();
        await reader;

        Assert.Empty(readerFailures);
        Assert.Empty(writerFailures);
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
        Assert.Equal(CourierProtocol.StatusOk, response.Status);
        Assert.Equal("opened:main.doc", response.Payload);
        Assert.Null(response.Reason);

        Assert.False(File.Exists(CommandsPath("worker", requestId)));
        AssertArchivedCommand("worker", requestId);
    }
}
