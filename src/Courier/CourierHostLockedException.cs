using System;
using System.IO;

namespace Courier
{
    /// <summary>
    /// 端点目录已被另一个实例服务时抛出的异常（SPEC §8）。
    /// <para>
    /// 只有当操作系统明确报告<b>共享冲突</b>时才抛它。磁盘故障、权限不足、路径不存在一律原样抛出
    /// 它们自己的异常：把"磁盘坏了"说成"程序已经在跑了"，会让用户永远等不到那个能启动的瞬间。
    /// </para>
    /// </summary>
    public sealed class CourierHostLockedException : IOException
    {
        /// <summary>拿不到锁的端点标识。</summary>
        public string EndpointId { get; }

        /// <summary>被争用的锁文件绝对路径，便于宿主直接报给用户。</summary>
        public string LockPath { get; }

        /// <summary>构造占用异常；消息里带上端点标识与锁文件路径，宿主无需再拼。</summary>
        /// <param name="endpointId">拿不到锁的端点标识。</param>
        /// <param name="lockPath">被争用的锁文件绝对路径。</param>
        internal CourierHostLockedException(string endpointId, string lockPath)
            : base("endpoint \"" + endpointId + "\" is already being served by another instance (host.lock is held exclusively): " + lockPath)
        {
            EndpointId = endpointId;
            LockPath = lockPath;
        }
    }
}
