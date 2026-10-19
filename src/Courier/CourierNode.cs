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
    /// 实例不是线程安全的：<see cref="Tick"/> 与 <see cref="Call"/> MUST NOT 并发调用（SPEC §13）。
    /// </para>
    /// </summary>
    public sealed class CourierNode : IDisposable
    {
        private readonly string mRootDirectory;
        private readonly string mEndpointId;
        private readonly string mDescription;
        private readonly Dictionary<string, HandlerEntry> mHandlers;
        private readonly object mCancelGate = new object();
        private readonly Dictionary<string, CancellationTokenSource> mActiveCancels;
        private readonly DateTime mStartedAtUtc;
        private string mEndpointDirectory = string.Empty;
        private string mCommandsDirectory = string.Empty;
        private string mClaimsDirectory = string.Empty;
        private string mResultsDirectory = string.Empty;
        private string mDeadletterDirectory = string.Empty;
        private string mCancelsDirectory = string.Empty;
        private string mProgressDirectory = string.Empty;
        private bool mDisposed;

        private CourierNode(string rootDirectory, string endpointId, string description)
        {
            mRootDirectory = rootDirectory;
            mEndpointId = endpointId;
            mDescription = description;
            mHandlers = new Dictionary<string, HandlerEntry>(StringComparer.Ordinal);
            mActiveCancels = new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
            mStartedAtUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// 打开（必要时创建）root 下的端点目录并写入 endpoint.json（SPEC §5）。
        /// </summary>
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

        /// <summary>
        /// 注册一个同步能力。同一个 (channel, action) 在一个端点上只能注册一次，重复注册是编程错误（SPEC §5）。
        /// </summary>
        /// <param name="channel">信道名，须满足 SPEC §4.3。</param>
        /// <param name="action">动作名，须满足 SPEC §4.3。</param>
        /// <param name="summary">面向人和工具的说明，写入 endpoint.json。</param>
        /// <param name="handler">处理函数，入参与返回值都是不透明字符串（SPEC §7）。</param>
        /// <exception cref="ArgumentException">channel 或 action 非法，或 (channel, action) 已注册。</exception>
        /// <exception cref="ArgumentNullException">handler 为 null。</exception>
        public void Handle(string channel, string action, string summary, Func<string, string> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            Register(channel, action, summary, payload => Task.FromResult(handler(payload)));
        }

        /// <summary>
        /// 注册一个异步能力。长命宿主 SHOULD 使用它，避免在 Tick 线程上阻塞（SPEC §14.3）。
        /// </summary>
        /// <param name="channel">信道名，须满足 SPEC §4.3。</param>
        /// <param name="action">动作名，须满足 SPEC §4.3。</param>
        /// <param name="summary">面向人和工具的说明，写入 endpoint.json。</param>
        /// <param name="handler">返回 <see cref="Task{String}"/> 的处理函数。</param>
        /// <exception cref="ArgumentException">channel 或 action 非法，或 (channel, action) 已注册。</exception>
        /// <exception cref="ArgumentNullException">handler 为 null。</exception>
        public void HandleAsync(string channel, string action, string summary, Func<string, Task<string>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            Register(channel, action, summary, handler);
        }

        /// <summary>
        /// 驱动一轮服务者循环（SPEC §13）：写心跳 → 处理 cancels/ → 按文件名升序处理至多
        /// <c>MAX_TICK_BATCH</c> 条命令 → 清理终态 progress。每一轮都是可重入安全点（SPEC §13.3）。
        /// <para>MUST NOT 并发调用；本方法不加锁、不起线程，时钟由宿主提供（SPEC §13）。</para>
        /// </summary>
        public void Tick()
        {
            ThrowIfDisposed();
            WriteHeartbeat();
            ProcessCancels();

            List<string> commandFiles;
            try
            {
                string[] files = Directory.GetFiles(mCommandsDirectory, "*" + CourierProtocol.JsonExtension);
                commandFiles = new List<string>(files);
            }
            catch (Exception caught) when (caught is DirectoryNotFoundException || caught is IOException)
            {
                // 目录被删或暂时不可读：本轮没有命令可处理，下一轮再看。
                return;
            }

            // SPEC §3.5：枚举顺序 MUST 按文件名升序，保证同一批积压的处理顺序在各实现间一致。
            commandFiles.Sort(StringComparer.Ordinal);
            int limit = commandFiles.Count < CourierProtocol.MaxTickBatch ? commandFiles.Count : CourierProtocol.MaxTickBatch;
            for (int index = 0; index < limit; index++)
            {
                ProcessCommand(commandFiles[index]);
            }
        }

        /// <summary>
        /// 异步投递并等待结果（SPEC §14）。等待期间不会驱动本节点的 Tick，因此可在 Tick 循环的线程上安全调用。
        /// </summary>
        /// <param name="endpointId">目标端点，须满足 SPEC §4.1。</param>
        /// <param name="channel">目标信道。</param>
        /// <param name="action">目标动作。</param>
        /// <param name="payload">不透明字符串，可为 null 或空串。</param>
        /// <param name="timeout">等待上限；超时返回 <c>unreachable</c>，不落盘（SPEC §9.2）。</param>
        /// <returns>四种结果之一，永不返回 null。</returns>
        /// <exception cref="ArgumentException">参数非法，或 timeout 不是正数。</exception>
        public async Task<CourierResponse> CallAsync(string endpointId, string channel, string action, string? payload, TimeSpan timeout)
        {
            ThrowIfDisposed();
            ValidateCall(endpointId, channel, action, timeout);
            string requestId;
            CourierResponse? early = CourierDispatcher.Deliver(mRootDirectory, mEndpointId, endpointId, channel, action, payload, out requestId);
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

        /// <summary>
        /// 同步投递并等待结果。只在没有 Tick 循环的短命进程里安全：如果在驱动 Tick 的线程上调用，
        /// 两端互等 results 会死锁（SPEC §14.3）。
        /// </summary>
        /// <param name="endpointId">目标端点，须满足 SPEC §4.1。</param>
        /// <param name="channel">目标信道。</param>
        /// <param name="action">目标动作。</param>
        /// <param name="payload">不透明字符串，可为 null 或空串。</param>
        /// <param name="timeout">等待上限；超时返回 <c>unreachable</c>。</param>
        /// <returns>四种结果之一，永不返回 null。</returns>
        /// <exception cref="ArgumentException">参数非法，或 timeout 不是正数。</exception>
        public CourierResponse Call(string endpointId, string channel, string action, string? payload, TimeSpan timeout)
        {
            // 只保留一份等待循环：同步版直接阻塞在异步版上，两条路径的退避与超时语义不会再各自漂移。
            return CallAsync(endpointId, channel, action, payload, timeout).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 扫描 root 下所有端点，读取 endpoint.json 与 heartbeat 推算存活状态（SPEC §6）。
        /// 只做一次目录扫描，不缓存结果；无法解析的端点被跳过而不是抛出。
        /// </summary>
        /// <returns>按 endpointId 升序排列的只读快照。</returns>
        public IReadOnlyList<CourierEndpointInfo> Endpoints()
        {
            ThrowIfDisposed();
            return CourierEndpointReader.ReadAll(Path.Combine(mRootDirectory, CourierProtocol.EndpointsDirectory));
        }

        /// <summary>
        /// 结束本端点的服务：只释放内存中的取消标记，不动磁盘上的任何文件——端点目录、claim 与数据全部保留
        /// （SPEC §2：端点退出不删目录）。
        /// </summary>
        public void Dispose()
        {
            if (mDisposed) return;
            mDisposed = true;
            lock (mCancelGate)
            {
                foreach (KeyValuePair<string, CancellationTokenSource> pair in mActiveCancels)
                {
                    pair.Value.Dispose();
                }
                mActiveCancels.Clear();
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

        private void Register(string channel, string action, string summary, Func<string, Task<string>> handler)
        {
            ThrowIfDisposed();
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
            // 内容就是裸的 UTC 时间戳，不带 JSON 引号——SPEC §6 第 1 条“无 JSON”。
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

                bool signalled;
                lock (mCancelGate)
                {
                    signalled = mActiveCancels.TryGetValue(requestId, out CancellationTokenSource? source);
                    if (signalled && source != null)
                    {
                        try
                        {
                            source.Cancel();
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
                    WriteResultAndArchive(requestId, CourierProtocol.StatusRejected, string.Empty, CourierProtocol.ReasonCancelledBeforeStart, true);
                }

                // 取消标记本身是投递方的意图，处理完就删；没能生效的（命令正在别处执行）也删，
                // 因为取消是尽力而为（SPEC §11.1），留着只会让下一轮重复判定。
                CourierIO.TryDelete(files[index]);
            }
        }

        private void ProcessCommand(string commandPath)
        {
            string requestId = Path.GetFileNameWithoutExtension(commandPath);
            string resultPath = Path.Combine(mResultsDirectory, requestId + CourierProtocol.JsonExtension);
            string claimPath = Path.Combine(mClaimsDirectory, requestId + CourierProtocol.JsonExtension);

            // 阶段 1（SPEC §13.1）：results 已存在说明这条命令处理完了，删掉残留命令即可，绝不重复执行。
            if (File.Exists(resultPath))
            {
                // 幂等跳过也要清干净：崩溃可能停在"写完 results、还没删 progress"之间（SPEC §11.2）。
                ArchiveCommand(requestId, commandPath);
                CourierIO.TryDelete(claimPath);
                DeleteProgress(requestId);
                return;
            }

            if (!CourierProtocol.IsValidRequestId(requestId))
            {
                DeadletterCommand(requestId, commandPath, CourierProtocol.ReasonInvalidCommand, "requestId does not match the file name", 0);
                return;
            }

            // 阶段 2/3（SPEC §8）：claim 未过期就说明另一个进程正在处理，本轮跳过；过期则接管并把 attempts +1。
            int attempts = 1;
            CourierClaim? existingClaim = null;
            string? claimText;
            if (CourierIO.TryReadAllText(claimPath, out claimText)) existingClaim = CourierClaim.TryParse(claimText);
            if (existingClaim != null)
            {
                if (!IsClaimExpired(existingClaim.ClaimedAtUtc)) return;
                attempts = existingClaim.Attempts + 1;
                if (attempts > CourierProtocol.MaxAttempts)
                {
                    DeadletterCommand(requestId, commandPath, CourierProtocol.ReasonHandlerError, "retry limit exceeded", existingClaim.Attempts);
                    return;
                }
            }

            // 阶段 4（SPEC §7.2）：先看长度再读入内存，超限文件永远不进进程地址空间。
            long length;
            try
            {
                length = new FileInfo(commandPath).Length;
            }
            catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException)
            {
                // 命令文件在枚举之后被归档或删掉了：本轮什么都不做，下一轮自然也不会再看到它。
                return;
            }

            if (length > CourierProtocol.MaxCommandFileBytes)
            {
                DeadletterCommand(requestId, commandPath, CourierProtocol.ReasonInvalidCommand, "command file exceeds " + CourierProtocol.MaxCommandFileBytes + " bytes", attempts);
                return;
            }

            // 阶段 5（SPEC §7）：解析失败、字段缺失或 requestId 与文件名不符，都进 deadletter 而不是抛异常给宿主。
            string? commandText;
            if (!CourierIO.TryReadAllText(commandPath, out commandText)) return;
            CourierCommand? command = CourierCommand.TryParse(commandText, requestId);
            if (command == null)
            {
                DeadletterCommand(requestId, commandPath, CourierProtocol.ReasonInvalidCommand, "command could not be parsed", attempts);
                return;
            }

            // 阶段 6（SPEC §7.1）：deadline 已过就 MUST NOT 执行，落 rejected/stale 并归档。
            if (DateTime.UtcNow > command.DeadlineUtc)
            {
                WriteResultAndArchive(requestId, CourierProtocol.StatusRejected, string.Empty, CourierProtocol.ReasonStale, false);
                return;
            }

            string key = command.Channel + "\u0000" + command.Action;
            HandlerEntry? handler;
            if (!mHandlers.TryGetValue(key, out handler) || handler == null)
            {
                // 阶段 7（SPEC §7）：路由在写入时确定，本端点没有这个能力就只能 rejected/unknown-action。
                WriteResultAndArchive(requestId, CourierProtocol.StatusRejected, string.Empty, CourierProtocol.ReasonUnknownAction, false);
                return;
            }

            // 阶段 8（SPEC §8）：首次认领用不覆盖语义创建，抢不到说明别人先到了，本轮跳过。
            // 接管过期 claim 时必须覆盖写：那条 claim 文件本来就还在盘上，继续用不覆盖语义
            // 会永远写不进去，命令就卡死在 commands/ 里，attempts 也不再增长（SPEC §13 第 3 步失效）。
            CourierClaim claim = CourierClaim.Create(mEndpointId, DateTime.UtcNow, attempts);
            try
            {
                CourierIO.WriteAtomic(claimPath, claim.ToJson(), existingClaim != null);
            }
            catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException)
            {
                // 抢不到 claim（别人先创建，或目录不可写）：本轮放弃，命令留在 commands/ 等下一轮。
                return;
            }

            using (var source = new CancellationTokenSource())
            {
                lock (mCancelGate)
                {
                    mActiveCancels[requestId] = source;
                }

                bool cancelRequested = false;
                try
                {
                    string payload;
                    string? reason = null;
                    string status = CourierProtocol.StatusOk;
                    try
                    {
                        Task<string> task = handler.Handler(command.Payload);
                        task.GetAwaiter().GetResult();
                        payload = task.Result ?? string.Empty;
                    }
                    catch (OperationCanceledException)
                    {
                        // SPEC §11.1：handler 采纳了取消。
                        payload = string.Empty;
                        status = CourierProtocol.StatusFailed;
                        reason = CourierProtocol.ReasonCancelled;
                    }
                    catch (Exception)
                    {
                        // SPEC §9.1：业务逻辑执行了但失败，是 failed 而不是 rejected；异常不得穿透到宿主。
                        payload = string.Empty;
                        status = CourierProtocol.StatusFailed;
                        reason = CourierProtocol.ReasonHandlerError;
                    }

                    cancelRequested = source.IsCancellationRequested && status == CourierProtocol.StatusOk;
                    WriteResult(requestId, status, payload, reason, cancelRequested);
                    ArchiveCommand(requestId, commandPath);
                    CourierIO.TryDelete(claimPath);
                    DeleteProgress(requestId);
                }
                finally
                {
                    lock (mCancelGate)
                    {
                        mActiveCancels.Remove(requestId);
                    }
                }
            }
        }

        private void WriteResultAndArchive(string requestId, string status, string payload, string reason, bool cancelRequested)
        {
            WriteResult(requestId, status, payload, reason, cancelRequested);
            ArchiveCommand(requestId, Path.Combine(mCommandsDirectory, requestId + CourierProtocol.JsonExtension));
            // SPEC §8：终态产生后 claim 必须消失，否则观察工具会看到一条永远"正在执行"的命令。
            CourierIO.TryDelete(Path.Combine(mClaimsDirectory, requestId + CourierProtocol.JsonExtension));
            DeleteProgress(requestId);
        }

        private void WriteResult(string requestId, string status, string payload, string? reason, bool cancelRequested)
        {
            // SPEC §12.1：必须先写 results 再搬命令；反过来会让命令在崩溃窗口里凭空消失。
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
            CourierIO.WriteAtomic(Path.Combine(mDeadletterDirectory, requestId + CourierProtocol.JsonExtension), deadletter.ToJson(), true);
            ArchiveCommand(requestId, commandPath);
            CourierIO.TryDelete(claimPath);
            DeleteProgress(requestId);
        }

        private void DeleteProgress(string requestId)
        {
            CourierIO.TryDelete(Path.Combine(mProgressDirectory, requestId + CourierProtocol.JsonExtension));
        }

        /// <summary>claim 存在且未过期即视为别人正在执行（SPEC §8.1）；claim 缺失或不可解析时返回 false，让取消立即生效。</summary>
        private static bool IsClaimedByOther(string claimPath)
        {
            string? text;
            if (!CourierIO.TryReadAllText(claimPath, out text)) return false;
            CourierClaim? claim = CourierClaim.TryParse(text);
            return claim != null && !IsClaimExpired(claim.ClaimedAtUtc);
        }

        private static bool IsClaimExpired(DateTime claimedAtUtc)
        {
            // SPEC §8.1：过期 = claimedAtUtc 距现在超过 CLAIM_WINDOW 且没有对应 result。
            return DateTime.UtcNow - claimedAtUtc > CourierProtocol.ClaimWindow;
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
            internal HandlerEntry(string channel, string action, string summary, Func<string, Task<string>> handler)
            {
                Channel = channel;
                Action = action;
                Summary = summary;
                Handler = handler;
            }

            internal string Channel { get; }
            internal string Action { get; }
            internal string Summary { get; }
            internal Func<string, Task<string>> Handler { get; }
        }
    }
}
