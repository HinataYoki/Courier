using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Courier.Tests;

/// <summary>
/// endpoint.json 里的可选 <c>metadata</c>（SPEC §5.1、§14）。
/// <para>
/// metadata 是自由 JSON：协议只规定"有就原样带着、没有就整个字段缺席"，不解释内容，
/// 也绝不写 <c>null</c>——读方看到 <c>"metadata": null</c> 会以为"有 metadata 但它是空的"。
/// </para>
/// </summary>
public sealed class CourierMetadataTests : TestRoot
{
    /// <summary>SPEC §5.1：metadata 原样写进 endpoint.json 的 action 条目。</summary>
    [Fact]
    public void Handle_带metadata_action条目原样保留()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            node.Handle("document", "open", "打开文档", (CourierCall call) => "ok");
            // 带 metadata 的重载：metadata 是第 4 个参数，handler 排最后（SPEC §5.1）。
            node.Handle("document", "close", "关闭文档", "{\"risk\":\"low\",\"version\":\"1.2.0\"}", (CourierCall call) => "ok");

            List<object?>? actions = CourierJson.GetArray(ReadJsonObject(EndpointJsonPath("worker")), "actions");
            Assert.NotNull(actions);
            Assert.Equal(2, actions!.Count);

            // actions 按 (channel, action) 序数升序：close 在前且带 metadata，open 在后且字段缺席。
            var first = Assert.IsType<Dictionary<string, object?>>(actions[0]);
            Assert.Equal("close", CourierJson.GetString(first, "action"));
            Assert.Equal("{\"risk\":\"low\",\"version\":\"1.2.0\"}", CourierEndpointReader.ReadMetadata(first));

            var second = Assert.IsType<Dictionary<string, object?>>(actions[1]);
            Assert.Equal("open", CourierJson.GetString(second, "action"));
            Assert.False(second.ContainsKey("metadata"));
        }
    }

    /// <summary>SPEC §5.1：没有 metadata 时字段必须整个缺席，不得写 null。</summary>
    [Fact]
    public void Handle_无metadata_字段缺席而不是null()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            node.Handle("document", "open", "打开文档", (CourierCall call) => "ok");
            node.DeclareState("document.open", "当前打开的文档");

            string text = ReadText(EndpointJsonPath("worker"));
            Assert.DoesNotContain("metadata", text, StringComparison.Ordinal);

            List<object?>? actions = CourierJson.GetArray(ReadJsonObject(EndpointJsonPath("worker")), "actions");
            var action = Assert.IsType<Dictionary<string, object?>>(actions![0]);
            Assert.False(action.ContainsKey("metadata"));
        }
    }

    /// <summary>SPEC §14：states 声明同样可以带 metadata。</summary>
    [Fact]
    public void DeclareState_带metadata_state条目原样保留()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            node.DeclareState("document.open", "当前打开的文档", "{\"revision\":\"sensitive\"}");

            List<object?>? states = CourierJson.GetArray(ReadJsonObject(EndpointJsonPath("worker")), "states");
            Assert.NotNull(states);
            var state = Assert.IsType<Dictionary<string, object?>>(states![0]);
            Assert.Equal("{\"revision\":\"sensitive\"}", CourierEndpointReader.ReadMetadata(state));
            Assert.Equal(3, state.Count);
        }
    }

    /// <summary>SPEC §5.1：metadata 必须是 JSON 对象；字符串、数组、坏 JSON 都当场报错。</summary>
    [Fact]
    public void DeclareState_metadata不是对象_抛出ArgumentException()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            Assert.Throws<ArgumentException>(() => node.DeclareState("document.open", "说明", "\"just a string\""));
            Assert.Throws<ArgumentException>(() => node.DeclareState("document.open", "说明", "[1,2,3]"));
            Assert.Throws<ArgumentException>(() => node.DeclareState("document.open", "说明", "{ broken"));
            Assert.Throws<ArgumentException>(() => node.DeclareState("document.open", "说明", "42"));
        }
    }

    /// <summary>SPEC §5.1：null 与空白等同于"没有 metadata"。</summary>
    [Fact]
    public void DeclareState_metadata为null或空白_等同没有()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            node.DeclareState("a", "说明", null);
            node.DeclareState("b", "说明", "   ");

            string text = ReadText(EndpointJsonPath("worker"));
            Assert.DoesNotContain("metadata", text, StringComparison.Ordinal);
        }
    }

    /// <summary>SPEC §5.1：读方对 <c>"metadata": null</c> 与非对象按"缺失"处理，不得因此丢弃整条能力。</summary>
    [Fact]
    public void 读取端_metadata为null或非对象_按缺失处理()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            node.Handle("document", "open", "打开文档", (CourierCall call) => "ok");
        }

        // 手写一份带畸形 metadata 的 endpoint.json：读方必须仍然读得出这条能力。
        string json = new CourierJsonObject()
            .AddString("endpointId", "worker")
            .AddString("description", string.Empty)
            .AddString("sessionId", "0123456789abcdef")
            .AddNumber("processId", 1)
            .AddString("startedAtUtc", CourierProtocol.FormatUtc(DateTime.UtcNow))
            .AddNumber("protocolVersion", CourierProtocol.PROTOCOL_VERSION)
            .AddRaw("actions", "[{\"channel\":\"a\",\"action\":\"b\",\"summary\":\"s\",\"metadata\":null},"
                + "{\"channel\":\"c\",\"action\":\"d\",\"summary\":\"s\",\"metadata\":\"text\"},"
                + "{\"channel\":\"e\",\"action\":\"f\",\"summary\":\"s\",\"metadata\":{\"k\":1}}]")
            .AddRaw("states", "[]")
            .Build();
        CourierIO.WriteAtomic(EndpointJsonPath("worker"), json, true);

        CourierEndpointInfo? info = CourierEndpointReader.TryRead(EndpointDirectory("worker"));
        Assert.NotNull(info);
        Assert.Equal(3, info!.Actions.Count);
        Assert.Null(info.Actions[0].Metadata);
        Assert.Null(info.Actions[1].Metadata);
        Assert.Equal("{\"k\":1}", info.Actions[2].Metadata);
    }

    /// <summary>metadata 不参与路由：同一个 (channel, action) 加不加 metadata 都只认一次。</summary>
    [Fact]
    public void metadata不影响路由与去重()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            node.Handle("document", "open", "打开文档", (CourierCall call) => "first");
            Assert.Throws<ArgumentException>(
                () => node.Handle("document", "open", "另一个", "{\"risk\":\"high\"}", (CourierCall call) => "second"));
        }
    }

}