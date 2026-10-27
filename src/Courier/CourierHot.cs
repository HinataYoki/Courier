using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Courier
{
    /// <summary>
    /// 热通道的协议常量与命名规则（见 docs/HOT-CHANNEL.md）。
    /// <para>
    /// 热通道是"数据面加速"，不是新的可信来源：同一批消息仍然要在 commands/ 与 results/ 里留下
    /// 逐字节相同的终态证据（SPEC §5.2 的第 2 条门槛）。所以这里出现的所有对象名都只是定位信息，
    /// 猜错名字的最坏后果是"打不开，回退文件通道"，不会改变语义。
    /// </para>
    /// <para>
    /// 全部对象都住在 Windows 的私有命名空间里（<c>CreatePrivateNamespaceW</c>）：私有命名空间
    /// 的对象在内核命名空间不可枚举，同用户的陌生进程既猜不到也列不出来。这条是热通道的信任边界，
    /// 与 SPEC §20 的"能写这个 root 的进程就是可信的"完全对齐 —— 能读到 endpoint.json 里的
    /// 命名空间别名，等价于能读这个 root。
    /// </para>
    /// <para>
    /// 本类型按职责拆成三个 partial 文件：常量与命名（本文件）、对端存活与进程句柄
    /// （<c>CourierHot.Liveness.cs</c>）、私有命名空间（<c>CourierHot.Namespace.cs</c>）。
    /// </para>
    /// </summary>
    internal static partial class CourierHot
    {
        /// <summary>endpoint.json 里 hot.transport 的取值。</summary>
        internal const string TRANSPORT = "mmap-ring";

        /// <summary>hot 块的格式版本；与协议版本相互独立。</summary>
        internal const int FORMAT_VERSION = 1;

        /// <summary>槽位数：同时可有多少个调用方持有热通道。</summary>
        internal const int SLOT_COUNT = 32;

        /// <summary>每个方向环形缓冲的槽数。</summary>
        internal const int CAPACITY = 16;

        /// <summary>单帧正文上限；放不下就回退文件通道，绝不阻塞投递方。</summary>
        internal const int BODY_BYTES = 2048;

        /// <summary>池头字节数。</summary>
        internal const int HEADER_BYTES = 256;

        /// <summary>单个槽描述符的字节数。</summary>
        internal const int DESC_BYTES = 128;

        /// <summary>环头字节数（每个槽两条环，共用这一段头）。</summary>
        internal const int RING_HEADER_BYTES = 64;

        /// <summary>帧头字节数。</summary>
        internal const int FRAME_HEADER_BYTES = 24;

        /// <summary>单帧总字节数。</summary>
        internal const int FRAME_BYTES = FRAME_HEADER_BYTES + BODY_BYTES;

        /// <summary>请求帧。</summary>
        internal const ushort KIND_REQUEST = 1;

        /// <summary>响应帧。</summary>
        internal const ushort KIND_RESPONSE = 2;

        /// <summary>
        /// 槽位数的硬上限。对方头部自报的槽数超过它，一律当作"这块池不是我们的"：
        /// 头部与映射长度都来自对端进程，越界值只能让后续每一次偏移计算变成越界访问。
        /// </summary>
        internal const int MAX_SLOT_COUNT = 256;

        /// <summary>单个方向环容量的硬上限，理由同 <see cref="MAX_SLOT_COUNT"/>。</summary>
        internal const int MAX_CAPACITY = 4096;

        /// <summary>
        /// 热通道是否可用。目前只有 Windows 有私有命名空间；其它平台一律退化为纯文件协议，
        /// 这条判断是"退化为默认行为"而不是"报错"。
        /// </summary>
        internal static bool IsSupported
        {
            get { return Environment.OSVersion.Platform == PlatformID.Win32NT; }
        }

        /// <summary>池的总字节数。</summary>
        /// <param name="slotCount">槽位数。</param>
        /// <param name="capacity">每个方向的环容量。</param>
        /// <returns>整块共享内存的字节数。</returns>
        internal static long TotalBytes(int slotCount, int capacity)
        {
            return HEADER_BYTES + ((long)DESC_BYTES * slotCount) + (RingBytesFor(capacity) * slotCount);
        }

        /// <summary>
        /// 单个槽的环区字节数：两个方向各一条独立的环。
        /// <para>
        /// 两条环必须各自拥有完整的 <paramref name="capacity"/> 个帧槽，不能共用一个帧数组：
        /// 共用时宿主写响应会覆写客户端还没读的请求，症状是"偶发丢一条请求"，而不是任何可诊断的错误。
        /// </para>
        /// </summary>
        /// <param name="capacity">每个方向的环容量。</param>
        /// <returns>字节数。</returns>
        internal static long RingBytesFor(int capacity)
        {
            return OneWayRingBytes(capacity) * 2;
        }

        /// <summary>单个方向一条环的字节数（环头 + <paramref name="capacity"/> 个帧槽）。</summary>
        /// <param name="capacity">该方向的环容量。</param>
        /// <returns>字节数。</returns>
        internal static long OneWayRingBytes(int capacity)
        {
            return RING_HEADER_BYTES + ((long)FRAME_BYTES * capacity);
        }

        /// <summary>
        /// 用映射的实际长度反算并校验池几何，而不是只信任头部自报的槽数与容量。
        /// <para>
        /// 头部来自另一个进程（甚至可能是崩溃残留或伪造的内存），单看它自己永远自洽；
        /// 唯一的客观事实是"我们真的映射到了多少字节"。二者必须互相印证，否则任何一次
        /// 偏移计算都可能越界，而越界访问抛出的 <see cref="ArgumentException"/> 会带走宿主泵线程。
        /// </para>
        /// </summary>
        /// <param name="mappedBytes">实际映射到的字节数（<c>MemoryMappedViewAccessor.Capacity</c>）。</param>
        /// <param name="slotCount">头部自报的槽位数。</param>
        /// <param name="capacity">头部自报的环容量。</param>
        /// <param name="ringBytes">头部自报的单槽环区字节数。</param>
        /// <param name="validatedSlotCount">校验通过的槽位数。</param>
        /// <param name="validatedCapacity">校验通过的环容量。</param>
        /// <returns>几何自洽且放得进映射时为 <c>true</c>。</returns>
        internal static bool TryValidateGeometry(
            long mappedBytes,
            int slotCount,
            int capacity,
            long ringBytes,
            out int validatedSlotCount,
            out int validatedCapacity)
        {
            validatedSlotCount = 0;
            validatedCapacity = 0;
            if (mappedBytes < HEADER_BYTES) return false;
            if (slotCount <= 0 || slotCount > MAX_SLOT_COUNT) return false;
            if (capacity <= 0 || capacity > MAX_CAPACITY) return false;
            long expectedRing = RingBytesFor(capacity);
            if (ringBytes != expectedRing) return false;
            if (TotalBytes(slotCount, capacity) > mappedBytes) return false;
            validatedSlotCount = slotCount;
            validatedCapacity = capacity;
            return true;
        }

        /// <summary>
        /// root 的稳定标签：同一路径（大小写不敏感）永远得到同一个值。
        /// 用途是让两个互不相干的 root 在内核命名空间里天然落在不同名字上，而不是互相撞名。
        /// </summary>
        /// <param name="rootDirectory">已规范化的 root 绝对路径。</param>
        /// <returns>16 个十六进制字符。</returns>
        internal static string RootTag(string rootDirectory)
        {
            string normalized = rootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
            byte[] hash;
            using (SHA256 sha = SHA256.Create())
            {
                hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
            }

            var text = new StringBuilder(16);
            for (int index = 0; index < 8; index++)
            {
                text.Append(hash[index].ToString("x2", CultureInfo.InvariantCulture));
            }

            return text.ToString();
        }

        /// <summary>
        /// 私有命名空间的别名。它同时是"这一代宿主"的身份：sessionId 一变，上一代的所有对象
        /// 都落在另一个命名空间里，重启不需要清理残留。
        /// </summary>
        /// <param name="rootDirectory">协议根目录。</param>
        /// <param name="endpointId">端点标识。</param>
        /// <param name="sessionId">本次运行的会话标识。</param>
        /// <returns>命名空间别名。</returns>
        internal static string NamespaceAlias(string rootDirectory, string endpointId, string sessionId)
        {
            return "courier-" + RootTag(rootDirectory) + "-" + endpointId + "-" + sessionId;
        }

        /// <summary>边界描述符的名字。它只是本地标签，真正的门槛是它挂上去的 SID 集合。</summary>
        /// <param name="rootDirectory">协议根目录。</param>
        /// <param name="endpointId">端点标识。</param>
        /// <returns>边界名字。</returns>
        internal static string BoundaryName(string rootDirectory, string endpointId)
        {
            return "courier-bd-" + RootTag(rootDirectory) + "-" + endpointId;
        }

        /// <summary>池对象在命名空间里的短名。</summary>
        internal const string POOL_OBJECT_NAME = "pool";

        /// <summary>"有新连接"门铃的短名。</summary>
        internal const string ACCEPT_OBJECT_NAME = "accept";

        /// <summary>
        /// 抢槽互斥锁的短名。它是命名互斥体而不是信号量：持有者在 WaitOne 与 Release 之间被强杀时，
        /// 互斥体带 abandoned 语义（下一个等待者接管），信号量没有，那个槽会永远抢不回来。
        /// </summary>
        internal const string CLAIM_OBJECT_NAME = "claim";

        /// <summary>第 slot 个槽的门铃短名。</summary>
        /// <param name="slot">槽号。</param>
        /// <param name="a2b"><c>true</c> 表示客户端 → 宿主方向的"有新请求"门铃。</param>
        /// <returns>命名空间内的短名。</returns>
        internal static string DoorObjectName(int slot, bool a2b)
        {
            return "s" + slot.ToString(CultureInfo.InvariantCulture) + (a2b ? "-a2b" : "-b2a");
        }

        /// <summary>门铃名的前缀。同一个私有命名空间里的对象名仍然要全局唯一，所以别名必须整段带上。</summary>
        /// <param name="rootDirectory">协议根目录（保留参数，别名已含 root 标签）。</param>
        /// <param name="endpointId">端点标识（保留参数）。</param>
        /// <param name="alias">私有命名空间别名。</param>
        /// <returns>拼对象全名用的前缀。</returns>
        internal static string DoorPrefix(string rootDirectory, string endpointId, string alias)
        {
            return alias;
        }

        /// <summary>当前进程号；取不到时返回 0。</summary>
        internal static int CurrentProcessId
        {
            get { return CourierProtocol.CurrentProcessId(); }
        }

        // ---- 池头 ----（相对池首字节的偏移）
        /// <summary>魔数；最后写入，是"这块池可用"的发布点。</summary>
        internal const int OFF_MAGIC = 0;

        /// <summary>池格式版本。</summary>
        internal const int OFF_VERSION = 4;

        /// <summary>槽位数。</summary>
        internal const int OFF_SLOT_COUNT = 8;

        /// <summary>每个方向的环容量（槽数）。</summary>
        internal const int OFF_CAPACITY = 12;

        /// <summary>宿主进程号。</summary>
        internal const int OFF_HOST_PID = 16;

        /// <summary>单个槽占的字节数（环头 + 全部帧槽）。</summary>
        internal const int OFF_RING_BYTES = 24;

        /// <summary>池创建时刻（UTC Ticks）。</summary>
        internal const int OFF_CREATED_UTC = 32;

        // ---- 槽描述符 ----（相对槽描述符首字节的偏移）
        /// <summary>0 = 空闲，1 = 已被某进程占据。</summary>
        internal const int OFF_STATE = 0;

        /// <summary>这个槽被占用的代数；每次重新占据或回收都加一，用来识别"新主人"。</summary>
        internal const int OFF_GENERATION = 4;

        /// <summary>占据者的进程号。</summary>
        internal const int OFF_OWNER_PID = 8;

        /// <summary>客户端 → 宿主方向的写指针。</summary>
        internal const int OFF_A2_BHEAD = 16;

        /// <summary>客户端 → 宿主方向的读指针。</summary>
        internal const int OFF_A2_BTAIL = 24;

        /// <summary>宿主 → 客户端方向的写指针。</summary>
        internal const int OFF_B2_AHEAD = 32;

        /// <summary>宿主 → 客户端方向的读指针。</summary>
        internal const int OFF_B2_ATAIL = 40;

        /// <summary>这个槽最近一次活动的时刻（UTC Ticks），只用于诊断。</summary>
        internal const int OFF_LAST_SEEN = 48;

        // ---- 帧 ----（相对帧首字节的偏移）
        /// <summary>提交字：写满整帧后最后写它，读到 head 对应的值才算这一帧成立。</summary>
        internal const int OFF_SEQ = 0;

        /// <summary>调用方自选的关联号，响应原样带回。</summary>
        internal const int OFF_FRAME_ID = 8;

        /// <summary>帧类型。</summary>
        internal const int OFF_KIND = 12;

        /// <summary>保留位。</summary>
        internal const int OFF_FLAGS = 14;

        /// <summary>正文有效字节数。</summary>
        internal const int OFF_LENGTH = 16;

        /// <summary>正文的 CRC32。</summary>
        internal const int OFF_CRC = 20;

        // ---- 环头 ----（相对单个槽环区首字节的偏移）
        /// <summary>客户端 → 宿主方向的环形缓冲起始偏移。</summary>
        internal const int OFF_RING_A2_B = 0;

        /// <summary>宿主 → 客户端方向的环形缓冲相对 A2B 的步长。</summary>
        internal const int OFF_RING_STRIDE = RING_HEADER_BYTES;
    }
}
