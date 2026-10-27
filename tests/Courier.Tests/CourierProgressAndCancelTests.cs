using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Courier.Tests;

/// <summary>
/// 进度与取消的对外入口（SPEC §13.2、§13.1）。
/// <para>
/// 这两条能力此前只有服务侧的"读"与"删"，没有对应的写入口：端点会在 endpoint.json 里自述
/// <c>cancels</c>/<c>progress</c> 能力，却没有任何 API 能把一条取消或一条进度写出去。
/// 这里锁住三件事：写入文件的形状符合规范、终态之后不再写、取消真的能到达在途 handler。
/// </para>
/// </summary>
public sealed class CourierProgressAndCancelTests : TestRoot
{
    /// <summary>SPEC §13.2：进度是服务者覆盖写的单份快照，字段与规范示例一致。</summary>
    [Fact]
    public void ReportProgress_写出规范形状的快照()
    {
        using CourierNode node = OpenNode("worker");

        string requestId = DeliverCommand("worker", "document", "export", "main.doc");
        node.ReportProgress(requestId, 42, "导出第 42/100 页");

        Dictionary<string, object?> document = ReadJsonObject(ProgressPath("worker", requestId));
        Assert.Equal(requestId, CourierJson.GetString(document, "requestId"));
        Assert.Equal((long?)42, CourierJson.GetInteger(document, "percent"));
        Assert.Equal("导出第 42/100 页", CourierJson.GetString(document, "message"));
        Assert.NotNull(CourierJson.GetString(document, "updatedAtUtc"));

        // 只保留最近一次快照：再报一次就整份覆盖，不是追加一行。
        node.ReportProgress(requestId, 80, null);
        document = ReadJsonObject(ProgressPath("worker", requestId));
        Assert.Equal((long?)80, CourierJson.GetInteger(document, "percent"));
        // message 为 null 时字段整个缺席（MUST NOT 写 null）。
        Assert.False(document.ContainsKey("message"));
        Assert.Single(RequestIds(ProgressDirectory("worker")));
    }

    /// <summary>SPEC §13.2：终态产生后 progress MUST 删除；终态之后补报进度 MUST NOT 再留下文件。</summary>
    [Fact]
    public void ReportProgress_终态之后不再写且被清理()
    {
        using CourierNode node = OpenNode("worker");
        node.Handle("document", "export", "导出", payload => "exported");

        string requestId = DeliverCommand("worker", "document", "export", "main.doc");
        node.ReportProgress(requestId, 10, "starting");

        node.WaitForIdle(TimeSpan.FromSeconds(5));

        // 命令跑完：results 有终态，progress 已被 CleanProgress 清掉（§13.2 的 MUST）。
        Assert.NotNull(ReadResponse("worker", requestId));
        Assert.False(File.Exists(ProgressPath("worker", requestId)));

        // 终态之后补报一条：MUST NOT 落盘，否则读者会看到一条永远清不掉的残留。
        node.ReportProgress(requestId, 99, "late");
        Assert.False(File.Exists(ProgressPath("worker", requestId)));
    }

    /// <summary>SPEC §4.2、§13.2：requestId 与 percent 都在写盘前校验，非法值绝不能拼进路径。</summary>
    [Fact]
    public void ReportProgress_非法参数抛异常且不落盘()
    {
        using CourierNode node = OpenNode("worker");
        string requestId = CourierProtocol.NewRequestId();

        Assert.Throws<ArgumentException>(() => node.ReportProgress("../escape", 10, null));
        Assert.Throws<ArgumentException>(() => node.ReportProgress("not-a-request-id", 10, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => node.ReportProgress(requestId, -1, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => node.ReportProgress(requestId, 101, null));

        // 目录由端点初始化时铺好（SPEC §2），这里断言的是"一个字节都没写进去"。
        Assert.Empty(RequestIds(ProgressDirectory("worker")));
    }

    /// <summary>SPEC §13.1：投递取消写的是 cancels/&lt;requestId&gt;.json，三个字段齐全。</summary>
    [Fact]
    public void RequestCancel_写出规范形状的取消文件()
    {
        using CourierNode caller = OpenNode("caller");
        using CourierNode target = OpenNode("worker");
        string requestId = CourierProtocol.NewRequestId();

        Assert.True(caller.RequestCancel("worker", requestId, "user-aborted"));

        Dictionary<string, object?> document = ReadJsonObject(CancelsPath("worker", requestId));
        Assert.Equal(requestId, CourierJson.GetString(document, "requestId"));
        Assert.Equal("user-aborted", CourierJson.GetString(document, "reason"));
        Assert.NotNull(CourierJson.GetString(document, "requestedAtUtc"));

        // reason 为 null 时落成默认值，字段始终存在（示例里它是必有的那一格）。
        string second = CourierProtocol.NewRequestId();
        Assert.True(caller.RequestCancel("worker", second, null));
        Assert.Equal("requested", CourierJson.GetString(ReadJsonObject(CancelsPath("worker", second)), "reason"));

        // 重复取消是同一份意图：覆盖写，不在 cancels/ 里堆第二份。
        Assert.True(caller.RequestCancel("worker", requestId, "again"));
        Assert.Equal(2, RequestIds(CancelsDirectory("worker")).Count);
    }

    /// <summary>SPEC §13.1：取消写到不存在的端点时返回 false，且不在磁盘上留下任何东西。</summary>
    [Fact]
    public void RequestCancel_端点不存在时返回false且不落盘()
    {
        using CourierNode caller = OpenNode("caller");
        string requestId = CourierProtocol.NewRequestId();

        Assert.False(caller.RequestCancel("ghost", requestId, "nope"));
        Assert.False(Directory.Exists(EndpointDirectory("ghost")));
        Assert.Throws<ArgumentException>(() => caller.RequestCancel("Bad Id", requestId, null));
        Assert.Throws<ArgumentException>(() => caller.RequestCancel("worker", "nope", null));
    }

    /// <summary>
    /// SPEC §13.1：投递方写下的取消 MUST 在下一轮 Tick 被读到，并在命令开始执行前把它拒掉。
    /// 这是"API 与坑位对得上"的端到端证据——不是手写文件，而是走公开入口。
    /// </summary>
    [Fact]
    public void RequestCancel_下一轮Tick拒掉尚未开始的命令()
    {
        using CourierNode caller = OpenNode("caller");
        using CourierNode worker = OpenNode("worker");
        int calls = 0;
        worker.Handle("document", "export", "导出", payload =>
        {
            calls++;
            return "should-not-run";
        });

        string requestId = DeliverCommand("worker", "document", "export", "main.doc");
        Assert.True(caller.RequestCancel("worker", requestId, "user-aborted"));

        worker.WaitForIdle(TimeSpan.FromSeconds(5));

        Assert.Equal(0, calls);
        CourierResponse? response = ReadResponse("worker", requestId);
        Assert.NotNull(response);
        Assert.Equal(CourierProtocol.STATUS_REJECTED, response.Status);
        Assert.Equal(CourierProtocol.REASON_CANCELLED_BEFORE_START, response.Reason);
        Assert.True(response.CancelRequested);
        AssertArchivedCommand("worker", requestId);
        // 取消文件已被服务者消费掉。
        Assert.Empty(RequestIds(CancelsDirectory("worker")));
    }

    /// <summary>
    /// SPEC §13.1：取消 MUST 能到达正在执行的 handler，而 handler 可以忽略它。
    /// 走公开入口（<see cref="CourierNode.RequestCancel"/>）而不是手写文件。
    /// </summary>
    [Fact]
    public async Task RequestCancel_到达正在执行的handler()
    {
        using CourierNode caller = OpenNode("caller");
        using CourierNode worker = OpenNode("worker");

        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        worker.HandleAsync("document", "export", "导出", async (payload, token) =>
        {
            entered.TrySetResult(true);
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!token.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                await Task.Delay(5).ConfigureAwait(false);
            }

            cancelled.TrySetResult(token.IsCancellationRequested);
            return "finished";
        });

        string requestId = DeliverCommand("worker", "document", "export", "main.doc");

        // 先跑起来：Tick 会在名额内启动 handler，然后立刻返回（泵模型，SPEC §16.1）。
        RunTickUntil(worker, () => entered.Task.IsCompleted, "handler 没有在预期时间内开始执行");

        Assert.True(caller.RequestCancel("worker", requestId, "user-aborted"));
        RunTickUntil(worker, () => cancelled.Task.IsCompleted, "取消没有通过公开入口送到在途 handler");

        Assert.True(await cancelled.Task);
        worker.WaitForIdle(TimeSpan.FromSeconds(10));

        CourierResponse? response = ReadResponse("worker", requestId);
        Assert.NotNull(response);
        // handler 响应了取消并正常返回：终态仍是 ok，cancelRequested 说明执行期间收到过取消。
        Assert.Equal(CourierProtocol.STATUS_OK, response.Status);
        Assert.True(response.CancelRequested);
    }

    /// <summary>反复 Tick 直到条件满足或超时。与 CourierCallTests 的私有辅助同一套语义。</summary>
    private static void RunTickUntil(CourierNode node, Func<bool> condition, string failure)
    {
        for (int attempt = 0; attempt < 300; attempt++)
        {
            node.Tick();
            if (condition()) return;
            Thread.Sleep(10);
        }

        Assert.True(condition(), failure);
    }
}
