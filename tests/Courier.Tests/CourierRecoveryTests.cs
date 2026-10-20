using System;
using System.IO;
using System.Threading;
using Xunit;

namespace Courier.Tests;

/// <summary>
/// 崩溃恢复（SPEC §8、§10、§13 的 Process 分支）。
/// </summary>
public sealed class CourierRecoveryTests : TestRoot
{
    /// <summary>SPEC §8：claim 未过期说明别处正在处理，本轮跳过且不得触碰 claim。</summary>
    [Fact]
    public void Tick_claim未过期_不执行且保留claim()
    {
        using CourierNode node = OpenNode("worker");
        int calls = 0;
        node.Handle("document", "open", "打开文档", handlerPayload =>
        {
            Interlocked.Increment(ref calls);
            return "opened";
        });

        string requestId = DeliverCommand("worker", "document", "open", "main.doc");
        WriteFreshClaimFile("worker", requestId, 1);

        node.Tick();

        Assert.Equal(0, calls);
        CourierClaim? claim = ReadClaim("worker", requestId);
        Assert.NotNull(claim);
        Assert.Equal(1, claim.Attempts);
        Assert.Null(ReadResponse("worker", requestId));
        Assert.True(File.Exists(CommandsPath("worker", requestId)));
    }

    /// <summary>SPEC §13 阶段 3：claim 已过期时重新认领，attempts 必须递增（SPEC §8：重试计数只在 claim 里）。</summary>
    [Fact]
    public void Tick_claim已过期_重新认领且attempts递增()
    {
        using CourierNode node = OpenNode("worker");
        string requestId = DeliverCommand("worker", "document", "open", "main.doc");

        int calls = 0;
        int observedAttempts = -1;
        node.Handle("document", "open", "打开文档", handlerPayload =>
        {
            Interlocked.Increment(ref calls);
            CourierClaim? liveClaim = ReadClaim("worker", requestId);
            if (liveClaim != null) observedAttempts = liveClaim.Attempts;
            return "opened";
        });

        WriteExpiredClaimFile("worker", requestId, 1);

        // SPEC §13.1：Tick 只负责启动，handler 在线程池上跑，所以必须等到空闲才看得到终态。
        node.WaitForIdle(TimeSpan.FromSeconds(5));

        Assert.Equal(1, calls);
        Assert.Equal(2, observedAttempts);
        Assert.Null(ReadClaim("worker", requestId));
        CourierResponse? response = ReadResponse("worker", requestId);
        Assert.NotNull(response);
        Assert.Equal(CourierProtocol.StatusOk, response.Status);
        AssertArchivedCommand("worker", requestId);
    }

    /// <summary>SPEC §10 第 3 条：attempts 超过上限后进 deadletter，同时归档命令、删除 claim。</summary>
    [Fact]
    public void Tick_attempts已达上限_进deadletter并归档命令()
    {
        using CourierNode node = OpenNode("worker");
        int calls = 0;
        node.Handle("document", "open", "打开文档", handlerPayload =>
        {
            Interlocked.Increment(ref calls);
            return "opened";
        });

        string requestId = DeliverCommand("worker", "document", "open", "main.doc");
        WriteExpiredClaimFile("worker", requestId, CourierProtocol.MaxAttempts);

        node.Tick();

        Assert.Equal(0, calls);

        CourierDeadletter? deadletter = ReadDeadletter("worker", requestId);
        Assert.NotNull(deadletter);
        Assert.Equal(requestId, deadletter.RequestId);
        AssertReasonInSpecTable(deadletter.Reason);
        Assert.Equal(CourierProtocol.MaxAttempts, deadletter.Attempts);

        Assert.Null(ReadClaim("worker", requestId));
        Assert.False(File.Exists(CommandsPath("worker", requestId)));
        AssertArchivedCommand("worker", requestId);
    }

    /// <summary>SPEC §13 阶段 1：results 已存在时幂等跳过，绝不重跑 handler，并清掉命令与 claim。</summary>
    [Fact]
    public void Tick_results已存在_跳过执行并归档命令()
    {
        using CourierNode node = OpenNode("worker");
        int calls = 0;
        node.Handle("document", "open", "打开文档", handlerPayload =>
        {
            Interlocked.Increment(ref calls);
            return "should-not-run";
        });

        string requestId = DeliverCommand("worker", "document", "open", "main.doc");
        WriteResultFile("worker", requestId, CourierProtocol.StatusOk, "cached", null);
        WriteFreshClaimFile("worker", requestId, 1);

        node.Tick();

        Assert.Equal(0, calls);

        CourierResponse? response = ReadResponse("worker", requestId);
        Assert.NotNull(response);
        Assert.Equal(CourierProtocol.StatusOk, response.Status);
        Assert.Equal("cached", response.Payload);

        Assert.Null(ReadClaim("worker", requestId));
        Assert.False(File.Exists(CommandsPath("worker", requestId)));
        AssertArchivedCommand("worker", requestId);
    }
}
