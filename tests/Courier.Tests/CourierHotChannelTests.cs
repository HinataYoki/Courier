using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Courier.Tests;

/// <summary>
/// 热通道的**协议身份**（SPEC §5.2、§2）。
/// <para>
/// 热通道是实现层的性能旁路，不是协议能力。它唯一会出现在别人磁盘上的东西是一份实现私有的描述
/// <c>endpoints/&lt;id&gt;/hot/descriptor.json</c>；而 <c>endpoint.json</c> 里 MUST NOT 出现 `hot` 字段
/// （§5.2 的硬条款，且读到它的实现 MUST 忽略它）。这组测试锁住的就是这条边界：
/// 关掉开关时任何地方都不该出现热通道痕迹，打开时痕迹也只在实现私有目录里。
/// </para>
/// </summary>
public sealed class CourierHotChannelTests : TestRoot
{
    private static CourierNodeOptions Hot(bool enabled)
    {
        return new CourierNodeOptions { EnableHotChannel = enabled };
    }

    private string HotDirectory(string endpointId)
    {
        return Path.Combine(EndpointDirectory(endpointId), CourierLayout.Default.HotEvidenceDirectory);
    }

    private string DescriptorPath(string endpointId)
    {
        return Path.Combine(HotDirectory(endpointId), CourierHotDescriptor.HOT_FILE_NAME);
    }

    /// <summary>SPEC §5.2：endpoint.json 里 MUST NOT 出现 hot 字段——开与关都一样。</summary>
    [Fact]
    public void EndpointJson_无论热通道开关都不写hot字段()
    {
        using (CourierNode off = CourierNode.Open(Root, "plain", null, null, Hot(false)))
        {
            off.Handle("document", "open", "打开文档", payload => "ok");
            off.Tick();
            Dictionary<string, object?> document = ReadJsonObject(EndpointJsonPath("plain"));
            Assert.False(document.ContainsKey("hot"));
        }

        using (CourierNode on = CourierNode.Open(Root, "fast", null, null, Hot(true)))
        {
            on.Handle("document", "open", "打开文档", payload => "ok");
            on.Tick();
            Dictionary<string, object?> document = ReadJsonObject(EndpointJsonPath("fast"));
            // 热通道打开时也不许写：描述走实现私有目录，协议文件里一个字都没有。
            Assert.False(document.ContainsKey("hot"));
        }
    }

    /// <summary>SPEC §2：描述写在实现私有目录里，关掉开关时连这个目录都不该出现。</summary>
    [Fact]
    public void 关闭开关_不产生任何热通道痕迹()
    {
        using CourierNode node = CourierNode.Open(Root, "plain", null, null, Hot(false));
        node.Handle("document", "open", "打开文档", payload => "ok");
        node.Tick();

        Assert.False(Directory.Exists(HotDirectory("plain")));
        Assert.False(CourierHotDescriptor.TryRead(Root, CourierLayout.Default, "plain", out CourierHotDescriptor? descriptor));
        Assert.Null(descriptor);
    }

    /// <summary>
    /// Windows 上：打开开关后 MUST 在 <c>hot/descriptor.json</c> 里留下一条自校验通过的描述。
    /// 描述缺失说明热通道"看起来开着、实际没起"，调用方每次调用都会白付一次失败的打开开销，
    /// 所以这里不能像旧版那样把它当合规结果放行。
    /// <para>
    /// 非 Windows：热通道在本实现里不可用（命名节/事件/互斥体都是 Windows 专有），
    /// 此时 MUST 一个痕迹都不留。xunit 2.9.3 没有运行期跳过接口（<c>Assert.Skip</c> 是 v3 API，
    /// 抛 <c>SkipException</c> 在 v2 runner 里会被报成 FAIL），所以这一分支用"平台确实不支持"
    /// 与"确实没留痕迹"两条真断言收尾，而不是裸 return 让用例空转通过。
    /// </para>
    /// </summary>
    [Fact]
    public void 打开开关_描述只出现在实现私有目录或被整体放弃()
    {
        using CourierNode node = CourierNode.Open(Root, "fast", null, null, Hot(true));
        node.Handle("document", "open", "打开文档", payload => "ok");
        node.Tick();

        if (!CourierHot.IsSupported)
        {
            // 显式跳过（xunit 2.9.3 无 Assert.Skip）：先把"这台机器确实不是 Windows"钉住，
            // 再断言不支持时必须一个痕迹都不留——两条都是会真失败的断言，不是空转。
            Assert.NotEqual(PlatformID.Win32NT, Environment.OSVersion.Platform);
            Assert.False(Directory.Exists(HotDirectory("fast")), "不支持热通道的平台 MUST NOT 留下实现私有目录");
            return;
        }

        Assert.True(
            File.Exists(DescriptorPath("fast")),
            "Windows 上打开开关后必须写出描述文件: " + DescriptorPath("fast") +
            "；若热通道被放弃，服务故障应记录在 LastServiceFault 里，实际为: " + (node.LastServiceFault ?? "<null>"));

        Assert.True(CourierHotDescriptor.TryRead(Root, CourierLayout.Default, "fast", out CourierHotDescriptor? descriptor));
        Assert.NotNull(descriptor);
        Assert.Equal(CourierHot.TRANSPORT, ReadJsonObject(DescriptorPath("fast"))["transport"] as string ?? string.Empty);
        Assert.True(descriptor!.HostPid > 0);
        Assert.True(descriptor.Slots > 0);
        Assert.True(descriptor.Capacity > 0);
        Assert.False(string.IsNullOrEmpty(descriptor.Alias));
        Assert.Null(node.LastServiceFault);
    }

    /// <summary>SPEC §2：实现私有的东西删掉不改变任何协议行为——删掉描述后端点照常服务命令。</summary>
    [Fact]
    public void 删掉私有描述_端点仍按文件通道工作()
    {
        using CourierNode node = CourierNode.Open(Root, "fast", null, null, Hot(true));
        node.Handle("document", "open", "打开文档", payload => "opened:" + payload);
        node.Tick();

        if (File.Exists(DescriptorPath("fast")))
        {
            File.Delete(DescriptorPath("fast"));
        }

        // 描述没了，但文件通道是权威：命令照常被处理，终态证据照常落盘。
        string requestId = DeliverCommand("fast", "document", "open", "main.doc");
        node.WaitForIdle(TimeSpan.FromSeconds(5));

        CourierResponse? response = ReadResponse("fast", requestId);
        Assert.NotNull(response);
        Assert.Equal(CourierProtocol.STATUS_OK, response.Status);
        Assert.Equal("opened:main.doc", response.Payload);
    }

    /// <summary>
    /// 端点正常退出后不该留下指向已死池的描述：留下的描述会让调用方白白多做一次打开尝试。
    /// 先把"退出前描述确实存在"钉住，再断言退出后被摘掉，否则在热通道根本没起来的机器上
    /// 这条用例会因为描述自始不存在而无条件通过。
    /// </summary>
    [Fact]
    public void 正常退出_不留下指向已死池的描述()
    {
        using (CourierNode node = CourierNode.Open(Root, "fast", null, null, Hot(true)))
        {
            node.Handle("document", "open", "打开文档", payload => "ok");
            node.Tick();

            if (!CourierHot.IsSupported)
            {
                // 同上：非 Windows 上热通道本来就起不来，这里只验证"确实没留下任何描述"。
                Assert.NotEqual(PlatformID.Win32NT, Environment.OSVersion.Platform);
                Assert.False(File.Exists(DescriptorPath("fast")));
                return;
            }

            Assert.True(
                File.Exists(DescriptorPath("fast")),
                "退出前描述必须存在，否则这条用例无法证明退出会清理它: " + DescriptorPath("fast"));
        }

        Assert.False(File.Exists(DescriptorPath("fast")), "退出后 MUST NOT 留下指向已死池的描述");
    }

    /// <summary>
    /// 崩溃留下的过期描述（池已经没了）必须只让调用方多做一次失败的打开，然后无条件下落到文件通道。
    /// 这是"共享内存不可用就回退磁盘"的核心保证：热通道坏了不能把整条调用链带坏。
    /// </summary>
    [Fact]
    public void 过期描述_不影响调用_自动回落到文件通道()
    {
        using CourierNode server = CourierNode.Open(Root, "worker", null, null, Hot(true));
        server.Handle("document", "open", "打开文档", payload => "opened:" + payload);
        server.Tick();

        // 伪造一份指向早已不存在的池的描述：alias 随便写，hostPid 指向一个不可能存在的进程。
        string directory = HotDirectory("worker");
        Directory.CreateDirectory(directory);
        CourierIO.WriteAtomic(
            DescriptorPath("worker"),
            "{\"version\":1,\"transport\":\"" + CourierHot.TRANSPORT + "\",\"alias\":\"courier-deadbeef-worker-deadbeef\",\"hostPid\":1,\"slots\":32,\"capacity\":16,\"maxBodyBytes\":2048,\"evidence\":\"file\"}",
            true);

        using CourierNode caller = CourierNode.Open(Root, "caller", null, null, Hot(true));
        CourierResponse response = CourierCallWithTickingAsync(server, caller);

        Assert.Equal(CourierProtocol.STATUS_OK, response.Status);
        Assert.Equal("opened:main.doc", response.Payload);
        Assert.True(File.Exists(ResultsPath("worker", response.RequestId)));
    }

    /// <summary>在服务端持续 Tick 的同时发一条调用，返回响应。</summary>
    private CourierResponse CourierCallWithTickingAsync(CourierNode server, CourierNode caller)
    {
        return CallWithTickingAsync(server, caller, "worker", "document", "open", "main.doc", TimeSpan.FromSeconds(5))
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>SPEC §5.2：描述格式是自校验的——版本或传输方式不对时读方必须当成"没有描述"。</summary>
    [Fact]
    public void 描述不合规时读方当作没有描述()
    {
        string directory = HotDirectory("fast");
        Directory.CreateDirectory(directory);

        CourierHotDescriptor? descriptor;
        CourierIO.WriteAtomic(DescriptorPath("fast"), "{\"version\":99,\"transport\":\"mmap-ring\",\"alias\":\"a\",\"hostPid\":1,\"slots\":32,\"capacity\":16}", true);
        Assert.False(CourierHotDescriptor.TryRead(Root, CourierLayout.Default, "fast", out descriptor));
        Assert.Null(descriptor);

        CourierIO.WriteAtomic(DescriptorPath("fast"), "{\"version\":1,\"transport\":\"pipe\",\"alias\":\"a\",\"hostPid\":1,\"slots\":32,\"capacity\":16}", true);
        Assert.False(CourierHotDescriptor.TryRead(Root, CourierLayout.Default, "fast", out descriptor));
        Assert.Null(descriptor);

        CourierIO.WriteAtomic(DescriptorPath("fast"), "not json at all", true);
        Assert.False(CourierHotDescriptor.TryRead(Root, CourierLayout.Default, "fast", out descriptor));
        Assert.Null(descriptor);
    }
}
