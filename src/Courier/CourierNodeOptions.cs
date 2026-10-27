using System;

namespace Courier
{
    /// <summary>
    /// 端点的可选行为开关（SPEC §7、§8、§15）。
    /// <para>
    /// 分两类，默认值不同：
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <b>协议可选能力</b>（<see cref="AcquireHostLock"/>、<see cref="PublishRootRegistry"/>、
    /// <see cref="EnableHotJournal"/>）默认关闭，关闭时的行为与没有这些开关时逐字节相同：
    /// 单进程、单端点的宿主不会因为升级库而忽然多出一把锁、一个注册表文件或一份账本。
    /// 需要它们的宿主显式打开。
    /// </item>
    /// <item>
    /// <b>实现私有的性能旁路</b>（<see cref="EnableHotChannel"/>）默认**开启**：它是同一台机器上
    /// "用内存叫醒对方"的加速，不在 <c>endpoint.json</c> 里声明任何东西（SPEC §5.2、§2 的实现私有目录），
    /// 关掉它只是让每次投递多等一个轮询间隔，不改变任何协议可观察的结果。
    /// </item>
    /// <item>
    /// <b>磁盘卫生</b>（<see cref="ArchiveRetention"/>、<see cref="DeadletterRetention"/>、
    /// <see cref="ArchiveSweepInterval"/>）默认**开启**：SPEC §15.3 要求 <c>archive/</c> 的保留策略 MUST 有界，
    /// 一个只会搬入、不会删除的实现迟早把磁盘填满；在线的 <c>deadletter/</c> 同理（§12、§15.3）。
    /// 默认保留 14 天；需要长期留档的宿主必须显式调大保留期（或配成 0 自己兜住）。
    /// </item>
    /// </list>
    /// </summary>
    public sealed class CourierNodeOptions
    {
        /// <summary>注册表刷新间隔的下限（SPEC §7：限频刷新，例如 ≥1 s）。</summary>
        internal static readonly TimeSpan MIN_REGISTRY_REFRESH_INTERVAL = TimeSpan.FromSeconds(1);

        /// <summary>archive 保留期默认值：14 天（SPEC §15.3 要求有界）。</summary>
        internal static readonly TimeSpan DEFAULT_ARCHIVE_RETENTION = TimeSpan.FromDays(14);

        /// <summary>archive 清理的默认限频：一小时一轮。</summary>
        internal static readonly TimeSpan DEFAULT_ARCHIVE_SWEEP_INTERVAL = TimeSpan.FromHours(1);

        /// <summary>deadletter 保留期默认值：与 archive 同为 14 天（SPEC §12、§15.3 要求证据有界）。</summary>
        internal static readonly TimeSpan DEFAULT_DEADLETTER_RETENTION = TimeSpan.FromDays(14);

        /// <summary>archive 清理间隔的下限；再密就是在拿服务循环换一点点磁盘。</summary>
        internal static readonly TimeSpan MIN_ARCHIVE_SWEEP_INTERVAL = TimeSpan.FromMinutes(1);

        /// <summary>创建默认选项：可选能力全关，磁盘卫生按默认保留期开启（见类型注释）。</summary>
        public CourierNodeOptions()
        {
            AcquireHostLock = false;
            PublishRootRegistry = false;
            RegistryRefreshInterval = MIN_REGISTRY_REFRESH_INTERVAL;
            EnableHotJournal = false;
            EnableHotChannel = true;
            ArchiveRetention = DEFAULT_ARCHIVE_RETENTION;
            DeadletterRetention = DEFAULT_DEADLETTER_RETENTION;
            ArchiveSweepInterval = DEFAULT_ARCHIVE_SWEEP_INTERVAL;
        }

        /// <summary>
        /// 是否在打开端点时持有 <c>endpoints/&lt;id&gt;/host.lock</c> 的独占句柄（SPEC §8）。
        /// <para>
        /// 打开时默认关闭：一个宿主可能合法地同时打开同一个端点的多个节点（例如测试、或把
        /// <see cref="CourierNode"/> 当作无状态客户端用），默认开锁会把这类用法直接判死。
        /// 需要"一个端点目录只有一个服务者"的宿主把它设为 true。
        /// </para>
        /// </summary>
        public bool AcquireHostLock { get; set; }

        /// <summary>
        /// 是否维护 root 级注册表 <c>&lt;root&gt;/endpoint.json</c>（SPEC §7）。默认关闭。
        /// <para>
        /// 注册表只是"不能列目录的宿主"的加速视图，<c>endpoints/</c> 目录才是名单。
        /// 打开后本端点会在打开时、每次刷新、以及 Dispose 时重写它。
        /// </para>
        /// </summary>
        public bool PublishRootRegistry { get; set; }

        /// <summary>
        /// 打开注册表后，<see cref="CourierNode.Tick"/> 里刷新注册表的最小间隔（SPEC §7）。
        /// 小于 1 s 的值会被抬到 1 s：注册表刷新要重读-合并-原子替换一整个文件，
        /// 每轮 Tick 都做会把服务循环的固定开销推高一个数量级。
        /// </summary>
        public TimeSpan RegistryRefreshInterval { get; set; }

        /// <summary>
        /// 是否启用同一台机器上的内存热通道（默认**开启**）。
        /// <para>
        /// 热通道只是"叫醒"的加速：命令仍然先原子写进 <c>commands/</c>，终态仍然写进
        /// <c>results/&lt;requestId&gt;.json</c>，所以对任何观察方它都表现为符合 SPEC 的
        /// <c>commands/</c> → <c>results/</c>（SPEC §5.2 末尾那段"这不是快通道，这是快速的文件通道"）。
        /// 会合信息写在实现私有目录 <c>endpoints/&lt;id&gt;/hot/descriptor.json</c>，
        /// **不会**往 <c>endpoint.json</c> 里塞 <c>hot</c> 字段（SPEC §5.2 规定它 MUST NOT 出现）。
        /// </para>
        /// <para>
        /// 关掉它不影响正确性：每次投递退回文件轮询，最坏多花一个轮询间隔。
        /// 共享内存段、私有命名空间或门铃在任何一步失败时，实现同样自动退回文件协议，不需要这个开关兜底。
        /// </para>
        /// </summary>
        public bool EnableHotChannel { get; set; }

        /// <summary>
        /// 是否把热通道的调用记进 <c>endpoints/&lt;id&gt;/hot/</c> 下的账本。**默认关闭**。
        /// <para>
        /// 账本不在 SPEC §2 的目录布局里，也不是任何协议语义的依据——它是热通道"这条消息走的是内存
        /// 而不是文件"的旁证，而每一条调用的强证据始终是 <c>results/&lt;requestId&gt;.json</c>。
        /// 关掉它只少一份可离线翻阅的旁证，不影响正确性，也不再产生一个永不消失的追加文件。
        /// </para>
        /// <para>
        /// 打开后账本仍然**有界**：单份涨到 4 MiB 就换代并删掉上一代，
        /// 打开时还会清掉本目录里不属于本次运行、超过 24 小时没动过的旧账本。
        /// </para>
        /// </summary>
        public bool EnableHotJournal { get; set; }

        /// <summary>
        /// <c>archive/</c> 的保留期（SPEC §15.3：保留策略 MUST 有界）。默认 14 天。
        /// <para>
        /// 服务者会把 <c>archive/&lt;yyyy-MM-dd&gt;/&lt;endpointId&gt;/</c> 里**日期整目录早于**
        /// "今天减去保留期"的那些删掉。它只删自己的端点子目录，绝不碰别的端点的归档。
        /// 设为 <see cref="TimeSpan.Zero"/> 表示不清理——那意味着 archive 无界增长，
        /// 只应在宿主自己另有保留机制时使用。
        /// </para>
        /// </summary>
        public TimeSpan ArchiveRetention { get; set; }

        /// <summary>
        /// 在线 <c>endpoints/&lt;id&gt;/deadletter/</c> 的保留期（SPEC §12、§15.3）。默认 14 天。
        /// <para>
        /// 死信是终态证据，所以清理比 archive 多一道闸：**只有归档副本
        /// <c>archive/&lt;date&gt;/&lt;id&gt;/deadletter/</c> 确实存在时**才删在线那一份。
        /// 两份副本各自有界，而证据在两次删除之间始终至少存在一份。
        /// </para>
        /// <para>
        /// 设为 <see cref="TimeSpan.Zero"/> 表示不清理——那意味着在线死信无界增长，
        /// 只应在宿主自己另有保留机制时使用（对应 SPEC §15.3「保留期 MUST 允许配成不清理」）。
        /// </para>
        /// </summary>
        public TimeSpan DeadletterRetention { get; set; }

        /// <summary>
        /// archive 清理的限频。小于 1 分钟的值会被抬到 1 分钟；实际每一轮只做一次目录列举，
        /// 但把固定开销摊薄到每小时一次比每 200 ms 扫一遍 archive 划算得多。
        /// 在线死信的清理与 archive 共用这一个节奏：它们都是同一类维护动作。
        /// </summary>
        public TimeSpan ArchiveSweepInterval { get; set; }

        /// <summary>把刷新间隔夹进允许范围，避免宿主传 0 或负值导致每轮 Tick 都写盘。</summary>
        internal TimeSpan EffectiveRegistryRefreshInterval
        {
            get
            {
                if (RegistryRefreshInterval < MIN_REGISTRY_REFRESH_INTERVAL) return MIN_REGISTRY_REFRESH_INTERVAL;
                return RegistryRefreshInterval;
            }
        }

        /// <summary>把 archive 清理间隔夹进允许范围。</summary>
        internal TimeSpan EffectiveArchiveSweepInterval
        {
            get
            {
                if (ArchiveSweepInterval < MIN_ARCHIVE_SWEEP_INTERVAL) return MIN_ARCHIVE_SWEEP_INTERVAL;
                return ArchiveSweepInterval;
            }
        }

        /// <summary>规范化后的保留期；负值按 0（不清理）处理。</summary>
        internal TimeSpan EffectiveArchiveRetention
        {
            get
            {
                if (ArchiveRetention < TimeSpan.Zero) return TimeSpan.Zero;
                return ArchiveRetention;
            }
        }

        /// <summary>
        /// 规范化后的死信保留期；负值按 0（不清理）处理。
        /// <para>
        /// 还会被夹到 archive 保留期以内（archive 开着清理时）：归档副本是删在线死信的前提，
        /// 而副本自己活不过 archive 保留期。保留期比它更长时，那道闸会在副本消失之后永远为假，
        /// 在线死信反而一条也删不掉——所以这里夹住，让"两份副本各自有界"真的成立。
        /// archive 配成 0（不清理）时不夹：那种情况下没有任何东西会删归档副本。
        /// </para>
        /// </summary>
        internal TimeSpan EffectiveDeadletterRetention
        {
            get
            {
                TimeSpan retention = DeadletterRetention < TimeSpan.Zero ? TimeSpan.Zero : DeadletterRetention;
                TimeSpan archive = EffectiveArchiveRetention;
                if (archive > TimeSpan.Zero && retention > archive) return archive;
                return retention;
            }
        }
    }
}
