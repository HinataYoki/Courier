using System;
using System.IO;
using Xunit;

namespace Courier.Tests;

/// <summary>
/// 在线 <c>endpoints/&lt;id&gt;/deadletter/</c> 的保留策略（SPEC §12、§15.3）。
/// <para>
/// 死信是终态证据，所以清理比 archive 多一道闸：**只有归档副本确实存在时**才删在线那一份。
/// 这一类测试都直接调 <see cref="CourierDeadletterPruner"/>，因为要精确控制 `failedAtUtc`
/// （文件名只有 requestId，过期与否只能读内容），而不是去等真实时钟。
/// </para>
/// </summary>
public sealed class CourierDeadletterTests : TestRoot
{
    private string ArchiveRoot
    {
        get { return Path.Combine(Root, CourierProtocol.ARCHIVE_DIRECTORY); }
    }

    private string WriteDeadletter(string endpointId, DateTime failedAtUtc, string? requestId = null)
    {
        string id = requestId ?? CourierProtocol.NewRequestId();
        CourierIO.WriteAtomic(
            DeadletterPath(endpointId, id),
            CourierDeadletter.Create(id, CourierProtocol.REASON_INVALID_COMMAND, "test", failedAtUtc, 1).ToJson(),
            true);
        return id;
    }

    private void WriteArchivedCopy(string endpointId, string requestId, DateTime utc)
    {
        string date = utc.ToString(CourierProtocol.DATE_FORMAT, System.Globalization.CultureInfo.InvariantCulture);
        string directory = Path.Combine(ArchiveRoot, date, endpointId, CourierProtocol.DEADLETTER_DIRECTORY);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, requestId + CourierProtocol.JSON_EXTENSION), "{}");
    }

    /// <summary>SPEC §12：归档副本存在且已过期时，在线那一份才被删。</summary>
    [Fact]
    public void Prune_归档副本存在且过期_删除在线死信()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            DateTime old = DateTime.UtcNow - TimeSpan.FromDays(30);
            string requestId = WriteDeadletter("worker", old);
            WriteArchivedCopy("worker", requestId, old);

            int removed = CourierDeadletterPruner.Prune(
                DeadletterDirectory("worker"), ArchiveRoot, "worker", DateTime.UtcNow - TimeSpan.FromDays(14));

            Assert.Equal(1, removed);
            Assert.False(File.Exists(DeadletterPath("worker", requestId)));
        }
    }

    /// <summary>SPEC §12：没有归档副本时绝不删在线死信——两份都消失等于命令被静默丢弃（§1 不变式 3）。</summary>
    [Fact]
    public void Prune_没有归档副本_保留在线死信()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            DateTime old = DateTime.UtcNow - TimeSpan.FromDays(30);
            string requestId = WriteDeadletter("worker", old);

            int removed = CourierDeadletterPruner.Prune(
                DeadletterDirectory("worker"), ArchiveRoot, "worker", DateTime.UtcNow - TimeSpan.FromDays(14));

            Assert.Equal(0, removed);
            Assert.True(File.Exists(DeadletterPath("worker", requestId)));
        }
    }

    /// <summary>SPEC §12：还在保留期内的死信不删，即使归档副本已经在。</summary>
    [Fact]
    public void Prune_未过保留期_保留死信()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            DateTime recent = DateTime.UtcNow - TimeSpan.FromHours(1);
            string requestId = WriteDeadletter("worker", recent);
            WriteArchivedCopy("worker", requestId, recent);

            int removed = CourierDeadletterPruner.Prune(
                DeadletterDirectory("worker"), ArchiveRoot, "worker", DateTime.UtcNow - TimeSpan.FromDays(14));

            Assert.Equal(0, removed);
            Assert.True(File.Exists(DeadletterPath("worker", requestId)));
        }
    }

    /// <summary>SPEC §12、§15.3：读不懂的证据绝不删——解析不出 failedAtUtc 的记录原样保留。</summary>
    [Fact]
    public void Prune_解析不出的记录_原样保留()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            string requestId = CourierProtocol.NewRequestId();
            // 缺 reason 的记录：CourierDeadletter.TryParse 会返回 null。
            CourierIO.WriteAtomic(
                DeadletterPath("worker", requestId),
                new CourierJsonObject().AddString("requestId", requestId).Build(),
                true);
            WriteArchivedCopy("worker", requestId, DateTime.UtcNow - TimeSpan.FromDays(30));

            int removed = CourierDeadletterPruner.Prune(
                DeadletterDirectory("worker"), ArchiveRoot, "worker", DateTime.UtcNow - TimeSpan.FromDays(14));

            Assert.Equal(0, removed);
            Assert.True(File.Exists(DeadletterPath("worker", requestId)));
        }
    }

    /// <summary>SPEC §12：名字不是合法 requestId 的文件（宿主自己放的）不动。</summary>
    [Fact]
    public void Prune_名字非法_不动()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            string path = Path.Combine(DeadletterDirectory("worker"), "notes.txt.json");
            File.WriteAllText(path, "{}");

            int removed = CourierDeadletterPruner.Prune(
                DeadletterDirectory("worker"), ArchiveRoot, "worker", DateTime.UtcNow - TimeSpan.FromDays(14));

            Assert.Equal(0, removed);
            Assert.True(File.Exists(path));
        }
    }

    /// <summary>SPEC §12：删在线死信之前，归档副本必须已由服务者在进 deadletter 时写好。</summary>
    [Fact]
    public void Tick_进deadletter时同时写归档副本()
    {
        using CourierNode node = OpenNode("worker");
        int calls = 0;
        node.Handle("document", "open", "打开文档", handlerPayload =>
        {
            calls++;
            return "should-not-run";
        });

        // payload 写成数字 → 解析失败 → 进 deadletter。
        string requestId = CourierProtocol.NewRequestId();
        DateTime now = DateTime.UtcNow;
        string json = new CourierJsonObject()
            .AddString("requestId", requestId)
            .AddString("channel", "document")
            .AddString("action", "open")
            .AddNumber("payload", 42)
            .AddString("issuedAtUtc", CourierProtocol.FormatUtc(now))
            .AddString("deadlineUtc", CourierProtocol.FormatUtc(now + TimeSpan.FromSeconds(30)))
            .AddString("issuedBy", "test-caller")
            .Build();
        WriteCommandFile("worker", requestId, json);

        node.Tick();

        Assert.Equal(0, calls);
        Assert.Null(ReadResponse("worker", requestId));
        Assert.NotNull(ReadDeadletter("worker", requestId));

        // 归档副本与在线那份内容一致（都是死信记录本身）。
        string archived = AssertArchivedDeadletter("worker", requestId);
        string online = ReadText(DeadletterPath("worker", requestId));
        Assert.Equal(online, ReadText(archived));
    }

    /// <summary>SPEC §15.3：保留期配成 0 表示不清理（上限责任回到宿主）。</summary>
    [Fact]
    public void Tick_保留期为零_不清理在线死信()
    {
        var options = new CourierNodeOptions
        {
            ArchiveRetention = TimeSpan.FromDays(14),
            DeadletterRetention = TimeSpan.Zero,
            ArchiveSweepInterval = TimeSpan.FromMinutes(1)
        };

        using (CourierNode node = CourierNode.Open(Root, "worker", null, null, options))
        {
            DateTime old = DateTime.UtcNow - TimeSpan.FromDays(400);
            string requestId = WriteDeadletter("worker", old);
            WriteArchivedCopy("worker", requestId, old);

            node.Tick();

            Assert.True(File.Exists(DeadletterPath("worker", requestId)));
        }
    }

    /// <summary>SPEC §12、§15.3：Tick 里的维护步骤会真的把过期在线死信清掉。</summary>
    [Fact]
    public void Tick_过期且已归档的在线死信被清掉()
    {
        var options = new CourierNodeOptions
        {
            ArchiveRetention = TimeSpan.FromDays(14),
            DeadletterRetention = TimeSpan.FromDays(14),
            ArchiveSweepInterval = TimeSpan.FromMinutes(1)
        };

        using (CourierNode node = CourierNode.Open(Root, "worker", null, null, options))
        {
            DateTime old = DateTime.UtcNow - TimeSpan.FromDays(40);
            string requestId = WriteDeadletter("worker", old);
            WriteArchivedCopy("worker", requestId, old);
            string fresh = WriteDeadletter("worker", DateTime.UtcNow - TimeSpan.FromHours(1));
            WriteArchivedCopy("worker", fresh, DateTime.UtcNow - TimeSpan.FromHours(1));

            node.Tick();

            Assert.False(File.Exists(DeadletterPath("worker", requestId)));
            Assert.True(File.Exists(DeadletterPath("worker", fresh)));
        }
    }
}
