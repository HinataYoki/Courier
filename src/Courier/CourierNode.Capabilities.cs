using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Courier
{
    /// <summary>
    /// <see cref="CourierNode"/> 的能力表落盘（endpoint.json）、root 注册表刷新与心跳（SPEC §5、§6、§7）。
    /// </summary>
    public sealed partial class CourierNode
    {
        /// <summary>
        /// 把当前能力表整体原子替换成 endpoint.json（SPEC §5）。
        /// <para>
        /// MUST 是整体替换而非增量追加：追加会留下永远 <c>rejected/unknown-action</c> 的幽灵能力。
        /// actions 与 states 各按序数升序，capabilities 一个都没声明时整块缺席。
        /// </para>
        /// </summary>
        private void WriteEndpointJson()
        {
            var builder = new CourierJsonObject()
                .AddString("endpointId", mEndpointId)
                .AddString("description", mDescription)
                // SPEC §5：sessionId 让读方分辨"同一端点的新一轮运行"——processId 会被复用，startedAtUtc 可能被复制。
                .AddString("sessionId", mSessionId)
                .AddNumber("processId", CourierProtocol.CurrentProcessId())
                .AddString("startedAtUtc", CourierProtocol.FormatUtc(mStartedAtUtc))
                .AddNumber("protocolVersion", CourierProtocol.PROTOCOL_VERSION)
                .AddRaw("actions", CourierJson.Array(BuildActionParts()))
                .AddRaw("states", CourierJson.Array(BuildStateParts()));

            // SPEC §5：capabilities 是可选的**能力自述**。一个都没声明时整块省略——空数组会让读方以为
            // "故意声明了零个能力"，而真相是"这份实现没打算谈这个"。
            List<string> capabilityParts = BuildCapabilityParts();
            if (capabilityParts.Count > 0) builder.AddRaw("capabilities", CourierJson.Array(capabilityParts));

            // SPEC §5.2：endpoint.json 里 MUST NOT 出现 hot 字段。热通道的会合信息改走实现私有目录
            // endpoints/<id>/hot/descriptor.json（SPEC §2「实现私有文件与目录」），因此这里一个字都不写。
            CourierIO.WriteAtomic(Path.Combine(mEndpointDirectory, mLayout.EndpointFileName), builder.Build(), true);
        }

        /// <summary>
        /// 序列化 <c>actions</c> 数组的每个元素（SPEC §5.1）：按 (channel, action) 序数升序，便于 diff 与逐行处理。
        /// </summary>
        /// <returns>已序列化的元素，拼成数组交给 <see cref="CourierJson.Array"/>。</returns>
        private List<string> BuildActionParts()
        {
            var actions = new List<HandlerEntry>(mHandlers.Values);
            actions.Sort(CompareHandlers);

            var parts = new List<string>(actions.Count);
            for (int index = 0; index < actions.Count; index++)
            {
                var action = new CourierJsonObject()
                    .AddString("channel", actions[index].Channel)
                    .AddString("action", actions[index].Action)
                    .AddString("summary", actions[index].Summary);
                // SPEC §5.1：metadata 是可选的自由 JSON。没有就整块缺席——写 null 会让读方以为"有 metadata 但是空的"。
                if (actions[index].Metadata != null) action.AddRaw("metadata", actions[index].Metadata!);
                parts.Add(action.Build());
            }

            return parts;
        }

        /// <summary>
        /// 序列化 <c>states</c> 数组的每个元素（SPEC §14）：按名字序数升序，便于 diff 与逐行处理。
        /// </summary>
        /// <returns>已序列化的元素，拼成数组交给 <see cref="CourierJson.Array"/>。</returns>
        private List<string> BuildStateParts()
        {
            var states = new List<StateEntry>(mStates);
            states.Sort(CompareStates);

            var stateParts = new List<string>(states.Count);
            for (int index = 0; index < states.Count; index++)
            {
                var state = new CourierJsonObject()
                    .AddString("name", states[index].Name)
                    .AddString("summary", states[index].Summary);
                if (states[index].Metadata != null) state.AddRaw("metadata", states[index].Metadata!);
                stateParts.Add(state.Build());
            }

            return stateParts;
        }

        /// <summary>
        /// 序列化 <c>capabilities</c> 数组的每个元素（SPEC §5）：按名字序数升序。
        /// </summary>
        /// <returns>已序列化的元素，拼成数组交给 <see cref="CourierJson.Array"/>；零个能力时为空表。</returns>
        private List<string> BuildCapabilityParts()
        {
            var capabilities = new List<string>(mCapabilities);
            capabilities.Sort(StringComparer.Ordinal);

            var capabilityParts = new List<string>(capabilities.Count);
            for (int index = 0; index < capabilities.Count; index++)
            {
                capabilityParts.Add("\"" + CourierJson.Escape(capabilities[index]) + "\"");
            }

            return capabilityParts;
        }

        // ==================== root 注册表（SPEC §7）====================

        /// <summary>
        /// 把本端点写进 root 级注册表（SPEC §7）。可选能力：<see cref="CourierNodeOptions.PublishRootRegistry"/>
        /// 没打开时这个方法立刻返回，连一个文件都不碰。
        /// </summary>
        /// <param name="initial">
        /// true 表示"打开端点后立刻登记"（此时必须成功，失败就重试）；false 表示 Tick 里的限频刷新，
        /// 没到间隔就跳过，且抢不到 <c>registry.lock</c> 只是本轮作罢。
        /// </param>
        private void PublishRootRegistry(bool initial)
        {
            if (!mOptions.PublishRootRegistry) return;

            if (!initial)
            {
                // SPEC §7：限频。每轮 Tick 都重读-合并-原子替换一整个文件，会把服务循环的固定开销推高一个数量级。
                TimeSpan elapsed = DateTime.UtcNow - mLastRegistryPublishUtc;
                if (elapsed < mOptions.EffectiveRegistryRefreshInterval) return;
            }

            var entry = new CourierRegistryEntry(mEndpointId, mDescription, mSessionId, RegistryState());
            try
            {
                // 打开时允许修剪：此时我们刚刚启动，目录状态最可信。运行期间的刷新只做 upsert，
                // 免得两个端点互相把对方刚写进去的条目当垃圾删掉。
                // 抢不到锁是正常的（别的端点正在写），下一轮再来。限频本身就是对重试的约束，
                // 所以这里不需要额外的"失败后放弃"状态：一个临时错误应该能在下一轮自己好起来。
                CourierRootRegistry.Publish(mRootDirectory, mLayout, entry, initial);
            }
            catch (Exception caught) when (IsIoFailure(caught))
            {
                // SPEC §7：注册表是可选的加速视图，写不进去就少一条，绝不影响本端点服务。
            }
            finally
            {
                mLastRegistryPublishUtc = DateTime.UtcNow;
            }
        }

        /// <summary>Dispose 时把本端点从注册表里摘掉（SPEC §7）。失败不影响退出。</summary>
        private void UnpublishRootRegistry()
        {
            if (!mOptions.PublishRootRegistry) return;
            try
            {
                CourierRootRegistry.Remove(mRootDirectory, mLayout, mEndpointId);
            }
            catch (Exception caught) when (IsIoFailure(caught))
            {
                // 摘不掉只是让注册表多一条指向已退出端点的记录；权威是 heartbeat，读方会看到 dead。
            }
        }

        /// <summary>
        /// 写进注册表的存活状态快照（SPEC §6、§7）。它的权威仍是心跳文件，这里只是让刚起的端点
        /// 不必等到第二份数据出现才有状态可写。
        /// </summary>
        /// <returns><c>alive</c>、<c>stale</c> 或 <c>dead</c>。</returns>
        private string RegistryState()
        {
            // 0 表示本进程还没成功写过心跳（端点刚打开时的第一轮）：按"刚刚还活着"算，
            // 这与 SPEC §7 "state 是快照、允许过期"一致。其余情况按距上次成功落盘的真实年龄推算，
            // 免得一个持续写盘失败的端点永远把自己报成 alive。
            long ticks = Interlocked.Read(ref mLastHeartbeatTicks);
            long ageMs = ticks == 0 ? 0 : (DateTime.UtcNow.Ticks - ticks) / TimeSpan.TicksPerMillisecond;
            return CourierProtocol.StateFromHeartbeatAge(ageMs);
        }

        /// <summary>
        /// SPEC §6：把当前 UTC 时刻写成心跳。这是唯一能证明"本端点还活着"的证据，权威高于注册表。
        /// </summary>
        private void WriteHeartbeat()
        {
            // SPEC §6：心跳是单文件单时间戳，覆盖写；MUST NOT 塞进 endpoint.json，否则每次心跳都会改能力表。
            // 内容就是裸的 UTC 时间戳，不带 JSON 引号——SPEC §6 第 1 条"无 JSON"。
            // durable:false —— 心跳每轮重写，丢一次下一轮就补上；为它付一次 fsync 没有收益。
            CourierIO.WriteAtomic(
                Path.Combine(mEndpointDirectory, mLayout.HeartbeatFileName),
                CourierProtocol.FormatUtc(DateTime.UtcNow),
                true,
                false);

            // 只在落盘成功之后更新本地时间戳：写失败还记成"刚写过"，就会把 write-heartbeat 的故障
            // 从可观测状态里抹掉。
            Interlocked.Exchange(ref mLastHeartbeatTicks, DateTime.UtcNow.Ticks);
        }

        /// <summary>endpoint.json 里 actions 数组的排序：先 channel 后 action，均按序数升序（SPEC §5.1）。</summary>
        /// <param name="left">左比较项。</param>
        /// <param name="right">右比较项。</param>
        /// <returns>序数比较结果。</returns>
        private static int CompareHandlers(HandlerEntry left, HandlerEntry right)
        {
            int channel = string.CompareOrdinal(left.Channel, right.Channel);
            return channel != 0 ? channel : string.CompareOrdinal(left.Action, right.Action);
        }
    }
}
