using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Xunit;

namespace Courier.Tests;

/// <summary>
/// CourierStream 的端到端语义：有界环、序号追赶、超时、断开重连、描述清理与传输选择。
/// </summary>
public sealed class CourierStreamTests : TestRoot
{
    /// <summary>写入的记录可以通过公共读取端按序号读回，且不产生命令或结果文件。</summary>
    [Fact]
    public void 写入记录_读取端按序返回_不产生命令结果文件()
    {
        using CourierNode node = OpenNode("stream-host");
        using CourierStreamWriter writer = node.OpenStream(
            "trace",
            new CourierStreamOptions { Capacity = 8, MaxRecordBytes = 1024 });

        Assert.True(writer.TryWrite(Encoding.UTF8.GetBytes("first")));
        Assert.True(writer.TryWrite(Encoding.UTF8.GetBytes("second")));
        Assert.Equal(2, writer.WrittenSequence);

        using CourierStreamReader reader = CourierStreamReader.Open(Root, "stream-host", "trace");
        Assert.True(reader.TryRead(3000, out CourierStreamRecord? first));
        Assert.NotNull(first);
        Assert.Equal(1, first!.Sequence);
        Assert.Equal("first", Encoding.UTF8.GetString(first.Payload));
        Assert.True(first.TimestampUtcTicks > 0);

        Assert.True(reader.TryRead(3000, out CourierStreamRecord? second));
        Assert.NotNull(second);
        Assert.Equal(2, second!.Sequence);
        Assert.Equal("second", Encoding.UTF8.GetString(second.Payload));

        Assert.Empty(ListFiles(CommandsDirectory("stream-host")));
        Assert.Empty(ListFiles(ResultsDirectory("stream-host")));
    }

    /// <summary>当前平台的默认传输必须可读；Windows 优先 mmap-ring，其他平台使用 UDS。</summary>
    [Fact]
    public void 默认传输_按平台选择并完成收发()
    {
        using CourierNode node = OpenNode("transport");
        using CourierStreamWriter writer = node.OpenStream("trace");
        Assert.True(writer.TryWrite(new byte[] { 1, 2, 3 }));

        string descriptorPath = Path.Combine(
            CourierStreamDescriptor.DirectoryFor(EndpointDirectory("transport"), "trace"),
            CourierStreamDescriptor.FILE_NAME);
        Dictionary<string, object?> descriptor = ReadJsonObject(descriptorPath);
        string transport = Assert.IsType<string>(descriptor["transport"]);
        Assert.False(ReadJsonObject(EndpointJsonPath("transport")).ContainsKey("streams"));
        Assert.False(ReadJsonObject(EndpointJsonPath("transport")).ContainsKey("stream"));
        if (CourierStreamWindowsHost.IsSupported)
        {
            Assert.Equal(CourierStreamDescriptor.TRANSPORT_MMAP_RING, transport);
        }
        else
        {
            Assert.Equal(CourierStreamDescriptor.TRANSPORT_UNIX_SOCKET, transport);
        }

        using CourierStreamReader reader = CourierStreamReader.Open(Root, "transport", "trace");
        Assert.True(reader.TryRead(3000, out CourierStreamRecord? record));
        Assert.NotNull(record);
        Assert.Equal(new byte[] { 1, 2, 3 }, record!.Payload);
    }

    /// <summary>环满时丢最旧记录，读取端从请求序号追赶并报告跳过的数量。</summary>
    [Fact]
    public void 环满_丢最旧记录并报告追赶信息()
    {
        using CourierNode node = OpenNode("bounded");
        using CourierStreamWriter writer = node.OpenStream(
            "trace",
            new CourierStreamOptions { Capacity = 2, MaxRecordBytes = 64 });

        Assert.True(writer.TryWrite(Encoding.UTF8.GetBytes("one")));
        Assert.True(writer.TryWrite(Encoding.UTF8.GetBytes("two")));
        Assert.True(writer.TryWrite(Encoding.UTF8.GetBytes("three")));
        Assert.Equal(1, writer.DroppedCount);

        using CourierStreamReader reader = CourierStreamReader.Open(
            Root,
            "bounded",
            "trace",
            sinceSequence: 1);
        Assert.Equal(2, reader.OldestSequence);
        Assert.Equal(1, reader.SkippedBefore);
        Assert.Equal(1, reader.DroppedBefore);

        Assert.True(reader.TryRead(3000, out CourierStreamRecord? first));
        Assert.NotNull(first);
        Assert.Equal(2, first!.Sequence);
        Assert.Equal("two", Encoding.UTF8.GetString(first.Payload));

        Assert.True(reader.TryRead(3000, out CourierStreamRecord? second));
        Assert.NotNull(second);
        Assert.Equal(3, second!.Sequence);
        Assert.Equal("three", Encoding.UTF8.GetString(second.Payload));
    }

    /// <summary>无数据时按超时返回 false，后续写入仍能被同一读取端收到。</summary>
    [Fact]
    public void 无数据_超时返回false且连接继续可用()
    {
        using CourierNode node = OpenNode("timeout");
        using CourierStreamWriter writer = node.OpenStream("trace");
        using CourierStreamReader reader = CourierStreamReader.Open(Root, "timeout", "trace");

        Assert.False(reader.TryRead(50, out CourierStreamRecord? empty));
        Assert.Null(empty);
        Assert.True(reader.IsConnected);

        Assert.True(writer.TryWrite(Encoding.UTF8.GetBytes("later")));
        Assert.True(reader.TryRead(3000, out CourierStreamRecord? record));
        Assert.NotNull(record);
        Assert.Equal("later", Encoding.UTF8.GetString(record!.Payload));
    }

    /// <summary>读取端断开不会影响写入端；重新连接后可以从环里继续追赶。</summary>
    [Fact]
    public void 读取端断开_写入端继续工作且可重新连接()
    {
        using CourierNode node = OpenNode("reconnect");
        using CourierStreamWriter writer = node.OpenStream(
            "trace",
            new CourierStreamOptions { Capacity = 8, MaxRecordBytes = 128 });
        Assert.True(writer.TryWrite(Encoding.UTF8.GetBytes("before")));

        using (CourierStreamReader first = CourierStreamReader.Open(Root, "reconnect", "trace"))
        {
            Assert.True(first.TryRead(3000, out CourierStreamRecord? record));
            Assert.Equal("before", Encoding.UTF8.GetString(record!.Payload));
        }

        Assert.True(writer.TryWrite(Encoding.UTF8.GetBytes("after")));
        using CourierStreamReader second = CourierStreamReader.Open(
            Root,
            "reconnect",
            "trace",
            sinceSequence: 2);
        Assert.True(second.TryRead(3000, out CourierStreamRecord? reconnected));
        Assert.NotNull(reconnected);
        Assert.Equal(2, reconnected!.Sequence);
        Assert.Equal("after", Encoding.UTF8.GetString(reconnected.Payload));
    }

    /// <summary>释放写入端后描述文件被清理，读取端得到明确的“不连接”状态。</summary>
    [Fact]
    public void 释放写入端_清理描述且读取端失效()
    {
        string descriptorPath;
        using (CourierNode node = OpenNode("cleanup"))
        {
            using CourierStreamWriter writer = node.OpenStream("trace");
            descriptorPath = Path.Combine(
                CourierStreamDescriptor.DirectoryFor(EndpointDirectory("cleanup"), "trace"),
                CourierStreamDescriptor.FILE_NAME);
            Assert.True(File.Exists(descriptorPath));

            using CourierStreamReader reader = CourierStreamReader.Open(Root, "cleanup", "trace");
            Assert.True(reader.IsConnected);
        }

        Assert.False(File.Exists(descriptorPath));
        using CourierNode reopened = OpenNode("cleanup");
        Assert.Throws<IOException>(() => CourierStreamReader.Open(Root, "cleanup", "trace"));
    }

    /// <summary>直接使用 UDS 后端也能完成端到端收发，保证 Windows 以外的平台不受 mmap 影响。</summary>
    [Fact]
    public void UnixSocket后端_端到端收发()
    {
        const string endpointId = "unix-stream";
        string endpointDirectory = EndpointDirectory(endpointId);
        Directory.CreateDirectory(endpointDirectory);
        const string socketFile = "stream-test.sock";
        using var host = new CourierStreamUnixHost(
            CourierStreamDescriptor.DirectoryFor(endpointDirectory, "trace"),
            socketFile,
            4,
            128);
        var descriptor = new CourierStreamDescriptor(
            "trace",
            "streamtestsession",
            CourierProtocol.CurrentProcessId(),
            4,
            128,
            CourierStreamDescriptor.TRANSPORT_UNIX_SOCKET,
            host.SocketFile,
            host.SocketDirectory,
            null,
            null);
        Assert.True(descriptor.TryWrite(endpointDirectory));
        Assert.True(host.TryWrite(Encoding.UTF8.GetBytes("uds")));

        using CourierStreamReader reader = CourierStreamReader.Open(Root, endpointId, "trace");
        Assert.True(reader.TryRead(3000, out CourierStreamRecord? record));
        Assert.NotNull(record);
        Assert.Equal("uds", Encoding.UTF8.GetString(record!.Payload));
    }

    /// <summary>UDS 读取端的零超时必须立即返回已有记录，而不是把连接误判为空闲。</summary>
    [Fact]
    public void UnixSocket后端_零超时读取已有记录()
    {
        const string endpointId = "unix-immediate";
        string endpointDirectory = EndpointDirectory(endpointId);
        Directory.CreateDirectory(endpointDirectory);
        const string socketFile = "stream-immediate.sock";
        using var host = new CourierStreamUnixHost(
            CourierStreamDescriptor.DirectoryFor(endpointDirectory, "trace"),
            socketFile,
            8,
            128);
        var descriptor = new CourierStreamDescriptor(
            "trace",
            "streamtestsession",
            CourierProtocol.CurrentProcessId(),
            8,
            128,
            CourierStreamDescriptor.TRANSPORT_UNIX_SOCKET,
            host.SocketFile,
            host.SocketDirectory,
            null,
            null);
        Assert.True(descriptor.TryWrite(endpointDirectory));
        Assert.True(host.TryWrite(Encoding.UTF8.GetBytes("first")));

        using CourierStreamReader reader = CourierStreamReader.Open(Root, endpointId, "trace");
        Assert.True(reader.TryRead(3000, out CourierStreamRecord? first));
        Assert.Equal("first", Encoding.UTF8.GetString(first!.Payload));

        Assert.True(host.TryWrite(Encoding.UTF8.GetBytes("second")));
        CourierStreamRecord? second = null;
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && !reader.TryRead(0, out second))
        {
            Thread.Sleep(5);
        }

        // 旧实现会在 deadline 判断处直接返回 false，循环结束时 second 仍为 null。
        Assert.NotNull(second);
        Assert.Equal("second", Encoding.UTF8.GetString(second!.Payload));
    }

    /// <summary>超长记录被拒绝，且不会消耗序号。</summary>
    [Fact]
    public void 记录超过上限_拒绝写入且不推进序号()
    {
        using CourierNode node = OpenNode("oversize");
        using CourierStreamWriter writer = node.OpenStream(
            "trace",
            new CourierStreamOptions { Capacity = 4, MaxRecordBytes = 4 });

        Assert.False(writer.TryWrite(new byte[5]));
        Assert.Equal(0, writer.WrittenSequence);
        Assert.True(writer.TryWrite(new byte[4]));
        Assert.Equal(1, writer.WrittenSequence);
    }

    /// <summary>非法流名与篡改过的描述必须在打开前被拒绝。</summary>
    [Fact]
    public void 非法流名与描述_被拒绝()
    {
        using CourierNode node = OpenNode("invalid");
        Assert.Throws<ArgumentException>(() => node.OpenStream("Bad Name"));
        Assert.Throws<ArgumentException>(() => CourierStreamReader.Open(Root, "invalid", "Bad Name"));

        Assert.Null(CourierStreamDescriptor.TryParse(
            "{\"version\":1,\"transport\":\"unknown\",\"stream\":\"trace\",\"sessionId\":\"s\",\"hostPid\":1,\"capacity\":4,\"maxRecordBytes\":8}"));
        Assert.Null(CourierStreamDescriptor.TryParse(
            "{\"version\":1,\"transport\":\"unix-socket\",\"stream\":\"trace\",\"sessionId\":\"s\",\"hostPid\":1,\"capacity\":4,\"maxRecordBytes\":8,\"socketFile\":\"../escape.sock\"}"));
    }

    /// <summary>崩溃遗留的描述在宿主进程已不存在时必须允许新会话接管。</summary>
    [Fact]
    public void 崩溃遗留描述_宿主已退出时允许接管()
    {
        using CourierNode node = OpenNode("stale");
        string directory = CourierStreamDescriptor.DirectoryFor(EndpointDirectory("stale"), "trace");
        Directory.CreateDirectory(directory);
        string descriptorPath = Path.Combine(directory, CourierStreamDescriptor.FILE_NAME);
        CourierIO.WriteAtomic(
            descriptorPath,
            "{\"version\":1,\"transport\":\"unix-socket\",\"stream\":\"trace\",\"sessionId\":\"dead\",\"hostPid\":2147483647,\"capacity\":4,\"maxRecordBytes\":128,\"socketFile\":\"dead.sock\"}",
            true);

        using CourierStreamWriter writer = node.OpenStream("trace");
        Assert.True(writer.TryWrite(Encoding.UTF8.GetBytes("recovered")));
        using CourierStreamReader reader = CourierStreamReader.Open(Root, "stale", "trace");
        Assert.True(reader.TryRead(3000, out CourierStreamRecord? record));
        Assert.NotNull(record);
        Assert.Equal("recovered", Encoding.UTF8.GetString(record!.Payload));
    }
}
