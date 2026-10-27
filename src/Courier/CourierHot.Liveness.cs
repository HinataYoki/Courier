using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Courier
{
    /// <summary>对端进程的存活判定结论。</summary>
    /// <remarks>
    /// 三态而不是布尔：拿不到进程句柄有两种完全不同的原因 —— "那个 pid 不存在"和"那个进程活得很好、
    /// 只是它的完整性级别不允许我打开"。把后者当成前者会让调用方每次调用都丢弃并重建热连接。
    /// </remarks>
    internal enum PeerState
    {
        /// <summary>拿不到死亡句柄，但也不能断言对方已死；只能等门铃，不能据此判死。</summary>
        Unknown = 0,

        /// <summary>对端进程确实存在。</summary>
        Alive = 1,

        /// <summary>对端进程确实已经不存在。</summary>
        Dead = 2
    }

    /// <summary>
    /// 热通道的存活判定：把"进程是不是还在"这件事问清楚（docs/HOT-CHANNEL.md §3.4）。
    /// <para>
    /// 热通道的唯一的 liveness 依据是可等待的进程句柄（<c>OpenProcess(SYNCHRONIZE)</c>），
    /// 它能和门铃一起进同一个 <c>WaitAny</c>，于是"对端崩了"和"对端发了帧"是同一类事件。
    /// 但打开失败并不总是等于死亡：跨完整性级别（宿主提权、调用方未提权）时内核回
    /// <c>ERROR_ACCESS_DENIED</c>，而真正的"pid 不存在"回的是 <c>ERROR_INVALID_PARAMETER</c>。
    /// 两者混为一谈的后果是 <see cref="CourierHotPool.PeerAlive"/> 恒为 false，
    /// 缓存的热连接每次调用都被丢弃重建，热通道退化成"每次都重新建连"。
    /// </para>
    /// </summary>
    internal static partial class CourierHot
    {
        /// <summary><c>PROCESS_SYNCHRONIZE</c>：只要求"能等它退出"，是最小权限。</summary>
        private const uint SYNCHRONIZE_ACCESS = 0x00100000;

        /// <summary>进程存在，但调用方权限不足（跨完整性级别）。</summary>
        private const int ERROR_ACCESS_DENIED = 5;

        /// <summary>pid 不存在（含已经退出的进程）。</summary>
        private const int ERROR_INVALID_PARAMETER = 87;

        /// <summary>打开进程句柄；只请求 <c>SYNCHRONIZE</c>，不请求任何读写权限。</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        /// <summary>
        /// 打开对端进程的"已退出"可等待句柄，并区分"拿不到"的两种原因。
        /// </summary>
        /// <param name="pid">对端进程号。</param>
        /// <param name="state">打开结果的对端状态；<c>Dead</c> 表示 pid 确实不存在。</param>
        /// <returns>可等待句柄；<c>null</c> 表示拿不到（<paramref name="state"/> 说明原因）。</returns>
        /// <remarks>
        /// 拿不到句柄时调用方必须退化为"只等门铃"，而不是判死：判死会让一次调用白白丢掉热通道，
        /// 而只等门铃的最坏后果是这一次多等到自己的截止时间，随后的文件通道永远兜得住。
        /// </remarks>
        internal static WaitHandle? OpenDeathHandle(int pid, out PeerState state)
        {
            state = ProbePeer(pid);
            if (state == PeerState.Dead) return null;
            if (!IsSupported) return null;

            IntPtr handle = OpenProcess(SYNCHRONIZE_ACCESS, false, pid);
            if (handle == IntPtr.Zero) return null;
            return new ProcessExitHandle(handle);
        }

        /// <summary>
        /// 只做存活判定，不保留句柄。用于"手上的池没有死亡句柄"的降级路径。
        /// </summary>
        /// <param name="pid">对端进程号。</param>
        /// <returns>对端状态；权限不足时返回 <c>Alive</c>（进程存在，只是打不开）。</returns>
        internal static PeerState ProbePeer(int pid)
        {
            if (pid <= 0) return PeerState.Dead;
            if (!IsSupported) return PeerState.Unknown;

            IntPtr handle = OpenProcess(SYNCHRONIZE_ACCESS, false, pid);
            if (handle != IntPtr.Zero)
            {
                var safeHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(handle, true);
                safeHandle.Dispose();
                return PeerState.Alive;
            }

            switch (Marshal.GetLastWin32Error())
            {
                case ERROR_INVALID_PARAMETER:
                    return PeerState.Dead;
                case ERROR_ACCESS_DENIED:
                    // 进程在，只是它的完整性级别不接受我们打开句柄。判死会让热通道白白退场。
                    return PeerState.Alive;
                default:
                    return PeerState.Unknown;
            }
        }

        /// <summary>当前进程是否还活着；用于回收崩溃进程占着的槽。</summary>
        /// <param name="pid">待判定的进程号。</param>
        /// <returns>进程存在（含权限不足无法打开）时为 <c>true</c>。</returns>
        /// <remarks>
        /// 回收槽是不可逆的破坏性动作，所以只有拿到"确实不存在"的证据才返回 <c>false</c>：
        /// 判错方向会把一个活着的调用方的槽抢走，它下一次发送就落进别人的环里。
        /// </remarks>
        internal static bool IsProcessAlive(int pid)
        {
            if (pid <= 0) return false;
            PeerState state = ProbePeer(pid);
            if (state == PeerState.Dead) return false;
            if (state == PeerState.Alive) return true;

            // 未知（非 Windows 或非预期的 Win32 错误）：退回托管实现再问一次，同样只在有证据时判死。
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    return !process.HasExited;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (Win32Exception)
            {
                return true;
            }
            catch (NotSupportedException)
            {
                return true;
            }
        }
    }
}
