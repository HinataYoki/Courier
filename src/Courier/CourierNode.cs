using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Courier
{
    /// <summary>
    /// 一个 Courier 端点：既是命令的服务者，也是其他端点的调用方（SPEC §1：两端对等，没有客户端/服务端之分）。
    /// <para>
    /// 所有落盘都经由 <see cref="CourierIO.WriteAtomic"/>，所有读取都带 SPEC §3.4 的有界重试。
    /// <see cref="Tick"/> 是非阻塞的泵（SPEC §13）：启动 handler 后立即返回，不等它跑完，
    /// 因此 handler 执行期间心跳、取消与租约续约都照常推进。
    /// </para>
    /// <para>
    /// 线程约束：<see cref="Tick"/> MUST NOT 并发调用（SPEC §13.1）；handler 在线程池上运行，与 Tick 并发。
    /// 能力表在第一次 Tick 之前写完即冻结（SPEC §5），之后再注册会抛 <see cref="InvalidOperationException"/>。
    /// </para>
    /// </summary>
    public sealed class CourierNode : IDisposable
    {
        private readonly string mRootDirectory;
        private readonly string mEndpointId;
        private readonly string mDescription;
        private readonly Dictionary<string, HandlerEntry> mHandlers;

        /// <summary>保护 <see cref="mInflight"/>：Tick 线程、handler 线程与 Dispose 都会碰它（SPEC §13.1）。</summary>
        private readonly object mGate = new object();

        private readonly Dictionary<string, InflightEntry> mInflight;
        private readonly DateTime mStartedAtUtc;
        private string mEndpointDirectory = string.Empty;
        private string mCommandsDirectory = string.Empty;
        private string mClaimsDirectory = string.Empty;
        private string mResultsDirectory = string.Empty;
        private string mDeadletterDirectory = string.Empty;
        private string mCancelsDirectory = string.Empty;
        private string mProgressDirectory = string.Empty;
        private bool mStarted;
        private bool mDisposed;

        private CourierNode(string rootDirectory, string endpointId, string description)
        {
            mRootDirectory = rootDirectory;
            mEndpointId = endpointId;
            mDescription = description;
            mHandlers = new Dictionary<string, HandlerEntry>(StringComparer.Ordinal);
            mInflight = new Dictionary<string, InflightEntry>(StringComparer.Ordinal);
            mStartedAtUtc = DateTime.UtcNow;
        }

        /// <summary>打开（必要时创建）root 下的端点目录并写入 endpoint.json（SPEC §5）。</summary>
        /// <param name="rootDirectory">协议根目录；不得位于共享临时目录或云同步目录（SPEC §17）。</param>
        /// <param name="endpointId">端点标识，必须满足 SPEC §4.1 的正则，否则抛出 <see cref="ArgumentException"/> 且不写任何文件。</param>
        /// <param name="description">端点自述，可为 null。</param>
        /// <exception cref="ArgumentException">rootDirectory 为空或 endpointId 非法。</exception>
        /// <exception cref="IOException">目录创建或 endpoint.json 写入失败。</exception>
        public static CourierNode Open(string rootDirectory, string endpointId, string? description = null)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("root directory is required", nameof(rootDirectory));
            if (!CourierProtocol.IsValidEndpointId(endpointId))
            {
                // SPEC §4.1：endpointId 是唯一的路径拼接入口，非法时直接拒绝启动且不写任何文件。
                throw new ArgumentException("invalid endpointId: must match ^[a-z0-9]([a-z0-9-]{0,62}[a-z0-9])?$", nameof(endpointId));
            }

            string root = Path.GetFullPath(rootDirectory);
            var node = new CourierNode(root, endpointId, description ?? string.Empty);
            node.InitializeDirectories();
            return node;
        }

        /// <summary>注册一个同步能力。同一个 (channel, action) 在一个端点上只能注册一次，重复注册是编程错误（SPEC §5）。</summary>
        /// <param name="channel">信道名，须满足 SPEC §4.3。</param>
        /// <param name="action">动作名，须满足 SPEC §4.3。</param>
        /// <param name="summary">面向人和工具的说明，写入 endpoint.json。</param>
        /// <param name="handler">处理函数；<c>token</c> 在投递方请求取消时被触发（SPEC §11.1）。</param>
        /// <exception cref="ArgumentException">channel 或 action 非法，或 (channel, action) 已注册。</exception>
        /// <exception cref="ArgumentNullException">handler 为 null。</exception>
        /// <exception cref="InvalidOperationException">第一次 Tick 之后才注册（SPEC §5：能力表已冻结）。</exception>
        public void Handle(string channel, string action, string summary, Func<string, CancellationToken, string> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            Register(channel, action, summary, (payload, token) => Task.FromResult(handler(payload, token)));
        }

        /// <summary>注册一个忽略取消的同步能力。仅用于不需要响应取消的短 handler。</summary>
        /// <param name="channel">信道名，须满足 SPEC §4.3。</param>
        /// <param name="action">动作名，须满足 SPEC §4.3。</param>
        /// <param name="summary">面向人和工具的说明，写入 endpoint.json。</param>
        /// <param name="handler">处理函数，入参与返回值都是不透明字符串（SPEC §7）。</param>
        public void Handle(string channel, string action, string summary, Func<string, string> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            Register(channel, action, summary, (payload, token) => Task.FromResult(handler(payload)));
        }

        /// <summary>注册一个异步能力。长命宿主 SHOULD 使用它，handler 与 Tick 并发推进（SPEC §13.1）。</summary>
        /// <param name="channel">信道名，须满足 SPEC §4.3。</param>
        /// <param name="action">动作名，须满足 SPEC §4.3。</param>
        /// <param name="summary">面向人和工具的说明，写入 endpoint.json。</param>
        /// <param name="handler">返回 <see cref="Task{String}"/> 的处理函数；<c>token</c> 在取消请求到达时触发。</param>
        public void HandleAsync(string channel, string action, string summary, Func<string, CancellationToken, Task<string>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            Register(channel, action, summary, handler);
        }

        /// <summary>注册一个忽略取消的异步能力。</summary>
        /// <param name="channel">信道名，须满足 SPEC §4.3。</param>
        /// <param name="action">动作名，须满足 SPEC §4.3。</param>
        /// <param name="summary">面向人和工具的说明，写入 endpoint.json。</param>
        /// <param name="handler">返回 <see cref="Task{String}"/> 的处理函数。</param>
        public void HandleAsync(string channel, string action, string summary, Func<string, Task<string>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            Register(channel, action, summary, (payload, token) => handler(payload));
        }

        /// <summary>
        /// 驱动一轮服务者循环（SPEC §13）：写心跳 → 处理 cancels/ → 收割已完成的 in-flight → 续约仍在执行的
        /// in-flight → 按文件名升序在名额内启动至多 <c>MAX_TICK_BATCH</c> 条命令 → 清理终态 progress。
        /// <para>这是一个**非阻塞的泵**：启动 handler 后立即返回，MUST NOT 等 handler 跑完（SPEC §13.1）。MUST NOT 并发调用。</para>
        /// </summary>
        public void Tick()
        {
            ThrowIfDisposed();
            // SPEC §5：第一次 Tick 之前是能力表的写入窗口，之后冻结。
            mStarted = true;

            // 每一步各自兜住 I/O 故障：任何一步失败都不该让服务循环停摆（SPEC §13.2 最后一条）。
            try { WriteHeartbeat(); }
            catch (Exception caught) when (IsIoFailure(caught)) { }
            try { ProcessCancels(); }
            catch (Exception caught) when (IsIoFailure(caught)) { }
            ReapInflight();
            try { RenewInflight(); }
            catch (Exception caught) when (IsIoFailure(caught)) { }
            try { StartCommands(); }
            catch (Exception caught) when (IsIoFailure(caught)) { }
            try { CleanProgress(); }
            catch (Exception caught) when (IsIoFailure(caught)) { }
        }

        /// <summary>反复 Tick 直到没有在途 handler，或超时。宿主在退出前、测试在断言前用它把泵跑空。</summary>
        /// <param name="timeout">等待上限；到点仍有在途 handler 时直接返回，不抛异常。</param>
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
                Thread.Sleep(5);
            }
        }

        /// <summary>异步投递并等待结果（SPEC §14）。等待期间不会驱动本节点的 Tick，因此可在 Tick 循环的线程上安全调用。</summary>
        /// <param name="endpointId">目标端点，须满足 SPEC §4.1。</param>
        /// <param name="channel">目标信道。</param>
        /// <param name="action">目标动作。</param>
        /// <param name="payload">不透明字符串，可为 null 或空串。</param>
        /// <param name="timeout">等待上限，同时作为命令的 deadline（SPEC §7.1）；超时返回 <c>unreachable</c>，不落盘（SPEC §9.2）。</param>
        /// <returns>四种结果之一，永不返回 null。</returns>
        /// <exception cref="ArgumentException">参数非法，或 timeout 不是正数。</exception>
        public async Task<CourierResponse> CallAsync(string endpointId, string channel, string action, string? payload, TimeSpan timeout)
        {
            ThrowIfDisposed();
            ValidateCall(endpointId, channel, action, timeout);
            string requestId;
            // SPEC §7.1：deadline 由调用方按次指定。等待上限就是这条命令的 deadline——
            // 比等待时间更短的 deadline 会让服务者先判 stale，比它更长则让命令在调用方放弃后还占着队列。
            CourierResponse? early = CourierDispatcher.Deliver(mRootDirectory, mEndpointId, endpointId, channel, action, payload, timeout, out requestId);
            if (early != null) return early;

            // SPEC §14.2：10 ms 起指数退避到 200 ms 上限，MUST NOT 无限等待。
            string resultPath = Path.Combine(mRootDirectory, CourierProtocol.EndpointsDirectory, endpointId, CourierProtocol.ResultsDirectory, requestId + CourierProtocol.JsonExtension);
            DateTime deadline = DateTime.UtcNow + timeout;
            int delayMilliseconds = 10;
            while (true)
            {
                string? text;
                if (CourierIO.TryReadAllText(resultPath, out text))
                {
                    CourierResponse? response = CourierResponse.TryParse(text);
                    if (response != null) return response;
                }

                if (DateTime.UtcNow >= deadline) break;
                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0) break;
                await Task.Delay(delayMilliseconds < remaining ? delayMilliseconds : remaining).ConfigureAwait(false);
                delayMilliseconds *= 2;
                if (delayMilliseconds > 200) delayMilliseconds = 200;
            }

            // SPEC §9.2：unreachable 是调用方合成的结果，不写任何文件。
            return new CourierResponse(requestId, CourierProtocol.StatusUnreachable, string.Empty, null, false, DateTime.UtcNow);
        }

        /// <summary>同步投递并等待结果。只在没有 Tick 循环的短命进程里安全（SPEC §14.3）。</summary>
        /// <param name="endpointId">目标端点，须满足 SPEC §4.1。</param>
        /// <param name="channel">目标信道。</param>
        /// <param name="action">目标动作。</param>
        /// <param name="payload">不透明字符串，可为 null 或空串。</param>
        /// <param name="timeout">等待上限，同时作为命令的 deadline。</param>
        /// <returns>四种结果之一，永不返回 null。</returns>
        /// <exception cref="ArgumentException">参数非法，或 timeout 不是正数。</exception>
        public CourierResponse Call(string endpointId, string channel, string action, string? payload, TimeSpan timeout)
        {
            // 只保留一份等待循环：同步版直接阻塞在异步版上，两条路径的退避与超时语义不会再各自漂移。
            return CallAsync(endpointId, channel, action, payload, timeout).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        /// <summary>扫描 root 下所有端点，读取 endpoint.json 与 heartbeat 推算存活状态（SPEC §6）。只做一次目录扫描，不缓存结果。</summary>
        /// <returns>按 endpointId 升序排列的只读快照。</returns>
        public IReadOnlyList<CourierEndpointInfo> Endpoints()
        {
            ThrowIfDisposed();
            return CourierEndpointReader.ReadAll(Path.Combine(mRootDirectory, CourierProtocol.EndpointsDirectory));
        }

        /// <summary>
        /// 结束本端点的服务。在途 handler 会被取消并最多等 2 秒收尾，期间已产出的终态照常落盘；
        /// 仍跑不完的那些，其命令留在 <c>commands/</c>，租约到期后由下一次 Tick 按 <c>attempts + 1</c> 重试——
        /// 这是 at-least-once 的正常路径，不是丢失（SPEC §1、§8.1）。磁盘上的端点目录、claim 与数据全部保留（SPEC §2）。
        /// </summary>
        public void Dispose()
        {
            if (mDisposed) return;
            mDisposed = true;

            List<InflightEntry> entries;
            lock (mGate)
            {
                entries = new List<InflightEntry>(mInflight.Values);
            }
            for (int index = 0; index < entries.Count; index++)
            {
                try { entries[index].Source.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            // 给已经采纳取消的 handler 一点时间，让它们的结果还来得及写成终态。
            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                ReapInflight();
                lock (mGate)
                {
                    if (mInflight.Count == 0) break;
                }
                Thread.Sleep(5);
            }

            lock (mGate)
            {
                foreach (InflightEntry entry in mInflight.Values) entry.Source.Dispose();
                mInflight.Clear();
            }
        }
        private void InitializeDirectories()
        {
            string endpointsRoot = Path.Combine(mRootDirectory, CourierProtocol.EndpointsDirectory);
            CourierIO.EnsureDirectory(endpointsRoot);

            // SPEC §4.1 已在 Open 里校验过 endpointId，这里是唯一一处把它拼进路径的地方。
            mEndpointDirectory = Path.Combine(endpointsRoot, mEndpointId);
            mCommandsDirectory = Path.Combine(mEndpointDirectory, CourierProtocol.CommandsDirectory);
            mClaimsDirectory = Path.Combine(mEndpointDirectory, CourierProtocol.ClaimsDirectory);
            mResultsDirectory = Path.Combine(mEndpointDirectory, CourierProtocol.ResultsDirectory);
            mDeadletterDirectory = Path.Combine(mEndpointDirectory, CourierProtocol.DeadletterDirectory);
            mCancelsDirectory = Path.Combine(mEndpointDirectory, CourierProtocol.CancelsDirectory);
            mProgressDirectory = Path.Combine(mEndpointDirectory, CourierProtocol.ProgressDirectory);

            CourierIO.EnsureDirectory(mEndpointDirectory);
            CourierIO.EnsureDirectory(mCommandsDirectory);
            CourierIO.EnsureDirectory(mClaimsDirectory);
            CourierIO.EnsureDirectory(mResultsDirectory);
            CourierIO.EnsureDirectory(mDeadletterDirectory);
            CourierIO.EnsureDirectory(mCancelsDirectory);
            CourierIO.EnsureDirectory(mProgressDirectory);

            // SPEC §5：endpoint.json 必须整体原子替换，绝不能增量追加——追加会留下永远 rejected/unknown-action 的幽灵能力。
            WriteEndpointJson();
            WriteHeartbeat();
        }

        private void Register(string channel, string action, string summary, Func<string, CancellationToken, Task<string>> handler)
        {
            ThrowIfDisposed();
            // SPEC §5：能力表在第一次 Tick 之前写完即冻结，运行期改它等于让"这个端点能做什么"随时间变化。
            if (mStarted) throw new InvalidOperationException("capabilities are frozen after the first Tick (SPEC §5)");
            if (!CourierProtocol.IsValidName(channel)) throw new ArgumentException("invalid channel: must match ^[A-Za-z0-9_.-]{1,64}$", nameof(channel));
            if (!CourierProtocol.IsValidName(action)) throw new ArgumentException("invalid action: must match ^[A-Za-z0-9_.-]{1,64}$", nameof(action));
            string key = channel + "\u0000" + action;
            if (mHandlers.ContainsKey(key))
            {
                // SPEC §5：重复注册是编程错误，MUST 在注册时报错，不能悄悄覆盖。
                throw new ArgumentException("duplicate registration for " + channel + "/" + action, nameof(action));
            }

            mHandlers.Add(key, new HandlerEntry(channel, action, summary ?? string.Empty, handler));
            WriteEndpointJson();
        }

        private void WriteEndpointJson()
        {
            var actions = new List<HandlerEntry>(mHandlers.Values);
            actions.Sort(CompareHandlers);

            var parts = new List<string>(actions.Count);
            for (int index = 0; index < actions.Count; index++)
            {
                parts.Add(new CourierJsonObject()
                    .AddString("channel", actions[index].Channel)
                    .AddString("action", actions[index].Action)
                    .AddString("summary", actions[index].Summary)
                    .Build());
            }

            string json = new CourierJsonObject()
                .AddString("endpointId", mEndpointId)
                .AddString("description", mDescription)
                .AddNumber("processId", CourierProtocol.CurrentProcessId())
                .AddString("startedAtUtc", CourierProtocol.FormatUtc(mStartedAtUtc))
                .AddNumber("protocolVersion", CourierProtocol.ProtocolVersion)
                .AddRaw("actions", CourierJson.Array(parts))
                .Build();

            CourierIO.WriteAtomic(Path.Combine(mEndpointDirectory, CourierProtocol.EndpointFileName), json, true);
        }

        private void WriteHeartbeat()
        {
            // SPEC §6：心跳是单文件单时间戳，覆盖写；MUST NOT 塞进 endpoint.json，否则每次心跳都会改能力表。
            // 内容就是裸的 UTC 时间戳，不带 JSON 引号——SPEC §6 第 1 条"无 JSON"。
            // durable:false —— 心跳每轮重写，丢一次下一轮就补上；为它付一次 fsync 没有收益。
            CourierIO.WriteAtomic(
                Path.Combine(mEndpointDirectory, CourierProtocol.HeartbeatFileName),
                CourierProtocol.FormatUtc(DateTime.UtcNow),
                true,
                false);
        }

        private static void ValidateCall(string endpointId, string channel, string action, TimeSpan timeout)
        {
            if (!CourierProtocol.IsValidEndpointId(endpointId)) throw new ArgumentException("invalid endpointId", nameof(endpointId));
            if (!CourierProtocol.IsValidName(channel)) throw new ArgumentException("invalid channel", nameof(channel));
            if (!CourierProtocol.IsValidName(action)) throw new ArgumentException("invalid action", nameof(action));
            if (timeout <= TimeSpan.Zero) throw new ArgumentException("timeout must be positive", nameof(timeout));
        }

        /// <summary>SPEC §11.1：把取消意图送到正在执行的 handler 手里，或对尚未开始的命令直接落终态。</summary>
        private void ProcessCancels()
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(mCancelsDirectory, "*" + CourierProtocol.JsonExtension);
            }
            catch (Exception caught) when (caught is DirectoryNotFoundException || caught is IOException)
            {
                return;
            }

            // 取消的处理顺序不影响结果，不做排序，省掉一次 O(n log n) 分配。
            for (int index = 0; index < files.Length; index++)
            {
                string requestId = Path.GetFileNameWithoutExtension(files[index]);
                if (!CourierProtocol.IsValidRequestId(requestId))
                {
                    CourierIO.TryDelete(files[index]);
                    continue;
                }

                // SPEC §11.1：对**正在执行**的 requestId 触发 CancellationToken。这正是 Tick 不被 handler
                // 阻塞换来的能力——handler 在别的线程上跑，Tick 照常进来把信号递到它手里。
                bool signalled = false;
                lock (mGate)
                {
                    InflightEntry? entry;
                    if (mInflight.TryGetValue(requestId, out entry) && entry != null)
                    {
                        signalled = true;
                        try
                        {
                            entry.Source.Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                    }
                }

                // SPEC §11.1：命令尚未开始执行时，直接落 rejected/cancelled-before-start 并归档，不进 handler。
                // 但"尚未开始"必须以 claims/ 为准（SPEC §8.1）：命令文件还在、claim 却新鲜，说明另一个进程正在跑，
                // 此时写终态会让同一 requestId 出现两次结果，还留下一条永远删不掉的孤儿 claim。
                if (!signalled
                    && !IsClaimedByOther(Path.Combine(mClaimsDirectory, requestId + CourierProtocol.JsonExtension))
                    && File.Exists(Path.Combine(mCommandsDirectory, requestId + CourierProtocol.JsonExtension)))
                {
                    WriteResultAndArchive(
                        requestId,
                        Path.Combine(mCommandsDirectory, requestId + CourierProtocol.JsonExtension),
                        CourierProtocol.StatusRejected,
                        string.Empty,
                        CourierProtocol.ReasonCancelledBeforeStart,
                        true);
                }

                // 取消标记本身是投递方的意图，处理完就删；没能生效的（命令正在别处执行）也删，
                // 因为取消是尽力而为（SPEC §11.1），留着只会让下一轮重复判定。
                CourierIO.TryDelete(files[index]);
            }
        }

        /// <summary>SPEC §13 第 6 步：在名额内按文件名升序启动命令，启动即返回，不等 handler 结束。</summary>
        private void StartCommands()
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(mCommandsDirectory, "*" + CourierProtocol.JsonExtension);
            }
            catch (Exception caught) when (caught is DirectoryNotFoundException || caught is IOException)
            {
                // 目录被删或暂时不可读：本轮没有命令可启动，下一轮再看。
                return;
            }

            // SPEC §3.5：枚举顺序 MUST 按文件名升序，保证同一批积压的处理顺序在各实现间一致。
            var commandFiles = new List<string>(files);
            commandFiles.Sort(StringComparer.Ordinal);

            int started = 0;
            for (int index = 0; index < commandFiles.Count && started < CourierProtocol.MaxTickBatch; index++)
            {
                string requestId = Path.GetFileNameWithoutExtension(commandFiles[index]);
                lock (mGate)
                {
                    // SPEC §13.1：名额满了就停，不阻塞等别人跑完。
                    if (mInflight.Count >= CourierProtocol.MaxInflight) return;
                    if (mInflight.ContainsKey(requestId)) continue;
                }

                if (StartCommand(commandFiles[index])) started++;
            }
        }

        /// <summary>SPEC §13.2 的 Start：走完 1–8 步后把 handler 交给线程池；返回是否真的启动了。</summary>
        private bool StartCommand(string commandPath)
        {
            string requestId = Path.GetFileNameWithoutExtension(commandPath);
            string resultPath = Path.Combine(mResultsDirectory, requestId + CourierProtocol.JsonExtension);
            string claimPath = Path.Combine(mClaimsDirectory, requestId + CourierProtocol.JsonExtension);

            // 第 1 步：results 已存在说明这条命令处理完了，删掉残留命令即可，绝不重复执行。
            if (File.Exists(resultPath))
            {
                // 幂等跳过也要清干净：崩溃可能停在"写完 results、还没删 progress"之间（SPEC §11.2）。
                ArchiveCommand(requestId, commandPath);
                CourierIO.TryDelete(claimPath);
                DeleteProgress(requestId);
                return false;
            }

            if (!CourierProtocol.IsValidRequestId(requestId))
            {
                DeadletterCommand(requestId, commandPath, CourierProtocol.ReasonInvalidCommand, "requestId does not match the file name", 0);
                return false;
            }

            // 第 2/3 步（SPEC §8.1）：租约未到期就说明持有者还在跑，本轮跳过；到期则接管并把 attempts +1。
            int attempts = 1;
            CourierClaim? existingClaim = null;
            string? claimText;
            if (CourierIO.TryReadAllText(claimPath, out claimText)) existingClaim = CourierClaim.TryParse(claimText);
            if (existingClaim != null)
            {
                if (!IsClaimExpired(existingClaim.ExpiresAtUtc)) return false;
                attempts = existingClaim.Attempts + 1;
                if (attempts > CourierProtocol.MaxAttempts)
                {
                    DeadletterCommand(requestId, commandPath, CourierProtocol.ReasonHandlerError, "retry limit exceeded", existingClaim.Attempts);
                    return false;
                }
            }

            // 第 4 步（SPEC §7.2）：先看长度再读入内存，超限文件永远不进进程地址空间。
            long length;
            try
            {
                length = new FileInfo(commandPath).Length;
            }
            catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException)
            {
                // 命令文件在枚举之后被归档或删掉了：本轮什么都不做，下一轮自然也不会再看到它。
                return false;
            }

            if (length > CourierProtocol.MaxCommandFileBytes)
            {
                DeadletterCommand(requestId, commandPath, CourierProtocol.ReasonInvalidCommand, "command file exceeds " + CourierProtocol.MaxCommandFileBytes + " bytes", attempts);
                return false;
            }

            // 第 5 步（SPEC §7）：解析失败、字段缺失或 requestId 与文件名不符，都进 deadletter 而不是抛异常给宿主。
            string? commandText;
            if (!CourierIO.TryReadAllText(commandPath, out commandText)) return false;
            CourierCommand? command = CourierCommand.TryParse(commandText, requestId);
            if (command == null)
            {
                DeadletterCommand(requestId, commandPath, CourierProtocol.ReasonInvalidCommand, "command could not be parsed", attempts);
                return false;
            }

            // 第 6 步（SPEC §7.1）：deadline 已过就 MUST NOT 执行，落 rejected/stale 并归档。
            if (DateTime.UtcNow > command.DeadlineUtc)
            {
                WriteResultAndArchive(requestId, commandPath, CourierProtocol.StatusRejected, string.Empty, CourierProtocol.ReasonStale, false);
                return false;
            }

            string key = command.Channel + "\u0000" + command.Action;
            HandlerEntry? handler;
            if (!mHandlers.TryGetValue(key, out handler) || handler == null)
            {
                // 第 7 步（SPEC §7）：路由在写入时确定，本端点没有这个能力就只能 rejected/unknown-action。
                WriteResultAndArchive(requestId, commandPath, CourierProtocol.StatusRejected, string.Empty, CourierProtocol.ReasonUnknownAction, false);
                return false;
            }

            // 第 8 步（SPEC §8.1）：首次认领用不覆盖语义创建，抢不到说明别人先到了，本轮跳过。
            // 接管过期租约时必须覆盖写：那条 claim 文件本来就还在盘上，继续用不覆盖语义
            // 会永远写不进去，命令就卡死在 commands/ 里，attempts 也不再增长。
            DateTime now = DateTime.UtcNow;
            CourierClaim claim = CourierClaim.Create(mEndpointId, now + CourierProtocol.ClaimWindow, attempts);
            try
            {
                CourierIO.WriteAtomic(claimPath, claim.ToJson(), existingClaim != null);
            }
            catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException)
            {
                // 抢不到 claim（别人先创建，或目录不可写）：本轮放弃，命令留在 commands/ 等下一轮。
                return false;
            }

            // 第 9 步（SPEC §13.2）：把 handler 交给线程池，Tick 立刻继续往下走，不等它。
            var source = new CancellationTokenSource();
            CancellationToken token = source.Token;
            string payloadText = command.Payload;
            Func<string, CancellationToken, Task<string>> target = handler.Handler;
            Task<string> task = Task.Run(() => target(payloadText, token));
            var entry = new InflightEntry(requestId, commandPath, command.DeadlineUtc, source, task);
            lock (mGate)
            {
                mInflight[requestId] = entry;
            }
            return true;
        }

        /// <summary>SPEC §13 第 4 步：把已完成（或已超时）的 in-flight 写成终态并释放名额。</summary>
        private void ReapInflight()
        {
            var ready = new List<InflightEntry>();
            lock (mGate)
            {
                foreach (InflightEntry entry in mInflight.Values)
                {
                    if (entry.Task.IsCompleted || DateTime.UtcNow > entry.DeadlineUtc) ready.Add(entry);
                }
                for (int index = 0; index < ready.Count; index++) mInflight.Remove(ready[index].RequestId);
            }

            for (int index = 0; index < ready.Count; index++)
            {
                // SPEC §13.2：收割路径的失败 MUST NOT 穿透 Tick。写 results 的 I/O 失败已在 Finish 内单独处理，
                // 这里再兜一层，防止归档或释放 CTS 时的意外把整个服务循环打死。
                try { Finish(ready[index]); }
                catch (Exception caught) when (IsIoFailure(caught)) { }
            }
        }

        /// <summary>SPEC §13.2 的 Reap：写终态、归档、删 claim、删 progress，每一步失败都不中断其余步骤。</summary>
        private void Finish(InflightEntry entry)
        {
            // SPEC §8.1：租约被别人接管后 MUST 放弃执行，且 MUST NOT 写 results/——
            // 新持有者会产出这条命令唯一的终态，我们这份结果属于已经被作废的那次执行。
            if (entry.Abandoned)
            {
                DisposeSource(entry);
                return;
            }

            string status = CourierProtocol.StatusOk;
            string payload = string.Empty;
            string? reason = null;
            bool cancelRequested = false;

            if (entry.Task.IsCompleted)
            {
                if (entry.Task.IsCanceled)
                {
                    // SPEC §11.1：handler 采纳了取消。
                    status = CourierProtocol.StatusFailed;
                    reason = CourierProtocol.ReasonCancelled;
                }
                else if (entry.Task.IsFaulted)
                {
                    // SPEC §9.1：业务逻辑执行了但失败，是 failed 而不是 rejected；异常不得穿透到宿主。
                    Exception? error = entry.Task.Exception;
                    status = CourierProtocol.StatusFailed;
                    reason = error != null && error.GetBaseException() is OperationCanceledException
                        ? CourierProtocol.ReasonCancelled
                        : CourierProtocol.ReasonHandlerError;
                }
                else
                {
                    payload = entry.Task.Result ?? string.Empty;
                }

                cancelRequested = entry.Source.IsCancellationRequested && status == CourierProtocol.StatusOk;
            }
            else
            {
                // SPEC §13.2 第 4 条：超 HANDLER_TIMEOUT 必须立即写终态，MUST NOT 等 handler 返回——
                // 一个不响应取消的 handler 可能永远不返回，等它等于让这条命令永远没有结果。
                status = CourierProtocol.StatusFailed;
                reason = CourierProtocol.ReasonHandlerTimeout;
                try
                {
                    entry.Source.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            try
            {
                WriteResult(entry.RequestId, status, payload, reason, cancelRequested);
            }
            catch (Exception caught) when (IsIoFailure(caught))
            {
                // 写不出终态就什么都不清：命令留在 commands/、claim 留到租约到期，下一轮按 attempts + 1 重来。
                // 先归档再删 claim 会让这条命令连证据一起消失，那才是真正不可恢复的丢件（SPEC §1 不变式 3）。
                DisposeSource(entry);
                return;
            }

            ArchiveCommand(entry.RequestId, entry.CommandPath);
            CourierIO.TryDelete(Path.Combine(mClaimsDirectory, entry.RequestId + CourierProtocol.JsonExtension));
            DeleteProgress(entry.RequestId);
            DisposeSource(entry);
        }

        /// <summary>
        /// 释放一个 in-flight 的 CancellationTokenSource，但只在 handler 已经停下时才释放。
        /// 对已经超时判负、却仍在自己跑的 handler 释放 CTS，会让它下一次访问 token 抛
        /// <see cref="ObjectDisposedException"/>——把协议的正确性建立在用户代码不再碰 token 上是不成立的。
        /// 宁可让一个跑飞的 handler 多留一个 CTS，也不能反过来打断它。
        /// </summary>
        /// <param name="entry">要释放的 in-flight 条目。</param>
        private static void DisposeSource(InflightEntry entry)
        {
            if (!entry.Task.IsCompleted) return;
            entry.Source.Dispose();
        }


        /// <summary>SPEC §13 第 5 步与 §8.1：对仍在执行的 in-flight 续约；发现租约已被接管就放弃执行。</summary>
        private void RenewInflight()
        {
            List<InflightEntry> snapshot;
            lock (mGate)
            {
                snapshot = new List<InflightEntry>(mInflight.Values);
            }

            DateTime now = DateTime.UtcNow;
            for (int index = 0; index < snapshot.Count; index++)
            {
                InflightEntry entry = snapshot[index];
                if (entry.Task.IsCompleted || entry.Abandoned) continue;
                if (now - entry.LastRenewedUtc < CourierProtocol.ClaimRenewInterval) continue;

                string claimPath = Path.Combine(mClaimsDirectory, entry.RequestId + CourierProtocol.JsonExtension);
                string? claimText;
                CourierClaim? current = null;
                if (CourierIO.TryReadAllText(claimPath, out claimText)) current = CourierClaim.TryParse(claimText);
                if (current == null || !string.Equals(current.Owner, mEndpointId, StringComparison.Ordinal))
                {
                    // 租约丢了：别人已经接管，我们这次执行的结果没有资格落盘（SPEC §8.1）。
                    entry.Abandoned = true;
                    try
                    {
                        entry.Source.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    continue;
                }

                try
                {
                    CourierIO.WriteAtomic(claimPath, current.Renew(now).ToJson(), true);
                    entry.LastRenewedUtc = now;
                }
                catch (Exception caught) when (IsIoFailure(caught))
                {
                    // 这一次续约失败不要紧：租约还有大半个窗口，下一个 Tick 再试。
                }
            }
        }

        /// <summary>SPEC §11.2 与 §13 第 7 步：终态已产生的命令，其 progress 文件必须消失。</summary>
        private void CleanProgress()
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(mProgressDirectory, "*" + CourierProtocol.JsonExtension);
            }
            catch (Exception caught) when (caught is DirectoryNotFoundException || caught is IOException)
            {
                return;
            }

            for (int index = 0; index < files.Length; index++)
            {
                string requestId = Path.GetFileNameWithoutExtension(files[index]);
                if (File.Exists(Path.Combine(mResultsDirectory, requestId + CourierProtocol.JsonExtension)))
                {
                    CourierIO.TryDelete(files[index]);
                }
            }
        }

        private void WriteResultAndArchive(string requestId, string commandPath, string status, string payload, string? reason, bool cancelRequested)
        {
            // SPEC §12.1：先写 results 再搬命令；反过来会让命令在崩溃窗口里凭空消失。
            // 写失败时不归档也不删 claim：让下一轮按 attempts + 1 重来，好过把证据清掉。
            try
            {
                WriteResult(requestId, status, payload, reason, cancelRequested);
            }
            catch (Exception caught) when (IsIoFailure(caught))
            {
                return;
            }

            ArchiveCommand(requestId, commandPath);
            // SPEC §8：终态产生后 claim 必须消失，否则观察工具会看到一条永远"正在执行"的命令。
            CourierIO.TryDelete(Path.Combine(mClaimsDirectory, requestId + CourierProtocol.JsonExtension));
            DeleteProgress(requestId);
        }

        private void WriteResult(string requestId, string status, string payload, string? reason, bool cancelRequested)
        {
            var response = new CourierResponse(requestId, status, payload, reason, cancelRequested, DateTime.UtcNow);
            CourierIO.WriteAtomic(Path.Combine(mResultsDirectory, requestId + CourierProtocol.JsonExtension), response.ToJson(), true);
        }

        private void ArchiveCommand(string requestId, string commandPath)
        {
            if (!File.Exists(commandPath)) return;
            try
            {
                string archiveDirectory = Path.Combine(
                    mRootDirectory,
                    CourierProtocol.ArchiveDirectory,
                    DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    mEndpointId,
                    CourierProtocol.CommandsDirectory);
                CourierIO.EnsureDirectory(archiveDirectory);
                // 覆盖写走 CourierIO.MoveOverwrite：File.Move 的三参重载不在 netstandard2.1 里。
                CourierIO.MoveOverwrite(commandPath, Path.Combine(archiveDirectory, requestId + CourierProtocol.JsonExtension));
            }
            catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException)
            {
                // SPEC §12.1：归档失败 MUST 被忽略并继续，不能因为搬不动就让整条命令重来。
            }
        }

        private void DeadletterCommand(string requestId, string commandPath, string reason, string detail, int attempts)
        {
            string claimPath = Path.Combine(mClaimsDirectory, requestId + CourierProtocol.JsonExtension);
            var deadletter = CourierDeadletter.Create(requestId, reason, detail, DateTime.UtcNow, attempts);
            try
            {
                CourierIO.WriteAtomic(Path.Combine(mDeadletterDirectory, requestId + CourierProtocol.JsonExtension), deadletter.ToJson(), true);
            }
            catch (Exception caught) when (IsIoFailure(caught))
            {
                // 写不出 deadletter 就保持原状：命令与 claim 都还在，下一轮会再次走到这里（SPEC §13.2 最后一条）。
                return;
            }

            ArchiveCommand(requestId, commandPath);
            CourierIO.TryDelete(claimPath);
            DeleteProgress(requestId);
        }

        private void DeleteProgress(string requestId)
        {
            CourierIO.TryDelete(Path.Combine(mProgressDirectory, requestId + CourierProtocol.JsonExtension));
        }

        /// <summary>claim 的租约未到期即视为别人正在执行（SPEC §8.1）；claim 缺失或不可解析时返回 false，让取消立即生效。</summary>
        private static bool IsClaimedByOther(string claimPath)
        {
            string? text;
            if (!CourierIO.TryReadAllText(claimPath, out text)) return false;
            CourierClaim? claim = CourierClaim.TryParse(text);
            return claim != null && !IsClaimExpired(claim.ExpiresAtUtc);
        }

        private static bool IsClaimExpired(DateTime expiresAtUtc)
        {
            // SPEC §8.1：租约到期且没有对应 result，持有者即视为已死。
            return DateTime.UtcNow > expiresAtUtc;
        }

        /// <summary>服务循环只兜 I/O 类故障；编程错误（NullReference 等）必须冒出来，不能被静默吞掉。</summary>
        private static bool IsIoFailure(Exception caught)
        {
            return caught is IOException || caught is UnauthorizedAccessException || caught is NotSupportedException;
        }

        private void ThrowIfDisposed()
        {
            if (mDisposed) throw new ObjectDisposedException(nameof(CourierNode));
        }

        private static int CompareHandlers(HandlerEntry left, HandlerEntry right)
        {
            int channel = string.CompareOrdinal(left.Channel, right.Channel);
            return channel != 0 ? channel : string.CompareOrdinal(left.Action, right.Action);
        }

        private sealed class HandlerEntry
        {
            internal HandlerEntry(string channel, string action, string summary, Func<string, CancellationToken, Task<string>> handler)
            {
                Channel = channel;
                Action = action;
                Summary = summary;
                Handler = handler;
            }

            internal string Channel { get; }
            internal string Action { get; }
            internal string Summary { get; }
            internal Func<string, CancellationToken, Task<string>> Handler { get; }
        }

        /// <summary>SPEC §13.1：一条正在执行的命令。字段只由 Tick 线程或 Dispose 改写，读写都在 mGate 之外时靠 volatile 语义的引用发布保证可见。</summary>
        private sealed class InflightEntry
        {
            internal InflightEntry(string requestId, string commandPath, DateTime deadlineUtc, CancellationTokenSource source, Task<string> task)
            {
                RequestId = requestId;
                CommandPath = commandPath;
                DeadlineUtc = deadlineUtc;
                Source = source;
                Task = task;
                LastRenewedUtc = DateTime.UtcNow;
            }

            internal string RequestId { get; }
            internal string CommandPath { get; }
            internal DateTime DeadlineUtc { get; }
            internal CancellationTokenSource Source { get; }
            internal Task<string> Task { get; }
            internal DateTime LastRenewedUtc { get; set; }
            internal bool Abandoned { get; set; }
        }
    }
}
