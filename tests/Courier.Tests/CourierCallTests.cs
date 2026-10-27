using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Courier.Tests;

/// <summary>
/// handler 可见的调用上下文 <see cref="CourierCall"/>（SPEC §9）。
/// <para>
/// 上下文只把信封里已经解析出来的字段交给 handler，协议语义一条都没变：投递仍是
/// at-least-once，同一个 <c>requestId</c> 可能被执行不止一次。
/// </para>
/// </summary>
public sealed class CourierCallTests : TestRoot
{
    /// <summary>SPEC §9：handler 看到的 requestId 必须等于命令文件名，也就是调用方拿到的那个。</summary>
    [Fact]
    public async Task Handle_带上下文的处理器_requestId等于命令文件名()
    {
        using (CourierNode server = OpenNode("server"))
        using (CourierNode caller = OpenNode("caller"))
        {
            string? observed = null;
            server.Handle("document", "open", "打开文档", (CourierCall call) =>
            {
                observed = call.RequestId;
                return "ok";
            });

            CourierResponse response = await CallWithTickingAsync(
                server, caller, "server", "document", "open", "main.doc", TimeSpan.FromSeconds(10));

            Assert.Equal(CourierProtocol.STATUS_OK, response.Status);
            Assert.Equal(response.RequestId, observed);
            Assert.True(File.Exists(ResultsPath("server", observed!)));
        }
    }

    /// <summary>SPEC §9：信封字段原样交给 handler，一个不差。</summary>
    [Fact]
    public async Task Handle_带上下文的处理器_信封字段齐全()
    {
        using (CourierNode server = OpenNode("worker"))
        {
            CourierCall? captured = null;
            server.Handle("document", "open", "打开文档", (CourierCall call) =>
            {
                captured = call;
                return "ok";
            });

            DateTime before = DateTime.UtcNow;
            string requestId = DeliverCommand("worker", "document", "open", "main.doc", TimeSpan.FromSeconds(30), "qa-bot");

            for (int attempt = 0; attempt < 200 && captured == null; attempt++)
            {
                server.Tick();
                if (captured == null) await Task.Delay(10);
            }

            Assert.NotNull(captured);
            Assert.Equal(requestId, captured!.RequestId);
            Assert.Equal("worker", captured.EndpointId);
            Assert.Equal("document", captured.Channel);
            Assert.Equal("open", captured.Action);
            Assert.Equal("qa-bot", captured.IssuedBy);
            Assert.Equal("main.doc", captured.Payload);
            // 从来没有 claim 开始的第一轮，attempt 必须是 1（SPEC §10 的计数从 1 起）。
            Assert.Equal(1, captured.Attempt);
            Assert.True(captured.IssuedAtUtc >= before - TimeSpan.FromSeconds(5));
            Assert.True(captured.DeadlineUtc > captured.IssuedAtUtc);
            Assert.True(captured.ReceivedAtUtc >= captured.IssuedAtUtc);
        }
    }

    /// <summary>SPEC §10.1：接管过期租约时 attempt 变成 2，handler 由此知道副作用可能已经发生过一次。</summary>
    [Fact]
    public async Task Handle_接管过期租约_attempt递增到2()
    {
        using (CourierNode server = OpenNode("worker"))
        {
            var attempts = new List<int>();
            server.Handle("document", "open", "打开文档", (CourierCall call) =>
            {
                lock (attempts) attempts.Add(call.Attempt);
                return "ok";
            });

            string requestId = DeliverCommand("worker", "document", "open", "main.doc");
            WriteExpiredClaimFile("worker", requestId, 1);

            await RunTickUntilAsync(server, () => attempts.Count > 0);

            // handler 进去和终态落盘之间隔着一次 Tick 的收割步骤（SPEC §16 第 4 步）：
            // 只等 handler 进入就断言结果文件，等于假定"handler 一返回结果就写完"，那在泵模型下不成立。
            await RunTickUntilAsync(server, () => File.Exists(ResultsPath("worker", requestId)));

            Assert.Single(attempts);
            Assert.Equal(2, attempts[0]);
        }
    }

    /// <summary>SPEC §9：payload 为空时上下文里是空串，不是 null。</summary>
    [Fact]
    public async Task Handle_没有payload_上下文里是空串()
    {
        using (CourierNode server = OpenNode("worker"))
        using (CourierNode caller = OpenNode("caller"))
        {
            string? observed = "not-called";
            server.Handle("document", "open", "打开文档", (CourierCall call) =>
            {
                observed = call.Payload;
                return "ok";
            });

            CourierResponse response = await CallWithTickingAsync(
                server, caller, "worker", "document", "open", null, TimeSpan.FromSeconds(10));

            Assert.Equal(CourierProtocol.STATUS_OK, response.Status);
            Assert.Equal(string.Empty, observed);
        }
    }

    /// <summary>
    /// SPEC §9：投递是 at-least-once。同一条命令被重演执行两次时 requestId 不变，attempt 递增。
    /// <para>
    /// 断言的是"恰好两次"：等待条件取终态落盘（<c>results/&lt;requestId&gt;.json</c> 存在），
    /// 而不是 <c>observed.Count &gt;= 2</c>——后者与下面的 <c>== 2</c> 同义，会把多跑掩盖掉。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Handle_重复投递_同一requestId且attempt递增()
    {
        using (CourierNode server = OpenNode("worker"))
        {
            var observed = new List<string>();
            server.Handle("document", "open", "打开文档", (CourierCall call) =>
            {
                lock (observed) observed.Add(call.RequestId + "#" + call.Attempt);
                return "ok";
            });

            DateTime now = DateTime.UtcNow;
            string requestId = CourierProtocol.NewRequestId();
            CourierCommand command = CourierCommand.Create(
                requestId, "document", "open", "main.doc", "test-caller", now, now + TimeSpan.FromSeconds(30));
            WriteCommandFile("worker", requestId, command.ToJson());

            // 必须等到终态真的落盘，而不是只等 handler 进入：下一行要删的就是这个文件，
            // 提前删等于删了个空，随后收割写出的结果会把第二轮直接挡在"已完成"上。
            await RunTickUntilAsync(server, () => File.Exists(ResultsPath("worker", requestId)));

            // 重演"上一位持有者写完结果之前就崩了"：结果与命令都回到待处理状态，claim 是一条过期租约。
            File.Delete(ResultsPath("worker", requestId));
            WriteCommandFile("worker", requestId, command.ToJson());
            WriteExpiredClaimFile("worker", requestId, 1);

            // 等终态真的落盘，而不是等 observed 计数达标：终态之后多空转几轮，
            // 让 at-least-once 可能出现的第三次执行有机会暴露出来，再断言精确次数。
            await RunTickUntilAsync(server, () => File.Exists(ResultsPath("worker", requestId)));
            for (int extra = 0; extra < 5; extra++)
            {
                server.Tick();
                await Task.Delay(10);
            }

            int count;
            lock (observed) count = observed.Count;
            Assert.Equal(2, count);
            Assert.Equal(requestId + "#1", observed[0]);
            Assert.Equal(requestId + "#2", observed[1]);
        }
    }

    /// <summary>SPEC §9：旧式 handler 仍然只拿 payload，源码兼容不受带上下文重载影响。</summary>
    [Fact]
    public async Task 旧式重载_仍然只接收payload()
    {
        using (CourierNode server = OpenNode("server"))
        using (CourierNode caller = OpenNode("caller"))
        {
            string? observed = null;
            server.Handle("document", "open", "打开文档", payload =>
            {
                observed = payload;
                return "opened:" + payload;
            });
            server.HandleAsync("document", "read", "读取文档", async (payload, token) =>
            {
                await Task.Yield();
                token.ThrowIfCancellationRequested();
                return "read:" + payload;
            });

            CourierResponse first = await CallWithTickingAsync(
                server, caller, "server", "document", "open", "main.doc", TimeSpan.FromSeconds(10));
            CourierResponse second = await CallWithTickingAsync(
                server, caller, "server", "document", "read", "main.doc", TimeSpan.FromSeconds(10));

            Assert.Equal("opened:main.doc", first.Payload);
            Assert.Equal("main.doc", observed);
            Assert.Equal("read:main.doc", second.Payload);
        }
    }

    /// <summary>带上下文的四参重载同样受"能力表冻结"约束（SPEC §5）。</summary>
    [Fact]
    public void 带上下文的重载_Tick之后注册被拒绝()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            node.Tick();
            Assert.Throws<InvalidOperationException>(() => node.Handle("document", "open", "打开文档", (CourierCall call) => "ok"));
            Assert.Throws<InvalidOperationException>(() => node.HandleAsync("document", "open", "打开文档", (CourierCall call) => Task.FromResult("ok")));
        }
    }

    /// <summary>带上下文的重载对非法名字与重复注册的校验与旧式一致。</summary>
    [Fact]
    public void 带上下文的重载_校验与旧式一致()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            Assert.Throws<ArgumentException>(() => node.Handle("文档", "open", "说明", (CourierCall call) => "ok"));
            Assert.Throws<ArgumentException>(() => node.Handle("document", "op en", "说明", (CourierCall call) => "ok"));
            // 首尾点会落到当前目录与父目录上，名字校验必须拒掉。
            Assert.Throws<ArgumentException>(() => node.Handle(".hidden", "open", "说明", (CourierCall call) => "ok"));
            Assert.Throws<ArgumentException>(() => node.Handle("document", "trailing.", "说明", (CourierCall call) => "ok"));
            Assert.Throws<ArgumentNullException>(() => node.Handle("document", "open", "说明", (Func<CourierCall, string>)null!));

            node.Handle("document", "open", "说明", (CourierCall call) => "ok");
            Assert.Throws<ArgumentException>(() => node.Handle("document", "open", "说明", (CourierCall call) => "ok"));
        }
    }

    private static async Task RunTickUntilAsync(CourierNode node, Func<bool> condition)
    {
        for (int attempt = 0; attempt < 300; attempt++)
        {
            node.Tick();
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.True(condition(), "条件在 3 秒内没有满足");
    }
}