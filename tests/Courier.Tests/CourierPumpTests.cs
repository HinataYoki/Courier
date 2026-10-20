using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Courier.Tests;

/// <summary>
/// 服务者循环是**非阻塞泵**（SPEC §13.1）：Tick 只启动与收割，不等 handler。
/// 这一组用例锁死泵的四条外部可观察行为——心跳不被 handler 拖死、取消能到达在途 handler、
/// 租约在长任务里持续续约、超时立即判负而不等 handler 返回。
/// </summary>
public sealed class CourierPumpTests : TestRoot
{
    /// <summary>
    /// SPEC §6：心跳的含义是「服务循环还活着」，不是「某个 handler 正在跑」。
    /// 一个跑了 6 秒的 handler（超过 HEARTBEAT_DEAD 的 15 秒之外还远，但远超 HEARTBEAT_ALIVE 5 秒）
    /// MUST NOT 让端点显示成 dead。
    /// </summary>
    [Fact]
    public void Tick_慢handler运行中_端点仍被判活()
    {
        using CourierNode node = OpenNode("worker");
        using var gate = new ManualResetEventSlim(false);
        node.Handle("document", "open", "打开文档", handlerPayload =>
        {
            gate.Wait(TimeSpan.FromSeconds(20));
            return "slow-done";
        });

        string requestId = DeliverCommand("worker", "document", "open", "main.doc");
        node.Tick();

        // handler 卡住的这 6 秒里，服务循环照常 Tick，心跳照常刷新。
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(6))
        {
            node.Tick();
            Thread.Sleep(50);
        }

        CourierEndpointInfo endpoint = Assert.Single(node.Endpoints());
        Assert.Equal(CourierProtocol.StateAlive, endpoint.State);
        Assert.True(endpoint.HeartbeatAgeMs < 5000, "心跳年龄 " + endpoint.HeartbeatAgeMs + " ms");

        // 放开 handler，命令仍要正常收尾——慢不等于丢。
        gate.Set();
        node.WaitForIdle(TimeSpan.FromSeconds(20));

        CourierResponse? response = ReadResponse("worker", requestId);
        Assert.NotNull(response);
        Assert.Equal(CourierProtocol.StatusOk, response.Status);
        Assert.Equal("slow-done", response.Payload);
    }

    /// <summary>
    /// SPEC §13.2 第 4 条：handler 超过 HANDLER_TIMEOUT（默认=命令剩余 deadline）时 MUST 立即写
    /// failed/handler-timeout，MUST NOT 等它返回——一个不响应取消的 handler 可能永远不返回。
    /// </summary>
    [Fact]
    public void Tick_handler超过deadline_立即写failed_handler_timeout且不等返回()
    {
        using CourierNode node = OpenNode("worker");
        using var gate = new ManualResetEventSlim(false);
        node.Handle("document", "open", "打开文档", handlerPayload =>
        {
            // 故意无视取消：MUST 仍然立刻判负，而不是陪着它等下去。
            gate.Wait(TimeSpan.FromSeconds(30));
            return "never-arrives";
        });

        // deadline 1.5 秒：命令一开始不 stale，跑到一半就超时。
        string requestId = DeliverCommand("worker", "document", "open", "main.doc", TimeSpan.FromSeconds(1.5));

        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            node.Tick();
            if (ReadResponse("worker", requestId) != null) break;
            Thread.Sleep(50);
        }

        CourierResponse? response = ReadResponse("worker", requestId);
        Assert.NotNull(response);
        Assert.Equal(CourierProtocol.StatusFailed, response.Status);
        Assert.Equal(CourierProtocol.ReasonHandlerTimeout, response.Reason);
        AssertReasonInSpecTable(response.Reason);
        Assert.Equal(string.Empty, response.Payload);
        AssertArchivedCommand("worker", requestId);
        Assert.Empty(RequestIds(ClaimsDirectory("worker")));

        // 判负发生在 1.5 秒后，而不是等 handler 自己跑完的 30 秒。
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "判负用了 " + watch.Elapsed.TotalSeconds + " 秒");

        gate.Set();
    }

    /// <summary>
    /// SPEC §8.1：租约是租约，不是「认领时刻 + 固定窗口」。长任务执行期间 MUST 续约，
    /// 否则一个正常的慢 handler 会把自己的命令判成「持有者已死」而被别人接管。
    /// </summary>
    [Fact]
    public void Tick_长任务执行中_租约被持续续约()
    {
        using CourierNode node = OpenNode("worker");
        using var gate = new ManualResetEventSlim(false);
        node.Handle("document", "open", "打开文档", handlerPayload =>
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            return "done";
        });

        string requestId = DeliverCommand("worker", "document", "open", "main.doc");
        node.Tick();

        CourierClaim? first = ReadClaim("worker", requestId);
        Assert.NotNull(first);

        // 跑够两个续约间隔（CLAIM_RENEW_INTERVAL 默认 2 秒），租约必须被往后推。
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            node.Tick();
            Thread.Sleep(50);
        }

        CourierClaim? renewed = ReadClaim("worker", requestId);
        Assert.NotNull(renewed);
        Assert.Equal(first.Attempts, renewed.Attempts);
        Assert.Equal(first.Owner, renewed.Owner);
        Assert.True(
            renewed.ExpiresAtUtc > first.ExpiresAtUtc,
            "租约没有续：" + CourierProtocol.FormatUtc(first.ExpiresAtUtc) + " -> " + CourierProtocol.FormatUtc(renewed.ExpiresAtUtc));

        gate.Set();
        node.WaitForIdle(TimeSpan.FromSeconds(20));
        Assert.Null(ReadClaim("worker", requestId));
    }

    /// <summary>
    /// SPEC §5：endpoint.json 在第一次 Tick 之前写完即冻结。运行期注册新能力等于让
    /// 「这个端点能做什么」随时间变化，投递方会拿到一个永远 rejected/unknown-action 的幽灵能力。
    /// </summary>
    [Fact]
    public void Handle_第一次Tick之后_能力表已冻结()
    {
        using CourierNode node = OpenNode("worker");
        node.Handle("document", "open", "打开文档", handlerPayload => "opened");

        node.Tick();

        Assert.Throws<InvalidOperationException>(
            () => node.Handle("document", "close", "关闭文档", handlerPayload => "closed"));
        Assert.Throws<InvalidOperationException>(
            () => node.HandleAsync("document", "close", "关闭文档", handlerPayload => Task.FromResult("closed")));

        // 被拒绝的注册不得污染能力表。
        CourierEndpointInfo endpoint = Assert.Single(node.Endpoints());
        Assert.Single(endpoint.Actions);
        Assert.Equal("open", endpoint.Actions[0].Action);
    }
}
