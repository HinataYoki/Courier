using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Courier
{
    /// <summary>
    /// <see cref="CourierNode"/> 的开关生命周期：释放 <c>host.lock</c>、建目录、处置等待句柄、空闲等待。
    /// <para>
    /// 这些动作只在打开与关闭两个时刻发生一次，与命令处理路径无关，因此单独成一个文件，
    /// 让主文件只看得到"节点由哪些字段组成、怎么被打开和关闭"。
    /// </para>
    /// </summary>
    public sealed partial class CourierNode
    {
        /// <summary>释放 host.lock 的独占句柄；重复调用安全（句柄已空时什么都不做）。</summary>
        private void ReleaseHostLock()
        {
            FileStream? stream = mHostLock;
            if (stream == null) return;
            mHostLock = null;
            try
            {
                AppDomain.CurrentDomain.ProcessExit -= ReleaseHostLockOnExit;
            }
            catch (Exception caught) when (caught is InvalidOperationException || caught is ArgumentException)
            {
                // 订阅/退订在 AppDomain 卸载期间可能失败；锁本身的释放不受影响。
            }

            try { stream.Dispose(); }
            catch (Exception caught) when (IsIoFailure(caught)) { }
        }

        /// <summary>
        /// 建出本端点的目录树、抢 host.lock、起热通道，最后把能力表与心跳写下去（SPEC §2、§5、§8）。
        /// 顺序是硬性的：锁在铺开子目录之前、endpoint.json 在能力表冻结之后、注册表在端点已能服务之后。
        /// </summary>
        private void InitializeDirectories()
        {
            string endpointsRoot = CourierLayout.Combine(mRootDirectory, mLayout.EndpointsDirectory);
            CourierIO.EnsureDirectory(endpointsRoot);

            // SPEC §4.1 已在 Open 里校验过 endpointId，这里是唯一一处把它拼进路径的地方。
            mEndpointDirectory = Path.Combine(endpointsRoot, mEndpointId);
            mCommandsDirectory = Path.Combine(mEndpointDirectory, mLayout.CommandsDirectory);
            mClaimsDirectory = Path.Combine(mEndpointDirectory, mLayout.ClaimsDirectory);
            mResultsDirectory = Path.Combine(mEndpointDirectory, mLayout.ResultsDirectory);
            mDeadletterDirectory = Path.Combine(mEndpointDirectory, mLayout.DeadletterDirectory);
            mCancelsDirectory = Path.Combine(mEndpointDirectory, mLayout.CancelsDirectory);
            mProgressDirectory = Path.Combine(mEndpointDirectory, mLayout.ProgressDirectory);
            mStateDirectory = Path.Combine(mEndpointDirectory, mLayout.StateDirectory);

            // SPEC §8：先抢锁再建目录。反过来的话，两个实例会同时走进来把目录铺开，第二个却在最后一步才被拒绝——
            // 那样"失败时不留半初始化目录"就成了一句空话。锁文件本身所在的目录必须先存在，所以顺序是先建
            // endpoints/<id>/ 再抢锁，成功之后才铺开其余子目录。
            if (mOptions.AcquireHostLock)
            {
                AcquireHostLockOrThrow(endpointsRoot);
            }

            EnsureEndpointDirectories();

            // 先建池再写描述：读方看到 descriptor.json 的那一刻，池与门铃必须已经在等着被打开，
            // 否则它按描述去连一个还不存在的名字，只能白跑一趟回退文件通道（docs/HOT-CHANNEL.md §5.1）。
            StartHotChannel();

            // SPEC §5：把本端点真正实现的可选能力写进能力目录。这是自述，不是协商——
            // 读方据此决定要不要降级（§21），缺声明也不构成拒绝投递的理由。
            DeclareImplementedCapabilities();

            // SPEC §5：endpoint.json 必须整体原子替换，绝不能增量追加——追加会留下永远 rejected/unknown-action 的幽灵能力。
            WriteEndpointJson();
            WriteHeartbeat();

            // SPEC §7：端点已经能服务了，此时把它的存在告诉 root。写失败只是让注册表少一条，不影响服务。
            PublishRootRegistry(true);
        }

        /// <summary>
        /// 拼出 <c>endpoints/&lt;endpointId&gt;/</c> 的绝对路径。只在 endpointId 已通过 SPEC §4.1 校验之后可用。
        /// </summary>
        /// <param name="endpointId">目标端点标识。</param>
        /// <returns>该端点目录的绝对路径。</returns>
        private string EndpointDirectoryOf(string endpointId)
        {
            return Path.Combine(CourierLayout.Combine(mRootDirectory, mLayout.EndpointsDirectory), endpointId);
        }

        /// <summary>按布局铺开端点目录下的全部子目录（SPEC §2：实现 SHOULD 在启动时创建缺失目录）。</summary>
        private void EnsureEndpointDirectories()
        {
            CourierIO.EnsureDirectory(mEndpointDirectory);
            CourierIO.EnsureDirectory(mCommandsDirectory);
            CourierIO.EnsureDirectory(mClaimsDirectory);
            CourierIO.EnsureDirectory(mResultsDirectory);
            CourierIO.EnsureDirectory(mDeadletterDirectory);
            CourierIO.EnsureDirectory(mCancelsDirectory);
            CourierIO.EnsureDirectory(mProgressDirectory);
            CourierIO.EnsureDirectory(mStateDirectory);
        }

        /// <summary>
        /// 按本节点**实际启用**的开关声明可选能力（SPEC §5）。
        /// <para>
        /// 声明的是"这个进程真的做了"，不是"这份协议定义了"。所以每一项都跟着 <see cref="CourierNodeOptions"/>
        /// 走：没开 host.lock 就不声明 <c>host-lock</c>，没开注册表就不声明 <c>registry</c>——
        /// 一份把没做的事也报上去的自述，比不写能力目录更糟（§5.2 对"声明了但可能没人实现"的批评同样适用）。
        /// </para>
        /// <para>
        /// <c>state</c>、<c>cancels</c> 与 <c>progress</c> 是无条件实现的（§14、§13.1、§13.2 的代码始终在），
        /// 所以无条件声明。
        /// </para>
        /// </summary>
        private void DeclareImplementedCapabilities()
        {
            DeclareCapability(CourierProtocol.CAPABILITY_STATE);
            DeclareCapability(CourierProtocol.CAPABILITY_CANCELS);
            DeclareCapability(CourierProtocol.CAPABILITY_PROGRESS);
            if (mOptions.AcquireHostLock) DeclareCapability(CourierProtocol.CAPABILITY_HOST_LOCK);
            if (mOptions.PublishRootRegistry) DeclareCapability(CourierProtocol.CAPABILITY_REGISTRY);
        }

        /// <summary>
        /// 以独占方式持有 <c>endpoints/&lt;id&gt;/host.lock</c>（SPEC §8）。抢不到就抛，绝不当成"先继续跑再说"：
        /// 两个服务者同时跑会互相接管租约、重复执行 handler，那比启动失败糟得多。
        /// </summary>
        /// <param name="endpointsRoot">已经创建好的 <c>endpoints/</c> 目录。</param>
        private void AcquireHostLockOrThrow(string endpointsRoot)
        {
            string endpointDirectory = Path.Combine(endpointsRoot, mEndpointId);
            CourierIO.EnsureDirectory(endpointDirectory);
            FileStream stream = CourierHostLock.Acquire(mEndpointId, Path.Combine(endpointDirectory, CourierProtocol.HOST_LOCK_FILE_NAME));
            mHostLock = stream;
            // 抢到锁的这一刻起就必须保证它会被释放：后续任何一步初始化失败，句柄都不能泄漏到进程退出。
            AppDomain.CurrentDomain.ProcessExit += ReleaseHostLockOnExit;
        }

        /// <summary>
        /// 进程退出时兜底释放 host.lock。正常路径走 <see cref="Dispose"/>，这里只防止"宿主没 Dispose 就退出"
        /// 把锁留到 OS 回收为止——虽然 OS 最终会回收，但同一进程里再开一个端点会先撞上自己。
        /// </summary>
        private void ReleaseHostLockOnExit(object? sender, EventArgs arguments)
        {
            FileStream? stream = mHostLock;
            mHostLock = null;
            if (stream == null) return;
            try { stream.Dispose(); }
            catch (Exception caught) when (IsIoFailure(caught)) { }
        }

        /// <summary>
        /// 反复 Tick 直到没有在途 handler，或超时。宿主在退出前、测试在断言前用它把泵跑空。
        /// <para>
        /// 这是"驱动泵"的便利入口，不改变 SPEC §16 的任何语义：Tick 仍然 MUST NOT 并发调用，
        /// 所以它 MUST NOT 与宿主自己的 Tick 循环并行跑。
        /// </para>
        /// </summary>
        /// <param name="timeout">等待上限；到点仍有在途 handler 时直接返回，不抛异常。</param>
        /// <exception cref="ObjectDisposedException">端点已关闭。</exception>
        public void WaitForIdle(TimeSpan timeout)
        {
            ThrowIfDisposed();
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                Tick();
                lock (mGate)
                {
                    if (mInflight.Count == 0) return;
                }

                int remainingMs = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remainingMs <= 0) return;
                mSettleEvent.WaitOne(remainingMs < 50 ? remainingMs : 50);
            }
        }

    }
}
