using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Courier.Tests;

/// <summary>
/// 语义区分（SPEC §9.1、§9.2、§15）：rejected 与 failed 不混，unreachable 不落盘。
/// </summary>
public sealed class CourierSemanticsTests : TestRoot
{
    /// <summary>SPEC §9.2：对端根本没启动时，unreachable 由调用方本地合成，一个字节都不落盘。</summary>
    [Fact]
    public void Call_端点不存在_返回unreachable且不落盘()
    {
        using CourierNode caller = OpenNode("caller");

        CourierResponse response = caller.Call("ghost", "document", "open", "payload", TimeSpan.FromMilliseconds(200));

        Assert.Equal(CourierProtocol.StatusUnreachable, response.Status);
        Assert.Null(response.Reason);
        Assert.False(response.IsOk);
        Assert.Equal(string.Empty, response.Payload);

        Assert.False(Directory.Exists(EndpointDirectory("ghost")));
        Assert.Empty(RequestIds(CommandsDirectory("caller")));
        Assert.Empty(RequestIds(ResultsDirectory("caller")));
    }

    /// <summary>SPEC §7：端点存在但没有注册该 (channel, action) 时，路由失败是 rejected/unknown-action。</summary>
    [Fact]
    public void Tick_未注册的action_返回rejected_unknown_action()
    {
        using CourierNode node = OpenNode("worker");
        node.Handle("document", "open", "打开文档", handlerPayload => "opened");

        string requestId = DeliverCommand("worker", "document", "close", "main.doc");

        node.Tick();

        CourierResponse? response = ReadResponse("worker", requestId);
        Assert.NotNull(response);
        Assert.Equal(CourierProtocol.StatusRejected, response.Status);
        Assert.Equal(CourierProtocol.ReasonUnknownAction, response.Reason);
        AssertReasonInSpecTable(response.Reason);
        Assert.False(response.IsOk);
        Assert.Empty(RequestIds(DeadletterDirectory("worker")));
        AssertArchivedCommand("worker", requestId);
    }

    /// <summary>SPEC §9.1：handler 抛异常是 failed/handler-error，异常 MUST NOT 穿透 Tick。</summary>
    [Fact]
    public void Tick_处理器抛异常_返回failed_handler_error()
    {
        using CourierNode node = OpenNode("worker");
        node.Handle("document", "open", "打开文档", handlerPayload => throw new InvalidOperationException("boom"));

        string requestId = DeliverCommand("worker", "document", "open", "main.doc");

        // Tick 不得把异常抛给宿主（SPEC §3.1）。
        node.Tick();

        CourierResponse? response = ReadResponse("worker", requestId);
        Assert.NotNull(response);
        Assert.Equal(CourierProtocol.StatusFailed, response.Status);
        Assert.Equal(CourierProtocol.ReasonHandlerError, response.Reason);
        AssertReasonInSpecTable(response.Reason);
        Assert.Equal(string.Empty, response.Payload);
        AssertArchivedCommand("worker", requestId);
        Assert.Empty(RequestIds(ClaimsDirectory("worker")));
    }

    /// <summary>SPEC §9.1：rejected 表示业务逻辑没跑，failed 表示跑了但失败，两者不得互相冒充。</summary>
    [Fact]
    public void Tick_拒绝与失败_落入不同的status且reason不串用()
    {
        using CourierNode node = OpenNode("worker");
        node.Handle("document", "boom", "总是失败", handlerPayload => throw new InvalidOperationException("boom"));

        string rejectedId = DeliverCommand("worker", "document", "missing", "x");
        string failedId = DeliverCommand("worker", "document", "boom", "x");

        node.Tick();

        CourierResponse? rejected = ReadResponse("worker", rejectedId);
        Assert.NotNull(rejected);
        Assert.Equal(CourierProtocol.StatusRejected, rejected.Status);
        Assert.Equal(CourierProtocol.ReasonUnknownAction, rejected.Reason);

        CourierResponse? failed = ReadResponse("worker", failedId);
        Assert.NotNull(failed);
        Assert.Equal(CourierProtocol.StatusFailed, failed.Status);
        Assert.Equal(CourierProtocol.ReasonHandlerError, failed.Reason);

        Assert.NotEqual(rejected.Status, failed.Status);
    }

    /// <summary>SPEC §15：ok 响应不带 reason，且 payload 原样透传。</summary>
    [Fact]
    public void Tick_处理器正常返回_写入ok且reason为null()
    {
        using CourierNode node = OpenNode("worker");
        node.Handle("document", "open", "打开文档", handlerPayload => "{\"echo\":\"" + handlerPayload + "\"}");

        string requestId = DeliverCommand("worker", "document", "open", "main.doc");

        node.Tick();

        CourierResponse? response = ReadResponse("worker", requestId);
        Assert.NotNull(response);
        Assert.Equal(requestId, response.RequestId);
        Assert.Equal(CourierProtocol.StatusOk, response.Status);
        Assert.Equal("{\"echo\":\"main.doc\"}", response.Payload);
        Assert.Null(response.Reason);
        Assert.True(response.IsOk);
        Assert.False(response.CancelRequested);
        Assert.True(response.CompletedAtUtc <= DateTime.UtcNow);
    }

    /// <summary>SPEC §14.2：对端不 Tick 时调用方必须超时返回 unreachable，而不是无限等待。</summary>
    [Fact]
    public void Call_对端不处理_超时返回unreachable()
    {
        using CourierNode server = OpenNode("server");
        using CourierNode caller = OpenNode("caller");
        server.Handle("document", "open", "打开文档", handlerPayload => "opened");

        CourierResponse response = caller.Call("server", "document", "open", "main.doc", TimeSpan.FromMilliseconds(300));

        Assert.Equal(CourierProtocol.StatusUnreachable, response.Status);
        Assert.Null(response.Reason);
        // 命令确实落盘了（对端只是没跑），但 results/ 与 deadletter/ 必须是空的。
        Assert.Single(RequestIds(CommandsDirectory("server")));
        Assert.Empty(RequestIds(ResultsDirectory("server")));
        Assert.Empty(RequestIds(DeadletterDirectory("server")));
    }

    /// <summary>SPEC §11.1：cancels/ 在命令执行前出现时，直接 rejected/cancelled-before-start 且不进 handler。</summary>
    [Fact]
    public void Tick_执行前收到取消_返回rejected_cancelled_before_start()
    {
        using CourierNode node = OpenNode("worker");
        int calls = 0;
        node.Handle("document", "open", "打开文档", handlerPayload =>
        {
            calls++;
            return "should-not-run";
        });

        string requestId = DeliverCommand("worker", "document", "open", "main.doc");
        WriteCancelFile("worker", requestId);

        node.Tick();

        Assert.Equal(0, calls);

        CourierResponse? response = ReadResponse("worker", requestId);
        Assert.NotNull(response);
        Assert.Equal(CourierProtocol.StatusRejected, response.Status);
        Assert.Equal(CourierProtocol.ReasonCancelledBeforeStart, response.Reason);
        AssertReasonInSpecTable(response.Reason);
        Assert.True(response.CancelRequested);
        AssertArchivedCommand("worker", requestId);
    }

    /// <summary>SPEC §11.1：handler 收到取消但不采纳时，响应仍是 ok，并带 cancelRequested=true。</summary>
    [Fact]
    public void Tick_处理器忽略取消_仍返回ok且带cancelRequested()
    {
        using CourierNode node = OpenNode("worker");

        string? requestId = null;
        bool cancelSeenByHandler = false;
        node.Handle("document", "open", "打开文档", handlerPayload =>
        {
            // "执行期间收到取消"只能由 handler 自己制造：写入 cancels/ 后重入一次 Tick。
            // 此时 mActiveCancels 已登记本 requestId（CourierNode.cs:511-516），
            // ProcessCancels 走 source.Cancel() 分支，不会写 cancelled-before-start；
            // 内层命令循环又因 claim 存在且未过期而直接返回，不会递归执行本命令。
            WriteCancelFile("worker", requestId!);
            node.Tick();
            cancelSeenByHandler = true;
            return "ignored-cancel";
        });

        requestId = DeliverCommand("worker", "document", "open", "main.doc");
        node.Tick();

        Assert.True(cancelSeenByHandler);

        // 取消文件已被 ProcessCancels 消费掉。
        Assert.Empty(RequestIds(CancelsDirectory("worker")));

        CourierResponse? response = ReadResponse("worker", requestId);
        Assert.NotNull(response);
        Assert.Equal(CourierProtocol.StatusOk, response.Status);
        Assert.Equal("ignored-cancel", response.Payload);
        Assert.Null(response.Reason);
        Assert.True(response.CancelRequested);
        Assert.True(response.IsOk);
        AssertArchivedCommand("worker", requestId);
        Assert.Empty(RequestIds(ClaimsDirectory("worker")));
    }
}
