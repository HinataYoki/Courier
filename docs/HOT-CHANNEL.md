# 热通道设计说明

> 状态：设计说明 + 可运行原型验证。协议以 [SPEC.md](SPEC.md) 为准，本文档不修改任何协议条款。
> 所有数字都是本机实测（Windows 11 Build 26300 / .NET 10 / 12 核，见 tools/ 下各基准）。
> 原型：tools/hotdemo/（HotPool.cs + Program.cs，约 900 行，可双进程跑通）。
>
> **这份文档是设计档案，不是使用说明。** 想看怎么用、怎么调、怎么关掉热通道，读 [README](../README.md)；
> 想改实现，读 `src/Courier/CourierHot*.cs` 与其中引用的各节。本文保留的是"为什么这么设计"以及
> 当时的实测数字——协议侧没有任何一条依赖它们。

---

## 0. 一句话结论

**文件系统继续做「可信基座」，内存通道只做「数据面」，而数据面必须住在 pagefile-backed 命名节里，不能住在文件映射里。**

这条判断不是设计偏好，是实测逼出来的：文件映射一旦被写过，下一次 open 就要等脏页写回，p50 4.1 ms —— 正好等于想逃离的那笔 FlushFileBuffers 的钱。换成 pagefile 命名节后同一操作 p50 12 µs，差 340 倍。

---

## 1. 问题重述

Courier 的目标形态是「AI agent 通过 CLI 操作一个常驻软件」。CLI 与软件是两个独立进程，需要一个稳定、可观测、零安装的进程间通道。

现状：以 root 目录为信道，commands/ → results/ 请求响应，state/ 状态发布。

实测出的三层瓶颈（按贡献从大到小）：

| 层 | 实测成本 | 说明 |
|---|---|---|
| **进程启动** | **19 ms（NativeAOT）/ 47 ms（JIT）/ 21 ms（Windows 自带 native 小工具）** | 每次 CLI 调用都要付一次，是绝对主项 |
| 落盘承诺 | 1.64 ms（含 FlushFileBuffers）vs 3.7 µs（不含） | 相差 2 个数量级 |
| 等待原语 | Thread.Sleep(1) = 15.5 ms | 本机定时器粒度被放大 15 倍 |
| 通道往返 | 文件 1.6 ms / 热池 6 µs / 管道 13 µs | 比进程启动小 3 个数量级 |

**关键推论：换掉文件系统只能解决第二项，而第二项恰恰是「落盘即承诺」这个设计目标本身。**

所以正确的切分不是「用内存替掉文件」，而是：

- **承诺（commit）留在文件**：命令一旦被受理，必须有可被 ls / cat 观察到的终态证据。
- **数据面（transport）搬到内存**：同一次调用里命令体与响应的搬运不必经过磁盘。

---

## 2. 实测：为什么几种「显而易见」的方案不成立

### 2.1 文件映射（file-backed mmap）—— 被脏页写回吃掉

tools/hotdemo 的 flushcost 探针，n=15：

| 载体 | 未写过数据 | 写过 530688 字节 |
|---|---|---|
| 文件映射 open (p50) | 34 µs | **4131 µs** |
| 文件映射 close (p50) | 10 µs | **1145 µs** |
| 命名节 open (p50) | 10 µs | **12 µs** |
| 命名节 close (p50) | 4 µs | **45 µs** |

原因：file-backed section 的脏页归 VFS 管，open 一个新句柄会把映射的写回代价算进去。共享内存环若放在文件里，每建立一个连接就要等上一次会话的脏页落地 —— 通道本身 5 µs，连接建立 4 ms。

这也是本文 §6.3 里「文件映射环形缓冲」那个 1.3 µs 的适用范围：它测的是同一进程内空转轮询，不含跨进程建立与写回。

### 2.2 命名管道 / Unix domain socket —— 快，但信任模型变了

| 通道 | p50 | p99 |
|---|---|---|
| 命名管道（.NET NamedPipeServerStream） | 13.1 µs | 33.2 µs |
| 命名节热池（本方案） | 3–6 µs | 8–16 µs |

管道本身没问题，问题在 SPEC §20.7 已经写明的：**管道名对同机任何进程可见可连，与「能写这个目录才可信」不等价**。要给管道补鉴权（能力令牌），而命名节同样需要令牌 —— 那就没有必要为了管道多付一层。

另外 .NET 的 NamedPipeServerStream 在 Unix 上会落到 /tmp/.dotnet/corefx/pipe/，正好踩 SPEC §20.3 的 /tmp 禁区。

### 2.3 目录变更通知（FileSystemWatcher）—— 可以，但只能当提示

tools/hotbench 的 watch 模式，300 次：p50 **948 µs**，p99 **5002 ms**（有漏事件，靠 5 秒超时兜底）。目录通知能把「秒级轮询」降到「毫秒级等待」，但尾部完全不可控，**不能作为唯一唤醒机制**。

### 2.4 结论矩阵

| 方案 | 往返 p50 | 需要改协议 | 信任边界 | 判定 |
|---|---|---|---|---|
| 文件 + 轮询（现状） | 1.6 ms + 轮询 | 否 | 不变 | 基线 |
| 文件 + 目录通知 | ~1 ms，尾部差 | 否 | 不变 | 可作为过渡（T0） |
| 文件映射环 | 1.3 µs（同进程）→ 3–5 ms（跨进程建连） | 是 | 不变 | **否决**（脏页） |
| 命名管道 | 13 µs | 是 | **改变** | 否决（无净收益） |
| **命名节槽池** | **3–6 µs** | 是 | 不变 | **采纳** |

---

## 3. 方案：热池（Hot Pool）

### 3.1 拓扑

```
              root/endpoint.json  (durable, 发现入口)
                     |
                     |  { pid, sessionId, hot:{ poolName, doorPrefix, slots, capacity } }
                     v
   客户端进程  ──────>  [ 命名节 pool (pagefile-backed) ]  <──────  宿主进程
   (CLI)                slot0 .. slot31                        (常驻软件)
                       每槽两条环：C→H 与 H→C
                       每槽一个 C→H 门铃 + 一个 H→C 门铃
```

- **槽 = 连接**，不是消息。客户端连上来先 CAS 抢一个槽，用完释放；32 槽够几百个串行 CLI 调用。
- **宿主不参与建连**。宿主启动时把整块池建好、把 32 对门铃建好、写 endpoint.json，之后只做「等门铃 → 排空槽 → 回写响应」。
- 这是相对上一版（per-client ring）的关键修正。那个版本每次连接要客户端 5.8 ms + 宿主 5–19 ms 建环，单发场景净亏。

### 3.2 布局（与 HotPool.cs 逐字段一致）

**池头** 256 字节：

```
偏移  字段
0     magic      = 0x34525543 ("CUR4")
4     version    = 1
8     slotCount
12    capacity   (每槽环深，默认 64)
16    hostPid
24    ringBytes  (每槽环的字节数 = 64 + 256 * capacity)
```

**槽描述符** 每槽 128 字节（描述符基址 = 256 + slot * 128），32 槽共 4 KiB，全在 L1/L2 里：

```
偏移  字段
0     state       int   0 = FREE / 1 = CLAIMED（CAS 抢槽就抢这一个字）
4     generation  int   槽被复用时 +1，让旧持有者的残留读写失效
8     ownerPid    long  占用者 pid
16    a2bHead     long  客户端写，宿主读
24    a2bTail     long  宿主写，客户端读
32    b2aHead     long  宿主写，客户端读
40    b2aTail     long  客户端写，宿主读
48    (pad 至 128，保证每个槽的头部独占缓存行)
```

**每槽环形缓冲** 紧跟描述符区之后，64 字节环头 + 256 字节 × capacity：

```
偏移  字段
0     seq      long    唯一提交字
8     frameId  int     请求-响应配对（客户端回环比对，防串槽）
12    kind     ushort  业务类型
14    flags    ushort
16    bodyLen  int
20    crc32    uint    body 校验
24    body...  (最多 MaxBodyBytes = 256 - 24 = 232 字节)
```

**提交协议**：写 body → 写 bodyLen → 写 crc32 → Thread.MemoryBarrier() → Volatile.Write(ref *seq, head + 1)。

**只有 seq 是提交字**，读者看到 seq 前进才认为帧有效，读完 body 再比一次 crc32。

> 踩过的坑：seq 占 8 字节，帧头字段必须从 +8 起。把 frameId 写在 +4 会覆盖 seq，症状是「收发都成功但 frameId 恒为 0」。

### 3.3 门铃

每槽两个命名 AutoResetEvent：

```
"courier-<rootTag>-<sessionId>-<slot>-a2b"
"courier-<rootTag>-<sessionId>-<slot>-b2a"
```

- rootTag = SHA256(规范化 root 路径) 前 8 字节。
- sessionId = 每代宿主新生成的随机串（原型当前取 64 位，落地改 128 位）。
- **必须带 sessionId**：宿主被杀时若还有客户端持着那个节的映射，内核不回收这个名字；只按 root 推导会让新一代宿主 CreateNew 直接失败。加了 sessionId 后每代宿主有自己的命名空间，旧一代无论多脏都互不影响。

已实测：宿主处理中 kill -9，同 root 立刻重启新宿主成功，新客户端 ok=300 fallback=0。

等待用 WaitHandle.WaitAny([bell, death], timeout)，**不是轮询**。内核级等待，无空转、无 Sleep。

### 3.4 存活检测

```csharp
const uint SYNCHRONIZE = 0x00100000;
[DllImport("kernel32.dll", SetLastError = true)]
static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
```

直接 OpenProcess(SYNCHRONIZE) 拿一个可 WaitOne 的内核句柄塞进 WaitAny。

- 打不开 = pid 已不存在 = 已经死了（不能当成「还不知道」）。这条把「死前先开句柄」的路径从 2009 ms 降到 0.84 ms。
- 用裸 OpenProcess 而不是 Process.GetProcessById：后者内部开销 1.2 ms，前者 0.27 ms。
- 实测崩溃检测延迟：**6.0–6.9 ms**，由内核 WaitAny 决定，不是超时预算、不是轮询周期。

### 3.5 背压与降级

- TrySend 环满返回 false，**绝不阻塞、绝不覆盖**。调用方必须回退文件通道。
- 抢不到槽（Claim 超时）同样回退。
- 收不到响应且对端已死 → **永久降级**到文件通道，同一 session 内不再信任这个池（因为重试会重复投递）。
- 全部回退路径已实测：删掉描述（当时还叫 `endpoint.json` 的 `hot` 字段，现在是 `endpoints/<id>/hot/descriptor.json`，见 §5.1）后客户端打印 hot unavailable 的 FileNotFoundException，随后 transport=file n=5 ok=5 fallback=5。

---

## 4. 投递语义：热通道是 at-most-once，文件通道是 at-least-once

这是本方案最需要写进规范的一节。

- 文件通道：写命令文件 → 宿主受理 → 写结果文件（终态证据）。可重复投递，靠结果文件去重。
- 热通道：帧可能丢（环满）、可能重复（重试）、**没有任何落盘证据**。

如果只是「快了」，语义退化，就没人敢用。所以必须补一条：**幂等键 + 有界去重表**。

```
宿主维护 dedupe: frameId -> result (最近 N 条, FIFO 淘汰)
收到帧：
  若 frameId 在 dedupe 中 → 直接回放结果，不重新执行
  否则 → 执行 → 写 results/<id>.json (durable) → 记入 dedupe → 回帧
```

这样：

1. 热通道丢了响应、客户端改走文件通道重试 → 宿主命中 dedupe，回放结果，不重复执行。
2. 热通道完全不可用 → 客户端全程走文件通道，行为与今天逐字节一致。
3. 宿主崩溃 → 客户端 WaitAny 在 6 ms 内感知 → 降级文件通道 → 若宿主重启则重新受理。

**这条要求把「终态证据」留在文件里**：即便全程走热通道，results/ 里也必须有那条记录。否则 ls results 就看不全，违背「落盘即承诺」。

去重表大小取 MAX_COMMANDS_BACKPRESSURE（4096）是可接受的上界。

---

## 5. 落地要改的规范条款

### 5.1 热通道描述：写进实现私有文件，不写进 endpoint.json

早期设计原本是把 `hot` 块塞进 `endpoint.json`。**落地时否掉了这个做法**：协议规范 §5.2 明写 `hot` 字段 MUST NOT 出现在 `endpoint.json` 里，往里面写就是自违约。现在描述写到实现私有目录：

```
endpoints/<id>/hot/descriptor.json     ← 描述（本文件；实现私有，SPEC §2）
endpoints/<id>/hot/hot-<sessionId>.log ← 账本（可选，默认不写；实现私有）
```

```json
{
  "version": 1,
  "transport": "mmap-ring",
  "alias": "courier-5a2ff2162b4ec905-app-a-b7350a469c304bcd",
  "hostPid": 18716,
  "slots": 32,
  "capacity": 16,
  "maxBodyBytes": 2048,
  "evidence": "file"
}
```

描述文件缺席时（端点在别的机器上、热通道被 `CourierNodeOptions.EnableHotChannel` 关掉、或池没建起来）客户端必须逐字节走文件通道——`CourierHotChannel.TryOpen` 拿到 null 描述符直接返回 null，调用方无条件下落到轮询。

> **字段名与最早的设计不同**（设计写于原型打通前）：设计里的 `poolName` + `doorPrefix` 合成一个 `alias`（池、门铃、边界都在这个私有命名空间下），`slotBytes` 与 `doorbell` 落到了 `CourierHot` 的常量里而不是描述符里。改描述符时以 `CourierHotDescriptor.ToJson()` 为准，并同步本节。

**为什么这个目录可以在协议外观测不到地存在**：SPEC §2 已补「实现私有文件与目录」条款——这类文件 MUST NOT 承载跨实现语义，读方 MUST 忽略，删掉它不改变任何一条协议行为。热通道正好符合：它只负责叫醒，终态证据始终是 `results/<requestId>.json`。

### 5.2 需要同时修改的既有条款

| 条款 | 现状 | 需要的改动 |
|---|---|---|
| §5.2 快通道 | 协议不包含，重新纳入需三条门槛 | **不需要改动了**：描述走实现私有目录（§2），`endpoint.json` 里不出现 `hot`，本条因此不适用于本实现。若将来想把它写回协议，仍需三条门槛，**第 1 条（双实现对拍）TODO：本仓库无法完成** |
| §2 实现私有文件与目录 | 已补条款（上一轮加的） | 热通道正好落在这一条下面：`endpoints/<id>/hot/` 是实现私有目录，读方忽略、删掉不改变任何协议行为 |
| §20.7 鉴权 | 管道无等价信任机制 | 命名节的等价机制：节名含随机 sessionId，而 sessionId 现在只在 `endpoint.json` 与实现私有描述里出现——后者同样要求能读 root 目录 |
| §22.3 非目标 | 「MUST NOT 扩展协议层快通道」 | **不需要解禁**：本实现没有扩展协议层，旁路整个在实现层 |
| §22.9 传输抽象 | MUST NOT 有 IMessageTransport | **维持不变**。热池不是传输抽象，是实现私有的旁路 |
| §19 常量 | MAX_STATE_BYTES 等 | 不新增协议常量。HOT_SLOTS=32、HOT_CAPACITY=16、HOT_BODY_BYTES=2048 全是实现内部常量（将来若进协议才需要写进 §19） |

### 5.3 安全：节名的保密性就是信任边界

SPEC §20 的模型是「能写这个目录的进程就是可信的」。命名节要做到等价，只有一条路：**节名必须只能从 root 目录里读到**。

- rootTag 由 root 路径派生，可猜；
- sessionId 是随机串，只在 endpoint.json 里；
- 所以「能算出节名」⇔「能读 endpoint.json」⇔「能读 root 目录」。

限制必须写清楚：**撤销 ACL 后，已读过 sessionId 的进程仍可连到本代宿主的池，直到宿主换代。** 这是 §20.7 那条的必然推论，不可消除，只能缩短窗口（宿主定期换 sessionId = 更频繁地重建池，成本 13–16 µs 级，可接受）。

---

## 6. 实测数据汇总

全部为本机实测（Windows 11 Build 26300 / .NET 10 / 12 核）。

### 6.1 延迟

| 指标 | 数值 |
|---|---|
| 稳态往返 p50（AOT，单客户端 n=5000） | **6.4–6.7 µs** |
| 稳态往返 p99 | 10.1–10.5 µs |
| 稳态往返 p50（4 客户端并发） | 2.5–6.6 µs |
| 连接建立（AOT 冷进程） | 150–220 µs |
| 连接建立（稳态，同进程反复） | 13–15 µs |
| 崩溃检测 | 6.0–6.9 ms |
| 单发端到端 wall clock（AOT） | 见 6.5 |
| 单发无操作对照（AOT version 子命令） | 见 6.5 |

### 6.2 吞吐

| 客户端数 | wall | aggregate |
|---|---|---|
| 1 | 75 ms | 13,333 msg/s |
| 2 | 76 ms | 26,315 msg/s |
| 4 | 87 ms | 45,977 msg/s |
| 8 | 124 ms | 64,516 msg/s |

15 个客户端进程全部 ok=1000 fallback=0 mismatch=0（上面每一格都是实测）。8 客户端时 p99 在 8–91 µs 之间抖动，其中三个超过 60 µs，max 到 435 µs —— 宿主单线程泵开始成为瓶颈。

### 6.2b 启动成本：这才是单发延迟的主项

前面所有「单发 29–50 ms」的逐次采样都被测量方式污染了 —— `date +%s%N` 在这个环境里每次要 13.5 ms。改成「跑 20 次取总墙钟再除」（tools/hotdemo/startfloor2.sh、single2.sh）：

| 程序 | 每次耗时（20 次平均） |
|---|---|
| Windows 自带 native hostname.exe | **21 ms** |
| AOT 版 hotdemo version（noop） | **19 ms** |
| AOT 版 hotdemo echo（完整一次热通道调用） | **24 ms** |
| AOT 版 hotdemo fileonce（完整一次文件通道调用） | **22 ms** |
| JIT 版 hotdemo version（dotnet apphost） | **47 ms** |

三个必须写进结论的事实：

1. **19–21 ms 是操作系统给的地板。** AOT 产物与 Windows 自带 native 小工具同档。Rust / C / Go 写出来也是这个数 —— 钱花在 CreateProcess、加载映像、初始化堆上，与语言无关。**换语言不会更快。**
2. **JIT 启动要付 2.5 倍。** 同样是 .NET，JIT 版 47 ms vs AOT 版 19 ms。这是本项目里**单项收益最大**的一个决定，比换通道（1.6 ms）大一个数量级。Courier 现网 `courier.exe version` 实测 **49 ms/次**（20 次摊销），已构建好的 AOT 版同机只要 19 ms。
3. **单发场景下两条通道打平**（24 ms vs 22 ms），因为两者都被 19 ms 的进程启动淹没。热通道的 6 µs 在这个尺度上看不见。

推论：**热通道不是为了「让单发 CLI 更快」存在的。** 它解决的是：

- 一次调用里通道部分的可确定性（22–24 ms 抖到 21 ms 地板，而不是 60 ms 里含 1.6 ms 落盘 + 0–200 ms 轮询）；
- 高频调用（agent 一轮里连发几十条命令）时的吞吐与尾延迟；
- 常驻客户端（B 档 stdio 助手）形态下每次调用只付 6 µs。

要让单发也接近地板，只有一条路：**别每次都起新进程**。这也是常驻客户端形态（宿主进程里直接持有 `CourierNode`，或起一个 stdio 助手进程转发）真正的价值所在。

### 6.3 对照

| 通道 | 往返 p50 |
|---|---|
| 热池（命名节） | 3–6 µs |
| 命名管道 | 13.1 µs |
| 文件映射环（跨进程建连） | 3–5 ms（脏页写回） |
| 文件 + 1 ms 轮询 | 15.5 ms |

### 6.4 落盘成本（tools/durabench，n=400）

| 模式 | p50 |
|---|---|
| 原子写 + FlushFileBuffers + rename | 1597 µs |
| 原子写（只 Flush，不刷盘） | 710 µs |
| 追加写 + 每次 FlushFileBuffers | 536 µs |
| 追加写（只进页缓存） | 1.9 µs |
| 追加写 + 批量刷（64 条或 2 ms） | 1.6 µs |

**批量刷（group commit）能把落盘成本摊到 1.6 µs。** 这是「保留 durable 语义」的第三条路：热通道负责实时性，results/ 用批量刷负责承诺。

### 6.5 已落地：账本从「每条一次 fsync」改成「组提交 + 有界」

原型阶段的账本（`src/Courier/CourierHotJournal.cs`）是「追加写 + 组提交 + `Flush(true)`」，且没有任何上限——一个端点跑一晚就多一个几十 MB、永远不会有人再读的文件。现在改成：

| 项 | 原型 | 现在 |
|---|---|---|
| 是否写 | 无条件 | **默认不写**（`CourierNodeOptions.EnableHotJournal`，默认 `false`） |
| 持久性 | 每条一次 `FlushFileBuffers` | 只 `Flush()`，不 fsync |
| 组提交窗口 | 64 条或 2 ms | 64 条或 **250 ms** |
| 单文件上限 | 无 | **4 MiB**，到顶换代（`hot-<sid>-<n>.log`）并删上一代 |
| 启动清理 | 无 | 删掉本目录里不属于本会话、且超过 24 小时没动过的账本 |

理由见 §4：账本从来不是协议语义的依据，**强证据始终是 `results/<requestId>.json`**（它仍然每次 `durable` 落盘）。账本记的只是「这条消息走的是内存而不是文件」的旁证，断电丢最近若干条不影响任何一条不变量。这条改动把账本的磁盘增长从「无界」变成「常数上界」，同时把写放大从每条约一次 fsync 降到批量刷的量级。

---

## 7. 未验证 / 风险

1. **协议身份**（已解决）。描述文件写在 `endpoints/<id>/hot/descriptor.json`，不在 `endpoint.json` 里（SPEC §5.2 的 `hot` 字段必须缺席），所以本实现不再自违约；`CourierNodeOptions.EnableHotChannel`（默认 `true`）可以整体关掉它，关掉后 `endpoint.json` 与合规实现逐字节相同。
2. **跨平台**。当前原型是 Windows 专用（MemoryMappedFile.CreateNew(name) + 命名 EventWaitHandle + OpenProcess）。Linux 对应 shm_open + eventfd/futex + pidfd；macOS 对应 shm_open + os_sync_wait_on_address（14+）+ kqueue EVFILT_PROC。.NET 在 Unix 上 MemoryMappedFile.CreateNew 是否落到 /tmp 未验证，若落 /tmp 则直接违反 SPEC §20.3，必须改 P/Invoke 直调 shm_open。
3. **双实现对拍**（§5.2 门槛第 1 条）。目前只有一份 C# 实现。第二份建议用 Rust 或 C 写一个只读校验器，最小成本满足门槛。**TODO：本仓库无法完成**（CLI 与库共享同一批源文件，不构成第二份实现），因此 §5.2 的门槛第 1 条在第二份实现出现前**不可能满足**，热通道不得写进协议。
4. **多槽并发下的伪共享**。当前每槽 256 字节对齐，但 32 槽的 head/tail 仍在同一批缓存行附近，8 客户端时 p99 抬到 61–91 µs。未做 padding 实验。
5. **宿主单线程泵**。8 客户端已达 64k msg/s 上限。若要更高需多泵或分片。
6. **sessionId 目前 64 位**（Substring(0, 16)），落地改为 128 位。
7. **去重表尚未实现**（第 4 节的设计）。当前原型的热通道是纯 at-most-once，靠「收不到就永久降级」保证不重复执行 —— 语义保守但没有重试能力。
8. **~~账本无限增长~~（已解决）**。账本现在默认关闭，打开时也有界（§6.5）。同类问题也不在热通道这一侧了：`archive/` 的保留清理已随 SPEC §15.3 落地；在线 `deadletter/` 此前在条款下没有合规删除路径，现由 SPEC §12 补上——进入死信时 MUST 同时写 `archive/<date>/<id>/deadletter/` 归档副本，在线那份只在**副本存在且过期**后 MAY 删除，两份各自有界、证据不断。
9. **文件回退的实测延迟**（p50 429 µs）是在同机空载下测的，未计入真实命令的处理时间。
10. **本机噪声**。12 核共享，p99 在多次运行间可从 10 µs 跳到 91 µs，max 稳定出现 1–2 次 5–10 ms 停顿（GC/调度）。尾部数字只能横向比较。

---

## 8. 建议的落地顺序

按「收益 ÷ 风险」排序，**不是按技术先进性排序**：

| 阶段 | 内容 | 风险 | 收益 |
|---|---|---|---|
| **T-1** | CLI 改 NativeAOT 发布 | 极低，零协议改动 | 启动 47 ms → 19 ms（本项目单项收益最大） |
| **T0** | 文件通道 + 目录变更通知 + 自适应退避（通知丢失退回轮询） | 极低，零协议改动 | 轮询延迟 200 ms → ~1 ms（p50） |
| **T0.5** | B 档 stdio 助手（常驻客户端） | 低，不改协议 | 单发从 19 ms 降到 6 µs 级 |
| **T1** | 热池只做请求响应数据面，描述写进实现私有的 `endpoints/<id>/hot/descriptor.json`，缺席则全走文件（**已落地**，含 `CourierNodeOptions.EnableHotChannel` 开关） | 低（原估「中，需改 SPEC §5.2/§20.7/§22.3」，实际靠 §2 实现私有目录条款绕开了协议改动） | 往返 1.6 ms → 6 µs |
| **T2** | 去重表 + 终态证据双写（热通道也写 results/） | 中，语义变更需测试 | 从「能快」变成「敢用」 |
| **T3** | 跨平台（Linux/macOS）+ 第二份实现对拍 | 高 | 满足 §5.2 三条门槛 |

**T-1、T0、T0.5 都不需要协议变更，可以先做。** 三项做完，agent 的感知延迟已经从「60–70 ms 启动 + 0–200 ms 轮询 + 1.6 ms 落盘」降到「6 µs + 一次 19 ms 摊销」。T1 的热池是在这个基础上把「每次调用的通道开销」也钉死，属于锦上添花，不是雪中送炭。

反过来说：**如果跳过 T-1/T0/T0.5 直接上热池，用户几乎感知不到区别** —— 这正是 b5 那一轮把 6 µs 通道拼出来后仍然要回头测启动成本的原因。

---

## 9. 复现

```bash
# 落盘成本
cd tools/durabench && dotnet run -c Release -- atomic
cd tools/durabench && dotnet run -c Release -- append-nosync

# 等待原语
cd tools/hotbench  && dotnet run -c Release -- granularity

# 热池自测
cd tools/hotdemo && dotnet build -c Release
./bin/Release/net10.0/hotdemo.exe selftest

# 热池双进程
./hotdemo.exe host  "$TEMP/hp" 2000000 &
./hotdemo.exe client "$TEMP/hp" 5000

# NativeAOT 发布（约 2–5 分钟）
cd tools/hotdemo && dotnet publish -c Release -r win-x64 \
  -p:PublishAot=true -p:StripSymbols=true -o out/aot

# 多客户端吞吐 / 崩溃检测 / 重启复原 / 单发对比
bash tools/hotdemo/scale.sh        # 1/2/4/8 客户端吞吐
bash tools/hotdemo/robust3.sh      # 宿主被杀 + 同 root 重启 + sessionId 隔离
bash tools/hotdemo/startfloor2.sh  # 进程启动地板（AOT vs JIT vs native）
bash tools/hotdemo/single2.sh      # 单发：文件通道 vs 热通道
```

原型文件：

- tools/hotdemo/HotPool.cs —— 命名节池、CAS 抢槽、环形帧、门铃、存活检测
- tools/hotdemo/Program.cs —— host / host-file / client / echo / probe / selftest / flushcost
- tools/hotdemo/*.sh —— 上述验证脚本
- tools/durabench/ —— 落盘成本（atomic / append / group commit）
- tools/hotbench/ —— 等待原语粒度、共享内存环、管道、目录通知
- tools/wakebench/ —— WakeByAddressSingle 跨进程唤醒可靠性（2 万次 0 丢失）
- tools/startbench/ —— NativeAOT 最小启动探针
