using System;
using System.Globalization;
using System.Threading;

#pragma warning disable CA1416

namespace Courier
{
    /// <summary>
    /// 池的抢槽与回收：谁能占一个槽、槽的原主人死了怎么收回、怎么判断"这个槽还是我的"。
    /// <para>
    /// 互斥手段是命名 <see cref="Mutex"/> 而不是命名信号量：命名信号量没有 abandoned 语义，
    /// 客户端在 WaitOne 与 Release 之间被强杀（任务管理器结束、崩溃）会永久占住信号量，
    /// 于是 Claim / Release / SweepDeadOwners 全部超时放弃，那个槽再也抢不回来，宿主泵还会每个
    /// 空闲周期被拖上一次超时。互斥体在内核里记录了持有者，持有者消失时下一个等待者拿到
    /// <see cref="AbandonedMutexException"/>（异常即"我拿到了"），槽因此可以自愈。
    /// </para>
    /// <para>
    /// 代数（<see cref="CourierHot.OFF_GENERATION"/>）：每次槽在 FREE 与 CLAIMED 之间转换都加一。
    /// 它解决的是 PID 复用：同一个 pid 在一秒内被系统重新分配是常态，只比 pid 会把"别人新抢的槽"
    /// 误判成"我的槽"，于是把响应写进素不相识的进程的环里。
    /// </para>
    /// </summary>
    internal sealed partial class CourierHotPool
    {
        /// <summary>抢槽时轮询间隔（毫秒）。池满时每秒重扫 4 次，既不至于饥饿也不会烧 CPU。</summary>
        private const int CLAIM_POLL_MS = 4;

        /// <summary>抢槽/释放时等互斥体的上限（毫秒）。等不到就放弃，绝不把调用方拖住。</summary>
        private const int CLAIM_LOCK_TIMEOUT_MS = 20;

        /// <summary>回收时等互斥体的上限（毫秒）；它跑在宿主泵上，不能和请求路径抢太久。</summary>
        private const int SWEEP_LOCK_TIMEOUT_MS = 50;

        /// <summary>
        /// 抢一个空闲槽。返回 -1 表示池满或抢槽锁拿不到，调用方据此回落到文件通道。
        /// </summary>
        /// <param name="timeoutMs">多久之内反复尝试；<c>0</c> 表示只扫一遍。</param>
        /// <returns>槽号，或 <c>-1</c>。</returns>
        internal int Claim(int timeoutMs)
        {
            int generation;
            return Claim(timeoutMs, out generation);
        }

        /// <summary>
        /// 抢一个空闲槽，并返回它的新代数（供 <see cref="StillOwned"/> 校验用）。
        /// </summary>
        /// <param name="timeoutMs">多久之内反复尝试；<c>0</c> 表示只扫一遍。</param>
        /// <param name="generation">抢到的槽的新代数；失败时为 <c>0</c>。</param>
        /// <returns>槽号，或 <c>-1</c>。</returns>
        internal int Claim(int timeoutMs, out int generation)
        {
            generation = 0;
            if (mDisposed) return -1;

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs < 0 ? 0 : timeoutMs);
            while (true)
            {
                int slot = TryClaimOnce(out generation);
                if (slot >= 0) return slot;
                if (DateTime.UtcNow >= deadline) return -1;
                Thread.Sleep(CLAIM_POLL_MS);
            }
        }

        /// <summary>
        /// 扫一遍所有槽，抢到第一个空闲的就返回。
        /// </summary>
        /// <param name="generation">抢到时的代数。</param>
        /// <returns>槽号；全被占或锁拿不到时为 <c>-1</c>。</returns>
        private int TryClaimOnce(out int generation)
        {
            generation = 0;
            if (!TryEnterClaimLock(CLAIM_LOCK_TIMEOUT_MS)) return -1;
            try
            {
                for (int slot = 0; slot < mSlotCount; slot++)
                {
                    long descriptor = DescriptorBase(slot);
                    if (mView.ReadInt32(descriptor + CourierHot.OFF_STATE) != STATE_FREE) continue;

                    int next = mView.ReadInt32(descriptor + CourierHot.OFF_GENERATION) + 1;
                    mView.Write(descriptor + CourierHot.OFF_GENERATION, next);
                    ResetSlotLocked(slot);
                    Thread.MemoryBarrier();
                    mView.Write(descriptor + CourierHot.OFF_OWNER_PID, CourierHot.CurrentProcessId);
                    Thread.MemoryBarrier();
                    mView.Write(descriptor + CourierHot.OFF_STATE, STATE_CLAIMED);
                    Thread.MemoryBarrier();
                    mView.Write(descriptor + CourierHot.OFF_LAST_SEEN, DateTime.UtcNow.Ticks);
                    generation = next;
                    return slot;
                }

                return -1;
            }
            finally
            {
                ReleaseClaimLock();
            }
        }

        /// <summary>
        /// 交还一个槽。只有仍归本进程时才真的释放：槽可能已经被回收并转手，此时清空会把别人的会话打掉。
        /// </summary>
        /// <param name="slot">槽号；越界直接忽略。</param>
        internal void Release(int slot)
        {
            if (mDisposed || !IsSlotValid(slot)) return;
            if (!TryEnterClaimLock(CLAIM_LOCK_TIMEOUT_MS)) return;
            try
            {
                long descriptor = DescriptorBase(slot);
                if (mView.ReadInt32(descriptor + CourierHot.OFF_STATE) != STATE_CLAIMED) return;
                if (mView.ReadInt32(descriptor + CourierHot.OFF_OWNER_PID) != CourierHot.CurrentProcessId) return;

                mView.Write(descriptor + CourierHot.OFF_OWNER_PID, 0);
                Thread.MemoryBarrier();
                mView.Write(descriptor + CourierHot.OFF_STATE, STATE_FREE);
                Thread.MemoryBarrier();
            }
            finally
            {
                ReleaseClaimLock();
            }
        }

        /// <summary>
        /// 回收原主人已经不在的槽。这是"客户端崩溃不漏槽"的唯一保证。
        /// </summary>
        /// <returns>本次回收的槽数。</returns>
        /// <remarks>
        /// 只在拿到"进程确实不存在"的证据时才回收：判错方向会把一个活着的调用方的槽抢走，
        /// 它下一次发送就落进别人的环里（<see cref="CourierHot.IsProcessAlive"/> 因此是三态的）。
        /// </remarks>
        internal int SweepDeadOwners()
        {
            if (mDisposed) return 0;
            if (!TryEnterClaimLock(SWEEP_LOCK_TIMEOUT_MS)) return 0;

            int freed = 0;
            try
            {
                int self = CourierHot.CurrentProcessId;
                for (int slot = 0; slot < mSlotCount; slot++)
                {
                    long descriptor = DescriptorBase(slot);
                    if (mView.ReadInt32(descriptor + CourierHot.OFF_STATE) != STATE_CLAIMED) continue;
                    int owner = mView.ReadInt32(descriptor + CourierHot.OFF_OWNER_PID);
                    if (owner == self) continue;
                    if (CourierHot.IsProcessAlive(owner)) continue;

                    mView.Write(descriptor + CourierHot.OFF_GENERATION, mView.ReadInt32(descriptor + CourierHot.OFF_GENERATION) + 1);
                    mView.Write(descriptor + CourierHot.OFF_OWNER_PID, 0);
                    Thread.MemoryBarrier();
                    mView.Write(descriptor + CourierHot.OFF_STATE, STATE_FREE);
                    Thread.MemoryBarrier();
                    freed++;
                }
            }
            finally
            {
                ReleaseClaimLock();
            }

            if (freed > 0)
            {
                CourierHotFaults.Record("sweep: reclaimed " + freed.ToString(CultureInfo.InvariantCulture) + " slot(s)");
            }

            return freed;
        }

        /// <summary>
        /// 读一个槽的主人信息（P2-11：宿主回铃前用它确认这个槽还是当初那个对端的）。
        /// </summary>
        /// <param name="slot">槽号。</param>
        /// <param name="ownerPid">当前主人 pid；空闲槽为 <c>0</c>。</param>
        /// <param name="generation">当前代数。</param>
        /// <returns>槽号合法时为 <c>true</c>。</returns>
        internal bool TryReadOwnership(int slot, out int ownerPid, out int generation)
        {
            ownerPid = 0;
            generation = 0;
            if (!IsSlotValid(slot)) return false;

            long descriptor = DescriptorBase(slot);
            Thread.MemoryBarrier();
            ownerPid = mView.ReadInt32(descriptor + CourierHot.OFF_OWNER_PID);
            generation = mView.ReadInt32(descriptor + CourierHot.OFF_GENERATION);
            Thread.MemoryBarrier();
            return true;
        }

        /// <summary>
        /// 槽是否仍归 (pid, 代数) 这一对。只比 pid 会在 PID 复用时误判，所以代数必须一起比。
        /// </summary>
        /// <param name="slot">槽号。</param>
        /// <param name="pid">持有者进程号。</param>
        /// <param name="generation">持有者记录的代数；<c>0</c> 表示只校验 pid（兼容旧调用点）。</param>
        /// <returns>仍归该持有者时为 <c>true</c>。</returns>
        internal bool StillOwned(int slot, int pid, int generation)
        {
            int owner;
            int current;
            if (!TryReadOwnership(slot, out owner, out current)) return false;
            if (owner != pid) return false;
            return generation == 0 || current == generation;
        }

        /// <summary>把槽的数据面与诊断字段清回初始状态（调用方已持有抢槽锁）。</summary>
        /// <param name="slot">槽号。</param>
        private void ResetSlotLocked(int slot)
        {
            long descriptor = DescriptorBase(slot);
            mView.Write(descriptor + CourierHot.OFF_A2_BHEAD, 0L);
            mView.Write(descriptor + CourierHot.OFF_A2_BTAIL, 0L);
            mView.Write(descriptor + CourierHot.OFF_B2_AHEAD, 0L);
            mView.Write(descriptor + CourierHot.OFF_B2_ATAIL, 0L);
            mView.Write(descriptor + CourierHot.OFF_LAST_SEEN, 0L);
            Thread.MemoryBarrier();
        }

        /// <summary>
        /// 在有超时的前提下拿抢槽互斥体。
        /// </summary>
        /// <param name="timeoutMs">上限毫秒。</param>
        /// <returns>拿到（含接管被遗弃的互斥体）时为 <c>true</c>。</returns>
        /// <remarks>
        /// <see cref="AbandonedMutexException"/> 不是失败：它表示上一个持有者没释放就消失了，
        /// 而内核已经把所有权交给了本线程，必须照常释放。
        /// </remarks>
        private bool TryEnterClaimLock(int timeoutMs)
        {
            try
            {
                if (mClaimLock.WaitOne(timeoutMs)) return true;
                CourierHotFaults.Record("claim: lock timeout");
                return false;
            }
            catch (AbandonedMutexException)
            {
                CourierHotFaults.Record("claim: reclaimed abandoned lock");
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        /// <summary>释放抢槽互斥体；失败只记诊断，绝不外传。</summary>
        private void ReleaseClaimLock()
        {
            try
            {
                mClaimLock.ReleaseMutex();
            }
            catch (ObjectDisposedException)
            {
                CourierHotFaults.Record("claim: lock disposed before release");
            }
            catch (ApplicationException)
            {
                // 本线程并不持有它：只可能来自被遗弃后的极端时序，属可恢复状态。
                CourierHotFaults.Record("claim: release without ownership");
            }
        }
    }
}
