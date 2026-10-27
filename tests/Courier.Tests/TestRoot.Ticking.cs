using System;
using System.Threading;
using System.Threading.Tasks;

namespace Courier.Tests;

/// <summary>
/// 泵的驱动与等待。全部按**可观察量**推进：终态文件是否落盘、claim 的到期时刻、
/// 事件是否置位——不用"跑满 N 秒再断言"这种固定墙钟窗口，慢机器上它既慢又不稳。
/// <para>
/// 抽成 partial 文件是为了让 <c>TestRoot.cs</c> 留在单文件 500 行的约束内。
/// </para>
/// </summary>
public abstract partial class TestRoot
{
    /// <summary>
    /// 反复 Tick 直到 <paramref name="condition"/> 成立，用于"终态落盘"这种由泵推进的观察点。
    /// 有界等待：超过 <paramref name="timeout"/> 后不再 Tick，把最后一次观察结果留给调用方断言。
    /// </summary>
    /// <param name="node">要驱动的服务者节点。</param>
    /// <param name="condition">观察条件，返回 true 即停止推进。</param>
    /// <param name="timeout">等待上限，默认 8 秒。</param>
    /// <returns><paramref name="condition"/> 是否在超时前成立。</returns>
    protected static bool TickUntil(CourierNode node, Func<bool> condition, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(8));
        while (DateTime.UtcNow < deadline)
        {
            node.Tick();
            if (condition()) return true;
            Thread.Sleep(1);
        }

        node.Tick();
        return condition();
    }

    /// <summary>
    /// 在后台持续 Tick 指定节点，直到返回的句柄被 Dispose。用于"测试线程要去做别的事
    /// （等事件、发取消），同时泵必须照常推进"的场景；Dispose 会停泵并等最后一轮 Tick 结束。
    /// </summary>
    /// <param name="node">要被驱动的服务者节点；Dispose 之后测试线程才继续断言，避免并发改动磁盘。</param>
    /// <returns>释放即停泵的句柄。</returns>
    protected static IDisposable StartTicking(CourierNode node)
    {
        return new TickingLoop(node);
    }

    /// <summary>
    /// 后台 Tick 循环：每轮 Tick 后短暂让出，Dispose 时取消并同步等待循环退出。
    /// </summary>
    private sealed class TickingLoop : IDisposable
    {
        private readonly CancellationTokenSource mStop = new CancellationTokenSource();
        private readonly Task mPump;

        /// <summary>启动后台泵线程。</summary>
        /// <param name="node">目标节点。</param>
        internal TickingLoop(CourierNode node)
        {
            mPump = Task.Run(async () =>
            {
                while (!mStop.IsCancellationRequested)
                {
                    node.Tick();
                    try
                    {
                        await Task.Delay(5, mStop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            });
        }

        /// <summary>停泵并等待循环线程退出；可重复调用。</summary>
        public void Dispose()
        {
            mStop.Cancel();
            try
            {
                mPump.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }

            mStop.Dispose();
        }
    }
}
