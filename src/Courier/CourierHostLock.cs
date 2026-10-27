using System;
using System.IO;

namespace Courier
{
    /// <summary>
    /// <c>host.lock</c> 的独占打开（SPEC §8）。
    /// <para>
    /// 用 OS 文件锁而不是 PID 文件：PID 文件需要判断"那个 PID 还活着吗"，而这个判断在 PID 复用、
    /// 容器与跨用户场景下都不可靠。OS 锁由内核维护，进程死亡时自动释放——这正是需要的语义。
    /// </para>
    /// <para>
    /// <b>只有共享冲突才算"已被占用"。</b>磁盘故障、权限不足、路径不存在是另一回事，把它们伪装成
    /// "已有实例在跑"会让一个坏掉的环境看起来像正常工作，用户永远等不到它启动。
    /// </para>
    /// </summary>
    internal static class CourierHostLock
    {
        /// <summary>Windows <c>ERROR_SHARING_VIOLATION</c>：文件已被别人以不兼容的共享模式打开。</summary>
        internal const int ERROR_SHARING_VIOLATION = 32;

        /// <summary>Windows <c>ERROR_LOCK_VIOLATION</c>：字节范围锁冲突。</summary>
        internal const int ERROR_LOCK_VIOLATION = 33;

        /// <summary>
        /// 打开并独占持有锁文件。成功返回的流必须一直持有到端点退出。
        /// </summary>
        /// <param name="endpointId">端点标识，只用于异常信息。</param>
        /// <param name="lockPath">锁文件绝对路径；目录必须已存在。</param>
        /// <returns>持有独占句柄的流；调用方负责 Dispose。</returns>
        /// <exception cref="CourierHostLockedException">操作系统报告共享冲突，即该端点已被别的实例服务。</exception>
        /// <exception cref="IOException">其它 I/O 故障（磁盘、路径、权限等），原样抛出，绝不伪装成占用。</exception>
        internal static FileStream Acquire(string endpointId, string lockPath)
        {
            try
            {
                // OpenOrCreate：文件不存在就建一个空文件。内容无意义，锁的语义完全来自共享模式。
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException caught) when (IsSharingConflict(caught))
            {
                throw new CourierHostLockedException(endpointId, lockPath);
            }
        }

        /// <summary>
        /// 尝试短持有地拿一次排他锁（注册表用，SPEC §7）。拿不到就返回 null，绝不阻塞服务循环：
        /// 注册表是可选视图，抢不到锁的下一轮 Tick 再来即可。
        /// </summary>
        /// <param name="lockPath">锁文件绝对路径；目录必须已存在。</param>
        /// <returns>持有独占句柄的流；共享冲突时返回 null。</returns>
        internal static FileStream? TryAcquire(string lockPath)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException caught) when (IsSharingConflict(caught))
            {
                return null;
            }
        }

        /// <summary>
        /// 判断一个 IOException 是不是共享冲突。只看低 16 位平台错误码：<c>HResult</c> 的高位是
        /// 严重性、设施等标记，不同 .NET 版本与不同调用路径并不一致，只有低 16 位是 OS 的 Win32 错误码。
        /// </summary>
        /// <param name="caught">捕获到的异常。</param>
        /// <returns>是否为 ERROR_SHARING_VIOLATION 或 ERROR_LOCK_VIOLATION。</returns>
        internal static bool IsSharingConflict(IOException caught)
        {
            int code = caught.HResult & 0xFFFF;
            return code == ERROR_SHARING_VIOLATION || code == ERROR_LOCK_VIOLATION;
        }
    }
}
