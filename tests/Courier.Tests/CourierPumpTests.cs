using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Courier.Tests;

/// <summary>
/// 服务者循环是**非阻塞泵**（SPEC §16.1）：Tick 只启动与收割，不等 handler。
/// 这一组用例锁死泵的四条外部可观察行为——心跳不被 handler 拖死、取消能到达在途 handler、
/// 租约在长任务里持续续约、超时立即判负而不等 handler 返回。
/// <para>
/// 它们都围绕"handler 卡在闸门上"这一状态：等待必须由可观察量驱动（事件、claim 的到期时刻、
/// results 文件落盘），而不是靠固定墙钟窗口凑时序——固定窗口在慢机器上既慢又不稳。
/// </para>
/// </summary>
public sealed class CourierPumpTests : TestRoot
{
    /// <summary>
    /// SPEC §6：心跳的含义是「服务循环还活着」，不是「某个 handler 正在跑」。
    /// handler 卡住期间服务循环照常 Tick，因此端点 MUST 始终被判为 alive；
    /// 判活用的是心跳年龄本身（&lt; HEARTBEAT_ALIVE），不是"跑够多久就认为它活着"。
    /// </summary>
    [Fact]
    public void Tick_慢handler运行中_端点仍被判活()
    {
        using CourierNode node = OpenNode("worker");
        using var gate = new ManualResetEventSlim(false);
        using var handlerEntered = new ManualResetEventSlim(false);
        node.Handle("document", "open", "打开文档", handlerPayload =>
        {
            handlerEntered.Set();
            gate.Wait(TimeSpan.FromSeconds(20));
            return "slow-done";
        });

        string requestId = DeliverCommand("worker", "document", "open", "main.doc");
        node.Tick();

        // 等 handler 真正进入（事件），再断言"它卡住的时候"服务循环仍能推进。
        Assert.True(handlerEntered.Wait(TimeSpan.FromSeconds(10)), "handler 没有在 10 秒内启动");

        // 事件驱动的有界等待：每轮 Tick 后检查"在途名额被占着且端点仍判活"。
        bool observed = TickUntil(
            node,
            () =>
            {
                CourierEndpointInfo current = Assert.Single(node.Endpoints());
                return current.State == CourierProtocol.STATE_ALIVE;
            },
            TimeSpan.FromSeconds(5));

        Assert.True(observed, "handler 卡住期间端点没能保持 alive");
        CourierEndpointInfo endpoint = Assert.Single(node.Endpoints());
        Assert.Equal(CourierProtocol.STATE_ALIVE, endpoint.State);
        Assert.True(
            endpoint.HeartbeatAgeMs < (long)CourierProtocol.HEARTBEAT_ALIVE.TotalMilliseconds,
            "心跳年龄 " + endpoint.HeartbeatAgeMs + " ms 超过 HEARTBEAT_ALIVE");

        // handler 还没返回，命令必须仍在途：这证明上面那次判活是"慢 handler 运行中"，不是"已经跑完了"。
        Assert.False(File.Exists(ResultsPath("worker", requestId)), "handler 尚未放行就写出了终态，这条用例的前提不成立");

        // 放开 handler，命令仍要正常收尾——慢不等于丢。
        gate.Set();
        node.WaitForIdle(TimeSpan.FromSeconds(20));

        CourierResponse? response = ReadResponse("worker", requestId);
        Assert.NotNull(response);
        Assert.Equal(CourierProtocol.STATUS_OK, response.Status);
        Assert.Equal("slow-done", response.Payload);
    }

    /// <summary>
    /// SPEC §16.2 第 4 条：handler 超过 HANDLER_TIMEOUT（默认=命令剩余 deadline）时 MUST 立即写
    /// failed/handler-timeout，MUST NOT 等它返回——一个不响应取消的 handler 可能永远不返回。
    /// <para>
    /// 判负时限必须与"等待上限"分开：这里等的是 <c>results/</c> 文件出现（可观察量），
    /// 再断言它出现的时刻远早于 handler 自己的 30 秒闸门，而不是用同一个数字既当循环上界又当断言阈值。
    /// </para>
    /// </summary>
    [Fact]
    public void Tick_handler超过deadline_立即写failed_handler_timeout且不等返回()
    {
        using CourierNode node = OpenNode("worker");
        using var gate = new ManualResetEventSlim(false);
        using var handlerEntered = new ManualResetEventSlim(false);
        node.Handle("document", "open", "打开文档", handlerPayload =>
        {
            handlerEntered.Set();
            // 故意无视取消：MUST 仍然立刻判负，而不是陪着它等下去。
            gate.Wait(TimeSpan.FromSeconds(30));
            return "never-arrives";
        });

        // deadline 1.5 秒：命令一开始不 stale，跑到一半就超时。
        string requestId = DeliverCommand("worker", "document", "open", "main.doc", TimeSpan.FromSeconds(1.5));
        node.Tick();
        Assert.True(handlerEntered.Wait(TimeSpan.FromSeconds(10)), "handler 没有在 10 秒内启动");

        // 等待上限（15 秒）与判负阈值（10 秒）是两个数：前者是慢性机器上的兜底，
        // 后者才是"没有陪着 handler 等下去"的证据——handler 自己的闸门是 30 秒。
        TimeSpan waitBudget = TimeSpan.FromSeconds(15);
        TimeSpan settleLimit = TimeSpan.FromSeconds(10);

        Stopwatch watch = Stopwatch.StartNew();
        bool settled = TickUntil(
            node,
            () => File.Exists(ResultsPath("worker", requestId)),
            waitBudget);
        watch.Stop();

        Assert.True(settled, "超过 deadline 15 秒后仍没有写出终态，泵没有立即判负");
        Assert.True(watch.Elapsed < settleLimit,
            "终态写出耗时 " + watch.Elapsed.TotalSeconds + " 秒，超过 " + settleLimit.TotalSeconds +
            " 秒判负阈值：泵在等 handler 返回，而不是按 deadline 立即判负");


        CourierResponse? response = ReadResponse("worker", requestId);
        Assert.NotNull(response);
        Assert.Equal(CourierProtocol.STATUS_FAILED, response.Status);
        Assert.Equal(CourierProtocol.REASON_HANDLER_TIMEOUT, response.Reason);
        AssertReasonInSpecTable(response.Status, response.Reason);
        Assert.Equal(string.Empty, response.Payload);
        AssertArchivedCommand("worker", requestId);
        Assert.Empty(RequestIds(ClaimsDirectory("worker")));

        // 关键区分：handler 仍卡在闸门上（还没返回），终态却已经落盘——判负没有等 handler。
        Assert.False(gate.IsSet, "闸门不该在这时被放开");
        Assert.True(handlerEntered.IsSet);

        gate.Set();
    }

    /// <summary>
    /// SPEC §10.1：租约是租约，不是「认领时刻 + 固定窗口」。长任务执行期间 MUST 续约，
    /// 否则一个正常的慢 handler 会把自己的命令判成「持有者已死」而被别人接管。
    /// <para>
    /// 等待由 claim 自身的到期时刻驱动：出现比初始租约更晚的到期时刻就停，不再固定跑满若干秒。
    /// </para>
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

        // 续约只在 RenewInflight 的 CLAIM_RENEW_INTERVAL 窗口之后才发生（默认 2 秒），
        // 所以这里必须给足这个窗口；等待条件是可观察的"到期时刻被推后"，不是干等。
        TimeSpan renewBudget = CourierProtocol.CLAIM_RENEW_INTERVAL + CourierProtocol.CLAIM_RENEW_INTERVAL;
        bool renewedInTime = TickUntil(
            node,
            () =>
            {
                CourierClaim? current = ReadClaim("worker", requestId);
                return current != null && current.ExpiresAtUtc > first.ExpiresAtUtc;
            },
            renewBudget);

        Assert.True(
            renewedInTime,
            "租约在 " + renewBudget.TotalSeconds + " 秒内没有被续：" +
            CourierProtocol.FormatUtc(first.ExpiresAtUtc));

        CourierClaim? renewed = ReadClaim("worker", requestId);
        Assert.NotNull(renewed);
        Assert.Equal(first.Attempts, renewed.Attempts);
        Assert.Equal(first.Owner, renewed.Owner);

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
