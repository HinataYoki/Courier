using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Courier.Tests;

/// <summary>
/// 并发读探针：一条后台线程反复读取一组文件，并逐个用调用方的校验器判定"读到的东西是否完整"。
/// <para>
/// 抽出来是因为两个并发用例（原子写的读写竞态、注册表的并发发布）需要同一条读循环，
/// 而"无退避忙等"正是它们共同的老问题：把核打满会让同一进程里并行跑的其他用例出现时序抖动。
/// 这里统一成"每轮扫描后让出时间片"，并只记录**第一条**失败（之后的失败都是同一原因的重复）。
/// </para>
/// <para>
/// 校验器返回 null 表示这一份文件合格，返回字符串表示不合格并把该字符串记进失败列表。
/// </para>
/// </summary>
public abstract partial class TestRoot
{
    /// <summary>单个文件读探针；一个实例就是一条后台线程。释放时停线程并等待其退出。</summary>
    protected sealed class ConcurrentFileReader : IDisposable
    {
        private readonly CancellationTokenSource mStop = new CancellationTokenSource();
        private readonly ManualResetEventSlim mReady = new ManualResetEventSlim(false);
        private readonly List<string> mFailures = new List<string>();
        private readonly Func<string[]> mEnumeratePaths;
        private readonly Func<string, string?> mInspect;
        private readonly Task mLoop;
        private int mSuccessfulReads;
        private bool mAnnounced;

        /// <summary>
        /// 启动读线程。线程立即开始工作，调用方用 <see cref="WaitUntilReady"/> 确认它真的读到了东西，
        /// 再去触发写者——否则写者可能在读者被调度起来之前就跑完，并发窗口形同虚设。
        /// </summary>
        /// <param name="enumeratePaths">返回本轮要检查的文件绝对路径；允许在目录不存在时返回空数组。</param>
        /// <param name="inspect">校验单个文件的内容，合格返回 null，不合格返回失败描述。</param>
        internal ConcurrentFileReader(Func<string[]> enumeratePaths, Func<string, string?> inspect)
        {
            mEnumeratePaths = enumeratePaths;
            mInspect = inspect;
            mLoop = Task.Run(Run);
        }

        /// <summary>成功读到并交给校验器的文件次数；为 0 说明并发窗口根本没被覆盖。</summary>
        internal int SuccessfulReads
        {
            get { return Volatile.Read(ref mSuccessfulReads); }
        }

        /// <summary>失败描述的第一条（之后不再记录），合格时为 null。</summary>
        internal string? FirstFailure
        {
            get
            {
                lock (mFailures) return mFailures.Count == 0 ? null : mFailures[0];
            }
        }

        /// <summary>
        /// 等待读线程完成第一轮扫描（目标目录起步为空时也算就位）。超时返回 false，由调用方断言。
        /// 调用方据此保证"随后触发的写操作确实发生在一个已经在循环的读者之后"，而不是让读者的启动与写者的冲刺赛跑。
        /// </summary>
        /// <param name="timeout">等待上限。</param>
        /// <returns>读线程是否已经在期限内完成过一轮扫描。</returns>
        internal bool WaitUntilReady(TimeSpan timeout)
        {
            return mReady.Wait(timeout);
        }

        /// <summary>停线程并等待它退出，保证之后的断言不会再有并发读在跑。</summary>
        public void Dispose()
        {
            mStop.Cancel();
            try
            {
                mLoop.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }

            mReady.Dispose();
            mStop.Dispose();
        }

        /// <summary>读循环本体：枚举 → 逐个读取并校验 → 让出时间片。</summary>
        private void Run()
        {
            while (!mStop.IsCancellationRequested)
            {
                string[] paths;
                try
                {
                    paths = mEnumeratePaths();
                }
                catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException)
                {
                    // 目录正被并发改动：让出一下时间片再来，不要空转，也不要把这当成内容损坏。
                    Thread.Sleep(1);
                    continue;
                }

                for (int index = 0; index < paths.Length; index++)
                {
                    if (mStop.IsCancellationRequested) return;

                    string? text;
                    if (!CourierIO.TryReadAllText(paths[index], out text)) continue;
                    Interlocked.Increment(ref mSuccessfulReads);

                    string? failure = mInspect(text);
                    if (failure != null)
                    {
                        lock (mFailures) mFailures.Add(failure);
                        return;
                    }
                }

                // 首轮扫描完成即视为"就位"：目标目录起步时可能还是空的（写者尚未动笔），
                // 此时用"读到过文件"当就位信号会让调用方永远等不到。
                if (!mAnnounced)
                {
                    mAnnounced = true;
                    mReady.Set();
                }

                // 退避：一轮扫描很轻，让出时间片即可，既保留并发覆盖又不把核打满。
                Thread.Yield();
            }
        }
    }
}
