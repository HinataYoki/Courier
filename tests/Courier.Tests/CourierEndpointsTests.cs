using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Courier.Tests;

/// <summary>
/// 端点目录与协议文件格式（SPEC §5、§6、§7、§12.1、§13）。
/// </summary>
public sealed class CourierEndpointsTests : TestRoot
{
    /// <summary>SPEC §5：endpoint.json 字段齐全，且 actions 按 (channel, action) 序数升序排列。</summary>
    [Fact]
    public void Open_注册若干能力_endpointJson字段齐全且actions按序数升序()
    {
        using CourierNode node = OpenNode("worker", "文档工作端");
        node.Handle("zeta", "b", "z-b", handlerPayload => "1");
        node.Handle("zeta", "a", "z-a", handlerPayload => "2");
        node.Handle("alpha", "z", "a-z", handlerPayload => "3");
        node.Handle("Alpha", "z", "大写 A", handlerPayload => "4");

        Dictionary<string, object?> document = ReadJsonObject(EndpointJsonPath("worker"));

        Assert.Equal("worker", CourierJson.GetString(document, "endpointId"));
        Assert.Equal("文档工作端", CourierJson.GetString(document, "description"));
        Assert.Equal((long)Environment.ProcessId, CourierJson.GetInteger(document, "processId"));
        Assert.Equal((long)CourierProtocol.ProtocolVersion, CourierJson.GetInteger(document, "protocolVersion"));
        Assert.NotNull(CourierJson.GetString(document, "startedAtUtc"));
        Assert.True(CourierProtocol.TryParseUtc(CourierJson.GetString(document, "startedAtUtc"), out DateTime startedAtUtc));
        Assert.True(startedAtUtc <= DateTime.UtcNow);

        List<object?>? rawActions = CourierJson.GetArray(document, "actions");
        Assert.NotNull(rawActions);
        Assert.Equal(4, rawActions.Count);

        var pairs = new List<string>();
        for (int index = 0; index < rawActions.Count; index++)
        {
            Dictionary<string, object?> action = Assert.IsType<Dictionary<string, object?>>(rawActions[index]);
            string? channel = CourierJson.GetString(action, "channel");
            string? name = CourierJson.GetString(action, "action");
            Assert.NotNull(channel);
            Assert.NotNull(name);
            Assert.NotNull(CourierJson.GetString(action, "summary"));
            pairs.Add(channel + "/" + name);
        }

        // 序数比较：'A'(0x41) < 'a'(0x61)，且 zeta/a 在 zeta/b 之前。
        Assert.Equal(new[] { "Alpha/z", "alpha/z", "zeta/a", "zeta/b" }, pairs);
    }

    /// <summary>SPEC §5：同一 (channel, action) 只允许注册一次，重复注册 MUST 直接报错。</summary>
    [Fact]
    public void Handle_重复注册同一能力_抛出ArgumentException()
    {
        using CourierNode node = OpenNode("worker");
        node.Handle("document", "open", "打开文档", handlerPayload => "ok");

        Assert.Throws<ArgumentException>(() => node.Handle("document", "open", "重复", handlerPayload => "ok"));
        Assert.Throws<ArgumentException>(() => node.HandleAsync("document", "open", "重复", handlerPayload => Task.FromResult("ok")));

        // 失败的注册不得污染能力表。
        Dictionary<string, object?> document = ReadJsonObject(EndpointJsonPath("worker"));
        List<object?>? rawActions = CourierJson.GetArray(document, "actions");
        Assert.NotNull(rawActions);
        Assert.Single(rawActions);
    }

    /// <summary>SPEC §6：Endpoints() 按 endpointId 序数升序，与目录创建顺序、心跳状态无关。</summary>
    [Fact]
    public void Endpoints_多个端点_按endpointId升序()
    {
        using CourierNode gamma = OpenNode("gamma");
        using CourierNode alpha = OpenNode("alpha");
        using CourierNode beta = OpenNode("beta");
        alpha.Handle("document", "open", "打开文档", handlerPayload => "ok");

        IReadOnlyList<CourierEndpointInfo> endpoints = alpha.Endpoints();

        Assert.Equal(3, endpoints.Count);
        Assert.Equal("alpha", endpoints[0].EndpointId);
        Assert.Equal("beta", endpoints[1].EndpointId);
        Assert.Equal("gamma", endpoints[2].EndpointId);

        Assert.Equal("打开文档", Assert.Single(endpoints[0].Actions).Summary);
        Assert.Empty(endpoints[1].Actions);
        Assert.Null(endpoints[0].Hot);
    }

    /// <summary>SPEC §6：State 完全由心跳年龄决定，三个阈值互不重叠（直接改文件内容，不 sleep）。</summary>
    [Fact]
    public void Endpoints_心跳年龄不同_State按阈值分为alive_stale_dead()
    {
        using CourierNode node = OpenNode("worker");
        DateTime now = DateTime.UtcNow;

        WriteHeartbeat("worker", now);
        Assert.Equal(CourierProtocol.StateAlive, CourierEndpointReader.ReadState(EndpointDirectory("worker")));
        Assert.Equal(CourierProtocol.StateAlive, Assert.Single(node.Endpoints()).State);

        WriteHeartbeat("worker", now - TimeSpan.FromSeconds(10));
        Assert.Equal(CourierProtocol.StateStale, CourierEndpointReader.ReadState(EndpointDirectory("worker")));
        Assert.Equal(CourierProtocol.StateStale, Assert.Single(node.Endpoints()).State);

        WriteHeartbeat("worker", now - TimeSpan.FromSeconds(60));
        Assert.Equal(CourierProtocol.StateDead, CourierEndpointReader.ReadState(EndpointDirectory("worker")));
        Assert.Equal(CourierProtocol.StateDead, Assert.Single(node.Endpoints()).State);
    }

    /// <summary>SPEC §6：心跳 MUST 覆盖写，反复 Tick 后文件里仍只有一个时间戳。</summary>
    [Fact]
    public void Tick_连续多次_心跳始终只有一个时间戳且不追加()
    {
        using CourierNode node = OpenNode("worker");

        node.Tick();
        long firstLength = new FileInfo(HeartbeatPath("worker")).Length;
        node.Tick();
        node.Tick();

        string text = ReadText(HeartbeatPath("worker"));
        Assert.Equal(firstLength, text.Length);

        // 一个时间戳里只出现一个 'T'（日期与时间的分隔符）；追加会让它变成两个。
        int separators = 0;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == 'T') separators++;
        }

        Assert.Equal(1, separators);
        Assert.DoesNotContain(Environment.NewLine, text, StringComparison.Ordinal);
    }

    /// <summary>SPEC §4.4：protocolVersion 不匹配时投递方本地失败，命令一个字节都不写。</summary>
    [Fact]
    public void Call_protocolVersion不匹配_返回rejected_invalid_command且不落盘()
    {
        using CourierNode server = OpenNode("server");
        using CourierNode caller = OpenNode("caller");

        string endpointJson = new CourierJsonObject()
            .AddString("endpointId", "server")
            .AddString("description", "未来版本端点")
            .AddNumber("processId", Environment.ProcessId)
            .AddString("startedAtUtc", CourierProtocol.FormatUtc(DateTime.UtcNow))
            .AddNumber("protocolVersion", 2)
            .AddRaw("actions", "[]")
            .Build();
        CourierIO.WriteAtomic(EndpointJsonPath("server"), endpointJson, true);

        CourierResponse response = caller.Call("server", "document", "open", "payload", TimeSpan.FromMilliseconds(200));

        Assert.Equal(CourierProtocol.StatusRejected, response.Status);
        Assert.Equal(CourierProtocol.ReasonInvalidCommand, response.Reason);
        AssertReasonInSpecTable(response.Reason);
        Assert.Empty(RequestIds(CommandsDirectory("server")));
        Assert.Empty(RequestIds(ResultsDirectory("server")));
    }

    /// <summary>SPEC §7：requestId 必须等于文件名，不一致时进 deadletter 而不是执行。</summary>
    [Fact]
    public void Tick_requestId与文件名不一致_进deadletter()
    {
        using CourierNode node = OpenNode("worker");
        int calls = 0;
        node.Handle("document", "open", "打开文档", handlerPayload =>
        {
            calls++;
            return "should-not-run";
        });

        string fileName = CourierProtocol.NewRequestId();
        string otherId = CourierProtocol.NewRequestId();
        DateTime issuedAtUtc = DateTime.UtcNow;
        string json = CourierCommand.Create(
            otherId, "document", "open", "main.doc", "test-caller", issuedAtUtc, issuedAtUtc + TimeSpan.FromSeconds(30)).ToJson();
        WriteCommandFile("worker", fileName, json);

        node.Tick();

        Assert.Equal(0, calls);
        Assert.Null(ReadResponse("worker", fileName));

        CourierDeadletter? deadletter = ReadDeadletter("worker", fileName);
        Assert.NotNull(deadletter);
        Assert.Equal(fileName, deadletter.RequestId);
        Assert.Equal(CourierProtocol.ReasonInvalidCommand, deadletter.Reason);
        Assert.False(File.Exists(CommandsPath("worker", fileName)));
        AssertArchivedCommand("worker", fileName);
    }

    /// <summary>SPEC §7：payload 必须是不透明字符串，写成数字属于解析失败，进 deadletter。</summary>
    [Fact]
    public void Tick_payload不是字符串_进deadletter()
    {
        using CourierNode node = OpenNode("worker");
        int calls = 0;
        node.Handle("document", "open", "打开文档", handlerPayload =>
        {
            calls++;
            return "should-not-run";
        });

        string requestId = CourierProtocol.NewRequestId();
        DateTime now = DateTime.UtcNow;
        string json = new CourierJsonObject()
            .AddString("requestId", requestId)
            .AddString("channel", "document")
            .AddString("action", "open")
            .AddNumber("payload", 42)
            .AddString("issuedAtUtc", CourierProtocol.FormatUtc(now))
            .AddString("deadlineUtc", CourierProtocol.FormatUtc(now + TimeSpan.FromSeconds(30)))
            .AddString("issuedBy", "test-caller")
            .Build();
        WriteCommandFile("worker", requestId, json);

        node.Tick();

        Assert.Equal(0, calls);
        Assert.Null(ReadResponse("worker", requestId));

        CourierDeadletter? deadletter = ReadDeadletter("worker", requestId);
        Assert.NotNull(deadletter);
        Assert.Equal(CourierProtocol.ReasonInvalidCommand, deadletter.Reason);
        AssertArchivedCommand("worker", requestId);
    }

    /// <summary>SPEC §13：单轮 Tick 最多处理 MAX_TICK_BATCH 条命令，剩下的留给下一轮。</summary>
    [Fact]
    public void Tick_命令超过批量上限_每轮最多处理16条()
    {
        using CourierNode node = OpenNode("worker");
        int calls = 0;
        node.Handle("document", "open", "打开文档", handlerPayload =>
        {
            calls++;
            return "ok";
        });

        const int total = 20;
        for (int index = 0; index < total; index++)
        {
            DeliverCommand("worker", "document", "open", "doc-" + index);
        }

        node.Tick();

        Assert.Equal(CourierProtocol.MaxTickBatch, calls);
        Assert.Equal(CourierProtocol.MaxTickBatch, RequestIds(ResultsDirectory("worker")).Count);
        Assert.Equal(total - CourierProtocol.MaxTickBatch, RequestIds(CommandsDirectory("worker")).Count);

        node.Tick();

        Assert.Equal(total, calls);
        Assert.Equal(total, RequestIds(ResultsDirectory("worker")).Count);
        Assert.Empty(RequestIds(CommandsDirectory("worker")));
    }

    /// <summary>SPEC §12.1：归档路径固定为 archive/&lt;yyyy-MM-dd&gt;/&lt;endpointId&gt;/commands/&lt;requestId&gt;.json，日期取处理时刻。</summary>
    [Fact]
    public void Tick_命令处理完成_归档到按UTC日期分层的路径()
    {
        using CourierNode node = OpenNode("worker");
        node.Handle("document", "open", "打开文档", handlerPayload => "ok");

        string requestId = DeliverCommand("worker", "document", "open", "main.doc");
        DateTime before = DateTime.UtcNow;
        node.Tick();
        DateTime after = DateTime.UtcNow;

        string archived = AssertArchivedCommand("worker", requestId);
        Assert.False(File.Exists(CommandsPath("worker", requestId)));

        string[] segments = Path.GetRelativePath(Root, archived)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, segments.Length);
        Assert.Equal("archive", segments[0]);
        Assert.Equal("worker", segments[2]);
        Assert.Equal("commands", segments[3]);
        Assert.Equal(requestId + ".json", segments[4]);

        string expectedDate = before.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        string alternateDate = after.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains(segments[1], new[] { expectedDate, alternateDate });

        // 归档下来的命令仍是完整可解析的协议文件。
        CourierCommand? archivedCommand = CourierCommand.TryParse(ReadText(archived), requestId);
        Assert.NotNull(archivedCommand);
        Assert.Equal(requestId, archivedCommand.RequestId);
        Assert.Equal("document", archivedCommand.Channel);
        Assert.Equal("main.doc", archivedCommand.Payload);
    }

    /// <summary>
    /// SPEC §6：心跳是"单个 UTC 时间戳字符串"，无 JSON——即裸时间戳，不带 JSON 引号、不带换行。
    /// 读取端是否容忍带引号是另一条用例，这里只锁死写出侧的线格式。
    /// </summary>
    [Fact]
    public void Tick_写心跳_内容为裸UTC时间戳字符串()
    {
        using CourierNode node = OpenNode("worker");

        node.Tick();

        string text = ReadText(HeartbeatPath("worker"));
        Assert.DoesNotContain("\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.NewLine, text, StringComparison.Ordinal);

        Assert.True(CourierProtocol.TryParseUtc(text, out DateTime heartbeat), "心跳不是可解析的 UTC 时间戳: " + text);
        Assert.Equal(DateTimeKind.Utc, heartbeat.Kind);
        Assert.True(heartbeat <= DateTime.UtcNow);
        Assert.True(DateTime.UtcNow - heartbeat < CourierProtocol.HeartbeatAlive);
    }

    /// <summary>
    /// SPEC §6 只规定写出侧是裸时间戳，但读取侧对别的实现多写一对 JSON 引号必须容忍：
    /// 判活是纯只读操作，因为对方多写了引号就把活端点判成 dead，代价远大于多剥一次引号。
    /// </summary>
    [Fact]
    public void ReadHeartbeatUtc_心跳带JSON引号_仍能读回并判活()
    {
        using CourierNode node = OpenNode("worker");
        node.Tick();

        string quoted = "\"" + CourierProtocol.FormatUtc(DateTime.UtcNow) + "\"";
        WriteRawHeartbeat("worker", quoted);

        DateTime? heartbeat = CourierEndpointReader.ReadHeartbeatUtc(EndpointDirectory("worker"));
        Assert.NotNull(heartbeat);
        Assert.Equal(DateTimeKind.Utc, heartbeat.Value.Kind);
        Assert.True(DateTime.UtcNow - heartbeat.Value < CourierProtocol.HeartbeatAlive);
        Assert.Equal(CourierProtocol.StateAlive, CourierEndpointReader.ReadState(EndpointDirectory("worker")));
    }

    /// <summary>SPEC §6 + §18：服务者写出的心跳必须能被自己的读取端读回并判活（读写两侧对同一线格式达成一致）。</summary>
    [Fact]
    public void Tick_写心跳_读取端能读回并判活()
    {
        using CourierNode node = OpenNode("worker");

        node.Tick();

        DateTime? heartbeat = CourierEndpointReader.ReadHeartbeatUtc(EndpointDirectory("worker"));
        Assert.NotNull(heartbeat);
        Assert.Equal(DateTimeKind.Utc, heartbeat.Value.Kind);
        Assert.True(DateTime.UtcNow - heartbeat.Value < CourierProtocol.HeartbeatAlive);
        Assert.Equal(CourierProtocol.StateAlive, CourierEndpointReader.ReadState(EndpointDirectory("worker")));
        Assert.Equal(CourierProtocol.StateAlive, Assert.Single(node.Endpoints()).State);
    }

    /// <summary>SPEC §6：心跳读不到时活性为 dead，Endpoints() 不得因此抛异常。</summary>
    [Fact]
    public void Endpoints_心跳文件缺失_状态为dead()
    {
        using CourierNode node = OpenNode("worker");
        File.Delete(HeartbeatPath("worker"));

        Assert.Equal(CourierProtocol.StateDead, CourierEndpointReader.ReadState(EndpointDirectory("worker")));

        IReadOnlyList<CourierEndpointInfo> endpoints = node.Endpoints();
        Assert.Equal(CourierProtocol.StateDead, Assert.Single(endpoints).State);
    }
}
