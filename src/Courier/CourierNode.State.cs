using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Courier
{
    /// <summary>
    /// <see cref="CourierNode"/> 的状态发布、进度上报与取消请求（SPEC §13、§14）。
    /// </summary>
    public sealed partial class CourierNode
    {
        /// <summary>
        /// 发布一个状态的最新值（SPEC §14）。覆盖写 <c>state/&lt;name&gt;.json</c>，没有回执、没有终态证据。
        /// </summary>
        /// <param name="name">已声明的状态名。</param>
        /// <param name="payloadJson">payload 的 JSON 文本；可为任意 JSON 值（对象、数组、字符串、数字、布尔、null）。</param>
        /// <returns>本次发布的 <c>revision</c>。</returns>
        /// <exception cref="ArgumentException">名字未声明，或 payloadJson 不是合法 JSON。</exception>
        /// <exception cref="IOException">写入失败；调用方自行决定是否重试。</exception>
        public long PublishState(string name, string payloadJson)
        {
            ThrowIfDisposed();
            if (!IsDeclaredState(name))
            {
                // SPEC §14：投递方 MUST NOT 猜测未声明的状态名，发布方同样不能。
                throw new ArgumentException("state \"" + name + "\" was never declared", nameof(name));
            }

            if (!CourierJson.TryParseValue(payloadJson, out _))
            {
                throw new ArgumentException("payload is not valid JSON", nameof(payloadJson));
            }

            // SPEC §14：revision MUST 单调递增。分配序号与落盘必须在同一个临界区里完成——
            // 只锁分配、把写盘放到锁外，两个发布者就可能先拿到 2、再由拿到 1 的那个后写，
            // 磁盘上的 revision 于是往回跳，而读方没有任何办法察觉。
            lock (mStateGate)
            {
                // 用端点内计数而不是读盘回填，避免"文件被外部删掉后重新从 1 开始"。
                mStateRevisions.TryGetValue(name, out long previous);
                long revision = previous + 1;

                string json = new CourierJsonObject()
                    .AddString("name", name)
                    .AddNumber("revision", revision)
                    .AddString("updatedAtUtc", CourierProtocol.FormatUtc(DateTime.UtcNow))
                    .AddRaw("payload", payloadJson)
                    .Build();

                // SPEC §14 + §19：状态文件超上限 MUST 拒写而不是截断——截断会写出一份坏 JSON。
                if (Encoding.UTF8.GetByteCount(json) > CourierProtocol.MAX_STATE_BYTES)
                {
                    throw new ArgumentException("state payload exceeds MAX_STATE_BYTES", nameof(payloadJson));
                }

                // 先落盘再推进计数：写失败就不该消耗掉一个 revision，否则下一次成功的发布会在磁盘上留下一个空洞。
                CourierIO.WriteAtomic(Path.Combine(mStateDirectory, name + CourierProtocol.JSON_EXTENSION), json, true);
                mStateRevisions[name] = revision;
                return revision;
            }
        }

        /// <summary>
        /// 发布一条命令的进度快照（SPEC §13.2）。覆盖写 <c>progress/&lt;requestId&gt;.json</c>，
        /// 只保留最近一次，没有回执、没有终态证据。
        /// <para>
        /// 这是**服务者侧**的写入口：handler 在 <see cref="CourierCall"/> 里拿到 <c>RequestId</c>，
        /// 用它调这里。终态产生后由 <see cref="CleanProgress"/> 负责删除（§13.2 的 MUST）。
        /// </para>
        /// <para>
        /// 写入不落 fsync：进度是尽力而为的快照，SPEC §3.3 明确允许省略 <c>Flush(true)</c>，
        /// 掉电丢一条进度既不改变任何终态，也不会让读者看到半截文件（原子写保证）。
        /// </para>
        /// </summary>
        /// <param name="requestId">正在执行的命令标识，须满足 SPEC §4.2。</param>
        /// <param name="percent">0..100 的整数。</param>
        /// <param name="message">给人看的短说明；null 或空串时该字段省略。</param>
        /// <exception cref="ArgumentException">requestId 非法。</exception>
        /// <exception cref="ArgumentOutOfRangeException">percent 不在 0..100。</exception>
        /// <exception cref="IOException">写入失败；进度不是证据，调用方通常直接忽略。</exception>
        public void ReportProgress(string requestId, int percent, string? message)
        {
            ThrowIfDisposed();
            if (!CourierProtocol.IsValidRequestId(requestId))
            {
                throw new ArgumentException("invalid requestId: must match ^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8,32}$", nameof(requestId));
            }

            if (percent < 0 || percent > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(percent), "percent must be between 0 and 100");
            }

            // SPEC §13.2：终态产生后 progress 文件 MUST 删除。这里先看一眼结果文件，避免"命令早结束了、
            // handler 才补一条进度"写出一份只能等下一轮 Tick 才被清掉的残留。
            if (File.Exists(Path.Combine(mResultsDirectory, requestId + CourierProtocol.JSON_EXTENSION))) return;

            var builder = new CourierJsonObject()
                .AddString("requestId", requestId)
                .AddNumber("percent", percent);
            if (!string.IsNullOrEmpty(message)) builder.AddString("message", message);
            string json = builder.AddString("updatedAtUtc", CourierProtocol.FormatUtc(DateTime.UtcNow)).Build();

            CourierIO.EnsureDirectory(mProgressDirectory);
            CourierIO.WriteAtomic(Path.Combine(mProgressDirectory, requestId + CourierProtocol.JSON_EXTENSION), json, true, false);
        }

        /// <summary>
        /// 向目标端点投递一个取消请求（SPEC §13.1）。写 <c>endpoints/&lt;id&gt;/cancels/&lt;requestId&gt;.json</c>。
        /// <para>
        /// 取消是**尽力而为**（§13.1）：目标端点下一次 <see cref="Tick"/> 才会看到它；命令已经在跑时
        /// 只能把信号递给 handler 的 <see cref="CancellationToken"/>，handler 完全可以忽略。
        /// 目标端点目录不存在（从未启动过）时返回 false，不写任何文件。
        /// </para>
        /// <para>
        /// 写入不落 fsync：取消是意图而不是证据，SPEC §3.3 允许省略 <c>Flush(true)</c>。
        /// </para>
        /// </summary>
        /// <param name="endpointId">目标端点，须满足 SPEC §4.1。</param>
        /// <param name="requestId">要取消的命令标识，须满足 SPEC §4.2。</param>
        /// <param name="reason">给人看的原因；null 时写成 <c>requested</c>。</param>
        /// <returns>是否已把取消请求写进目标端点。</returns>
        /// <exception cref="ArgumentException">endpointId 或 requestId 非法。</exception>
        public bool RequestCancel(string endpointId, string requestId, string? reason)
        {
            ThrowIfDisposed();
            if (!CourierProtocol.IsValidEndpointId(endpointId))
            {
                throw new ArgumentException("invalid endpointId: must match ^[a-z0-9]([a-z0-9-]{0,62}[a-z0-9])?$", nameof(endpointId));
            }

            if (!CourierProtocol.IsValidRequestId(requestId))
            {
                throw new ArgumentException("invalid requestId: must match ^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8,32}$", nameof(requestId));
            }

            string endpointDirectory = Path.Combine(CourierLayout.Combine(mRootDirectory, mLayout.EndpointsDirectory), endpointId);
            if (!Directory.Exists(endpointDirectory)) return false;

            string cancelsDirectory = Path.Combine(endpointDirectory, mLayout.CancelsDirectory);
            string json = new CourierJsonObject()
                .AddString("requestId", requestId)
                .AddString("requestedAtUtc", CourierProtocol.FormatUtc(DateTime.UtcNow))
                .AddString("reason", string.IsNullOrEmpty(reason) ? "requested" : reason)
                .Build();

            CourierIO.EnsureDirectory(cancelsDirectory);
            // 覆盖写：重复取消只是同一份意图，§3.3 没有把 cancels/ 划进"不覆盖"那一类。
            CourierIO.WriteAtomic(Path.Combine(cancelsDirectory, requestId + CourierProtocol.JSON_EXTENSION), json, true, false);
            return true;
        }

        /// <summary>状态名是否已声明（SPEC §14）。未声明的名字 MUST NOT 被发布，也不该被投递方猜测。</summary>
        /// <param name="name">待查询的状态名。</param>
        /// <returns>该名字是否在能力表里。</returns>
        private bool IsDeclaredState(string name)
        {
            // 声明的状态最多 MAX_STATE_NAMES 条（默认 256），线性扫描比维护第二份索引更省心也更省内存。
            for (int index = 0; index < mStates.Count; index++)
            {
                if (string.Equals(mStates[index].Name, name, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>endpoint.json 里 states 数组的排序：按名字序数升序（SPEC §5）。</summary>
        /// <param name="left">左比较项。</param>
        /// <param name="right">右比较项。</param>
        /// <returns>序数比较结果。</returns>
        private static int CompareStates(StateEntry left, StateEntry right)
        {
            return string.CompareOrdinal(left.Name, right.Name);
        }
    }
}
