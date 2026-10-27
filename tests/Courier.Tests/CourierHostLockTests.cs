using System;
using System.IO;
using System.Threading;
using Xunit;

namespace Courier.Tests;

/// <summary>
/// `host.lock` 单实例互斥（SPEC §8）。
/// <para>
/// 这一类测试全部在**独立临时根**里跑：锁的语义来自 OS 的共享模式，同一进程内的两个
/// <see cref="CourierNode"/> 一样会互相排斥（`FileShare.None` 是按句柄算的，不按进程算），
/// 因此不需要真的起两个进程——但语义与两个进程完全一致。
/// </para>
/// </summary>
public sealed class CourierHostLockTests : TestRoot
{
    private static CourierNodeOptions Locked()
    {
        return new CourierNodeOptions { AcquireHostLock = true };
    }

    private string HostLockPath(string endpointId)
    {
        return Path.Combine(EndpointDirectory(endpointId), CourierProtocol.HOST_LOCK_FILE_NAME);
    }

    /// <summary>SPEC §8：同一端点的第二个服务者必须被拒绝，且异常带得回是哪个端点。</summary>
    [Fact]
    public void Open_同一端点第二次打开_抛出CourierHostLockedException()
    {
        using CourierNode first = CourierNode.Open(Root, "worker", null, null, Locked());

        CourierHostLockedException caught = Assert.Throws<CourierHostLockedException>(
            () => CourierNode.Open(Root, "worker", null, null, Locked()));

        Assert.Equal("worker", caught.EndpointId);
        Assert.Equal(Path.GetFullPath(HostLockPath("worker")), caught.LockPath);
        Assert.IsAssignableFrom<IOException>(caught);
    }

    /// <summary>SPEC §8：第一个服务者退出后，锁必须已经释放，第二个才能起来。</summary>
    [Fact]
    public void Dispose_之后第二个实例可以打开同一端点()
    {
        CourierNode first = CourierNode.Open(Root, "worker", null, null, Locked());
        first.Dispose();

        using CourierNode second = CourierNode.Open(Root, "worker", null, null, Locked());
        Assert.True(File.Exists(HostLockPath("worker")));
    }

    /// <summary>SPEC §8：锁的语义只看共享模式，不看文件内容——空文件同样有效。</summary>
    [Fact]
    public void 锁文件存在但内容为空_仍然可以打开并持有()
    {
        Directory.CreateDirectory(EndpointDirectory("worker"));
        string lockPath = HostLockPath("worker");
        // 先留一个空文件：OpenOrCreate 必须复用它，而不是因为"文件已存在且非空/为空"就改变语义。
        File.WriteAllText(lockPath, string.Empty);
        Assert.Equal(0, new FileInfo(lockPath).Length);

        using CourierNode first = CourierNode.Open(Root, "worker", null, null, Locked());
        Assert.Equal(0, new FileInfo(lockPath).Length);
        Assert.Throws<CourierHostLockedException>(() => CourierNode.Open(Root, "worker", null, null, Locked()));
    }

    /// <summary>SPEC §8：抢先占用的是别人时同样判定为"已被服务"，与自己是不是同一个进程无关。</summary>
    [Fact]
    public void 外部句柄独占锁文件_端点打开被判定为已被占用()
    {
        Directory.CreateDirectory(EndpointDirectory("worker"));
        using (new FileStream(HostLockPath("worker"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<CourierHostLockedException>(() => CourierNode.Open(Root, "worker", null, null, Locked()));
        }

        // 外部句柄释放之后必须能正常打开：被拒绝的实例不该在目录里留下任何"我还活着"的痕迹。
        using CourierNode node = CourierNode.Open(Root, "worker", null, null, Locked());
        Assert.True(File.Exists(EndpointJsonPath("worker")));
    }

    /// <summary>SPEC §8：默认关闭。不打开这个开关时，同一端点开两个节点仍然合法（测试与无状态客户端这么用）。</summary>
    [Fact]
    public void 未开启开关_同一端点可以打开两个节点()
    {
        using CourierNode first = OpenNode("worker");
        using CourierNode second = OpenNode("worker");
        Assert.True(File.Exists(EndpointJsonPath("worker")));
    }

    /// <summary>
    /// SPEC §8：抢锁发生在铺开子目录之前，失败时不得留下半初始化的端点目录。
    /// 这里用"第一次被拒"来观察：被拒之后 `commands/` 不该存在。
    /// </summary>
    [Fact]
    public void 抢锁失败_不留下半初始化的子目录()
    {
        using CourierNode first = CourierNode.Open(Root, "worker", null, null, Locked());
        // first 已经铺开了目录，先把它清空成一个只有 host.lock 的干净现场。
        Directory.Delete(CommandsDirectory("worker"), true);

        Assert.Throws<CourierHostLockedException>(() => CourierNode.Open(Root, "worker", null, null, Locked()));
        Assert.False(Directory.Exists(CommandsDirectory("worker")));
    }

    /// <summary>SPEC §8：Dispose 可重复调用，第二次不得因为锁已经释放而抛。</summary>
    [Fact]
    public void Dispose重复调用_不抛异常且锁只释放一次()
    {
        CourierNode node = CourierNode.Open(Root, "worker", null, null, Locked());
        node.Dispose();
        node.Dispose();

        using CourierNode reopened = CourierNode.Open(Root, "worker", null, null, Locked());
        Assert.True(File.Exists(HostLockPath("worker")));
    }

    /// <summary>SPEC §8：不同端点各持一把锁，互不影响。</summary>
    [Fact]
    public void 不同端点_可以同时持有各自的锁()
    {
        using CourierNode a = CourierNode.Open(Root, "alpha", null, null, Locked());
        using CourierNode b = CourierNode.Open(Root, "beta", null, null, Locked());
        Assert.True(File.Exists(HostLockPath("alpha")));
        Assert.True(File.Exists(HostLockPath("beta")));
    }

    /// <summary>SPEC §8：没有可用的端点 ID 时必须在建目录之前就失败，不能先留下一个垃圾目录。</summary>
    [Fact]
    public void 非法endpointId_在建目录之前就失败()
    {
        Assert.Throws<ArgumentException>(() => CourierNode.Open(Root, "Worker", null, null, Locked()));
        Assert.False(Directory.Exists(EndpointsRoot));
    }
}
