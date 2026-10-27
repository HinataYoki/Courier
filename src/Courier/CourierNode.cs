using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Courier
{
    /// <summary>
    /// 一个 Courier 端点：既是命令的服务者，也是其他端点的调用方（SPEC §1：两端对等，没有客户端/服务端之分）。
    /// <para>
    /// 所有落盘都经由 <see cref="CourierIO.WriteAtomic"/>，所有读取都带 SPEC §3.4 的有界重试。
    /// <see cref="Tick"/> 是非阻塞的泵（SPEC §16）：启动 handler 后立即返回，不等它跑完，
    /// 因此 handler 执行期间心跳、取消与租约续约都照常推进。
    /// </para>
    /// <para>
    /// 线程约束：<see cref="Tick"/> MUST NOT 并发调用（SPEC §16.1）；handler 在线程池上运行，与 Tick 并发。
    /// 能力表在第一次 Tick 之前写完即冻结（SPEC §5），之后再注册会抛 <see cref="InvalidOperationException"/>。
    /// </para>
    /// <para>
    /// 这个类型按职责拆成若干 partial 文件：本文件放字段、打开与关闭的主线、服务故障留痕；
    /// 其余分别是生命周期收尾（Lifecycle：释放锁、铺目录、等待句柄、空闲等待）、注册（Registration）、
    /// 状态与取消（State）、能力表与注册表（Capabilities）、投递（Delivery）、服务泵（Service）与它的
    /// 启动半边（Service.Start）、结果与归档（Results）、热通道服务端（Hot）与调用方（HotClient）、
    /// 内部类型（Types）。
    /// </para>
    /// </summary>
    public sealed partial class CourierNode : IDisposable
    {
        private readonly string mRootDirectory;
        private readonly string mEndpointId;
        private readonly string mDescription;

        /// <summary>
        /// 目录布局。默认取自 <see cref="CourierLayout.Default"/>，也可由 root 下的
        /// courier.layout.json 或调用方显式传入 —— 目录名是接入软件的自由，不是协议的。
        /// </summary>
        private readonly CourierLayout mLayout;

        private readonly Dictionary<string, HandlerEntry> mHandlers;

        /// <summary>保护 <see cref="mInflight"/>：Tick 线程、handler 线程与 Dispose 都会碰它（SPEC §16.1）。</summary>
        private readonly object mGate = new object();

        private readonly Dictionary<string, InflightEntry> mInflight;

        /// <summary>SPEC §5：本次运行的会话标识，每次 Open 换新。读写方据此分辨"同一端点的新一轮运行"。</summary>
        private readonly string mSessionId;

        /// <summary>SPEC §14：本端点声明过的状态名与说明，写入 endpoint.json 的 states 数组。</summary>
        private readonly List<StateEntry> mStates;

        /// <summary>SPEC §5：本端点自述实现了哪些可选能力（<c>state</c>、<c>cancels</c> …），写入 endpoint.json 的 capabilities。</summary>
        private readonly List<string> mCapabilities;

        /// <summary>SPEC §14：每个状态名最近一次发布的 revision，保证单调递增。</summary>
        private readonly Dictionary<string, long> mStateRevisions;

        /// <summary>
        /// 保护 <see cref="mStateRevisions"/>，并把"分配 revision + 落盘"合成一个临界区：
        /// 只保护计数的话，两个线程可能先拿到 2、再让 1 后写盘，磁盘上的 revision 就倒退了（SPEC §14 要求单调）。
        /// </summary>
        private readonly object mStateGate = new object();

        private readonly DateTime mStartedAtUtc;

        /// <summary>SPEC §7、§8 的可选行为开关；默认全部关闭，关闭时行为与没有这个字段时逐字节相同。</summary>
        private readonly CourierNodeOptions mOptions;

        /// <summary>
        /// 有命令落定时置位：<see cref="Dispose"/> 与 <see cref="WaitForIdle"/> 靠它等泵跑空，
        /// 而不是每 5 ms 醒一次去数 <see cref="mInflight"/>。热泵另有一个自己的收割信号。
        /// </summary>
        private readonly AutoResetEvent mSettleEvent = new AutoResetEvent(false);

        /// <summary>SPEC §8：host.lock 的独占句柄，持有到 Dispose。为 null 表示没有开锁。</summary>
        private FileStream? mHostLock;

        /// <summary>上次写 root 注册表的时刻，用于限频刷新（SPEC §7）。</summary>
        private DateTime mLastRegistryPublishUtc = DateTime.MinValue;

        /// <summary>上次清理 archive 的时刻，用于限频（SPEC §15.3）。</summary>
        private DateTime mLastArchiveSweepUtc = DateTime.MinValue;

        /// <summary>上次心跳成功落盘的时刻（UTC Ticks）。注册表的存活快照据此推算，0 表示从未写过。</summary>
        private long mLastHeartbeatTicks;

        /// <summary>最近一次被兜住的服务步骤故障，形如"步骤名: 异常类型: 消息"；没有故障时为 null。</summary>
        private string? mLastServiceFault;

        private string mEndpointDirectory = string.Empty;
        private string mCommandsDirectory = string.Empty;
        private string mClaimsDirectory = string.Empty;
        private string mResultsDirectory = string.Empty;
        private string mDeadletterDirectory = string.Empty;
        private string mCancelsDirectory = string.Empty;
        private string mProgressDirectory = string.Empty;
        private string mStateDirectory = string.Empty;
        private bool mStarted;
        private bool mDisposed;

        /// <summary>
        /// 私有构造：只把字段装进实例，不碰文件系统。真正的初始化（建目录、抢锁、写能力表）在
        /// <see cref="Open(string, string, string?, CourierLayout?)"/> 里做，那里才有失败时收尾的上下文。
        /// </summary>
        /// <param name="rootDirectory">协议根目录。</param>
        /// <param name="endpointId">已校验过的端点标识。</param>
        /// <param name="description">端点自述。</param>
        /// <param name="layout">目录布局；必不为 null。</param>
        /// <param name="options">可选行为开关；null 表示全部取默认。</param>
        private CourierNode(string rootDirectory, string endpointId, string description, CourierLayout layout, CourierNodeOptions? options)
        {
            mRootDirectory = rootDirectory;
            mEndpointId = endpointId;
            mDescription = description;
            mLayout = layout;
            mOptions = options ?? new CourierNodeOptions();
            mHandlers = new Dictionary<string, HandlerEntry>(StringComparer.Ordinal);
            mInflight = new Dictionary<string, InflightEntry>(StringComparer.Ordinal);
            mSessionId = CourierProtocol.NewSessionId();
            mStates = new List<StateEntry>();
            mCapabilities = new List<string>();
            mStateRevisions = new Dictionary<string, long>(StringComparer.Ordinal);
            mStartedAtUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// 最近一次被服务循环兜住的 I/O 故障，形如"步骤名: 异常类型: 消息"。
        /// <para>
        /// 服务步骤的失败 MUST NOT 停摆泵（SPEC §16.2 最后一条），所以它们被就地兜住；但"兜住"不等于
        /// "无声"——一份永远看不到的故障会让宿主把"磁盘满了"误判成"对面没发命令"。这里保留最后一条，
        /// 供宿主记录日志或做健康检查。<see cref="Tick"/> 过程中没有失败时它是 null。
        /// </para>
        /// </summary>
        public string? LastServiceFault
        {
            get
            {
                string? fault = mLastServiceFault;
                return fault;
            }
        }

        /// <summary>打开（必要时创建）root 下的端点目录并写入 endpoint.json（SPEC §5）。</summary>
        /// <param name="rootDirectory">协议根目录；不得位于共享临时目录或云同步目录（SPEC §20）。</param>
        /// <param name="endpointId">端点标识，必须满足 SPEC §4.1 的正则，否则抛出 <see cref="ArgumentException"/> 且不写任何文件。</param>
        /// <param name="description">端点自述，可为 null。</param>
        /// <param name="layout">
        /// 目录布局；为 null 时读 root 下的 courier.layout.json，该文件也不存在则用 <see cref="CourierLayout.Default"/>。
        /// 接入软件可以借此把自己的目录约定（.courier、.agent、.kiro …）原样保留，而不必改协议实现。
        /// </param>
        /// <exception cref="ArgumentException">rootDirectory 为空或 endpointId 非法。</exception>
        /// <exception cref="IOException">目录创建或 endpoint.json 写入失败。</exception>
        /// <exception cref="InvalidDataException">courier.layout.json 存在但不可解析。</exception>
        public static CourierNode Open(string rootDirectory, string endpointId, string? description = null, CourierLayout? layout = null)
        {
            return Open(rootDirectory, endpointId, description, layout, null);
        }

        /// <summary>
        /// 打开端点，并显式指定可选行为（SPEC §7、§8）。<paramref name="options"/> 为 null 时等价于四个参数的重载。
        /// </summary>
        /// <param name="rootDirectory">协议根目录。</param>
        /// <param name="endpointId">端点标识，必须满足 SPEC §4.1。</param>
        /// <param name="description">端点自述，可为 null。</param>
        /// <param name="layout">目录布局；为 null 时读 root 下的 courier.layout.json。</param>
        /// <param name="options">
        /// 可选行为开关。打开 <see cref="CourierNodeOptions.AcquireHostLock"/> 后，本端点被另一个实例服务时
        /// 抛 <see cref="CourierHostLockedException"/>（SPEC §8）；打开 <see cref="CourierNodeOptions.PublishRootRegistry"/>
        /// 后维护 root 级注册表（SPEC §7）。
        /// </param>
        /// <returns>已初始化、可以开始注册能力的节点。</returns>
        /// <exception cref="CourierHostLockedException">该端点已被另一个实例服务（仅在开启 host.lock 时）。</exception>
        public static CourierNode Open(string rootDirectory, string endpointId, string? description, CourierLayout? layout, CourierNodeOptions? options)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("root directory is required", nameof(rootDirectory));
            if (!CourierProtocol.IsValidEndpointId(endpointId))
            {
                // SPEC §4.1：endpointId 是唯一的路径拼接入口，非法时直接拒绝启动且不写任何文件。
                throw new ArgumentException("invalid endpointId: must match ^[a-z0-9]([a-z0-9-]{0,62}[a-z0-9])?$", nameof(endpointId));
            }

            string root = Path.GetFullPath(rootDirectory);
            // 布局必须在建目录之前定下来：它决定 InitializeDirectories 去建哪些名字。
            CourierLayout resolved = layout ?? CourierLayout.Load(root);
            var node = new CourierNode(root, endpointId, description ?? string.Empty, resolved, options);
            try
            {
                node.InitializeDirectories();
            }
            catch
            {
                // 初始化到一半失败时，热通道的池与泵线程可能已经在跑，磁盘上还留着指向它的 descriptor.json。
                // 不收拾就抛，等于把一个后台线程和一个内核对象交给 GC 之外的运气。
                DisposeAfterFailedOpen(node);
                throw;
            }

            return node;
        }

        /// <summary>
        /// 打开失败时的收尾：尽力释放已经启动的东西，但绝不掩盖最初那个异常。
        /// </summary>
        /// <param name="node">初始化失败的节点，尚未交给调用方。</param>
        private static void DisposeAfterFailedOpen(CourierNode node)
        {
            try
            {
                node.Dispose();
            }
            catch (Exception cleanupFailure)
                when (cleanupFailure is IOException
                    || cleanupFailure is UnauthorizedAccessException
                    || cleanupFailure is ObjectDisposedException
                    || cleanupFailure is InvalidOperationException)
            {
                // 收尾再失败也无处可报：调用方要的是最初那条"为什么打不开"的异常，
                // 把它换成一条清理异常只会让诊断变难。
            }
        }

        /// <summary>
        /// 结束本端点的服务。在途 handler 会被取消并最多等 2 秒收尾，期间已产出的终态照常落盘；
        /// 仍跑不完的那些，其命令留在 <c>commands/</c>，租约到期后由下一次 Tick 按 <c>attempts + 1</c> 重试——
        /// 这是 at-least-once 的正常路径，不是丢失（SPEC §1、§10.1）。磁盘上的端点目录、claim 与数据全部保留（SPEC §2）。
        /// </summary>
        public void Dispose()
        {
            if (mDisposed) return;
            mDisposed = true;

            // 先停泵：泵与下面的 ReapInflight 都会走 Finish，两者同时跑就是并发执行服务步骤（SPEC §16.1）。
            // 热通道在 Dispose 期间退场，正在内存里等的那一方会看到对端已死，退回文件轮询。
            StopHotChannel();

            CancelInflightForShutdown();
            DrainInflightForShutdown();

            lock (mGate)
            {
                // 只释放已经停下的那些。对一个还在跑的 handler 释放 CTS，会让它下一次碰 token 时
                // 抛 ObjectDisposedException——那是把"我们不等了"变成"你的代码坏了"。宁可让几个
                // 跑飞的 CTS 交给 GC，也不反过来打断正在执行的用户代码。
                foreach (InflightEntry entry in mInflight.Values) DisposeSource(entry);
                mInflight.Clear();
            }

            // SPEC §7：先把自己从注册表里摘掉，再释放 host.lock。顺序反过来的话，另一个实例可能在
            // "锁已释放、条目还在"的窗口里启动并读到一条指向已退出的我们的记录。
            UnpublishRootRegistry();
            ReleaseHostLock();
            DisposeWaitHandles();
        }

        /// <summary>
        /// 给所有在途 handler 发一次取消。它们可能采纳，也可能忽略——忽略的会在下一行按 deadline 判负。
        /// </summary>
        private void CancelInflightForShutdown()
        {
            List<InflightEntry> entries;
            lock (mGate)
            {
                entries = new List<InflightEntry>(mInflight.Values);
            }

            for (int index = 0; index < entries.Count; index++)
            {
                // 已经跑完并释放过 CTS 的条目会在这里抛 ObjectDisposedException：那不是错误，
                // 只说明这条命令比我们先一步收工。用统一入口把"取消来得太晚"这一种情况吃掉。
                TryCancelSource(entries[index]);
            }
        }

        /// <summary>
        /// 等已经采纳取消的 handler 收工，最多两秒。每有命令落定都会置位 <see cref="mSettleEvent"/>，
        /// 所以这里是"等到有动静或到点"，不是固定间隔的空转（SPEC §16.2：Dispose 不阻塞服务循环）。
        /// </summary>
        private void DrainInflightForShutdown()
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            while (true)
            {
                ReapInflight();
                lock (mGate)
                {
                    if (mInflight.Count == 0) return;
                }

                int remainingMs = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remainingMs <= 0) return;
                mSettleEvent.WaitOne(remainingMs < 50 ? remainingMs : 50);
            }
        }

        /// <summary>
        /// 释放本节点自己的内核等待句柄。<see cref="StopHotChannel"/> 已经先停了泵，所以这里不会与
        /// 正在等待的线程抢同一个句柄；泵若没能在约定时间内退场，它等的是自己的局部引用，看到的
        /// <see cref="ObjectDisposedException"/> 会走它已有的退出分支。
        /// </summary>
        private void DisposeWaitHandles()
        {
            try { mHotStop.Dispose(); }
            catch (ObjectDisposedException) { }

            try { mHotWake.Dispose(); }
            catch (ObjectDisposedException) { }

            try { mSettleEvent.Dispose(); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>Dispose 之后继续用这个实例是编程错误，MUST 当场报出来而不是静默无操作。</summary>
        /// <exception cref="ObjectDisposedException">端点已关闭。</exception>
        private void ThrowIfDisposed()
        {
            if (mDisposed) throw new ObjectDisposedException(nameof(CourierNode));
        }

        /// <summary>
        /// 一次服务步骤的 I/O 故障留痕。服务循环 MUST NOT 因为某一步失败就停摆（SPEC §16.2 最后一条），
        /// 所以异常会被兜住；这里保证兜住之后至少留下可查的最后一条记录。
        /// </summary>
        /// <param name="step">失败的步骤名，用于定位是哪一段出了问题。</param>
        /// <param name="caught">被兜住的异常。</param>
        private void RecordServiceFault(string step, Exception caught)
        {
            mLastServiceFault = step + ": " + caught.GetType().Name + ": " + caught.Message;
        }

        /// <summary>
        /// 服务循环只兜 I/O 类故障；编程错误（NullReference 等）必须冒出来，不能被静默吞掉。
        /// <see cref="NotSupportedException"/> 也算在内：网络盘与某些虚拟文件系统把它当作"这个操作不支持"。
        /// </summary>
        /// <param name="caught">捕获到的异常。</param>
        /// <returns>是否属于应当被兜住的 I/O 类故障。</returns>
        private static bool IsIoFailure(Exception caught)
        {
            return caught is IOException || caught is UnauthorizedAccessException || caught is NotSupportedException;
        }
    }
}
