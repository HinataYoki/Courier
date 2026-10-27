using System;
using System.Collections.Generic;
using System.IO;

namespace Courier
{
    /// <summary>
    /// 热通道的会合描述：告诉潜在调用方"这个端点还有一条内存通道，叫什么名字、宿主是谁"。
    /// <para>
    /// 它写在 <c>endpoints/&lt;id&gt;/hot/descriptor.json</c> —— SPEC §2 的「实现私有文件与目录」。
    /// **MUST NOT 写进 <c>endpoint.json</c>**：SPEC §5.2 规定 v2 的 <c>hot</c> 字段 MUST NOT 出现。
    /// 换个实现读这个目录，读不出任何协议语义，所以它是实现私有的性能旁路，不是协议层快通道。
    /// </para>
    /// <para>
    /// 这一块是**缓存描述**，不是权威事实：读方拿不到就退回文件协议，读到了连不上也一样退回文件协议。
    /// 因此它 MUST NOT 携带任何"必须读到才能工作"的信息。
    /// </para>
    /// <para>
    /// 描述里的几何字段（slots / capacity / maxBodyBytes）在解析时就要与对端核一遍：两端的帧上限
    /// 不一致时，超限的那一端会一直投不进去，而症状只在"调用总是回落到文件通道"里体现，
    /// 从协议层完全看不出来（P3-16）。
    /// </para>
    /// </summary>
    internal sealed class CourierHotDescriptor
    {
        /// <summary>描述文件名（相对于 <see cref="CourierLayout.HotEvidenceDirectory"/> 指定的端点子目录）。</summary>
        internal const string HOT_FILE_NAME = "descriptor.json";

        /// <summary>块自身的格式版本。与 <see cref="CourierHot.FORMAT_VERSION"/>（内存布局版本）分开演进。</summary>
        internal const int DESCRIPTOR_VERSION = 1;

        /// <summary>evidence 取值：终态证据仍然由文件通道产生。热通道只负责叫醒，不负责记账。</summary>
        internal const string EVIDENCE_FILE = "file";

        /// <summary>接管一份描述；未声明单帧上限时按本实现的常量补齐。</summary>
        /// <param name="alias">私有命名空间别名。</param>
        /// <param name="hostPid">宿主进程号。</param>
        /// <param name="slots">槽位数。</param>
        /// <param name="capacity">每个方向的环容量。</param>
        internal CourierHotDescriptor(string alias, int hostPid, int slots, int capacity)
            : this(alias, hostPid, slots, capacity, CourierHot.BODY_BYTES)
        {
        }

        /// <summary>接管一份描述。</summary>
        /// <param name="alias">私有命名空间别名。</param>
        /// <param name="hostPid">宿主进程号。</param>
        /// <param name="slots">槽位数。</param>
        /// <param name="capacity">每个方向的环容量。</param>
        /// <param name="maxBodyBytes">对端可接收的单帧正文上限；<c>0</c> 表示未声明，按本实现的常量处理。</param>
        internal CourierHotDescriptor(string alias, int hostPid, int slots, int capacity, int maxBodyBytes)
        {
            Alias = alias;
            HostPid = hostPid;
            Slots = slots;
            Capacity = capacity;
            MaxBodyBytes = maxBodyBytes <= 0 ? CourierHot.BODY_BYTES : maxBodyBytes;
        }

        /// <summary>私有命名空间内的别名；池与门铃都挂在它下面。</summary>
        internal string Alias { get; private set; }

        /// <summary>宿主进程 pid。调用方据此打开进程句柄，用来判断"对端是不是已经死了"。</summary>
        internal int HostPid { get; private set; }

        /// <summary>槽位数。</summary>
        internal int Slots { get; private set; }

        /// <summary>每个方向的环容量。</summary>
        internal int Capacity { get; private set; }

        /// <summary>对端声明的单帧正文上限（字节）。</summary>
        internal int MaxBodyBytes { get; private set; }

        /// <summary>终态证据的来源；目前固定为文件通道。</summary>
        internal string Evidence { get; private set; } = EVIDENCE_FILE;

        /// <summary>把描述渲染成 JSON。字段与读取侧一一对应，多写的字段读方忽略。</summary>
        /// <returns>JSON 文本。</returns>
        internal string ToJson()
        {
            return new CourierJsonObject()
                .AddNumber("version", DESCRIPTOR_VERSION)
                .AddString("transport", CourierHot.TRANSPORT)
                .AddString("alias", Alias)
                .AddNumber("hostPid", HostPid)
                .AddNumber("slots", Slots)
                .AddNumber("capacity", Capacity)
                .AddNumber("maxBodyBytes", MaxBodyBytes)
                .AddString("evidence", Evidence)
                .Build();
        }

        /// <summary>解析描述对象本身（不是端点文件）。字段缺失或取值不一致时返回 null，调用方据此只用文件通道。</summary>
        /// <param name="json">描述文本。</param>
        /// <returns>解析结果，或 <c>null</c>。</returns>
        /// <remarks>
        /// <c>maxBodyBytes</c> 与 <c>evidence</c> 是"可以缺"的字段：早期版本的写入方没有它们，
        /// 缺省按本实现的常量补上；但一旦写了，就必须与对端核得上，否则宁可不连（P3-16）。
        /// </remarks>
        internal static CourierHotDescriptor? TryParse(string? json)
        {
            Dictionary<string, object?>? block;
            if (!CourierJson.TryParseObject(json, out block)) return null;
            if (block == null) return null;

            long? version = CourierJson.GetInteger(block, "version");
            if (version == null || version.Value != DESCRIPTOR_VERSION) return null;

            string? transport = CourierJson.GetString(block, "transport");
            if (!string.Equals(transport, CourierHot.TRANSPORT, StringComparison.Ordinal)) return null;

            string? alias = CourierJson.GetString(block, "alias");
            if (string.IsNullOrEmpty(alias)) return null;

            long? hostPid = CourierJson.GetInteger(block, "hostPid");
            if (hostPid == null || hostPid.Value <= 0 || hostPid.Value > int.MaxValue) return null;

            long? slots = CourierJson.GetInteger(block, "slots");
            if (slots == null || slots.Value <= 0 || slots.Value > CourierHot.MAX_SLOT_COUNT) return null;

            long? capacity = CourierJson.GetInteger(block, "capacity");
            if (capacity == null || capacity.Value <= 0 || capacity.Value > CourierHot.MAX_CAPACITY) return null;

            long? maxBodyBytes = CourierJson.GetInteger(block, "maxBodyBytes");
            if (maxBodyBytes != null && maxBodyBytes.Value < CourierHot.BODY_BYTES) return null;

            string? evidence = CourierJson.GetString(block, "evidence");
            if (evidence != null && !string.Equals(evidence, EVIDENCE_FILE, StringComparison.Ordinal)) return null;

            return new CourierHotDescriptor(
                alias,
                (int)hostPid.Value,
                (int)slots.Value,
                (int)capacity.Value,
                maxBodyBytes == null ? CourierHot.BODY_BYTES : (int)maxBodyBytes.Value);
        }

        /// <summary>实现私有的热通道目录（<c>endpoints/&lt;id&gt;/hot/</c>）。</summary>
        /// <param name="endpointDirectory">端点目录。</param>
        /// <param name="layout">布局。</param>
        /// <returns>目录路径。</returns>
        internal static string DirectoryFor(string endpointDirectory, CourierLayout layout)
        {
            return Path.Combine(endpointDirectory, layout.HotEvidenceDirectory);
        }

        /// <summary>描述文件的完整路径。</summary>
        /// <param name="endpointDirectory">端点目录。</param>
        /// <param name="layout">布局。</param>
        /// <returns>文件路径。</returns>
        internal static string PathFor(string endpointDirectory, CourierLayout layout)
        {
            return Path.Combine(DirectoryFor(endpointDirectory, layout), HOT_FILE_NAME);
        }

        /// <summary>
        /// 把描述写到实现私有目录。写不出来不是错误：读方拿不到描述就退回文件协议，
        /// 热通道本身也因此少一条旁路，协议行为不受影响（SPEC §2）。
        /// </summary>
        /// <param name="endpointDirectory">端点目录。</param>
        /// <param name="layout">布局。</param>
        /// <returns>是否写成功。</returns>
        /// <remarks>
        /// 目标路径上已经有**另一个宿主**留下的描述时拒绝覆写：端点目录可以被两个尚未拿到互斥锁的
        /// 节点共用（AcquireHostLock 默认关），后写者覆盖先到者的描述会让先到者的调用方全部连错池。
        /// 拒绝之后本地热通道照样能用（本进程自己持有池），只是不再对外宣告。
        /// </remarks>
        internal bool TryWrite(string endpointDirectory, CourierLayout layout)
        {
            try
            {
                string directory = DirectoryFor(endpointDirectory, layout);
                string path = Path.Combine(directory, HOT_FILE_NAME);
                if (IsOwnedByOther(path)) return false;

                CourierIO.EnsureDirectory(directory);
                CourierIO.WriteAtomic(path, ToJson(), true);
                return true;
            }
            catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException || caught is NotSupportedException)
            {
                return false;
            }
        }

        /// <summary>看目标路径上是否已经写着另一个宿主的描述。</summary>
        /// <param name="path">描述文件路径。</param>
        /// <returns>存在且明确属于别的宿主时为 <c>true</c>。</returns>
        private bool IsOwnedByOther(string path)
        {
            CourierHotDescriptor? existing = ReadAt(path);
            if (existing == null) return false;
            if (existing.HostPid == HostPid) return false;
            return !string.Equals(existing.Alias, Alias, StringComparison.Ordinal);
        }

        /// <summary>从任意路径直接读一份描述；读不到或不合规时返回 null。</summary>
        /// <param name="path">描述文件路径。</param>
        /// <returns>解析结果，或 <c>null</c>。</returns>
        private static CourierHotDescriptor? ReadAt(string path)
        {
            string? text;
            if (!CourierIO.TryReadAllText(path, out text)) return null;
            return TryParse(text);
        }

        /// <summary>
        /// 删掉描述文件，但只在它确实由本进程写下时才真的删。
        /// </summary>
        /// <param name="endpointDirectory">端点目录。</param>
        /// <param name="layout">布局。</param>
        /// <returns>描述已不存在或由本次调用删除时为 <c>true</c>；属于别的宿主而不动时为 <c>false</c>。</returns>
        /// <remarks>
        /// 保留这个两参形态是为了让宿主侧现有调用点（<c>CourierNode.HotClient.StopHotChannel</c>）
        /// 不必改动就拿到 P2-13 的修复：归属判断改为"描述里的 hostPid 是不是我自己"。
        /// 需要连别名一起核时用 <see cref="TryDeleteOwned"/>。
        /// </remarks>
        internal static bool TryDelete(string endpointDirectory, CourierLayout layout)
        {
            string path = PathFor(endpointDirectory, layout);
            if (!FileExists(path)) return true;

            CourierHotDescriptor? existing = ReadAt(path);
            if (existing != null && existing.HostPid != CourierHot.CurrentProcessId) return false;

            CourierIO.TryDelete(path);
            return true;
        }

        /// <summary>
        /// 只在描述确实属于本宿主时才删掉它。
        /// </summary>
        /// <param name="endpointDirectory">端点目录。</param>
        /// <param name="layout">布局。</param>
        /// <param name="alias">本进程的私有命名空间别名。</param>
        /// <param name="hostPid">本进程的进程号。</param>
        /// <returns>描述已不存在或由本次调用删除时为 <c>true</c>；属于别人而不动时为 <c>false</c>。</returns>
        /// <remarks>
        /// 端点目录里可能住着两个共用同一目录的节点：先退出的那个若无条件删除，会把仍在服务的
        /// 另一个节点的描述一起抹掉，对端于是每次都先撞一次连接失败（P2-13）。
        /// </remarks>
        internal static bool TryDeleteOwned(string endpointDirectory, CourierLayout layout, string alias, int hostPid)
        {
            string path = PathFor(endpointDirectory, layout);
            if (!FileExists(path)) return true;

            CourierHotDescriptor? existing = ReadAt(path);
            if (existing != null && (existing.HostPid != hostPid || !string.Equals(existing.Alias, alias, StringComparison.Ordinal)))
            {
                return false;
            }

            CourierIO.TryDelete(path);
            return true;
        }

        /// <summary>文件是否存在；把 IO 异常收敛成 <c>false</c>。</summary>
        /// <param name="path">路径。</param>
        /// <returns>存在时为 <c>true</c>。</returns>
        private static bool FileExists(string path)
        {
            try
            {
                return File.Exists(path);
            }
            catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>直接读目标端点的实现私有描述文件。端点不存在、文件读不到、内容不认识都返回 false。</summary>
        /// <param name="rootDirectory">协议根目录。</param>
        /// <param name="layout">布局。</param>
        /// <param name="endpointId">端点标识。</param>
        /// <param name="descriptor">解析结果。</param>
        /// <returns>读到合规描述时为 <c>true</c>。</returns>
        internal static bool TryRead(string rootDirectory, CourierLayout layout, string endpointId, out CourierHotDescriptor? descriptor)
        {
            descriptor = null;
            string endpointDirectory = Path.Combine(CourierLayout.Combine(rootDirectory, layout.EndpointsDirectory), endpointId);
            descriptor = ReadAt(PathFor(endpointDirectory, layout));
            return descriptor != null;
        }
    }
}
