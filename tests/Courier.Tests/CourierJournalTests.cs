using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Courier.Tests;

/// <summary>
/// 热通道账本 <c>endpoints/&lt;id&gt;/hot/hot-&lt;session&gt;.log</c> 的磁盘占用有界性。
/// <para>
/// 账本不在 SPEC §2 的布局里，也不是协议语义的依据：它是"这条消息走的是内存"的旁证，
/// 强证据始终是 <c>results/&lt;requestId&gt;.json</c>。因此它的测试只关心两件事：
/// 默认根本不该被创建；打开之后不该无限增长。
/// </para>
/// </summary>
public sealed class CourierJournalTests : TestRoot
{
    private string HotDirectory(string endpointId)
    {
        return Path.Combine(EndpointDirectory(endpointId), CourierLayout.Default.HotEvidenceDirectory);
    }

    private static CourierHotJournal OpenOrFail(string directory, string sessionId, long maxBytes, TimeSpan staleAge)
    {
        CourierHotJournal? journal = CourierHotJournal.Open(directory, sessionId, maxBytes, staleAge, out string? detail);
        Assert.Null(detail);
        Assert.NotNull(journal);
        return journal!;
    }

    /// <summary>
    /// 默认关闭：不打开开关就不该留下任何账本文件。
    /// <para>
    /// 端点自己会建 <c>hot/</c>（热通道要往那里写实现私有的 <c>descriptor.json</c>），
    /// 所以这里断言的是"没有账本"，而不是"没有目录"——目录存在与否是实现细节。
    /// 断言对整个端点目录递归枚举 <c>*.log</c>，因此目录不存在时这条用例照样能失败，
    /// 不会因为 <c>Directory.Exists</c> 为假而整条空转通过。
    /// </para>
    /// </summary>
    [Fact]
    public void 默认关闭_不写出账本文件()
    {
        using CourierNode node = OpenNode("worker");
        node.Tick();

        // 先断言被测目录确实存在，再断言里面没有账本：目录不存在时整条用例会空转通过，
        // 那样"默认不写账本"这条约束一旦被破坏（比如端点根本没落地）也照样绿。
        string endpointDirectory = EndpointDirectory("worker");
        Assert.True(Directory.Exists(endpointDirectory), "端点目录不存在，这条用例无法证明「没有账本」: " + endpointDirectory);
        Assert.Empty(Directory.GetFiles(endpointDirectory, "*.log", SearchOption.AllDirectories));
    }

    /// <summary>显式打开后才会有账本，而且第一条证据要能读回来。</summary>
    [Fact]
    public void 打开开关_写出可读的账本行()
    {
        using (CourierHotJournal journal = OpenOrFail(HotDirectory("worker"), "abcdef0123456789", CourierHotJournal.DEFAULT_MAX_BYTES, TimeSpan.FromHours(24)))
        {
            journal.Append("{\"a\":1}");
            journal.Append("{\"a\":2}");
        }

        string[] files = Directory.GetFiles(HotDirectory("worker"), "*.log");
        Assert.Single(files);
        string[] lines = File.ReadAllLines(files[0]);
        Assert.Equal(2, lines.Length);
        Assert.Equal("{\"a\":1}", lines[0]);
        Assert.Equal("{\"a\":2}", lines[1]);
    }

    /// <summary>涨到上限就换代，并且淘汰上一代——任何时刻磁盘上只有一份。</summary>
    [Fact]
    public void 超过上限_换代并淘汰上一代()
    {
        const string session = "0123456789abcdef";
        using (CourierHotJournal journal = OpenOrFail(HotDirectory("worker"), session, 512, TimeSpan.Zero))
        {
            for (int index = 0; index < 200; index++)
            {
                journal.Append("{\"n\":" + index + ",\"pad\":\"0123456789012345678901234567890123456789\"}");
            }
        }

        string[] files = Directory.GetFiles(HotDirectory("worker"), "*.log");
        Assert.Single(files);
        Assert.StartsWith("hot-" + session + "-", Path.GetFileName(files[0]));
    }

    /// <summary>打开时清掉不属于本次运行、且已经过期很久的账本；新鲜的、以及本次运行的都不动。</summary>
    [Fact]
    public void 打开时_清理陈旧的他会话账本()
    {
        string directory = HotDirectory("worker");
        Directory.CreateDirectory(directory);

        string stale = Path.Combine(directory, "hot-0000000000000000.log");
        string fresh = Path.Combine(directory, "hot-1111111111111111.log");
        string ours = Path.Combine(directory, "hot-2222222222222222.log");
        string unrelated = Path.Combine(directory, "notes.log");
        foreach (string path in new[] { stale, fresh, ours, unrelated }) File.WriteAllText(path, "x");

        DateTime old = DateTime.UtcNow - TimeSpan.FromDays(3);
        File.SetLastWriteTimeUtc(stale, old);
        File.SetLastWriteTimeUtc(unrelated, old);

        using (CourierHotJournal journal = OpenOrFail(directory, "2222222222222222", 4096, TimeSpan.FromHours(24)))
        {
            Assert.NotNull(journal);
        }

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(ours));
        // 名字不符合账本命名的文件一律不碰。
        Assert.True(File.Exists(unrelated));
    }

    /// <summary>轮转上限为正数时总字节数必须收敛，不会随写入量线性增长。</summary>
    [Fact]
    public void 轮转保持总字节有界()
    {
        string directory = HotDirectory("worker");
        const long maxBytes = 2048;
        using (CourierHotJournal journal = OpenOrFail(directory, "fedcba9876543210", maxBytes, TimeSpan.Zero))
        {
            for (int index = 0; index < 500; index++)
            {
                journal.Append("{\"n\":" + index + ",\"pad\":\"0123456789012345678901234567890123456789\"}");
            }
        }

        long total = Directory.GetFiles(directory, "*.log").Sum(path => new FileInfo(path).Length);
        // 上限之外还有一层 64 KiB 写缓冲，按"上限 + 一缓冲"给边界。
        Assert.True(total <= maxBytes + 64 * 1024 + 4096, "账本总字节数没有收敛: " + total);
    }
}
