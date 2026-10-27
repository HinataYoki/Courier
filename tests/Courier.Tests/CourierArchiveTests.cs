using System;
using System.IO;
using Xunit;

namespace Courier.Tests;

/// <summary>
/// <c>archive/</c> 的保留策略（SPEC §15.3：保留策略由实现决定，但 **MUST 有界**）。
/// <para>
/// 清理是服务者自己的维护动作，挂在 <see cref="CourierNode.Tick"/> 上、限频执行。
/// 这一类测试都跑独立临时根：布局是 root 级的，archive 树也在 root 下面。
/// </para>
/// </summary>
public sealed class CourierArchiveTests : TestRoot
{
    private string ArchiveRoot
    {
        get { return Path.Combine(Root, CourierProtocol.ARCHIVE_DIRECTORY); }
    }

    private static CourierNodeOptions Options(TimeSpan retention)
    {
        return new CourierNodeOptions { ArchiveRetention = retention, ArchiveSweepInterval = TimeSpan.FromMinutes(1) };
    }

    private string SeedArchive(DateTime utc, string endpointId)
    {
        string date = utc.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        string commands = Path.Combine(ArchiveRoot, date, endpointId, CourierProtocol.COMMANDS_DIRECTORY);
        string deadletter = Path.Combine(ArchiveRoot, date, endpointId, CourierProtocol.DEADLETTER_DIRECTORY);
        Directory.CreateDirectory(commands);
        Directory.CreateDirectory(deadletter);
        File.WriteAllText(Path.Combine(commands, "20260101T000000Z-01234567.json"), "{}");
        File.WriteAllText(Path.Combine(deadletter, "20260101T000001Z-01234568.json"), "{}");
        return Path.Combine(ArchiveRoot, date, endpointId);
    }

    /// <summary>早于保留期的归档按日期目录整目录删除；还在保留期内的原样保留。</summary>
    [Fact]
    public void Tick_删除早于保留期的端点归档()
    {
        string old = SeedArchive(DateTime.UtcNow - TimeSpan.FromDays(40), "worker");
        string recent = SeedArchive(DateTime.UtcNow - TimeSpan.FromDays(2), "worker");
        Assert.True(Directory.Exists(old));

        using (CourierNode node = CourierNode.Open(Root, "worker", null, null, Options(TimeSpan.FromDays(14))))
        {
            node.Tick();
        }

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
        Assert.True(File.Exists(Path.Combine(recent, CourierProtocol.COMMANDS_DIRECTORY, "20260101T000000Z-01234567.json")));
    }

    /// <summary>只删自己端点的子目录：别的端点的同名日期归档由它自己决定保留期。</summary>
    [Fact]
    public void Tick_不删除其他端点的归档()
    {
        string mine = SeedArchive(DateTime.UtcNow - TimeSpan.FromDays(40), "worker");
        string theirs = SeedArchive(DateTime.UtcNow - TimeSpan.FromDays(40), "other");

        using (CourierNode node = CourierNode.Open(Root, "worker", null, null, Options(TimeSpan.FromDays(14))))
        {
            node.Tick();
        }

        Assert.False(Directory.Exists(mine));
        Assert.True(Directory.Exists(theirs));
    }

    /// <summary>保留期配成 0 表示关掉清理：那等于 archive 无界，只能由宿主自己兜住。</summary>
    [Fact]
    public void 保留期为零_不做任何清理()
    {
        string old = SeedArchive(DateTime.UtcNow - TimeSpan.FromDays(400), "worker");

        using (CourierNode node = CourierNode.Open(Root, "worker", null, null, Options(TimeSpan.Zero)))
        {
            node.Tick();
        }

        Assert.True(Directory.Exists(old));
    }

    /// <summary>名字不是归档日期格式的目录（宿主自己放进来的东西）一律不猜、不动。</summary>
    [Fact]
    public void 非日期目录_不动()
    {
        string custom = Path.Combine(ArchiveRoot, "keepme");
        Directory.CreateDirectory(Path.Combine(custom, "worker", CourierProtocol.COMMANDS_DIRECTORY));
        File.WriteAllText(Path.Combine(custom, "worker", CourierProtocol.COMMANDS_DIRECTORY, "x.json"), "{}");

        using (CourierNode node = CourierNode.Open(Root, "worker", null, null, Options(TimeSpan.FromDays(1))))
        {
            node.Tick();
        }

        Assert.True(Directory.Exists(custom));
    }

    /// <summary>清理是限频的：一轮 Tick 里最多跑一次，不会每 Tick 都扫 archive。</summary>
    [Fact]
    public void 清理限频_同一秒内多轮Tick只扫一次()
    {
        string old = SeedArchive(DateTime.UtcNow - TimeSpan.FromDays(40), "worker");

        using (CourierNode node = CourierNode.Open(Root, "worker", null, null, Options(TimeSpan.FromDays(14))))
        {
            node.Tick();
            // 第一轮之后可以把现场重新造出来：若下一轮又扫了一遍，它会被再次删掉。
            Assert.False(Directory.Exists(old));
            string again = SeedArchive(DateTime.UtcNow - TimeSpan.FromDays(40), "worker");
            node.Tick();
            Assert.True(Directory.Exists(again), "清理必须限频，不能每轮 Tick 都扫 archive");
        }
    }
}
