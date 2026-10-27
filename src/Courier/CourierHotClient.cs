using System;
using System.Collections.Generic;
using System.IO;

namespace Courier
{
    /// <summary>
    /// 调用方持有的热通道缓存：同一个 (调用方, 目标端点) 复用已开好的槽，避免"每调一次就建一次连接"。
    /// <para>
    /// 为什么必须缓存：一次 <see cref="CourierHotPool.Open"/> 要打开映射、开进程句柄、打开门铃，
    /// 冷进程实测 0.15 ms（NativeAOT）/ 1.5 ms（JIT）；而通道本身一次往返只有 5 µs。
    /// 单次 CLI 调用都重新建连，热通道的收益会被建连成本吃掉。
    /// </para>
    /// <para>
    /// 缓存不改变正确性：拿不到槽、对端已死、帧装不下，一律回落到文件协议。
    /// </para>
    /// <para>
    /// 锁的粒度（P3-15）：建连最长可以阻塞 <c>timeoutMs</c>（内部按毫秒轮询），所以它绝不能在
    /// <see cref="mGate"/> 里做 —— 那会把访问不同端点的调用方串成一条队。锁内只做"查表、占位、登记"。
    /// </para>
    /// </summary>
    internal sealed class CourierHotClient : IDisposable
    {
        /// <summary>同一个目标端点最多缓存几条连接；超出的并发调用直接走文件协议。</summary>
        private const int MAX_CHANNELS_PER_ENDPOINT = 8;

        /// <summary>缓存里的一条连接：通道本体 + 是否被某次调用借走。</summary>
        private sealed class Holder
        {
            /// <summary>包住一条已打开的通道。</summary>
            /// <param name="channel">通道。</param>
            internal Holder(CourierHotChannel channel)
            {
                Channel = channel;
            }

            /// <summary>通道本体。</summary>
            internal CourierHotChannel Channel { get; private set; }

            /// <summary>是否正被某次调用占用。</summary>
            internal bool Busy { get; set; }
        }

        private readonly Dictionary<string, List<Holder>> mChannels;
        private readonly object mGate = new object();
        private bool mDisposed;

        /// <summary>建出一张空的连接缓存。</summary>
        internal CourierHotClient()
        {
            mChannels = new Dictionary<string, List<Holder>>(StringComparer.Ordinal);
        }

        /// <summary>
        /// 借一条到 <paramref name="endpointId"/> 的空闲连接。没有空闲连接时会新开一条
        /// （不超过 <see cref="MAX_CHANNELS_PER_ENDPOINT"/>）；任何失败都返回 null。
        /// </summary>
        /// <param name="rootDirectory">协议根目录。</param>
        /// <param name="endpointId">端点标识。</param>
        /// <param name="descriptor">对端描述。</param>
        /// <param name="timeoutMs">建连预算（只在需要新开连接时消耗）。</param>
        /// <returns>借到的通道，或 <c>null</c>。</returns>
        /// <remarks>
        /// 返回非 null 时调用方必须把通道归还给 <see cref="Return"/>：只有归还才会把它标回空闲。
        /// </remarks>
        internal CourierHotChannel? TryLease(string rootDirectory, string endpointId, CourierHotDescriptor descriptor, int timeoutMs)
        {
            bool disposed;
            bool full;
            lock (mGate)
            {
                disposed = mDisposed;
                if (!disposed)
                {
                    CourierHotChannel? leased = TakeFreeLocked(endpointId, descriptor);
                    if (leased != null) return leased;
                    full = IsFullLocked(endpointId);
                }
                else
                {
                    full = true;
                }
            }

            if (disposed || full) return null;

            // 建连在锁外：它内部会轮询最长 timeoutMs，锁内做会把所有端点串行化。
            CourierHotChannel? opened = CourierHotChannel.TryOpen(rootDirectory, endpointId, descriptor, timeoutMs);
            if (opened == null) return null;

            lock (mGate)
            {
                if (mDisposed)
                {
                    opened.Dispose();
                    return null;
                }

                List<Holder> holders = HoldersLocked(endpointId);
                if (holders.Count >= MAX_CHANNELS_PER_ENDPOINT)
                {
                    // 建连期间被别人把名额占满了：这条多开的连接直接退掉，本次调用走文件通道。
                    opened.Dispose();
                    return null;
                }

                var created = new Holder(opened);
                created.Busy = true;
                holders.Add(created);
                return opened;
            }
        }

        /// <summary>
        /// 在锁内找一条可复用的空闲连接；顺路把失效的丢掉。
        /// </summary>
        /// <param name="endpointId">端点标识。</param>
        /// <param name="descriptor">对端当前的描述。</param>
        /// <returns>可用的通道，或 <c>null</c>。</returns>
        private CourierHotChannel? TakeFreeLocked(string endpointId, CourierHotDescriptor descriptor)
        {
            List<Holder>? holders;
            if (!mChannels.TryGetValue(endpointId, out holders)) return null;
            if (holders == null) return null;

            for (int index = 0; index < holders.Count; index++)
            {
                Holder holder = holders[index];
                if (holder.Busy) continue;
                // 上一代宿主留下的连接：别名对不上、对端已死、或槽已被回收转手，就地作废。
                if (!IsUsable(holder.Channel, descriptor))
                {
                    holders.RemoveAt(index);
                    holder.Channel.Dispose();
                    index--;
                    continue;
                }

                holder.Busy = true;
                return holder.Channel;
            }

            return null;
        }

        /// <summary>连接是否还能用：别名一致、对端活着、槽仍归本进程（pid + 代数）。</summary>
        /// <param name="channel">待判定的通道。</param>
        /// <param name="descriptor">对端当前的描述。</param>
        /// <returns>可以继续用时为 <c>true</c>。</returns>
        private static bool IsUsable(CourierHotChannel channel, CourierHotDescriptor descriptor)
        {
            if (!string.Equals(channel.Alias, descriptor.Alias, StringComparison.Ordinal)) return false;
            if (!channel.PeerAlive()) return false;
            return channel.StillOwned();
        }

        /// <summary>这个端点的缓存是否已经到达上限。</summary>
        /// <param name="endpointId">端点标识。</param>
        /// <returns>到达上限时为 <c>true</c>。</returns>
        private bool IsFullLocked(string endpointId)
        {
            List<Holder>? holders;
            if (!mChannels.TryGetValue(endpointId, out holders)) return false;
            return holders != null && holders.Count >= MAX_CHANNELS_PER_ENDPOINT;
        }

        /// <summary>取（必要时建）这个端点的连接列表。</summary>
        /// <param name="endpointId">端点标识。</param>
        /// <returns>连接列表。</returns>
        private List<Holder> HoldersLocked(string endpointId)
        {
            List<Holder>? holders;
            if (mChannels.TryGetValue(endpointId, out holders) && holders != null) return holders;
            var created = new List<Holder>(1);
            mChannels[endpointId] = created;
            return created;
        }

        /// <summary>归还一条连接。连接已经失效时顺手丢掉，不让死连接留在缓存里。</summary>
        /// <param name="endpointId">端点标识。</param>
        /// <param name="channel">借出的通道。</param>
        /// <param name="healthy">这次调用后是否还健康（对端活着且槽仍归本方）。</param>
        internal void Return(string endpointId, CourierHotChannel channel, bool healthy)
        {
            lock (mGate)
            {
                List<Holder>? holders;
                if (!mChannels.TryGetValue(endpointId, out holders) || holders == null) return;

                for (int index = 0; index < holders.Count; index++)
                {
                    if (!ReferenceEquals(holders[index].Channel, channel)) continue;
                    if (healthy && !mDisposed)
                    {
                        holders[index].Busy = false;
                        return;
                    }

                    holders.RemoveAt(index);
                    channel.Dispose();
                    return;
                }
            }
        }

        /// <summary>释放全部缓存连接；幂等。连接在锁外释放，避免 Dispose 的阻塞拖住其他线程。</summary>
        public void Dispose()
        {
            List<Holder> drained = new List<Holder>();
            lock (mGate)
            {
                if (mDisposed) return;
                mDisposed = true;
                foreach (List<Holder> holders in mChannels.Values) drained.AddRange(holders);
                mChannels.Clear();
            }

            for (int index = 0; index < drained.Count; index++)
            {
                try { drained[index].Channel.Dispose(); }
                catch (Exception caught) when (caught is IOException || caught is ObjectDisposedException) { }
            }
        }
    }
}
