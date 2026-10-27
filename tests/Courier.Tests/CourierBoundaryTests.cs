using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Courier.Tests;

/// <summary>
/// 边界与拒绝（SPEC §4.1、§9.1、§9.2、§9.3）。
/// </summary>
public sealed class CourierBoundaryTests : TestRoot
{
    /// <summary>SPEC §9.1：取走时已过 deadline 的命令 MUST NOT 执行，落 rejected/stale 并归档。</summary>
    [Fact]
    public void Tick_命令超过deadline_写入rejected_stale()
    {
        using CourierNode node = OpenNode("worker");
        int calls = 0;
        node.Handle("document", "open", "打开文档", handlerPayload =>
        {
            calls++;
            return "should-not-run";
        });

        string requestId = DeliverCommand("worker", "document", "open", "main.doc", TimeSpan.FromSeconds(-30));

        // stale 判定发生在启动阶段，所以一轮 Tick 就够；这里仍走 WaitForIdle 以统一断言时机。
        node.WaitForIdle(TimeSpan.FromSeconds(5));

        Assert.Equal(0, calls);

        CourierResponse? response = ReadResponse("worker", requestId);
        Assert.NotNull(response);
        Assert.Equal(CourierProtocol.STATUS_REJECTED, response.Status);
        Assert.Equal(CourierProtocol.REASON_STALE, response.Reason);
        AssertReasonInSpecTable(response.Status, response.Reason);
        Assert.False(response.CancelRequested);

        Assert.False(File.Exists(CommandsPath("worker", requestId)));
        AssertArchivedCommand("worker", requestId);
        Assert.Empty(RequestIds(DeadletterDirectory("worker")));
    }

    /// <summary>SPEC §9.2：命令文件超过 MAX_COMMAND_FILE_BYTES 时 MUST 直接 deadletter，不解析、不执行。</summary>
    [Fact]
    public void Tick_命令文件超过上限_进deadletter()
    {
        using CourierNode node = OpenNode("worker");
        int calls = 0;
        node.Handle("document", "open", "打开文档", handlerPayload =>
        {
            calls++;
            return "should-not-run";
        });

        string requestId = CourierProtocol.NewRequestId();
        Directory.CreateDirectory(CommandsDirectory("worker"));
        // 故意绕过 CourierIO.WriteAtomic：这里要造的是"别家实现写坏的超限文件"。
        File.WriteAllText(CommandsPath("worker", requestId), new string('x', CourierProtocol.MAX_COMMAND_FILE_BYTES + 1));

        node.WaitForIdle(TimeSpan.FromSeconds(5));

        Assert.Equal(0, calls);
        Assert.Null(ReadResponse("worker", requestId));

        CourierDeadletter? deadletter = ReadDeadletter("worker", requestId);
        Assert.NotNull(deadletter);
        Assert.Equal(requestId, deadletter.RequestId);
        Assert.Equal(CourierProtocol.REASON_INVALID_COMMAND, deadletter.Reason);
        AssertDeadletterReasonInSpecTable(deadletter.Reason);
        Assert.Equal(1, deadletter.Attempts);

        Assert.False(File.Exists(CommandsPath("worker", requestId)));
        AssertArchivedCommand("worker", requestId);
    }

    /// <summary>SPEC §4.1：非法 endpointId MUST 拒绝启动，且不写任何文件。</summary>
    [Theory]
    [InlineData("App-A")]
    [InlineData("-a")]
    [InlineData("a_")]
    [InlineData("A")]
    [InlineData("app-a-")]
    [InlineData("app--")]
    [InlineData("端点")]
    [InlineData("app a")]
    public void Open_非法endpointId_抛出ArgumentException(string endpointId)
    {
        Assert.Throws<ArgumentException>(() => OpenNode(endpointId));
        Assert.False(Directory.Exists(EndpointsRoot));
    }

    /// <summary>SPEC §4.1：长度上限 64，65 个字符必须被拒绝。</summary>
    [Fact]
    public void Open_长度65的endpointId_抛出ArgumentException()
    {
        string endpointId = new string('a', 65);
        Assert.Throws<ArgumentException>(() => OpenNode(endpointId));
        Assert.False(Directory.Exists(EndpointsRoot));
    }

    /// <summary>SPEC §4.1：64 是合法上界（1 + 62 + 1），实现 MUST NOT 多拒一个字符。</summary>
    [Fact]
    public void Open_长度64的endpointId_接受()
    {
        string endpointId = new string('a', 64);
        using CourierNode node = OpenNode(endpointId);
        Assert.True(Directory.Exists(EndpointDirectory(endpointId)));
        Assert.True(File.Exists(EndpointJsonPath(endpointId)));
    }

    /// <summary>SPEC §4.1 / §4.3 / §17：非法调用参数属于编程错误，必须在投递前抛异常。</summary>
    [Theory]
    [InlineData("App-A", "document", "open")]
    [InlineData("ghost", "doc ument", "open")]
    [InlineData("ghost", "document", "op en")]
    [InlineData("ghost", "", "open")]
    public void Call_非法标识符_抛出ArgumentException(string endpointId, string channel, string action)
    {
        using CourierNode node = OpenNode("caller");
        Assert.Throws<ArgumentException>(() => node.Call(endpointId, channel, action, "payload", TimeSpan.FromSeconds(1)));
        Assert.Empty(RequestIds(CommandsDirectory("caller")));
    }

    /// <summary>SPEC §17：timeout 必须为正数，0 与负数都是编程错误。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Call_timeout非正数_抛出ArgumentException(int seconds)
    {
        using CourierNode node = OpenNode("caller");
        TimeSpan timeout = TimeSpan.FromSeconds(seconds);
        Assert.Throws<ArgumentException>(() => node.Call("ghost", "document", "open", "payload", timeout));
        await Assert.ThrowsAsync<ArgumentException>(() => node.CallAsync("ghost", "document", "open", "payload", timeout));
    }

    /// <summary>SPEC §9.3：payload 超上限时由投递方本地合成 rejected/invalid-command，一个命令文件都不写。</summary>
    [Fact]
    public void Call_payload超过上限_返回rejected_invalid_command且不落盘()
    {
        using CourierNode server = OpenNode("server");
        using CourierNode caller = OpenNode("caller");
        string payload = new string('x', CourierProtocol.MAX_COMMAND_FILE_BYTES);

        CourierResponse response = caller.Call("server", "document", "open", payload, TimeSpan.FromSeconds(1));

        Assert.Equal(CourierProtocol.STATUS_REJECTED, response.Status);
        Assert.Equal(CourierProtocol.REASON_INVALID_COMMAND, response.Reason);
        AssertReasonInSpecTable(response.Status, response.Reason);
        Assert.False(response.IsOk);
        Assert.Empty(RequestIds(CommandsDirectory("server")));
        Assert.Empty(RequestIds(ResultsDirectory("server")));
    }

    /// <summary>SPEC §9.3：转义会放大长度，payload 恰好等于上限时序列化后必然超限，仍须本地拒绝。</summary>
    [Fact]
    public void Call_payload接近上限且含转义字符_返回rejected_invalid_command()
    {
        using CourierNode server = OpenNode("server");
        using CourierNode caller = OpenNode("caller");
        string payload = new string('"', CourierProtocol.MAX_COMMAND_FILE_BYTES - 4096);

        CourierResponse response = caller.Call("server", "document", "open", payload, TimeSpan.FromSeconds(1));

        Assert.Equal(CourierProtocol.STATUS_REJECTED, response.Status);
        Assert.Equal(CourierProtocol.REASON_INVALID_COMMAND, response.Reason);
        Assert.Empty(RequestIds(CommandsDirectory("server")));
    }
}
