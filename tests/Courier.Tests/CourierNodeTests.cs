using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Courier.Tests;

/// <summary>
/// 节点生命周期与最小正常路径（SPEC §5、§16、§17）。
/// </summary>
public sealed class CourierNodeTests : TestRoot
{
    /// <summary>SPEC §2：Open 必须把端点目录与七个子目录一次性建好，并写出 endpoint.json 与 heartbeat。</summary>
    [Fact]
    public void Open_合法参数_创建协议目录与端点文件()
    {
        using CourierNode node = OpenNode("worker", "测试端点");

        Assert.True(Directory.Exists(EndpointDirectory("worker")));
        Assert.True(File.Exists(EndpointJsonPath("worker")));
        Assert.True(File.Exists(HeartbeatPath("worker")));
        Assert.True(Directory.Exists(CommandsDirectory("worker")));
        Assert.True(Directory.Exists(ClaimsDirectory("worker")));
        Assert.True(Directory.Exists(ResultsDirectory("worker")));
        Assert.True(Directory.Exists(DeadletterDirectory("worker")));
        Assert.True(Directory.Exists(CancelsDirectory("worker")));
        Assert.True(Directory.Exists(Path.Combine(EndpointDirectory("worker"), CourierProtocol.ProgressDirectory)));
    }

    /// <summary>SPEC §16：空队列的一轮 Tick 只写心跳，不得凭空产出 results 或 deadletter。</summary>
    [Fact]
    public void Tick_没有命令_不写任何结果文件()
    {
        using CourierNode node = OpenNode("worker");
        node.Handle("document", "open", "打开文档", handlerPayload => "opened");

        node.Tick();
        node.Tick();

        Assert.Empty(RequestIds(CommandsDirectory("worker")));
        Assert.Empty(RequestIds(ResultsDirectory("worker")));
        Assert.Empty(RequestIds(DeadletterDirectory("worker")));
        Assert.Empty(RequestIds(ClaimsDirectory("worker")));
    }

    /// <summary>Dispose 后所有公开入口都必须拒绝服务，且 Dispose 本身可重复调用。</summary>
    [Fact]
    public void Dispose_之后调用公开入口_抛出ObjectDisposedException()
    {
        CourierNode node = OpenNode("worker");
        node.Handle("document", "open", "打开文档", handlerPayload => "opened");
        node.Dispose();

        Assert.Throws<ObjectDisposedException>(() => node.Tick());
        Assert.Throws<ObjectDisposedException>(() => node.Endpoints());
        Assert.Throws<ObjectDisposedException>(() => node.Handle("document", "close", "关闭文档", handlerPayload => "closed"));
        node.Dispose();
    }

    /// <summary>Handle 的 handler 是必填参数，null 属于编程错误。</summary>
    [Fact]
    public void Handle_处理器为null_抛出ArgumentNullException()
    {
        using CourierNode node = OpenNode("worker");
        // 四个重载都接受 null 之外的 handler；显式转型是为了让编译器选中确定的那个重载。
        Assert.Throws<ArgumentNullException>(() => node.Handle("document", "open", "打开文档", (Func<string, string>)null!));
        Assert.Throws<ArgumentNullException>(() => node.Handle("document", "open", "打开文档", (Func<string, CancellationToken, string>)null!));
        Assert.Throws<ArgumentNullException>(() => node.HandleAsync("document", "open", "打开文档", (Func<string, Task<string>>)null!));
        Assert.Throws<ArgumentNullException>(() => node.HandleAsync("document", "open", "打开文档", (Func<string, CancellationToken, Task<string>>)null!));
    }

    /// <summary>SPEC §17：两端各自 Tick，调用方等待到 ok 响应。</summary>
    [Fact]
    public async Task Call_正常端点_返回ok且payload为handler返回值()
    {
        using CourierNode server = OpenNode("server");
        using CourierNode caller = OpenNode("caller");
        server.Handle("document", "open", "打开文档", handlerPayload => "opened:" + handlerPayload);

        CourierResponse response = await CallWithTickingAsync(
            server, caller, "server", "document", "open", "main.doc", TimeSpan.FromSeconds(10));

        Assert.Equal(CourierProtocol.StatusOk, response.Status);
        Assert.Equal("opened:main.doc", response.Payload);
        Assert.Null(response.Reason);
        Assert.True(response.IsOk);
        Assert.False(response.CancelRequested);
        Assert.True(CourierProtocol.IsValidRequestId(response.RequestId));
    }

    /// <summary>HandleAsync 走同一条处理管线，长命宿主 SHOULD 用它（SPEC §17.3）。</summary>
    [Fact]
    public async Task CallAsync_异步处理器_返回ok且payload为异步返回值()
    {
        using CourierNode server = OpenNode("server");
        using CourierNode caller = OpenNode("caller");
        server.HandleAsync("document", "open", "异步打开文档", async handlerPayload =>
        {
            await Task.Delay(1).ConfigureAwait(false);
            return "async:" + handlerPayload;
        });

        CourierResponse response = await CallWithTickingAsync(
            server, caller, "server", "document", "open", "main.doc", TimeSpan.FromSeconds(10));

        Assert.Equal(CourierProtocol.StatusOk, response.Status);
        Assert.Equal("async:main.doc", response.Payload);
    }

    /// <summary>payload 是不透明字符串，空串与 null 都必须原样送达 handler（SPEC §9）。</summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task Call_payload为空串或null_handler收到空串(string? payload)
    {
        using CourierNode server = OpenNode("server");
        using CourierNode caller = OpenNode("caller");
        string? observed = "not-called";
        server.Handle("document", "open", "打开文档", handlerPayload =>
        {
            observed = handlerPayload;
            return "ok";
        });

        CourierResponse response = await CallWithTickingAsync(
            server, caller, "server", "document", "open", payload, TimeSpan.FromSeconds(10));

        Assert.Equal(CourierProtocol.StatusOk, response.Status);
        Assert.Equal(string.Empty, observed);
    }
}
