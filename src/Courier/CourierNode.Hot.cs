using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Courier
{
    /// <summary>
    /// <see cref="CourierNode"/> 的热通道服务端：共享内存池的泵、槽处理与回铃（docs/HOT-CHANNEL.md）。
    /// </summary>
    public sealed partial class CourierNode
    {
        // ---- 热通道（docs/HOT-CHANNEL.md）----
        // 三个对象一起存在或一起缺席：命名空间给了"同一个 root 的同一代宿主"一个不可枚举的地址空间，
        // 池是那里面唯一的共享内存，账本是"这条消息走的是内存而不是文件"的唯一证据。
        private CourierHotNamespace? mHotNamespace;
        private CourierHotPool? mHotPool;
        private CourierHotJournal? mHotJournal;

        /// <summary>保护 <see cref="mHotWaiters"/>：Tick 线程登记等待者，handler 线程写终态时取走它。</summary>
        private readonly object mHotGate = new object();

        /// <summary>保护服务步骤：Tick 与 <see cref="Pump"/> 都从这里进，因此二者互斥（SPEC §16.1）。</summary>
        private readonly object mServiceGate = new object();

        /// <summary>requestId → 正在等它的那个槽。热通道只送"去看一眼"，终态仍然由 results/ 落盘。</summary>
        private readonly Dictionary<string, HotWaiter> mHotWaiters = new Dictionary<string, HotWaiter>(StringComparer.Ordinal);

        private readonly byte[] mHotBuffer = new byte[CourierHot.BODY_BYTES];

        /// <summary>泵线程的等待集合：第 0 位是停泵信号，其后逐槽对应宿主门铃（<see cref="CourierHotPool.HostBells"/>）。</summary>
        private WaitHandle[]? mHotWaitSet;

        /// <summary>泵线程：只在"某个槽上有帧"或"空闲超时"时醒来，其余时间阻塞在内核里。</summary>
        private Thread? mHotPump;

        /// <summary>停泵信号。它在等待集合的第 0 位，置位即让 <see cref="WaitHandle.WaitAny(WaitHandle[], int)"/> 立刻返回。</summary>
        private readonly ManualResetEvent mHotStop = new ManualResetEvent(false);

        /// <summary>
        /// 收割信号：handler 落定时由续延置位，让泵立刻跑一轮服务步骤去写终态。
        /// 没有它，终态只能等下一次 Tick 或泵的空闲超时——那正好把热通道省下来的延迟又还回去。
        /// </summary>
        private readonly AutoResetEvent mHotWake = new AutoResetEvent(false);

        /// <summary>客户端侧的通道租约表。一条热通道同时只服务一条在途请求，用完归还复用。</summary>
        private readonly CourierHotClient mHotClient = new CourierHotClient();

        /// <summary>endpointId → 对端 hot 描述。命中的正结果不再重读 endpoint.json，失效后（对端换代）重读。</summary>
        private readonly Dictionary<string, HotLookup> mHotLookups = new Dictionary<string, HotLookup>(StringComparer.Ordinal);

        /// <summary>建连与抢槽的等待上限。它不是命令的 deadline——超时就当对端没有热通道。</summary>
        private const int HOT_OPEN_TIMEOUT_MS = 50;

        /// <summary>泵的空闲唤醒周期：没有门铃时也要定期清扫死主占着的槽与过期的等待者。</summary>
        private const int HOT_PUMP_IDLE_MS = 100;

        /// <summary>Dispose 等泵退场的上限。泵不配合也继续关池——它已经不在等待集合里了。</summary>
        private const int HOT_PUMP_JOIN_MS = 500;

        /// <summary>等待者存活期。超过这么久还没等到终态，调用方早就放弃了，登记只会让这个字典无限增长。</summary>
        private const int HOT_WAITER_LIFETIME_SECONDS = 60;

        /// <summary>对端 hot 描述的缓存期。读一次 endpoint.json 比一次热往返贵两个数量级，不能每次调用都读盘。</summary>
        private const int HOT_DESCRIPTOR_TTL_SECONDS = 30;

        /// <summary>热等待的预算上限。宿主慢的时候文件轮询仍是兜底，旁路绝不吃满整条命令的等待时间。</summary>
        private const int HOT_WAIT_CAP_MS = 250;

        /// <summary>
        /// 建出这一代宿主的私有命名空间、槽池与账本。三步任意一步不成立就整体放弃热通道，
        /// 绝不留"池建好了但账本写不出来"这种半截状态——那会让证据链断在最不好查的地方。
        /// </summary>
        private void StartHotChannel()
        {
            // 两道门：宿主显式关掉（CourierNodeOptions.EnableHotChannel），或这台机器上没有可用的
            // 共享内存原语。任何一种都只是没有热通道——命令仍走 commands/ → results/，协议行为不变。
            if (!mOptions.EnableHotChannel || !CourierHot.IsSupported) return;

            string alias = CourierHot.NamespaceAlias(mRootDirectory, mEndpointId, mSessionId);
            CourierHotNamespace? ns = CourierHotNamespace.Create(mRootDirectory, mEndpointId, alias);
            if (ns == null) return;

            CourierHotPool? pool = CreateHotPool(alias);
            if (pool == null)
            {
                ns.Dispose();
                return;
            }

            mHotNamespace = ns;
            mHotPool = pool;
            mHotJournal = OpenHotJournal();

            // 会合信息写进实现私有目录 endpoints/<id>/hot/descriptor.json，绝不写进 endpoint.json：
            // SPEC §5.2 规定 v2 的 endpoint.json 里 hot 字段 MUST NOT 出现，而 §2 明确允许实现私有文件。
            // 写不出来就当作没有热通道（对端退回文件协议），不影响本端点服务。
            CourierHotDescriptor? descriptor = HotDescriptorOrNull();
            if (descriptor == null || !descriptor.TryWrite(mEndpointDirectory, mLayout))
            {
                StopHotChannel();
            }
        }

        /// <summary>
        /// 建共享内存池。建不出来只说明这台机器上热通道不可用，服务退回文件协议即可（docs/HOT-CHANNEL.md §5）；
        /// 但仍要留一条记录，否则"热通道为什么没生效"在宿主侧无从判断。
        /// </summary>
        /// <param name="alias">本次会话的命名空间别名。</param>
        /// <returns>建好的池；不可用时为 null。</returns>
        private CourierHotPool? CreateHotPool(string alias)
        {
            try
            {
                return CourierHotPool.Create(alias, mRootDirectory, mEndpointId, CourierProtocol.CurrentProcessId());
            }
            catch (Exception caught) when (IsIoFailure(caught))
            {
                RecordServiceFault("hot-pool", caught);
                return null;
            }
        }

        /// <summary>
        /// 按宿主开关打开热通道账本。账本默认不开（<see cref="CourierNodeOptions.EnableHotJournal"/>）：
        /// 它是"这条消息走的是内存"的旁证，强证据始终是 <c>results/&lt;requestId&gt;.json</c>；
        /// 开或不开、写不写得出来，都不影响热通道本身。
        /// </summary>
        /// <returns>账本；未启用或打不开时为 null。</returns>
        private CourierHotJournal? OpenHotJournal()
        {
            if (!mOptions.EnableHotJournal) return null;
            try
            {
                string? detail;
                return CourierHotJournal.Open(Path.Combine(mEndpointDirectory, mLayout.HotEvidenceDirectory), mSessionId, out detail);
            }
            catch (Exception caught) when (IsIoFailure(caught))
            {
                // 日志打不开只丢诊断证据，不影响服务能力。
                RecordServiceFault("hot-journal", caught);
                return null;
            }
        }

        /// <summary>本端点的 hot 描述；池或命名空间缺席时返回 null，于是实现私有目录里没有描述文件。</summary>
        private CourierHotDescriptor? HotDescriptorOrNull()
        {
            CourierHotPool? pool = mHotPool;
            CourierHotNamespace? ns = mHotNamespace;
            if (pool == null || ns == null) return null;
            return new CourierHotDescriptor(ns.Alias, CourierProtocol.CurrentProcessId(), pool.SlotCount, CourierHot.CAPACITY);
        }

        /// <summary>第一次 Tick 之后把泵跑起来。同一代宿主只起一条泵线程；Tick 本身不被它阻塞。</summary>
        private void EnsureHotPump()
        {
            CourierHotPool? pool = mHotPool;
            if (pool == null) return;
            if (mHotPump != null) return;

            EventWaitHandle[]? bells = pool.HostBells;
            if (bells == null) return;

            // 等待集合的次序是 Pump 的判定依据，改这里必须同步改那边：
            // 0 = 停泵，1 = 收割，2 = 新连接，3 起 = 每个槽一个"有请求"门铃。
            var set = new WaitHandle[bells.Length + 3];
            set[0] = mHotStop;
            set[1] = mHotWake;
            set[2] = pool.AcceptBell;
            for (int index = 0; index < bells.Length; index++) set[index + 3] = bells[index];
            mHotWaitSet = set;

            var pump = new Thread(Pump);
            mHotPump = pump;
            pump.IsBackground = true;
            pump.Name = "courier-hot-pump-" + mEndpointId;
            pump.Start();
        }

        /// <summary>
        /// 泵线程主体。它只做三件事：排空有帧的槽、跑一轮服务步骤、清扫过期登记。
        /// <para>
        /// 未捕获异常会带走整个宿主进程，所以这里兜住一切——但绝不静默：故障写进热账本，
        /// 泵自行退场，端点退回纯文件协议仍然完整可用（SPEC §16.2 最后一条的等价物）。
        /// </para>
        /// </summary>
        private void Pump()
        {
            try
            {
                PumpLoop();
            }
            catch (Exception caught)
            {
                RecordHotFault(caught);
            }
        }

        /// <summary>
        /// 热泵的循环体：等内核门铃，收到就驱动一轮服务步骤，直到 <see cref="mHotStop"/> 被置位。
        /// <para>
        /// 它是"宿主管不着 Tick 时也能及时收割"的那条腿。任何未预期的异常都让它安静退场，
        /// 服务语义退回纯文件通道（docs/HOT-CHANNEL.md §5）。
        /// </para>
        /// </summary>
        private void PumpLoop()
        {
            WaitHandle[]? set = mHotWaitSet;
            CourierHotPool? pool = mHotPool;
            if (set == null || pool == null) return;

            while (true)
            {
                int index;
                try
                {
                    index = WaitHandle.WaitAny(set, HOT_PUMP_IDLE_MS);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (index == 0) return;
                if (mDisposed) return;

                bool service;
                if (index == WaitHandle.WaitTimeout)
                {
                    // 空闲唤醒：把死主占着的槽和等太久没人取的登记收掉，然后照常收割一轮。
                    SweepHotWaiters();
                    try { pool.SweepDeadOwners(); }
                    catch (ObjectDisposedException) { return; }
                    service = true;
                }
                else if (index == 2)
                {
                    try { pool.SweepDeadOwners(); }
                    catch (ObjectDisposedException) { return; }
                    service = true;
                }
                else if (index > 2)
                {
                    service = DrainSlot(pool, index - 3);
                }
                else
                {
                    // index == 1：handler 落定的信号，只需要一轮服务步骤把终态写出去。
                    service = true;
                }

                if (service) RunHotServicePass();
            }
        }

        /// <summary>
        /// 排空一个槽的客户端→宿主方向。门铃会被内核合并，所以必须读到空为止，不能只读一条。
        /// </summary>
        /// <param name="pool">本端点的槽池。</param>
        /// <param name="slot">门铃对应的那个槽。</param>
        /// <returns>是否登记了新请求，需要跟着跑一轮服务步骤。</returns>
        private bool DrainSlot(CourierHotPool pool, int slot)
        {
            bool registered = false;
            while (true)
            {
                // kind/length 声明在 try 之外：out 变量的作用域就是它所在的那个块，
                // 写在调用行里只能让 try 块自己看见，出了块就查不到这个名字。
                bool received;
                ushort kind;
                int length;
                try
                {
                    received = pool.Receive(slot, true, out _, out kind, mHotBuffer, out length);
                }
                catch (ObjectDisposedException)
                {
                    return registered;
                }

                if (!received) return registered;
                if (kind != CourierHot.KIND_REQUEST || length <= 0) continue;
                if (!TryRegisterHotRequest(pool, slot, length)) continue;
                registered = true;
            }
        }

        /// <summary>
        /// 处理一帧已经读进 <see cref="mHotBuffer"/> 的请求：解析、认槽主、登记等待者。
        /// </summary>
        /// <param name="pool">本端点的槽池。</param>
        /// <param name="slot">帧来自哪个槽。</param>
        /// <param name="length">正文有效字节数。</param>
        /// <returns>登记了新等待者时为 true；这一帧被丢弃（解析失败或槽已易主）时为 false。</returns>
        private bool TryRegisterHotRequest(CourierHotPool pool, int slot, int length)
        {
            string text = Encoding.UTF8.GetString(mHotBuffer, 0, length);
            if (!CourierHotFrame.TryParseRequest(
                text,
                out string requestId,
                out string issuedBy,
                out string channel,
                out string action,
                out string payload,
                out DateTime deadlineUtc))
            {
                return false;
            }

            // 认下这个槽当前的主人。等结果的时间里槽可能被回收转手，回铃时就靠这两个值确认
            // 帧还写不写得进去；读不到归属说明槽已经不在任何人手里，这轮直接放弃。
            if (!pool.TryReadOwnership(slot, out int ownerPid, out int generation)) return false;

            // 这条命令可能已经被 Tick 那一轮处理完了（两条唤醒路径竞争同一个 requestId 是常态）。
            // 直接把已有的终态回过去，别让对面白等一个 budget。
            CourierResponse? settled = TryReadResult(requestId);
            if (settled != null)
            {
                SendHotResponse(pool, slot, ownerPid, generation, settled, issuedBy, channel, action, DateTime.UtcNow.Ticks);
                return false;
            }

            long createdUtcTicks = DateTime.UtcNow.Ticks;
            lock (mHotGate)
            {
                mHotWaiters[requestId] = new HotWaiter(slot, ownerPid, generation, issuedBy, channel, action, createdUtcTicks);
            }

            // deadline 只用来让"对端已经放弃"的登记早点被清掉，执行与否由 commands/ 里那条记录说话。
            _ = deadlineUtc;
            return true;
        }

        /// <summary>读一条已经存在的终态。读不到就说明还在途，交给等待者登记。</summary>
        private CourierResponse? TryReadResult(string requestId)
        {
            string? text;
            if (!CourierIO.TryReadAllText(Path.Combine(mResultsDirectory, requestId + CourierProtocol.JSON_EXTENSION), out text)) return null;
            return CourierResponse.TryParse(text);
        }

        /// <summary>
        /// handler 落定时的续延：只负责叫醒泵，不做任何业务动作。
        /// <para>
        /// 两个信号都要置位：热泵用 <see cref="mHotWake"/> 驱动一轮收割，而
        /// <see cref="Dispose"/> 与 <see cref="WaitForIdle"/> 在 <see cref="mSettleEvent"/> 上等泵跑空。
        /// 漏掉后者的后果是它们只能靠超时收场——那正是"每 5 ms 醒一次数一遍"要解决的问题。
        /// </para>
        /// </summary>
        /// <param name="settled">刚落定的 handler 任务；续延只把它当作一次事件。</param>
        private void SettleWake(Task<string> settled)
        {
            _ = settled;
            try { mHotWake.Set(); }
            catch (ObjectDisposedException) { }

            try { mSettleEvent.Set(); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// 热泵驱动的一轮服务步骤。与 Tick 走同一道门（SPEC §16.1），因此绝不会与宿主自己的 Tick 同时执行。
        /// I/O 故障按 Tick 的既有契约兜住；其它异常冒到 PumpLoop 外面由泵自己退场。
        /// </summary>
        private void RunHotServicePass()
        {
            if (mDisposed) return;
            lock (mServiceGate)
            {
                ServiceOnce();
            }
        }

        /// <summary>
        /// 终态落盘后回铃叫醒调用方。没有登记过的 requestId 直接返回——那条命令是宿主从 commands/ 里
        /// 自己发现的，本来就没有人在内存里等它。
        /// </summary>
        private void SignalHot(CourierResponse response)
        {
            CourierHotPool? pool = mHotPool;
            if (pool == null) return;

            HotWaiter? waiter;
            lock (mHotGate)
            {
                if (!mHotWaiters.TryGetValue(response.RequestId, out waiter)) return;
                mHotWaiters.Remove(response.RequestId);
            }

            if (waiter == null) return;
            SendHotResponse(pool, waiter.Slot, waiter.OwnerPid, waiter.Generation, response, waiter.IssuedBy, waiter.Channel, waiter.Action, waiter.CreatedUtcTicks);
        }

        /// <summary>
        /// 把一份终态写进对应槽的宿主→客户端环，并记一条账。投递失败不回退重投：文件通道本来就是权威。
        /// </summary>
        /// <param name="pool">宿主侧池。</param>
        /// <param name="slot">目标槽号。</param>
        /// <param name="ownerPid">登记时那个槽的主人进程号。</param>
        /// <param name="generation">登记时那个槽的代数；<c>0</c> 表示不校验。</param>
        /// <param name="response">要回的终态。</param>
        /// <param name="issuedBy">账本字段：发起方。</param>
        /// <param name="channel">账本字段：通道。</param>
        /// <param name="action">账本字段：动作。</param>
        /// <param name="createdUtcTicks">账本字段：等待起点。</param>
        /// <remarks>
        /// 走 <see cref="CourierHotPool.SendToOwner"/> 而不是 <see cref="CourierHotPool.Send"/>：
        /// 等结果的这段时间里调用方可能已经放弃、槽被回收转手给了另一个人，此时无条件写会把
        /// 一份不相干的终态塞进新主人的环里。确认不了主人就不投，文件通道那边本来就有一份。
        /// </remarks>
        private void SendHotResponse(
            CourierHotPool pool,
            int slot,
            int ownerPid,
            int generation,
            CourierResponse response,
            string issuedBy,
            string channel,
            string action,
            long createdUtcTicks)
        {
            string? frame = CourierHotFrame.BuildResponse(response);
            if (frame == null) return;
            byte[] body = Encoding.UTF8.GetBytes(frame);

            bool sent;
            try
            {
                sent = pool.SendToOwner(slot, ownerPid, generation, false, slot, CourierHot.KIND_RESPONSE, body, body.Length);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            CourierHotJournal? journal = mHotJournal;
            if (journal == null) return;
            try
            {
                long elapsedMicros = (DateTime.UtcNow.Ticks - createdUtcTicks) / 10;
                journal.Append(CourierHotFrame.BuildJournalLine(
                    response.RequestId,
                    issuedBy,
                    channel,
                    action,
                    sent ? response.Status : "fallback",
                    sent ? response.Reason : "response ring is full",
                    Encoding.UTF8.GetByteCount(response.Payload),
                    elapsedMicros < 0 ? 0 : elapsedMicros,
                    slot));
            }
            catch (ObjectDisposedException) { }
        }
    }
}
