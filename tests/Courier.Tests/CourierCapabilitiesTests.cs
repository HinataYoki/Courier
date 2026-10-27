using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Courier.Tests;

/// <summary>
/// `capabilities` 能力自述（SPEC §5.3）。
/// <para>
/// 它解决的是「读方在对方没实现可选能力时行为正确退化」（§21）无法判定的问题：没有这份自述，
/// 读方无从区分「对端没实现」与「对端实现了但这次没用上」。所以这一类测试既覆盖写方
/// （`DeclareCapability` 与 Open 时的自述），也覆盖读方（解析时的容忍度）。
/// </para>
/// </summary>
public sealed class CourierCapabilitiesTests : TestRoot
{
    private static List<object?>? ReadCapabilities(Dictionary<string, object?> document)
    {
        return CourierJson.GetArray(document, "capabilities");
    }

    /// <summary>SPEC §5.3：capabilities 按名字序数升序写进 endpoint.json。</summary>
    [Fact]
    public void DeclareCapability_写进endpointJson且按序数升序()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            node.DeclareCapability("zzz");
            node.DeclareCapability("aaa");

            List<object?>? capabilities = ReadCapabilities(ReadJsonObject(EndpointJsonPath("worker")));
            Assert.NotNull(capabilities);
            // 默认自述的三个（cancels、progress、state）与自定义的两个一起按序数 Ascending 排。
            var names = new List<string>();
            for (int index = 0; index < capabilities.Count; index++) names.Add(Assert.IsType<string>(capabilities[index]));
            Assert.Equal(new[] { "aaa", "cancels", "progress", "state", "zzz" }, names);
        }
    }

    /// <summary>SPEC §5.3：Open 自述的是"真的做了"，不是"规范定义了"——没开 host.lock 就不报 host-lock。</summary>
    [Fact]
    public void Open_按实际启用的开关自述能力()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            List<object?>? capabilities = ReadCapabilities(ReadJsonObject(EndpointJsonPath("worker")));
            Assert.NotNull(capabilities);
            var names = new List<string>();
            for (int index = 0; index < capabilities.Count; index++) names.Add(Assert.IsType<string>(capabilities[index]));
            Assert.Contains("state", names);
            Assert.Contains("cancels", names);
            Assert.Contains("progress", names);
            Assert.DoesNotContain("host-lock", names);
            Assert.DoesNotContain("registry", names);
        }

        var options = new CourierNodeOptions { AcquireHostLock = true, PublishRootRegistry = true };
        using (CourierNode node = CourierNode.Open(Root, "other", null, null, options))
        {
            List<object?>? capabilities = ReadCapabilities(ReadJsonObject(EndpointJsonPath("other")));
            Assert.NotNull(capabilities);
            var names = new List<string>();
            for (int index = 0; index < capabilities.Count; index++) names.Add(Assert.IsType<string>(capabilities[index]));
            Assert.Contains("host-lock", names);
            Assert.Contains("registry", names);
        }
    }

    /// <summary>SPEC §5.3：名字规则与 channel / action / state 同一套，重复声明是编程错误。</summary>
    [Fact]
    public void DeclareCapability_名字非法或重复_抛出ArgumentException()
    {
        using CourierNode node = OpenNode("worker");
        Assert.Throws<ArgumentException>(() => node.DeclareCapability("state"));
        Assert.Throws<ArgumentException>(() => node.DeclareCapability(".hidden"));
        Assert.Throws<ArgumentException>(() => node.DeclareCapability("trailing."));
        Assert.Throws<ArgumentException>(() => node.DeclareCapability("sp ace"));

        node.DeclareCapability("custom");
        Assert.Throws<ArgumentException>(() => node.DeclareCapability("custom"));
    }

    /// <summary>SPEC §5：capabilities 与 actions / states 同属能力表，第一次 Tick 之后冻结。</summary>
    [Fact]
    public void DeclareCapability_Tick之后_抛出InvalidOperationException()
    {
        using CourierNode node = OpenNode("worker");
        node.DeclareCapability("before");
        node.Tick();
        Assert.Throws<InvalidOperationException>(() => node.DeclareCapability("after"));
    }

    /// <summary>SPEC §5.3：读方按名字规则过滤，非法名与重复名都忽略，且一条坏声明不该让整份目录不可读。</summary>
    [Fact]
    public void 读方_忽略非法与重复的能力名()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            // 先开一个真实端点把目录铺好，再用手写的 endpoint.json 覆盖成"别家实现的写法"。
            node.Handle("document", "open", "打开文档", handlerPayload => "ok");
        }

        string json = new CourierJsonObject()
            .AddString("endpointId", "worker")
            .AddString("sessionId", "7f3a2b1c")
            .AddNumber("protocolVersion", CourierProtocol.PROTOCOL_VERSION)
            .AddRaw("actions", CourierJson.Array(new List<string>()))
            .AddRaw("capabilities", "[" + "\"state\"," + "\"bad name\"," + "\"state\"," + "42" + "]")
            .Build();
        CourierIO.WriteAtomic(EndpointJsonPath("worker"), json, true);

        CourierEndpointInfo? endpoint = Courier.CourierEndpointReader.TryRead(EndpointDirectory("worker"));
        Assert.NotNull(endpoint);
        Assert.Equal(new[] { "state" }, endpoint!.Capabilities);
    }

    /// <summary>SPEC §5.3：没有声明时整个字段缺席，读方得到空列表而不是抛错。</summary>
    [Fact]
    public void 读方_endpointJson没有capabilities时为空列表()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            node.Handle("document", "open", "打开文档", handlerPayload => "ok");
        }

        // 手写一份完全没有 capabilities 的 endpoint.json：早于 §5.3 的实现就是这个样子。
        string json = new CourierJsonObject()
            .AddString("endpointId", "worker")
            .AddString("sessionId", "7f3a2b1c")
            .AddNumber("protocolVersion", CourierProtocol.PROTOCOL_VERSION)
            .AddRaw("actions", CourierJson.Array(new List<string>()))
            .Build();
        CourierIO.WriteAtomic(EndpointJsonPath("worker"), json, true);

        Dictionary<string, object?> document = ReadJsonObject(EndpointJsonPath("worker"));
        Assert.Null(ReadCapabilities(document));

        CourierEndpointInfo? endpoint = Courier.CourierEndpointReader.TryRead(EndpointDirectory("worker"));
        Assert.NotNull(endpoint);
        Assert.Empty(endpoint!.Capabilities);
    }
}
