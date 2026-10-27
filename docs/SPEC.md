# Courier 协议规范

**状态**：稳定。
**地位**：本文是**唯一有约束力的文档**。
**目标读者**：要实现第二份 Courier 实现的人。读完本文应当能独立写出可互操作的实现。

---

## 1. 术语与一致性措辞

| 词 | 含义 |
|---|---|
| **root** | 一个 `.courier` 目录的绝对或相对路径。所有状态都在它下面。 |
| **端点（endpoint）** | root 下一个具名参与者，对应 `endpoints/<endpointId>/`。 |
| **服务者（server）** | 某个端点目录的**唯一**写入者：读 `commands/`、写 `results/`、维护 `claims/` 与 `heartbeat`。 |
| **投递方（sender）** | 向某端点 `commands/` 写入命令的任意进程。可以有任意多个。 |
| **调用方（caller）** | 投递方中"要等结果"的那一类。观察方只读不写。 |
| **状态快照（state）** | 端点发布的只读状态，对应 `state/<name>.json`。没有请求，也没有响应。 |
| **会话（session）** | 端点的一次运行，由 `sessionId` 唯一标识。重启即新会话，旧会话的任何结论都不代表新会话。 |

本文使用 RFC 2119 措辞：**MUST / MUST NOT / SHOULD / SHOULD NOT / MAY**。

**核心不变式**（其余条款都可以从这五条推出来）：

1. **一个端点目录只有一个服务者**，但可以有任意多个投递方。
2. **路由在写入时确定**：消息写进哪个目录，就只有该目录的服务者会处理。不存在运行期过滤。
3. **落盘即承诺**：出现在 `commands/` 的每条命令 MUST 最终产出一个终态证据（`results/` 中的响应，或 `deadletter/` 中的记录）。MUST NOT 静默丢弃。
4. **原子写只有一种**：同目录临时文件 → flush/close → rename。MUST NOT 直接写目标文件。
5. **读路径与写路径分离**：请求-响应走 `commands/` → `results/`，状态发布走 `state/`。两者 MUST NOT 互相替代——把状态塞进 `commands/`，每次读就变成一次有副作用的写；把请求塞进 `state/`，它就没有终态证据。

**不变式 1 的强制手段是可选的（§8）。** 「一个端点目录只有一个服务者」只有在实现提供并启用 `host.lock` 时才有内核级的保证；没启用时它是**约定**而不是**保证**——协议本身拦不住第二个服务者打开同一个端点目录，后果是重复执行与互相覆盖。凡是可能出现两个实例的宿主（用户双击两次、编辑器重复加载插件），MUST 启用 §8；生命周期由外部唯一性保证的宿主可以不开，但那个保证来自宿主，不来自本规范。

**投递语义是 at-least-once，不是 exactly-once。** 服务者崩溃、租约过期后被接管（§10.1），都可能让同一条命令被执行两次。需要恰好一次副作用的 handler 必须自己按 `requestId` 去重。协议不提供、也不假装提供 exactly-once。

**三条信道，三种可靠性，不要混用**：

| 信道 | 形态 | 可靠性 | 该用在什么上 |
|---|---|---|---|
| 请求-响应 | `commands/` → `results/` | 至少一次，有终态证据 | 有副作用、或需要回执的动作 |
| 状态发布 | `state/<name>.json` | 最新值，无回执 | 让外界看到"现在是什么样" |
| 快通道 | **不在协议内**（§5.2） | — | — |

选错信道的代价是结构性的：用状态发布传递"请执行"，会丢掉失败与重试；用请求-响应承载每秒 60 次的状态刷新，会在磁盘上写出每秒 60 个文件，还会让 `results/` 无限增长。

---

## 2. 信道布局

```
<root>/
├── endpoint.json                  # 可选：root 级端点清单，见 §7
├── endpoints/
│   └── <endpointId>/
│       ├── endpoint.json          # 能力目录，首次 Tick 前写完即冻结，退出不删
│       ├── heartbeat              # 单个 UTC 时间戳，每 tick 覆盖写
│       ├── host.lock              # 可选：单实例互斥，见 §8
│       ├── commands/              # 投递方写
│       │   └── <requestId>.json
│       ├── claims/                # 服务者写（独占创建）
│       │   └── <requestId>.json
│       ├── results/               # 服务者写
│       │   ├── <requestId>.json
│       │   └── gc.json            # 可选：GC 墓碑
│       ├── deadletter/            # 服务者写
│       │   └── <requestId>.json
│       ├── cancels/               # 投递方写（可选能力）
│       │   └── <requestId>.json
│       ├── progress/              # 服务者写（可选能力）
│       │   └── <requestId>.json
│       └── state/                 # 服务者写（可选能力，§14）
│           └── <name>.json
└── archive/
    └── <yyyy-MM-dd>/
        └── <endpointId>/
            ├── commands/<requestId>.json
            └── deadletter/<requestId>.json
```

**目录所有权（MUST）**：

| 目录 / 文件 | 唯一写入者 | 允许读者 |
|---|---|---|
| `endpoints/<id>/endpoint.json` | 该端点服务者（首次 Tick 前，之后冻结） | 所有人 |
| `endpoints/<id>/heartbeat` | 该端点服务者 | 所有人 |
| `endpoints/<id>/commands/` | **投递方**（任意多个） | 该端点服务者 |
| `endpoints/<id>/claims/` | 该端点服务者 | 该端点服务者、观察工具（只读） |
| `endpoints/<id>/results/` | 该端点服务者 | 所有人 |
| `endpoints/<id>/deadletter/` | 该端点服务者 | 所有人 |
| `endpoints/<id>/cancels/` | **投递方** | 该端点服务者 |
| `endpoints/<id>/progress/` | 该端点服务者 | 所有人 |
| `endpoints/<id>/state/` | 该端点服务者 | 所有人 |
| `endpoints/<id>/host.lock` | 该端点服务者 | 所有人（只读探测） |
| `<root>/endpoint.json` | 该 root 的**目录服务者**（§6） | 所有人 |
| `archive/` | 服务者（搬自己的）、显式授权的 GC 进程 | 所有人 |

**MUST NOT**：投递方写入 `results/`、`claims/`、`deadletter/`、`progress/`、`heartbeat`、`endpoint.json`。
**MUST NOT**：任何一方删除 `endpoints/<id>/` 目录本身（端点退出只让 heartbeat 停更）。
**MUST NOT**：任何第三方搬动或删除 `results/`（见 §16）。
**MUST NOT**：投递方写 `state/`。它是服务者单方面发布的视图，不是收件箱。
**MUST NOT**：用 `state/` 承载有副作用或有回执需求的调用（§1）。

**实现私有文件与目录。** 实现 MAY 在布局里新增本规范未定义的**实现私有**文件或子目录（本仓库的例子：`<root>/registry.lock`、`endpoints/<id>/hot/descriptor.json`、`endpoints/<id>/hot/hot-<sessionId>.log`）。它们：

- 不属于协议，MUST NOT 承载任何跨实现可观察的语义——**删掉它们不会改变任何一条协议行为**；
- 读方 MUST 忽略它们，MUST NOT 因为多出这类文件或目录而判定端点不可用（§3.1 的同一原则）；
- 其名称、格式、保留策略全部由实现自行决定，本规范不做约束，其它实现也不要求用同名文件。

实现私有的东西与协议布局的分界线是：**换个实现读它，读不出协议语义，就是实现私有。**

实现 SHOULD 在启动时创建缺失的目录，并容忍已存在。

---

## 3. 通用编码规则

### 3.1 JSON

- 编码 **UTF-8，不带 BOM**。
- 单行紧凑 JSON（无缩进、无换行）。便于 `cat`、`grep`、逐行处理。
- 字段名区分大小写。
- **未知字段 MUST 被忽略**，MUST NOT 因未知字段而拒收。这是前向兼容的唯一手段。
- 解析失败 MUST 产出 `rejected/invalid-command` 或进 `deadletter/`，MUST NOT 抛异常到宿主。

### 3.2 时间

- 全部是 UTC，RFC 3339 格式，以 `Z` 结尾，最多 7 位小数秒。
- 例：`2026-10-19T13:52:43.5120000Z`。
- 比较用 UTC 时刻，MUST NOT 用本地时间。
- 协议假定两端系统时钟偏差小于 `deadlineUtc` 的典型值（§19）。跨机器且无 NTP 时行为未定义。

### 3.3 原子写（MUST）

写入任何目标文件 `X` 的步骤：

1. 在同目录创建临时文件 `.X.<8位随机hex>.tmp`。
2. 写入完整内容。
3. `Flush()` 并 `Close()`。
4. rename 到 `X`。

约束：

- 临时文件 MUST 与目标**同目录同卷**，否则 rename 退化为拷贝+删除，不再原子。
- `Flush(true)`（fsync）**MAY** 省略。省略只影响"断电后是否丢最近消息"，不影响"其他进程永远看不到半写文件"。进程崩溃时操作系统仍会完成已 close 的写入。
- 命令文件与 claim 文件 MUST 以**不覆盖**语义创建（`FileMode.CreateNew` / `O_EXCL`）；撞名说明 requestId 重复，MUST 报错而不是覆盖。
- `endpoint.json`、`heartbeat`、`results/*.json`、`progress/*.json` MUST 允许覆盖（整体替换）。
- **MUST NOT 用"直接写目标文件"代替上述流程**（`File.WriteAllText` 到目标路径）。这会让读者看到空文件或半截 JSON，且只在负载下偶发。

### 3.4 读取重试（MUST）

读取任何 JSON 文件时 MUST 对 `IOException` / `UnauthorizedAccessException` 做有界重试（建议 200 次、总计不超过 1 秒）。

原因：Windows 上杀毒软件与索引器会在 rename 后短暂持有句柄，导致刚 rename 完成的文件瞬间读不到。**这不是异常情况，是常态**，不重试的实现会在真实机器上偶发失败。

### 3.5 枚举顺序

`commands/` 的枚举顺序 MUST 按**文件名升序**。因为 `requestId` 以定长 UTC 时间戳开头，字典序即时间序。

### 3.6 路径与重解析点（MUST）

实现拼出任何一个落在 root 之下的路径时，MUST 满足：

1. **拼接后再校验，不是拼接前。** 用 `Path.GetFullPath` 得到绝对路径，再 `Path.GetRelativePath(root, full)` 判定：结果若是绝对路径、或等于 `..`、或以 `..` + 目录分隔符开头，即判定逃逸，MUST 拒绝。
2. **比较前缀时必须带结尾分隔符。** 不带分隔符时 `/root/app` 会被 `/root/app-evil` 骗过。
3. **Windows 上大小写不敏感，其余平台敏感。**
4. **MUST 拒绝穿过重解析点（符号链接、junction、mount point）的路径。** 对 root 及其下每一段已存在的路径组件检查 `FileAttributes.ReparsePoint`，命中即拒绝；不存在的组件视为通过（它还没被创建，无从穿越）。
5. 上述检查 MUST 在**每次**落盘前做，MUST NOT 只在启动时做一次——目录可以在运行期间被替换成链接。

为什么值得这么麻烦：`endpointId`、`channel`、`action`、`state` 的名字最终都会变成路径片段。只靠正则挡不住一个已经被别人做成链接的中间目录——那不是输入的问题，是环境的问题，但后果一样：写到 root 外面去。

### 3.7 读与写的责任边界

- 协议的所有读取（`endpoint.json`、`heartbeat`、`results/`、`state/`、`claims/`）MUST 容忍**文件不存在**，把它当作"还没有"，MUST NOT 当作错误。
- 协议的所有写入 MUST 是**幂等**的：同一个目标写两次，结果必须与写一次相同。这条约束让崩溃恢复变成"再跑一遍"，而不是"猜上次做到哪"。
- 读取方 MUST NOT 因为一个字段类型不对就整体拒绝：类型不对按缺失处理（§3.1），除非该字段是 MUST 且缺失会让消息无法路由。

---

## 4. 标识符

### 4.1 endpointId

- 正则：`^[a-z0-9]([a-z0-9-]{0,62}[a-z0-9])?$`
- 长度 1–64，全小写，只允许 `a-z`、`0-9`、`-`，且不以 `-` 开头或结尾。
- 服务者启动时 MUST 校验；不合法 MUST 直接拒绝启动（不写任何文件）。
- **这是唯一的路径拼接入口**。不校验等于允许 `../` 打穿 root。

### 4.2 requestId

- 正则：`^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8,32}$`
- 格式：`{yyyyMMdd}T{HHmmss}Z-{8–32位小写hex}`，例：`20261019T135243Z-7f3a2b1c`。
- 随机段 MUST 存在，长度 8–32。用自增计数器代替随机段的实现**不满足本规范**：两个进程在同一秒内投递会撞名。
- 为什么允许更长的随机段：25 字符的定长 id 在一个高频投递的端点上每秒只有 4 G 个名字可用，这够用，但把它写死会让"想用 ULID/UUID 前缀"的调用方被迫改造。协议只约束**可解析性**与**时间序**，不约束随机段的具体宽度。
- 时间戳部分 SHOULD 取投递时刻；服务者 MUST NOT 依赖它与 `issuedAtUtc` 一致。

### 4.3 channel / action / state 名

- 正则（三者相同）：`^[A-Za-z0-9_.-]{1,64}$`
- 注册与调用时 MUST 完全一致（区分大小写）。不做大小写折叠。
- 名字**区分大小写但 MUST 只按序数比较**，MUST NOT 做区域性比较：`document` 与 `Document` 是两个不同的 channel，`ß` 与 `SS` 无关。
- 名字是路径片段，MUST 满足 §3.6；正则只是第一道闸。
- 允许 `.` 是为了 `kit.action` 这类命名习惯，但 `..`、以及以 `.` 开头或结尾的名字 MUST 被拒绝——它们在路径语义上有特殊含义。

### 4.4 protocolVersion

- 整数。当前为 `2`。
- **唯一的版本字段**。命令与响应里 MUST NOT 重复携带版本。
- 投递方 MUST 在投递前读取目标 `endpoint.json` 的 `protocolVersion` 并与自身实现版本比对：不同则**本地直接失败，不写任何文件**。
- 版本不匹配 MUST 在使用之前发现，而不是在解析失败之后。

---

## 5. `endpoint.json`

启动阶段写完即**冻结**，端点退出时**不删**。

```json
{"endpointId":"app-a","description":"桌面排版工具","sessionId":"7f3a2b1c","processId":41232,"startedAtUtc":"2026-10-19T13:50:00.0000000Z","protocolVersion":2,"actions":[{"channel":"document","action":"open","summary":"打开文档"}],"states":[{"name":"document","summary":"当前文档"}]}
```

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `endpointId` | string | MUST | 必须等于目录名 |
| `description` | string | SHOULD | 人类可读 |
| `sessionId` | string | MUST | 本次运行的唯一标识，每次启动换新。见下方说明 |
| `processId` | integer | MAY | **不得用于判活** |
| `startedAtUtc` | string | MAY | 上一次运行的值，**不得用于判活** |
| `protocolVersion` | integer | MUST | 见 §4.4 |
| `actions` | array | MUST | 可为空数组 |
| `states` | array | MAY | 状态声明，见 §14 |
| `capabilities` | array of string | MAY | **可选能力自述**，见 §5.3 |

`sessionId` 是 8–32 位小写 hex，每次启动 MUST 换新。它的作用是让读方分辨「这是同一个端点的新一轮运行」还是「还是原来那个」——`processId` 会被 OS 复用，`startedAtUtc` 可能被复制，只有 `sessionId` 是这个实例独有的。读到 `sessionId` 变了，就 MUST 丢弃此前基于旧会话做的任何判断（例如"这个端点的某个命令一定还在跑"）。

**MUST 整体原子替换，MUST NOT 增量追加。** 先构造本次运行的**完整** actions 列表再覆盖整个文件。增量追加会让上一版注册过、这一版已删除的能力永远留在文件里，形成一个永远返回 `rejected/unknown-action` 的幽灵能力。

**能力表在第一次 `Tick` 之前写完，此后冻结。** 逐条注册的实现每注册一条就整体重写一次文件，这没问题——只要全部发生在第一次 `Tick` 之前。第一次 `Tick` 之后 MUST NOT 再改 `endpoint.json`：实现 MUST 在此时拒绝新的能力注册并抛错，MUST NOT 静默改写。

理由：`endpoint.json` 是投递方与观察方唯一的能力来源。运行期改它，等于让"这个端点能做什么"变成一个随时间变化的目标——投递方刚读到的能力，可能在下一次读时就不存在了。冻结把能力表变回一个可缓存的常量。

### 5.1 actions 条目

```json
{"channel":"document","action":"open","summary":"打开文档"}
```

同一 `(channel, action)` 在一个 `endpoint.json` 内 MUST 唯一。重复注册是编程错误，服务者 MUST 在注册时直接报错。

**可选 `metadata`。** action 条目、state 条目、以及 `endpoint.json` 顶层都可以带一个 `metadata` 对象：

```json
{"channel":"document","action":"export","summary":"导出 PDF",
 "metadata":{"risk":"write","version":"1.2.0","inputSchema":{"path":"string"},"outputSchema":{"uri":"string"}}}
```

- `metadata` 的类型是 **JSON 对象**，内容是**自由 JSON**，协议不解释其中任何字段。实现可以写 `risk`、`inputSchema`、`outputSchema`、`version`、`deprecated` 等任意键。
- `metadata` 是 **MAY**：没有就 MUST **省略整个字段**，MUST NOT 写 `null`。读到 `null` 或非对象类型的实现 MUST 按"没有 metadata"处理（§3.7），而不是让整条能力变成不可读。
- `metadata` 只是**描述**，不参与路由、不影响风险等级判定、不改变原因码、不改变终态语义。投递方 MUST NOT 因为 `metadata` 缺失就拒绝投递。
- `metadata` 与能力表一起**冻结**（见上文 §5 的冻结规则）：第一次 `Tick` 之后 MUST NOT 再改。
- 读到不认识的 `metadata` 键必须忽略（§3.1）：它是留给宿主与上层工具协商的，不是协议契约。

**未知字段一律忽略。** `endpoint.json` 的顶层、`actions` 条目、`states` 条目都可能出现本规范未定义的字段（`metadata` 之外还有将来的扩展）。读方 MUST 忽略它们，MUST NOT 因为多出一个字段就判定整份能力目录不可用。

### 5.2 快通道：不声明

早期版本里 `hot` 字段声明了 named pipe / shared memory 快通道。**本规范删掉它。**

理由不是「不需要」，而是**声明的快通道无法被验证**：协议里写一条 `hot`，实现完全可以忽略它、只走 `commands/`，而且不违反任何一条 MUST。一份「声明了但可能没人实现」的规范，比没有这条规范更糟——它让互操作性从「可验证」退化成「看运气」。

本规范的做法是把快通道从**协议层**挪到**实现层的性能优化**：一个实现当然可以在同一台机器上把 `commands/` 落盘这一步用内存映射加速，只要它对所有观察方仍然表现为符合本规范的 `commands/` → `results/`。这不是快通道，这是快速的文件通道。

这条路线对本仓库是**已经落地的事实**，可以当作「怎样才算没违反本节」的参考实现：描述（池名、槽数、容量）写在实现私有的 `endpoints/<id>/hot/descriptor.json`（§2），不写进 `endpoint.json`；旁路只负责叫醒，不负责记账——每条命令仍然完整走 `commands/` 落盘，每个终态仍然写 `results/<requestId>.json`，去重仍然靠那个文件。**把这条旁路整个删掉，协议行为一个字都不变**，这就是它与 §5.2 所禁的东西的分界。

重新把快通道纳入协议的前提（三条全部满足才考虑）：

1. 至少有两份**独立实现**都真的实现了它，可以互相对拍。
2. 有一条**可观测的**一致性测试能证明「走快通道」与「走文件通道」产生相同的终态证据。
3. 协议里能写出一条**可被拒绝**的条款——即「没实现快通道」能被测出来是违规，而不是靠自觉。

在那之前，`endpoint.json` 的 `hot` 字段 **MUST NOT 出现**。读到它的实现 MUST 忽略它（向前兼容：未来版本可能重新引入）。

> **本条与本仓库实现的关系（已修正）**：热通道原型曾把 `hot` 块写进 `endpoint.json`，违反本条；现在它只把描述写到实现私有目录 `endpoints/<id>/hot/descriptor.json`（§2），`endpoint.json` 里**一个字都不写**。所以本仓库当前**满足**本条，§22 第 3 条（协议层快通道）同样不受影响——那仍然是实现层的性能旁路。上面三条门槛与本节其余文字保持原样：**未来若想把它写回 `endpoint.json`，仍然一条都不能少**。跟踪项见 `HOT-CHANNEL.md` §5.1、§7。

### 5.3 能力自述 `capabilities`（可选）

`endpoint.json` 的顶层 MAY 带一个 `capabilities` 数组，声明**本实现真的做了哪些可选能力**：

```json
{"endpointId":"app-a","description":"桌面排版工具","sessionId":"7f3a2b1c","protocolVersion":2,
 "actions":[…],"states":[…],"capabilities":["cancels","state"]}
```

- 元素类型是 **string**，取值是 §13、§14、§7、§8 定义的**可选能力名**：`state`、`cancels`、`progress`、`registry`、`host-lock`。
- **MAY**：没有就 MUST **省略整个字段**，MUST NOT 写 `null`、MUST NOT 写空数组代替——空数组的含义是「故意声明了零个能力」，与「不谈这个」是两回事。
- 声明是**自述**，不是协商：读方 MUST NOT 因为对方没声明某个能力就拒绝投递，也 MUST NOT 因为对方声明了就必须使用它。
- 读方读到**不认识**的能力名 MUST 忽略（§3.1），MUST NOT 判定整份能力目录不可用。
- 与能力表一起**冻结**（§5）：第一次 `Tick` 之后 MUST NOT 再改。
- 数组 SHOULD 按名字序数升序输出，便于 diff。

它解决的是 §21 那条「读方在对方没实现可选能力时行为正确退化」无法判定的问题：没有这份自述，读方无从区分「对端没实现 `cancels`」与「对端实现了但这次没用上」。这也是 §5.2 要求的**可被拒绝的条款**的雏形——声明与实际不符是可以被测出来的。

---

## 6. `heartbeat`

- 内容是**单个 UTC 时间戳字符串**，无 JSON、无换行亦可。
- 每次 `Tick` MUST 覆盖写一次（原子写，§3.3）。
- 判活阈值（可配置，默认值）：

| 状态 | 条件 |
|---|---|
| `alive` | age ≤ 5 s |
| `stale` | 5 s < age ≤ 15 s |
| `dead` | age > 15 s |

- 心跳 MUST NOT 塞进 `endpoint.json`：那会让每次心跳重写能力目录，既浪费又破坏"写一次"的语义。
- 心跳 MUST 用覆盖写，MUST NOT 追加，避免无限增长。

**心跳的含义是"服务循环还活着"，不是"某个 handler 正在跑"。** handler 在服务者内部执行（§16.1），执行期间服务循环 MUST 继续按宿主节奏 `Tick`，心跳照常更新。一个跑了 20 秒的 handler MUST NOT 让端点显示成 `dead`——观察方看到 `dead` 就应当认为这个端点再也处理不了任何命令，而不是"它很忙"。

换句话说：**心跳不能由 handler 的进度决定。** 如果心跳只在"一轮 Tick 从头跑到尾"之后才更新，那么任何慢 handler 都会把端点自己判死，而杀死它的正是它自己。

---

## 7. 注册表 `endpoint.json`（root 级，可选）

路径：`<root>/endpoint.json`。与端点自己的 `<root>/endpoints/<id>/endpoint.json` 同名但**不同文件、不同含义**。

这一份是 **root 级的端点清单**：让没有目录列举能力的观察方一次读到「这个 root 下现在有哪些端点」。

```json
{"protocolVersion":2,"generatedAtUtc":"2026-10-19T13:52:53.9000000Z","endpoints":[
  {"endpointId":"app-a","description":"主编辑器","sessionId":"7f3a2b1c","state":"alive"},
  {"endpointId":"app-b","description":"","sessionId":"9c1d4e5f","state":"dead"}
]}
```

- 由**目录服务者**维护：MUST 整体原子替换（§3.3），MUST NOT 增量追加。
- 条目 MUST 按 `endpointId` 序数升序。
- `state` 是快照值，允许过期；读方 MUST NOT 把它当作权威，需要权威时读端点自己的 `heartbeat`（§6）。
- 该文件**可选**。没有它，观察方 MUST 退化为列举 `endpoints/` 目录——这是本规范的首选路径，注册表只是为了照顾不能列目录的宿主。
- 注册表 MUST NOT 被当作「端点存活」的唯一依据：一个端点可能刚启动还没被登记，也可能刚崩溃还没被移除。**权威永远是端点自己写的 `heartbeat`。**

**多写者协调是实现细节，不是协议要求。** 多个端点会同时写同一份注册表，"读自己的旧内容再补一条"会让一个端点的条目覆盖掉另一个的。实现 SHOULD 用「短持有锁 + 重读-合并-整体原子替换」：

- 锁文件（本仓库的实现用 `<root>/registry.lock`）只是**实现私有的协调手段**。它不属于协议，读方 MUST NOT 依赖它存在，其它语言实现也不要求用同名文件。
- 持锁时间 MUST 只覆盖"重读—合并—替换"这一小段，MUST NOT 横跨任何 I/O 等待。抢不到锁 MUST NOT 阻塞服务循环：放弃本轮，下一次刷新再来。
- 合并不以"读不到"为由丢掉别人的条目：注册表缺失或损坏时从 `endpoints/` 目录重建，而不是写出一份只有自己的文件。
- 刷新时机由实现决定，SHOULD 限频（例如 ≥ 1 s 一次），打开端点时登记、退出时摘除。

为什么降级为可选：FileBridge 的 `engine.json` 是必需的，因为它的宿主（Unity 编辑器）里观察方常常只有一个路径、不能列目录。Courier 不假设宿主有这个限制，所以把它做成可选的加速手段，而不是协议的地基。

---

## 8. `host.lock`（单实例互斥，可选）

路径：`endpoints/<endpointId>/host.lock`。

一个端点目录只能有一个服务者（§1 不变式 1）。如果宿主可能被启动两次（用户双击两次、编辑器重复加载插件），需要一把 OS 级的锁来拒绝第二个实例。

- 服务者启动时 MUST 以 `FileMode.OpenOrCreate` + `FileShare.None` 打开 `host.lock` 并**持有到退出**。
- 拿不到锁时 MUST 判定为「该端点已被另一个实例服务」，MUST NOT 继续初始化目录。
- 只有 OS 明确报告的**共享冲突**才算「已被占用」：Windows 上 `HResult & 0xFFFF` 等于 `32`（`ERROR_SHARING_VIOLATION`）或 `33`（`ERROR_LOCK_VIOLATION`）。磁盘故障、权限不足、路径不存在 MUST NOT 被伪装成「已有实例」——那会让一个坏掉的磁盘看起来像「程序已经在跑了」，用户永远等不到它启动。
- 文件内容无意义，可写任意字节（包括空）。锁的语义完全来自 OS 的共享模式，不来自文件内容。
- 该文件**可选**。没有它，协议不提供任何单实例保证，两个服务者会互相踩。
- 抢锁 MUST 发生在铺开其余子目录**之前**：失败时 MUST 不留下一套半初始化的目录。锁文件本身所在的 `endpoints/<endpointId>/` 必须先存在，所以顺序是"建 `endpoints/<id>/` → 抢锁 → 建其余子目录"。
- 句柄 MUST 持有到退出。正常路径由 `Dispose` 释放，进程意外终止由 OS 自动释放；`Dispose` MUST 可重复调用。

**与 §1 不变式 1 的关系。** 不变式 1 是核心不变式，但本规范把它的**强制手段**降级成了可选：不开本节就没有单实例保证。这是有意的取舍（有些宿主的生命周期由外部唯一性保证），代价必须被认识到——不是「协议保证单实例」，而是「协议给出保证单实例的手段，用不用是宿主的选择」。

为什么用 OS 锁而不是 PID 文件：PID 文件需要判断「那个 PID 还活着吗」，而这个判断在 PID 复用、容器、跨用户场景下都不可靠。OS 文件锁由内核维护，进程死亡时自动释放——这正是我们需要的语义，且不需要任何心跳或超时来猜。

---

## 9. `command`

路径：`endpoints/<targetId>/commands/<requestId>.json`。文件名即 `requestId`。

```json
{"requestId":"20261019T135243Z-7f3a2b1c","channel":"document","action":"open","payload":"{\"path\":\"Main.doc\"}","issuedAtUtc":"2026-10-19T13:52:43.5120000Z","deadlineUtc":"2026-10-19T13:52:53.5120000Z","issuedBy":"app-b"}
```

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `requestId` | string | MUST | MUST 等于文件名（去掉 `.json`），否则 `rejected/invalid-command` |
| `channel` | string | MUST | §4.3 |
| `action` | string | MUST | §4.3 |
| `payload` | string | MUST | **不透明字符串**。Courier 从不解析。可为空串 |
| `issuedAtUtc` | string | MUST | 投递时刻 |
| `deadlineUtc` | string | MUST | 见 §9.1 |
| `issuedBy` | string | SHOULD | 投递方端点 id。**不参与路由**，只用于审计与回信。**不是凭据** |

### 9.1 deadlineUtc

- 服务者取走命令时若 `now > deadlineUtc`，MUST NOT 执行，直接写 `rejected/stale` 并归档。
- 目的：防止"软件关了两小时后开机，执行一堆陈旧命令"。
- 投递方 MUST 用 `issuedAtUtc + deadline` 生成该字段，其中 `deadline` 默认 `DEFAULT_DEADLINE`（10 s，§19），可由调用方按次覆盖（§17.2），但 MUST NOT 超过 `MAX_DEADLINE`（默认 300 s）。超过时按 `MAX_DEADLINE` 截断，MUST NOT 报错。
- 为什么要有上限：`deadlineUtc` 是"这条命令最晚可以开始执行"的时刻，不是"最晚必须完成"。一个 300 s 的 deadline 意味着命令可以在队列里躺五分钟才被处理。没有上限时，一次误传的极大值就能让一条命令在磁盘上存活到下一次重启。

### 9.2 大小上限

- 命令文件总字节数 MUST ≤ `MAX_COMMAND_FILE_BYTES`（默认 1 MiB + 4 KiB）。
- 服务者 MUST **先看文件长度再决定是否读入内存**。超限直接 `deadletter/`，MUST NOT 把内容读进内存。
- 没有这条上限时，一条巨大命令会在任何校验发生之前耗尽服务者内存。

### 9.3 投递方的前置检查（MUST）

写入前，投递方 MUST 依次检查：

1. 目标 `endpoints/<targetId>/endpoint.json` 存在且 `protocolVersion` 匹配（§4.4）；否则本地失败，**不写文件**。
2. `commands/` 中 `.json` 文件数 < `MAX_COMMANDS_BACKPRESSURE`（默认 4096）；否则本地返回 `rejected/backpressure`，**不写文件**。
3. `payload` 序列化后的长度 ≤ `MAX_COMMAND_FILE_BYTES`；否则本地失败，**不写文件**。

`rejected/backpressure` 由投递方**合成**，不落盘——它不是任何一方的答复。

---

## 10. `claims/`

路径：`endpoints/<id>/claims/<requestId>.json`。

```json
{"owner":"app-a","expiresAtUtc":"2026-10-19T13:52:53.9000000Z","attempts":1}
```

- MUST 以**不覆盖**语义创建（`FileMode.CreateNew`）。创建失败（已存在）说明别的服务循环已在处理，本次跳过。
- `attempts` 从 1 开始，每次重新认领 +1。
- **重试计数 MUST 写在这里，MUST NOT 写进命令文件。** 命令是投递方的产物，服务者改它等于篡改来信。
- 处理结束后 MUST 删除 claim。

### 10.1 claim 是一个租约

claim 不是"我领了这条命令"的记号，而是一个**有到期时刻的租约**：`expiresAtUtc` 之前，别的服务循环看到它就必须退让；`expiresAtUtc` 之后，它自动失效。

- 认领时 MUST 写 `expiresAtUtc = now + CLAIM_WINDOW`（默认 `CLAIM_WINDOW = 10 s`，§19）。
- 认领方 MUST 在 handler 执行期间**续约**：每 `CLAIM_RENEW_INTERVAL`（默认 2 s）把 `expiresAtUtc` 推到 `now + CLAIM_WINDOW`，原子覆盖。
- 续约 MUST 只在租约仍然属于自己时进行：重读 claim，若 `owner` 不是自己或文件已消失，说明租约已被别人接管，MUST **立即放弃执行**，MUST NOT 写 `results/`。
- 租约过期且没有对应 result，视为持有者已死，别的服务循环可以接管并 `attempts + 1`。
- 若 `attempts` 超过 `MAX_ATTEMPTS`（默认 3），接管方 MUST 转 `deadletter/`，MUST NOT 再执行。
- **claim 文件存在但解析不出来时，实现 MUST 按"可以接管"处理**（`attempts + 1`，覆盖写），MUST NOT 把它当成"别人正在处理"而跳过。理由：一份读不懂的 claim 谁也证明不了有效，而它一旦挡住接管，这条命令就会被一张坏文件永久卡死在 `commands/`——没有任何超时能解开它。

这样定义之后，`CLAIM_WINDOW` 就不再需要"大于最长 handler 执行时间"——慢 handler 靠续约维持租约，不靠一个拍脑袋的大窗口。原先那种写法在数学上无法与 `DEFAULT_DEADLINE` 同时成立（窗口要大于 handler、又要小于 10 s 的 deadline），是自相矛盾的。

**租约过期被接管，同一条命令就可能被执行两次。** 这是 at-least-once 的代价（§1），不是可以靠调参数消除的缺陷：持有者可能只是在续约前恰好被操作系统挂起了。协议选择把这条窗口写明白，而不是假装它不存在。需要恰好一次的 handler MUST 自己按 `requestId` 去重。

---

## 11. `response`

路径：`endpoints/<id>/results/<requestId>.json`。

```json
{"requestId":"20261019T135243Z-7f3a2b1c","status":"ok","payload":"{\"openedAtUtc\":\"2026-10-19T13:52:43.9000000Z\"}","completedAtUtc":"2026-10-19T13:52:43.9020000Z","reason":null}
```

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `requestId` | string | MUST | 等于文件名 |
| `status` | string | MUST | `ok` / `rejected` / `failed` |
| `payload` | string | MAY | 成功时的结果；不透明字符串 |
| `completedAtUtc` | string | MUST | |
| `reason` | string | 条件必填 | `status != ok` 时 MUST 出现，见 §18 |

### 11.1 三种落盘状态

| 值 | 含义 | 调用方应做什么 |
|---|---|---|
| `ok` | 执行成功 | 读 `payload` |
| `rejected` | 请求本身有问题，重试无意义 | 修正请求，**不要原样重试** |
| `failed` | 请求合法，执行过程出错 | 可重试，或转人工 |

**`rejected` 与 `failed` MUST 分开。** 混成一个值，调用方无法决定是改参数还是重试。

### 11.2 调用方合成的两种结果（不落盘）

| 值 | 何时 | 说明 |
|---|---|---|
| `unreachable` | 等到超时仍无 response | 对端未启动、已崩溃或从未处理。**没有文件可读**，因为它不是任何一方的答复 |
| `rejected/backpressure` | 投递前 `commands/` 超阈值 | 见 §9.3 |

---

## 12. `deadletter/`

路径：`endpoints/<id>/deadletter/<requestId>.json`。

```json
{"requestId":"20261019T135243Z-7f3a2b1c","reason":"invalid-command","detail":"payload field missing","failedAtUtc":"2026-10-19T13:52:44.0000000Z","attempts":3}
```

进 `deadletter/` 的情形（MUST 全部覆盖）：

1. 命令文件超过大小上限（§9.2）。
2. 命令 JSON 解析失败、必填字段缺失、`requestId` 与文件名不一致。
3. `attempts` 超过 `MAX_ATTEMPTS`（默认 3）。

进入 deadletter 后 MUST 同时归档原命令文件（§15），MUST 删除对应 claim。

**deadletter 是终态证据。** 有它的命令不算静默丢弃，这是 §1 不变式 3 的兜底。

**保留（MUST 有界）。** 死信不允许无限堆积，但删它必须**先留一份归档副本**，否则就是静默丢弃：

- 进入 deadletter 时 MUST 同时在 `archive/<yyyy-MM-dd>/<endpointId>/deadletter/<requestId>.json` 写一份内容相同的副本（§15.1）。**这一条从可选升为 MUST**：在线死信可被清理之后，它是证据链的另一半。副本用原子写（§3.3）而不是 rename——在线那份必须留着。
- 在线 `endpoints/<id>/deadletter/<requestId>.json` 只有在下列条件**全部**成立时才 MAY 删除：
  1. `failedAtUtc` 早于保留期；
  2. 归档副本确实存在；
  3. 保留期不是「不清理」。
- 归档副本跟着 §15.3 的日期目录保留策略一起过期。两份副本各自有界，任意时刻证据至少存在一份。
- 实现 MUST 让保留期可配、且 MUST 允许配成「不清理」（此时上限责任回到宿主），与 §15.3 的边界一致。
- 清理 MUST NOT 阻塞服务循环：SHOULD 限频执行，单轮工作量 MUST 有界（例如一轮只检查固定条数），单轮失败 MUST NOT 影响本轮其余步骤。
- 读不到文件内容、解析不出 `failedAtUtc`、或名字不是合法 `requestId` 的记录 MUST 原样保留——删一条读不懂的证据比留着一堆更糟。

---

## 13. `cancels/` 与 `progress/`（可选能力）

### 13.1 cancels

路径：`endpoints/<id>/cancels/<requestId>.json`，由**投递方**写。

```json
{"requestId":"20261019T135243Z-7f3a2b1c","requestedAtUtc":"2026-10-19T13:52:45.0000000Z","reason":"user-aborted"}
```

- 服务者 MUST 在**每次** `Tick` 检查 `cancels/`，对**正在执行**的 requestId 触发 `CancellationToken`。
- 命令尚未开始执行时收到 cancel：MUST 直接写 `rejected/cancelled-before-start` 并归档，不执行。
- handler **MAY 忽略取消**。此时仍按原逻辑执行完，响应 MUST 携带 `"cancelRequested":true`，让调用方知道取消来得太晚。
- 响应仍只有 `ok`/`rejected`/`failed` 三种，MUST NOT 引入第四种终态。
- 取消是**尽力而为**，不是保证。协议不承诺"发出 cancel 后 handler 一定停止"。
- 投递方发出取消的方式就是**原子写这一个文件**（§3.3，允许覆盖：同一个 requestId 重复请求取消是幂等的）。协议不定义"取消 API"，只定义这个文件的形状——**写进去了就等于发出了取消**，任何实现都可以用一个普通文件写完成这件事。
- 取消是**意图**而不是证据，因此省略 `Flush(true)` 是合规的（§3.3）：断电丢掉它最多让一条命令跑完，不会让任何观察方误判终态。这也意味着取消 MUST NOT 被当作"命令已停止"的凭据——唯一的凭据仍是 `results/<requestId>.json`（§11）。
- 字段容错：`requestId` MUST 与文件名（去掉 `.json`）一致——**文件名就是这条取消的键**，与 `commands/` 的约定相同（§9）。服务者 MAY 只按文件名判定而不解析文件内容（那是最省的读法，也是本仓库的做法）；若它解析了内容并发现不一致，MUST 忽略该文件而不是按内容生效。
- `requestedAtUtc` 与 `reason` 都只用于排查：缺失或不可解析时服务者 MUST 仍然按"文件存在"认这条取消，MUST NOT 因此忽略它。`reason` 是自由文本，读方 MUST NOT 依赖它的具体取值。

**这条能力成立的前提是 §16.1：`Tick` 不被 handler 阻塞。** 如果 `Tick` 在 handler 返回之前不返回，那么"每个 Tick 检查 cancels/"就退化成"handler 跑完之前永远不检查"——取消文件只能等 handler 自己结束才被发现，等于没有取消。实现 MUST 让 handler 与 `Tick` 并发推进，并且 MUST 把 `CancellationToken` 传进 handler。

handler 收到取消后有两种合规做法：抛 `OperationCanceledException`（→ `failed/cancelled`），或者自行收尾返回 `ok`（→ `ok` + `cancelRequested:true`）。两种都合法，区别只是"取消来得及"还是"来不及"。

### 13.2 progress

路径：`endpoints/<id>/progress/<requestId>.json`，由服务者**覆盖写**。

```json
{"requestId":"20261019T135243Z-7f3a2b1c","percent":42,"message":"导出第 42/100 页","updatedAtUtc":"2026-10-19T13:52:47.0000000Z"}
```

- **只保留最近一次快照，不做流式、不做历史。** 读者看到的永远是最新值，中间的进度可能被跳过。
- 命令终态产生后 MUST 删除对应 progress 文件。
- 不写 progress 的端点 MUST 被调用方正确当作"没有进度信息"，而不是"卡住了"。
- `percent` 是 0..100 的整数；`message` 是给人看的自由文本，MAY 省略。读到不认识或类型不对的字段 MUST 忽略（§3.1），MUST NOT 因此判整份快照不可用。
- 进度是**尽力而为**的快照，不是证据：省略 `Flush(true)` 是合规的（§3.3），断电丢掉最近一条不影响任何终态。它不是日志——`progress/` 下每个 requestId 至多一个文件，写它 MUST 是覆盖写（§3.3），MUST NOT 追加。
- 服务者 MAY 在任何时刻停止写进度（包括正在执行中），这不需要任何声明；读方 MUST 把"文件不再更新"当作"没有更多进度"，而不是"命令挂了"——命令是否还活着由 heartbeat（§6）与 claim 租约（§10.1）决定，`progress/` MUST NOT 被当作存活依据。

---

## 14. `state/`：状态发布（可选能力）

路径：`endpoints/<id>/state/<name>.json`，由**服务者**覆盖写，`<name>` 遵循 §4.3。

```json
{"name":"document","revision":42,"updatedAtUtc":"2026-10-19T13:52:53.9000000Z","payload":{"activeTab":"Main.doc","dirty":true}}
```

- `revision` MUST 单调递增（同一端点内），从 1 开始。它不是版本号，是「这个端点第几次发布这个状态」。
- `payload` 不透明，可以是任意 JSON 值（对象、数组、字符串、数字、布尔、null）。协议不解释它。
- 读者永远看到**最新值**：没有历史、没有回执、没有终态证据。读到旧值不算错误；读到 `revision` 倒退才说明有第二个写者（§2 所有权）。
- 读方 MUST 容忍文件不存在（§3.7）：不存在等于「这个端点还没发布过这个状态」，不是「状态是空」。
- 状态名 MUST 在 `endpoint.json` 的 `states` 数组里声明过，投递方 MUST NOT 猜测未声明的状态名。声明格式：`{"name":"document","summary":"当前文档"}`，可选带 `metadata` 对象（§5.1 的同一套规则：自由 JSON、没有就省略、读方忽略不认识的键）。
- 状态文件的字段是固定的四个（`name`/`revision`/`updatedAtUtc`/`payload`）。CLI 一类的观察工具可以在**输出**里额外包一层 `endpointId` 之类的上下文，但那只是显示层，不改变本文件的内容。
- 状态 MUST NOT 承载有副作用的操作（§1 不变式 5）。想「让对端做点什么」就走 `commands/`。

### 14.1 什么时候用 state 而不是 command

| 需求 | 用哪个 | 为什么 |
|---|---|---|
| 读当前文档路径 | `state/document` | 没有副作用，读多少次都一样 |
| 打开一个文档 | `commands/` + `document/open` | 有副作用，需要终态证据 |
| 编辑器帧率、内存占用 | `state/telemetry` | 高频覆盖写，旧值无意义 |
| 导出 PDF | `commands/` + `document/export` | 长任务，需要进度与取消 |
| 插件是否已加载 | `state/plugins` | 读侧只关心当前值 |

判据只有一条：**这次交互需要「谁在什么时候得到了什么结论」吗？** 需要就是请求-响应，不需要就是状态发布。

### 14.2 与 FileBridge `snapshots/` 的对应

FileBridge 的 `snapshots/<kit>.json` 是各 Kit 自己写的状态快照，`ReadSnapshot<T>()` 读最新值。`state/<name>.json` 就是它的协议化：

- 名字从「Kit 名」推广成「任意 state 名」，因为 Courier 不假设调用方知道对方内部是怎么分模块的。
- 增加 `revision`，让读方能分辨「读到了新值」与「还是刚才那个值」——FileBridge 靠 `updatedAtUtc` 猜，但同一毫秒内两次发布就分辨不出来。
- 增加 `endpoint.json` 的 `states` 声明，让读方知道该期待哪些名字，而不是硬编码字符串。

---

## 15. 归档与 GC

### 15.1 归档（MUST）

命令产生终态后，服务者 MUST 把命令文件移动到：

```
archive/<yyyy-MM-dd>/<endpointId>/commands/<requestId>.json
```

死信 MUST 另外写一份内容相同的副本到 `archive/<yyyy-MM-dd>/<endpointId>/deadletter/<requestId>.json`（§12：在线死信可被清理之后，这份副本是证据链的另一半；用原子写而不是 rename，因为在线那份必须留着）。命令文件本身仍然按上面的规则移到 `archive/.../commands/`。

- 日期取**处理时刻**（UTC）的日期，不是投递时刻。
- 移动 MUST 是同卷 rename。
- 顺序 MUST 是：**先写 results（或 deadletter），再移动命令**。反过来会在崩溃时丢证据。
- 移动失败（如目标已存在）MUST 忽略并继续；下一个 tick 会因"results 已存在"而幂等跳过。

### 15.2 results 的 GC（MUST）

`results/` **MUST NOT 被第三方搬动或删除**——它是幂等检查（§10.1）的唯一依据。搬走等于让重放检查失效，会导致已成功的命令被重新执行。

只有服务者自己可以清理，且 MUST 按以下顺序：

1. 写 `results/gc.json`：`{"cutoffUtc":"...","removedBefore":"<删除后仍存在的最小 requestId>","generatedAtUtc":"..."}`
2. 删除 `cutoffUtc` 之前完成的响应文件。

这样读者看到 gc.json 就知道"某时间点之前的结果已被回收"，而不是误判为"从没处理过"。

墓碑字段的精确含义：

| 字段 | 类型 | 说明 |
|---|---|---|
| `cutoffUtc` | string | 本次回收的时间分界：`completedAtUtc` 严格早于它的响应被删除 |
| `removedBefore` | string | 删除**之后**仍然存在的**最小** `requestId`（序数比较）。一条都没剩下时是**空串** |
| `generatedAtUtc` | string | 写墓碑的时刻 |

`removedBefore` MUST NOT 猜一个不存在的 `requestId`：它是"从这里往下都已经被回收了"的下界，猜错就等于让读方相信一条从未存在过的响应。全部删光时只能写空串。

其余约束：

- **只删"已完成的响应"。** 命令还在 `commands/`、claim 还在 `claims/`、或响应文件解析不出来，MUST 跳过——在途命令绝不删。名字不是合法 `requestId` 的文件（§4.2）MUST 原样保留。
- **`gc.json` 自身永不删除**，也不因回收而被清空：它是唯一的事后解释。
- **幂等。** 重复调用 MUST NOT 报错，MUST NOT 破坏已有墓碑；没有可删的东西时 SHOULD 仍然刷新墓碑。
- 删除阶段单个文件失败 MUST NOT 中断整轮；如果实际幸存集合与预估不同，实现 SHOULD 用真实的 `removedBefore` 重写一次墓碑。
- 墓碑写不出来时 MUST NOT 开始删除：没有解释的消失比留着一堆旧响应糟得多。

**读方的义务（最小条款）。** 一个结果文件缺失、而 `gc.json` 存在时，读方 MUST 用墓碑把两种情形分开：

| 情形 | 判断 | 含义 |
|---|---|---|
| 已被回收 | `requestId` **序数小于** `removedBefore`，或 `removedBefore` 是空串 | 这条响应**曾经存在**，被 GC 删了。重放它不会得到任何结果 |
| 从未处理 | 其它 | 对端没处理过它，或还没处理完 |

读方 MUST NOT 把「墓碑存在」直接当成「我要的东西被回收了」——墓碑是**时间下界**，不是全集。
读方也 MUST NOT 因为墓碑缺失、损坏或读不出来就判定「从未处理」以外的任何结论（§3.7）。

这条对调用方的可见形态由实现决定：本规范的 `response` 与原因码集合（§18）**不因它而改变**——`unreachable` 仍然是 `unreachable`，`rejected` 仍然是 `rejected`。墓碑只解释「为什么没有文件」，不引入第四个终态、不新增原因码。

### 15.3 磁盘保留策略（archive 与在线 deadletter）

- `archive/` 是保留策略的主要作用位置（§15.1、§22）。
- 在线 `endpoints/<id>/deadletter/` 也属于允许清理的位置，但**必须**满足 §12 的额外条件（归档副本先存在）。
- `results/` 只能按 §15.2 的 GC 清理，`state/`、`progress/`、`cancels/`、`commands/`、`claims/` 不在任何允许删除的清单里。
- 保留策略由实现决定（建议按日期目录整目录删除），MUST 有界。

**「有界」的含义（MUST）。** 一个持续运行的实现 MUST 让 `archive/` 的总量收敛——不能只搬入、不删除，让它在数天内涨到与磁盘同阶。保留期与清理频率由实现自行决定，本规范只约束下面几条边界：

- 清理 MUST 按**日期目录**整目录删除（`archive/<yyyy-MM-dd>/<endpointId>/`），MUST NOT 逐文件挑着删。日期目录一旦过期，里面整批都已经过期；而逐文件删会和正在搬入的一次 rename 抢同一个位置。
- 删的 MUST 只是**该端点自己的子目录**。多个端点共享同一棵 `archive/` 树，替别的端点决定保留期不是本端点的事。
- 目录名 MUST 先按 `yyyy-MM-dd` 解析成功才参与比较；解析失败的目录（用户自己放的东西）MUST 原样保留，MUST NOT 猜。
- 保留期 MUST 可配，且 MUST 允许配成「不清理」（此时上限责任回到宿主）。
- 清理 MUST NOT 阻塞服务循环：它 SHOULD 限频执行（一轮只做一次目录列举），单轮失败 MUST NOT 影响本轮其余步骤。

**在线死信的保留（已补条款，见 §12）。** 早期版本这里记着「`endpoints/<id>/deadletter/` 无合规删除路径」的遗留问题。现在它有了：§12 把「进入 deadletter 时同时写归档副本」从可选升为 MUST，并允许在**归档副本存在且过期后**删除在线那一份。两份副本各自有界，证据链不断。

---

## 16. 服务者处理循环（`Tick`）

`Tick` 是一个**非阻塞的泵**：它把命令从队列里推进到"执行中"，把执行完的收回来写成终态，然后立刻返回。它 MUST NOT 等任何 handler 跑完。

```
Tick():
  1. 确保自身目录存在
  2. 原子覆盖写 heartbeat
  3. 处理 cancels/：对 in-flight 的 requestId 触发取消
  4. 收割：把已完成的 in-flight 结果写成终态（results/ 或 deadletter/），
     归档命令、删除 claim、删除 progress、释放 in-flight 名额
  5. 续约：对仍在执行的 in-flight，把 claim 的 expiresAtUtc 推到 now + CLAIM_WINDOW
  6. 启动：枚举 commands/*.json，按文件名升序，在名额内最多启动 MAX_TICK_BATCH 条
     —— 启动即返回，不等 handler 结束
  7. 清理终态已完成的 progress/ 文件
  8. 刷新 root 注册表（可选能力，§7）：限频，抢不到锁就跳过，绝不阻塞本轮
```

第 8 步是可选能力的挂载点，默认不存在：不开注册表的实现与只有前七步的实现逐字节相同。

**实现私有的维护步骤。** 实现 MAY 在这条流水线的**任意位置**插入维护动作（本仓库的例子：§15.3 的 archive 保留清理、以及把 `progress/` 里已经产出终态的那些文件删掉），只要它满足：单轮工作量有界、失败不穿透 `Tick`、不改变前七步任何一条的可观察结果、且不含任何协议语义。**换句话说：任何观察方都不能因为多跑了或少跑了这类步骤而看到不同的协议终态。** 这类步骤与第 8 步的区别只在「是否产生协议可观察的结果」——注册表虽然可选，但它产出的文件是协议定义的；维护步骤产出的东西（或删掉的东西）全在实现私有范围里（§2）。

`Tick` MUST 是可重入的安全点：每次调用要么把某条命令推进到"执行中"，要么写出一条终态，要么什么都不做。**它 MUST NOT 因为某条命令执行得久而阻塞。**

### 16.1 handler 与 Tick 并发推进

handler 在 `Tick` 之外执行（线程池、异步任务，或宿主自己的执行器），`Tick` 只负责启动与收割。

- in-flight 数量 MUST ≤ `MAX_INFLIGHT`（默认 4）。名额满时第 6 步直接跳过，不阻塞。
- `MAX_TICK_BATCH`（默认 16）限制的是**单轮启动多少条**，不是单轮跑完多少条。
- `Tick` 与 handler MUST 能并发推进；实现 MUST 用锁或不可变快照保护 in-flight 表，MUST NOT 让 handler 直接改 `Tick` 正在遍历的集合。
- **MUST NOT 并发调用 `Tick`。** 两个线程同时 Tick 会同时看到"无 result、无 claim"，然后各写一次，破坏 claim 的独占语义。这条约束无法在运行时便宜地检测，只能写成规则并由实现声明。
- `Tick` 的单次工作量 MUST 有界：至多启动 `MAX_TICK_BATCH` 条、至多收割当前 in-flight 数。

**为什么必须这样设计。** 如果 `Tick` 同步跑完 handler 再返回，会同时坏掉三件事：心跳（§6）在 handler 执行期间不更新，慢 handler 把端点自己判死；取消（§13.1）只在 Tick 开头检查，而 handler 运行期间根本进不了 Tick，取消永远送不到；claim 租约（§10.1）无法续约，任何超过 `CLAIM_WINDOW` 的 handler 都会被别的循环接管、并行执行两次。这三件事不是三个独立的 bug，是同一个设计错误的三个症状：**把"推进队列"和"执行任务"绑在同一根线程上**。

### 16.2 单条命令的启动与收割

启动（`Start(requestId)`）：

```
 1. results/<id>.json 存在        → 幂等跳过；归档命令、删除 claim、返回
 2. claims/<id>.json 存在且未过期  → 跳过（别处正在处理）
 3. claims/<id>.json 存在且已过期  → attempts+1；若 > MAX_ATTEMPTS 进 deadletter
 4. 文件大小 > 上限                → deadletter，不读入内存
 5. 读取并解析失败                 → deadletter
 6. now > deadlineUtc             → results{rejected, stale} → 归档
 7. 无对应 (channel, action)       → results{rejected, unknown-action} → 归档
 8. 独占创建 claims/<id>.json，写 expiresAtUtc = now + CLAIM_WINDOW
 9. 把 (requestId, CTS, 起始时刻) 放进 in-flight 表，在线程池上启动 handler
```

收割（`Reap()`，第 4 步）对每个已完成的 in-flight：

```
 1. handler 正常返回              → results{ok, payload}
 2. handler 抛 OperationCanceled  → results{failed, cancelled}
 3. handler 抛其他异常            → results{failed, handler-error}
 4. 超过 HANDLER_TIMEOUT          → results{failed, handler-timeout}，尽力 Cancel
 5. 写 results → 归档命令 → 删 claim → 删 progress
    （写 results 失败则止步，见下；其后各步的 I/O 失败 MUST NOT 中断其余步骤）
```

- 第 4 条 MUST 立即写终态，MUST NOT 等 handler 返回：一个不响应取消的 handler 可能永远不返回，等它等于让这条命令永远没有结果。
- 写终态后若 handler 又返回了，其结果 MUST 被丢弃（终态已写，MUST NOT 覆盖）。
- 被放弃等待的 handler 之后抛出的异常 MUST 被观察掉（取一次 `Task.Exception` 或挂一个只观察的续延），MUST NOT 让它变成无人处理的 `UnobservedTaskException`。观察不等于采纳：这条命令的终态早就写过了，异常只是被收走。
- 收割路径 MUST 用 `try/catch` 包住每一步 I/O，MUST NOT 让一个 `IOException` 穿透 `Tick` 把整个服务循环打死。
- 但**写 `results/` 失败是一个例外：此时 MUST NOT 归档命令、MUST NOT 删 claim**。命令留在 `commands/`、claim 留到租约到期，下一轮按 `attempts + 1` 重来。先归档再删 claim 会让这条命令连证据一起消失——那才是真正不可恢复的丢件（§1 不变式 3）。宁可重做一次，不可无凭据地丢掉。

**HANDLER_TIMEOUT 的默认值是命令的剩余 deadline。** 也就是说，一条命令从投递到产出终态，最迟不超过它自己的 `deadlineUtc`——这与 §9.1 的语义一致：deadline 既是"最晚开始执行"，也是"最晚可以有结果"。实现 MAY 配置更短的 handler 时限。

### 16.3 交给 handler 的上下文

只把 `payload` 交给 handler 时，handler 拿不到这条命令的 `requestId`，也就无法把自己的日志、端点状态和投递方的等待对齐。实现 MAY 把**已经解析出来的信封字段**一并交给 handler，作为一次调用的上下文：

| 上下文字段 | 来源 | 说明 |
|---|---|---|
| `requestId` | 命令文件名 | 与 `commands/<requestId>.json`、`results/<requestId>.json` 同一个键 |
| `channel` / `action` | 信封 | §4.3 |
| `issuedBy` | 信封的 `issuedBy` | 缺失时取实现自己的投递方标识（§9） |
| `issuedAtUtc` / `deadlineUtc` | 信封 | §9.1 |
| `attempt` | 本次认领的 `attempts`（§10） | 从 1 起；大于 1 表示租约被接管过 |
| `payload` | 信封 | 不透明字符串（§9） |

- 上下文的字段 MUST 与信封**逐字段一致**，实现 MUST NOT 在传给 handler 时改写它们。
- 上下文只是**便利**：它不改变投递语义。**投递仍是 at-least-once**（§1、§10.1），同一个 `requestId` 可能带着不同的 `attempt` 到达不止一次，需要恰好一次的 handler MUST 自己按 `requestId` 去重。
- 不支持上下文的实现 MUST 仍然只传 `payload` 与 `CancellationToken`；这不构成互操作问题——上下文的字段全部来自磁盘上已经存在的信封。

---

## 17. 投递方语义

### 17.1 投递

- MUST 原子写（§3.3），MUST 不覆盖。
- MUST 在写入前完成 §9.3 的三项检查。

### 17.2 等待终态

调用方 MUST 按下述顺序判定，且 MUST 在 `timeout` 内完成：

1. 轮询 `results/<requestId>.json`；出现即返回。
2. 超时仍无结果 → 返回合成的 `unreachable`。

- 轮询间隔 SHOULD 从 10 ms 起，指数退避到 200 ms 上限。
- MUST NOT 无限等待。
- MUST NOT 在等待期间删除或修改命令文件（它属于服务者的处理流程）。

### 17.3 互调死锁（实现约束）

**调用方**（`Call`/`CallAsync` 的使用者）MUST 使用非阻塞等待（异步/让出线程），MUST NOT 在驱动 `Tick` 的线程上同步阻塞等待另一个端点。同步阻塞只在没有 `Tick` 循环的短命进程（CLI）上安全。

**handler 内部 MUST NOT 同步等待另一个端点。** 这条规则把调用链深度限制为 1，从结构上排除 A→B→C→A 环。

§16.1 之后 `Tick` 本身不再被 handler 阻塞，所以这条规则的理由变了——但结论没变。现在的风险不是"Tick 停了"，而是**在途名额被等待中的 handler 占满**：`MAX_INFLIGHT`（默认 4）是个很小的数，四个 handler 各自阻塞等待另一个端点，这个端点就再也启动不了第五条命令。它不会死锁整个进程，但会让端点对外表现为"活着但什么都不做"——比崩溃更难诊断。

异步 handler 在 `await` 另一个端点时同样占着名额，只是不占线程。需要长链路协作的宿主 MUST 自行调大 `MAX_INFLIGHT`，或者把协作拆成两次独立投递。

---

## 18. 原因码总表

| status | reason | 谁产生 | 含义 |
|---|---|---|---|
| `ok` | — | 服务者 | 成功 |
| `rejected` | `stale` | 服务者 | 取走时已过 `deadlineUtc` |
| `rejected` | `unknown-action` | 服务者 | 未注册该 `(channel, action)` |
| `rejected` | `invalid-command` | 服务者 | 解析失败、字段缺失、requestId 不匹配、超限 |
| `rejected` | `cancelled-before-start` | 服务者 | 执行前收到取消 |
| `rejected` | `backpressure` | **投递方** | `commands/` 超阈值，未投递 |
| `failed` | `handler-error` | 服务者 | handler 抛异常 |
| `failed` | `handler-timeout` | 服务者 | handler 超过声明时限 |
| `failed` | `cancelled` | 服务者 | handler 观察到取消并主动放弃 |
| `unreachable` | — | **调用方** | 超时无响应，无文件可读 |

**实现 MUST 只使用表中出现的原因码。** 新增原因码需要提升 `protocolVersion`。

§15.2 的墓碑**不是**第四个终态、也**不是**原因码：结果被回收时调用方看到的仍然是 `unreachable`。墓碑只解释「为什么没有文件」，把它做成 `status` 或 `reason` 都会打破上面这条禁令。

---

## 19. 参数与默认值

| 参数 | 默认值 | 可配置 | 约束 |
|---|---|---|---|
| `PROTOCOL_VERSION` | `2` | MUST NOT | 提升版本需要新文档 |
| `HEARTBEAT_ALIVE` | 5 s | MAY | 必须 < `HEARTBEAT_DEAD` |
| `HEARTBEAT_DEAD` | 15 s | MAY | |
| `DEFAULT_DEADLINE` | 10 s | MAY | 按调用可覆盖（§9.1） |
| `MAX_DEADLINE` | 300 s | MAY | 调用方给的 `deadline` 超过此值时截断 |
| `CLAIM_WINDOW` | 10 s | MAY | 租约长度；MUST > `CLAIM_RENEW_INTERVAL` 的两倍 |
| `CLAIM_RENEW_INTERVAL` | 2 s | MAY | 续约间隔；MUST < `CLAIM_WINDOW` |
| `MAX_INFLIGHT` | 4 | MAY | ≥ 1；单个端点同时在跑的 handler 上限 |
| `HANDLER_TIMEOUT` | 命令剩余 deadline | MAY | 超过即 `failed/handler-timeout` |
| `MAX_ATTEMPTS` | 3 | MAY | ≥ 1 |
| `MAX_COMMAND_FILE_BYTES` | 1 MiB + 4 KiB | MUST NOT | 超出即 deadletter |
| `MAX_TICK_BATCH` | 16 | MAY | ≥ 1；单轮最多**启动**多少条 |
| `MAX_COMMANDS_BACKPRESSURE` | 4096 | MAY | 投递方判定 |
| `DEFAULT_TICK_INTERVAL` | 200 ms | MAY | 由宿主决定，协议不强制 |
| `MAX_STATE_BYTES` | 1 MiB | MAY | 状态文件上限，超出 MUST 拒写 |
| `MAX_STATE_NAMES` | 256 | MAY | 单个端点声明的状态名上限 |
| `MAX_CAPABILITIES` | 256 | MAY | 单个端点自述的能力名上限（§5.3） |
| `HOST_LOCK_TIMEOUT` | 0（立即） | MAY | 拿 `host.lock` 的等待上限 |
| `ARCHIVE_RETENTION` | 14 天 | MAY | `archive/` 的保留期；配成 0 表示不清理（§15.3） |
| `DEADLETTER_RETENTION` | 14 天 | MAY | 在线 `deadletter/` 的保留期；配成 0 表示不清理（§12） |
| `RETENTION_SWEEP_INTERVAL` | 1 小时 | MAY | 保留策略的清理频率下限，避免每 Tick 扫目录（§15.3） |

---

## 20. 安全要求

1. **不做鉴权。能写这个目录的进程就是可信的。** `issuedBy` 只用于审计与回信，不是凭据。
2. **`endpointId` 校验是强制的**（§4.1），这是唯一的路径拼接入口。
3. **root MUST NOT 位于多用户共享的临时目录**（`%TEMP%`、`/tmp`）。其他用户能往 `commands/` 投东西，而协议没有鉴权。多用户机器上 root MUST 位于用户私有目录。
4. **root SHOULD NOT 位于云同步目录**（OneDrive / Dropbox / iCloud）。同步客户端会在 rename 前后插入干扰，直接破坏 §3.3 的原子性。
5. **payload 不透明**。Courier MUST NOT 解析或校验 payload 内容；业务校验在 handler 里做。
6. **必须有 DoS 上限**：`MAX_TICK_BATCH`、`MAX_COMMAND_FILE_BYTES`、`MAX_COMMANDS_BACKPRESSURE` 全部生效。
7. **一旦重新引入快通道（§5.2），信任边界会跟着变**。命名管道没有"能写这个目录才可信"的等价机制，同机任何进程都能连那个管道名。重新引入时必须同时定义它的鉴权模型，不能默认继承文件系统的权限。

---

## 21. 一致性清单

一份实现要声称"符合 Courier"，下列每一条都必须是可验证的：

**编码与原子性**
- [ ] 所有写入走"同目录临时文件 → flush → rename"（§3.3）
- [ ] 命令与 claim 用不覆盖语义创建（§3.3）
- [ ] 读 JSON 对 `IOException` 有界重试（§3.4）
- [ ] JSON 忽略未知字段（§3.1）
- [ ] 拼出的路径不逃逸 root，且不穿过重解析点（§3.6）
- [ ] 读缺失的文件当作「还没有」而不是错误（§3.7）

**标识与版本**
- [ ] `endpointId` 非法时拒绝启动（§4.1）
- [ ] `requestId` 含随机段（§4.2）
- [ ] 版本不匹配时投递方不写任何文件（§4.4）
- [ ] `sessionId` 每次启动换新（§5）
- [ ] 名字只按序数比较，拒绝 `..` 与首尾点（§4.3）

**路由与所有权**
- [ ] 投递方从不写 `results/`、`claims/`、`heartbeat`（§2）
- [ ] 端点退出不删目录（§2、§6）
- [ ] 投递方从不写 `state/`（§2、§14）
- [ ] `state/` 只承载无副作用的最新值，没有历史也没有回执（§14）
- [ ] 若提供 root 注册表，它是整体原子替换且按 `endpointId` 升序（§7）
- [ ] 若提供 root 注册表，合并写入不以「读不到」为由丢弃别人的条目，抢不到锁不阻塞服务循环（§7）
- [ ] 若提供 `host.lock`，只有共享冲突被判为「已被占用」，磁盘与权限故障不被伪装成竞争（§8）
- [ ] 若提供 `host.lock`，抢锁发生在铺开子目录之前，失败不留半初始化目录（§8）
- [ ] 若可能同时启动两个实例，`host.lock` 已启用——它是 §1 不变式 1 的唯一强制手段（§8）

**生命周期**
- [ ] 每条命令最终产出 results 或 deadletter（§1 不变式 3）
- [ ] claim 过期后先查 results 再重做（§10、§16）
- [ ] 续约失败（租约被接管）时放弃执行且不写 `results/`（§10.1）
- [ ] `attempts` 记在 claim 而非命令里（§10）
- [ ] 超过 `deadlineUtc` 的命令不执行（§9.1）
- [ ] 超限命令先看大小再读（§9.2）
- [ ] 归档顺序是"先写结果，后移命令"（§15.1）
- [ ] `results/` 不被第三方搬动，GC 先写墓碑（§15.2）
- [ ] `archive/` 的保留策略有界：按日期目录整目录删、只删自己的端点、非日期目录不动（§15.3）
- [ ] 死信进入时同时写了 `archive/<date>/<id>/deadletter/` 副本；在线死信只在归档副本存在且过期后才被删（§12、§15.3）
- [ ] GC 的 `removedBefore` 是删除后仍存在的最小 `requestId`，删光时为空串（§15.2）
- [ ] GC 不删在途命令，不删 `gc.json`，重复调用幂等（§15.2）
- [ ] 状态文件超过 `MAX_STATE_BYTES` 时拒写而非截断（§14、§19）

**语义**
- [ ] `rejected` 与 `failed` 严格分开（§11.1）
- [ ] `unreachable` 不落盘（§11.2）
- [ ] 只使用 §18 的原因码
- [ ] `Tick` 不并发（§16）

**并发模型**
- [ ] `Tick` 不等 handler，慢 handler 期间心跳照常更新（§6、§16.1）
- [ ] handler 收到 `CancellationToken`，取消能在 handler 执行期间送达（§13.1）
- [ ] 在途 handler 续约 claim，租约不假过期（§10.1）
- [ ] 在途数量受 `MAX_INFLIGHT` 限制（§16.1）
- [ ] 超过 `HANDLER_TIMEOUT` 立即写 `failed/handler-timeout`，不等 handler 返回（§16.2）
- [ ] 收割路径的 I/O 失败不穿透 `Tick`（§16.2）
- [ ] 第一次 `Tick` 之后再注册能力被拒绝（§5）

**互操作**
- [ ] 与另一份实现双向对拍：命令、响应、死信、取消、进度、状态格式完全一致（**TODO：本仓库无法完成，见本节的「已知无法在本仓库完成项」**）
- [ ] 读方在对方没实现可选能力（`state/`、注册表、`host.lock`、cancels、progress）时行为正确退化
- [ ] `metadata` 缺失时字段省略而不是 `null`；读到未知字段与未知 `metadata` 键一律忽略（§5.1）
- [ ] `capabilities` 缺失时字段省略（不写 `null`、不写空数组）；读到不认识的能力名忽略而不是判目录不可用（§5.3）
- [ ] 声明了 `cancels` 的实现真的会在每次 `Tick` 读 `cancels/`（§13.1），且 `requestId` 与文件名不一致的取消文件被忽略
- [ ] 声明了 `progress` 的实现真的会覆盖写 `progress/<requestId>.json`，并在终态产生后删掉它（§13.2）
- [ ] `endpoint.json` 里没有 `hot` 字段；热通道（若实现有）只在实现私有目录留痕（§5.2、§2）
- [ ] 结果缺失且墓碑存在时，读方按 `removedBefore` 区分「已被回收」与「从未处理」，且不因此改变 `status` 或原因码（§15.2）

**已知无法在本仓库完成项（TODO，保持未勾选）**

- **「与另一份实现双向对拍」无法在本仓库内完成。** 仓库只交付**一份**实现（`src/Courier/`；CLI 通过 `<Compile Include>` 链接同一批源文件，第二个 `AssemblyName` 不等于第二份实现）。双向对拍需要第二份**独立**实现，即 Python / C 档。在它出现之前，本条 **TODO**，MUST NOT 因为「单实现自测全绿」而勾选。
- 因此本仓库当前的结论只覆盖**读方的容忍度**（读到未知字段、未知 `metadata` 键、缺失的可选能力时行为正确退化，§3.1、§5.1），**不覆盖写方的格式一致性**。
- 热通道的描述曾以 `hot` 字段写进 `endpoint.json`（违反 §5.2），现已改写到实现私有目录 `endpoints/<id>/hot/descriptor.json`，冲突解除。但「把它写回协议」仍然卡在这里：解除 §22 第 3 条需要 §5.2 的三条门槛全部满足，第 1 条至今没有第二个实现方。详见 `HOT-CHANNEL.md` §5.1 与 §7。

---

## 22. 非目标（明确不做，实现 MUST NOT 自行扩展）

下列能力不属于 Courier。实现 MUST NOT 以"可选"名义引入。

1. 网络传输。跨机器请挂载共享目录。
2. 多写者事务、分布式一致性。
3. **协议层的快通道**（named pipe / shared memory / file mapping）。理由与重新引入的前提见 §5.2。文件通道之外的加速属于实现自由，不属于协议。
4. 鉴权。能写这个目录的进程即视为可信（§20 第 1 条）。
5. payload 的 schema 校验。payload 对 Courier 是不透明字符串。
6. MCP 适配层。与 MCP 是替代关系。
7. 跨语言 SDK 统一化。正式实现只有 C#，其余语言走 CLI 或 stdio 助手。
8. 绑定任何宿主框架类型。
9. 传输抽象层（如 `IMessageTransport`）。
10. 消息过滤与订阅。收件人在写入时由目录确定（§2 不变式 2）。
11. **中心化的服务发现**。root 级 `endpoint.json`（§7）是可选的缓存视图，不是注册中心：它不参与路由，也不被信任为存活依据。`endpoints/` 目录才是名单。
12. 状态订阅与变更通知。`state/` 只提供"读最新值"，不提供"值变了告诉我"。轮询是唯一被规范的方式。
13. 恰好一次投递。见 §1 的 at-least-once 说明。
14. **能力协商**。`capabilities`（§5.3）只是自述：读方 MAY 据此降级，但协议不定义握手、不定义版本协商、也不允许"没声明就拒绝投递"。它是给观察方看的，不是给投递路径做分支的。

