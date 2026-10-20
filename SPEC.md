# Courier 协议规范 v1

**状态**：草案（Draft）。协议版本 `1`。
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

本文使用 RFC 2119 措辞：**MUST / MUST NOT / SHOULD / SHOULD NOT / MAY**。

**核心不变式**（其余条款都可以从这四条推出来）：

1. **一个端点目录只有一个服务者**，但可以有任意多个投递方。
2. **路由在写入时确定**：消息写进哪个目录，就只有该目录的服务者会处理。不存在运行期过滤。
3. **落盘即承诺**：出现在 `commands/` 的每条命令 MUST 最终产出一个终态证据（`results/` 中的响应，或 `deadletter/` 中的记录）。MUST NOT 静默丢弃。
4. **原子写只有一种**：同目录临时文件 → flush/close → rename。MUST NOT 直接写目标文件。

**投递语义是 at-least-once，不是 exactly-once。** 服务者崩溃、租约过期后被接管（§8.1），都可能让同一条命令被执行两次。需要恰好一次副作用的 handler 必须自己按 `requestId` 去重。协议不提供、也不假装提供 exactly-once。

---

## 2. 信道布局

```
<root>/
├── endpoints/
│   └── <endpointId>/
│       ├── endpoint.json          # 能力目录，首次 Tick 前写完即冻结，退出不删
│       ├── heartbeat              # 单个 UTC 时间戳，每 tick 覆盖写
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
│       └── progress/              # 服务者写（可选能力）
│           └── <requestId>.json
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
| `archive/` | 服务者（搬自己的）、显式授权的 GC 进程 | 所有人 |

**MUST NOT**：投递方写入 `results/`、`claims/`、`deadletter/`、`progress/`、`heartbeat`、`endpoint.json`。
**MUST NOT**：任何一方删除 `endpoints/<id>/` 目录本身（端点退出只让 heartbeat 停更）。
**MUST NOT**：任何第三方搬动或删除 `results/`（见 §12）。

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
- 协议假定两端系统时钟偏差小于 `deadlineUtc` 的典型值（§16）。跨机器且无 NTP 时行为未定义。

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

---

## 4. 标识符

### 4.1 endpointId

- 正则：`^[a-z0-9]([a-z0-9-]{0,62}[a-z0-9])?$`
- 长度 1–64，全小写，只允许 `a-z`、`0-9`、`-`，且不以 `-` 开头或结尾。
- 服务者启动时 MUST 校验；不合法 MUST 直接拒绝启动（不写任何文件）。
- **这是唯一的路径拼接入口**。不校验等于允许 `../` 打穿 root。

### 4.2 requestId

- 正则：`^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}$`
- 格式：`{yyyyMMdd}T{HHmmss}Z-{8位小写hex}`，例：`20261019T135243Z-7f3a2b1c`。
- 随机段 MUST 存在。用自增计数器代替随机段的实现**不满足本规范**：两个进程在同一秒内投递会撞名。
- 时间戳部分 SHOULD 取投递时刻；服务者 MUST NOT 依赖它与 `issuedAtUtc` 一致。

### 4.3 channel / action

- 正则（两者相同）：`^[A-Za-z0-9_.-]{1,64}$`
- 注册与调用时 MUST 完全一致（区分大小写）。不做大小写折叠。

### 4.4 protocolVersion

- 整数。当前为 `1`。
- **唯一的版本字段**。命令与响应里 MUST NOT 重复携带版本。
- 投递方 MUST 在投递前读取目标 `endpoint.json` 的 `protocolVersion` 并与自身实现版本比对：不同则**本地直接失败，不写任何文件**。
- 版本不匹配 MUST 在使用之前发现，而不是在解析失败之后。

---

## 5. `endpoint.json`

启动阶段写完即**冻结**，端点退出时**不删**。

```json
{"endpointId":"app-a","description":"桌面排版工具","processId":41232,"startedAtUtc":"2026-10-19T13:50:00.0000000Z","protocolVersion":1,"actions":[{"channel":"document","action":"open","summary":"打开文档"}]}
```

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `endpointId` | string | MUST | 必须等于目录名 |
| `description` | string | SHOULD | 人类可读 |
| `processId` | integer | MAY | **不得用于判活** |
| `startedAtUtc` | string | MAY | 上一次运行的值，**不得用于判活** |
| `protocolVersion` | integer | MUST | 见 §4.4 |
| `actions` | array | MUST | 可为空数组 |
| `hot` | object | MAY | 快通道声明，见 §5.2 |

**MUST 整体原子替换，MUST NOT 增量追加。** 先构造本次运行的**完整** actions 列表再覆盖整个文件。增量追加会让上一版注册过、这一版已删除的能力永远留在文件里，形成一个永远返回 `rejected/unknown-action` 的幽灵能力。

**能力表在第一次 `Tick` 之前写完，此后冻结。** 逐条注册的实现每注册一条就整体重写一次文件，这没问题——只要全部发生在第一次 `Tick` 之前。第一次 `Tick` 之后 MUST NOT 再改 `endpoint.json`：实现 MUST 在此时拒绝新的能力注册并抛错，MUST NOT 静默改写。

理由：`endpoint.json` 是投递方与观察方唯一的能力来源。运行期改它，等于让"这个端点能做什么"变成一个随时间变化的目标——投递方刚读到的能力，可能在下一次读时就不存在了。冻结把能力表变回一个可缓存的常量。

### 5.1 actions 条目

```json
{"channel":"document","action":"open","summary":"打开文档"}
```

同一 `(channel, action)` 在一个 `endpoint.json` 内 MUST 唯一。重复注册是编程错误，服务者 MUST 在注册时直接报错。

### 5.2 可选 `hot` 字段

```json
{"kind":"namedPipe","address":"courier-app-a-7f3a2b1c","protocol":"courier-hot/1","fallback":"file"}
```

- `kind` 取值：`namedPipe`、`sharedMemory`、`fileMapping`。
- `fallback` MUST 为 `"file"`。
- 缺省该字段即表示"只有文件通道"，这是**合法且常见**的形态。
- 快通道的线格式**不在本规范范围内**。本规范只规定：声明了 `hot` 的端点，在快通道连不上、超时或 `protocol` 不匹配时 MUST 回落 `commands/`。
- 快通道承载的调用 MUST NOT 产生 `commands/` 或 `results/` 文件，因此 MUST NOT 用于必须可审计、可重放的调用。

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

**心跳的含义是"服务循环还活着"，不是"某个 handler 正在跑"。** handler 在服务者内部执行（§13.1），执行期间服务循环 MUST 继续按宿主节奏 `Tick`，心跳照常更新。一个跑了 20 秒的 handler MUST NOT 让端点显示成 `dead`——观察方看到 `dead` 就应当认为这个端点再也处理不了任何命令，而不是"它很忙"。

换句话说：**心跳不能由 handler 的进度决定。** 如果心跳只在"一轮 Tick 从头跑到尾"之后才更新，那么任何慢 handler 都会把端点自己判死，而杀死它的正是它自己。

---

## 7. `command`

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
| `deadlineUtc` | string | MUST | 见 §7.1 |
| `issuedBy` | string | SHOULD | 投递方端点 id。**不参与路由**，只用于审计与回信。**不是凭据** |

### 7.1 deadlineUtc

- 服务者取走命令时若 `now > deadlineUtc`，MUST NOT 执行，直接写 `rejected/stale` 并归档。
- 目的：防止"软件关了两小时后开机，执行一堆陈旧命令"。
- 投递方 MUST 用 `issuedAtUtc + deadline` 生成该字段，其中 `deadline` 默认 `DEFAULT_DEADLINE`（10 s，§16），可由调用方按次覆盖（§14.2），但 MUST NOT 超过 `MAX_DEADLINE`（默认 300 s）。超过时按 `MAX_DEADLINE` 截断，MUST NOT 报错。
- 为什么要有上限：`deadlineUtc` 是"这条命令最晚可以开始执行"的时刻，不是"最晚必须完成"。一个 300 s 的 deadline 意味着命令可以在队列里躺五分钟才被处理。没有上限时，一次误传的极大值就能让一条命令在磁盘上存活到下一次重启。

### 7.2 大小上限

- 命令文件总字节数 MUST ≤ `MAX_COMMAND_FILE_BYTES`（默认 1 MiB + 4 KiB）。
- 服务者 MUST **先看文件长度再决定是否读入内存**。超限直接 `deadletter/`，MUST NOT 把内容读进内存。
- 没有这条上限时，一条巨大命令会在任何校验发生之前耗尽服务者内存。

### 7.3 投递方的前置检查（MUST）

写入前，投递方 MUST 依次检查：

1. 目标 `endpoints/<targetId>/endpoint.json` 存在且 `protocolVersion` 匹配（§4.4）；否则本地失败，**不写文件**。
2. `commands/` 中 `.json` 文件数 < `MAX_COMMANDS_BACKPRESSURE`（默认 4096）；否则本地返回 `rejected/backpressure`，**不写文件**。
3. `payload` 序列化后的长度 ≤ `MAX_COMMAND_FILE_BYTES`；否则本地失败，**不写文件**。

`rejected/backpressure` 由投递方**合成**，不落盘——它不是任何一方的答复。

---

## 8. `claims/`

路径：`endpoints/<id>/claims/<requestId>.json`。

```json
{"owner":"app-a","expiresAtUtc":"2026-10-19T13:52:53.9000000Z","attempts":1}
```

- MUST 以**不覆盖**语义创建（`FileMode.CreateNew`）。创建失败（已存在）说明别的服务循环已在处理，本次跳过。
- `attempts` 从 1 开始，每次重新认领 +1。
- **重试计数 MUST 写在这里，MUST NOT 写进命令文件。** 命令是投递方的产物，服务者改它等于篡改来信。
- 处理结束后 MUST 删除 claim。

### 8.1 claim 是一个租约

claim 不是"我领了这条命令"的记号，而是一个**有到期时刻的租约**：`expiresAtUtc` 之前，别的服务循环看到它就必须退让；`expiresAtUtc` 之后，它自动失效。

- 认领时 MUST 写 `expiresAtUtc = now + CLAIM_WINDOW`（默认 `CLAIM_WINDOW = 10 s`，§16）。
- 认领方 MUST 在 handler 执行期间**续约**：每 `CLAIM_RENEW_INTERVAL`（默认 2 s）把 `expiresAtUtc` 推到 `now + CLAIM_WINDOW`，原子覆盖。
- 续约 MUST 只在租约仍然属于自己时进行：重读 claim，若 `owner` 不是自己或文件已消失，说明租约已被别人接管，MUST **立即放弃执行**，MUST NOT 写 `results/`。
- 租约过期且没有对应 result，视为持有者已死，别的服务循环可以接管并 `attempts + 1`。
- 若 `attempts` 超过 `MAX_ATTEMPTS`（默认 3），接管方 MUST 转 `deadletter/`，MUST NOT 再执行。

这样定义之后，`CLAIM_WINDOW` 就不再需要"大于最长 handler 执行时间"——慢 handler 靠续约维持租约，不靠一个拍脑袋的大窗口。原先那种写法在数学上无法与 `DEFAULT_DEADLINE` 同时成立（窗口要大于 handler、又要小于 10 s 的 deadline），是自相矛盾的。

**租约过期被接管，同一条命令就可能被执行两次。** 这是 at-least-once 的代价（§1），不是可以靠调参数消除的缺陷：持有者可能只是在续约前恰好被操作系统挂起了。协议选择把这条窗口写明白，而不是假装它不存在。需要恰好一次的 handler MUST 自己按 `requestId` 去重。

---

## 9. `response`

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
| `reason` | string | 条件必填 | `status != ok` 时 MUST 出现，见 §15 |

### 9.1 三种落盘状态

| 值 | 含义 | 调用方应做什么 |
|---|---|---|
| `ok` | 执行成功 | 读 `payload` |
| `rejected` | 请求本身有问题，重试无意义 | 修正请求，**不要原样重试** |
| `failed` | 请求合法，执行过程出错 | 可重试，或转人工 |

**`rejected` 与 `failed` MUST 分开。** 混成一个值，调用方无法决定是改参数还是重试。

### 9.2 调用方合成的两种结果（不落盘）

| 值 | 何时 | 说明 |
|---|---|---|
| `unreachable` | 等到超时仍无 response | 对端未启动、已崩溃或从未处理。**没有文件可读**，因为它不是任何一方的答复 |
| `rejected/backpressure` | 投递前 `commands/` 超阈值 | 见 §7.3 |

---

## 10. `deadletter/`

路径：`endpoints/<id>/deadletter/<requestId>.json`。

```json
{"requestId":"20261019T135243Z-7f3a2b1c","reason":"invalid-command","detail":"payload field missing","failedAtUtc":"2026-10-19T13:52:44.0000000Z","attempts":3}
```

进 `deadletter/` 的情形（MUST 全部覆盖）：

1. 命令文件超过大小上限（§7.2）。
2. 命令 JSON 解析失败、必填字段缺失、`requestId` 与文件名不一致。
3. `attempts` 超过 `MAX_ATTEMPTS`（默认 3）。

进入 deadletter 后 MUST 同时归档原命令文件（§12），MUST 删除对应 claim。

**deadletter 是终态证据。** 有它的命令不算静默丢弃，这是 §1 不变式 3 的兜底。

---

## 11. `cancels/` 与 `progress/`（可选能力）

### 11.1 cancels

路径：`endpoints/<id>/cancels/<requestId>.json`，由**投递方**写。

```json
{"requestId":"20261019T135243Z-7f3a2b1c","requestedAtUtc":"2026-10-19T13:52:45.0000000Z","reason":"user-aborted"}
```

- 服务者 MUST 在**每次** `Tick` 检查 `cancels/`，对**正在执行**的 requestId 触发 `CancellationToken`。
- 命令尚未开始执行时收到 cancel：MUST 直接写 `rejected/cancelled-before-start` 并归档，不执行。
- handler **MAY 忽略取消**。此时仍按原逻辑执行完，响应 MUST 携带 `"cancelRequested":true`，让调用方知道取消来得太晚。
- 响应仍只有 `ok`/`rejected`/`failed` 三种，MUST NOT 引入第四种终态。
- 取消是**尽力而为**，不是保证。协议不承诺"发出 cancel 后 handler 一定停止"。

**这条能力成立的前提是 §13.1：`Tick` 不被 handler 阻塞。** 如果 `Tick` 在 handler 返回之前不返回，那么"每个 Tick 检查 cancels/"就退化成"handler 跑完之前永远不检查"——取消文件只能等 handler 自己结束才被发现，等于没有取消。实现 MUST 让 handler 与 `Tick` 并发推进，并且 MUST 把 `CancellationToken` 传进 handler。

handler 收到取消后有两种合规做法：抛 `OperationCanceledException`（→ `failed/cancelled`），或者自行收尾返回 `ok`（→ `ok` + `cancelRequested:true`）。两种都合法，区别只是"取消来得及"还是"来不及"。

### 11.2 progress

路径：`endpoints/<id>/progress/<requestId>.json`，由服务者**覆盖写**。

```json
{"requestId":"20261019T135243Z-7f3a2b1c","percent":42,"message":"导出第 42/100 页","updatedAtUtc":"2026-10-19T13:52:47.0000000Z"}
```

- **只保留最近一次快照，不做流式、不做历史。** 读者看到的永远是最新值，中间的进度可能被跳过。
- 命令终态产生后 MUST 删除对应 progress 文件。
- 不写 progress 的端点 MUST 被调用方正确当作"没有进度信息"，而不是"卡住了"。

---

## 12. 归档与 GC

### 12.1 归档（MUST）

命令产生终态后，服务者 MUST 把命令文件移动到：

```
archive/<yyyy-MM-dd>/<endpointId>/commands/<requestId>.json
```

deadletter 同理移动到 `archive/<yyyy-MM-dd>/<endpointId>/deadletter/`（可选，取决于保留策略）。

- 日期取**处理时刻**（UTC）的日期，不是投递时刻。
- 移动 MUST 是同卷 rename。
- 顺序 MUST 是：**先写 results（或 deadletter），再移动命令**。反过来会在崩溃时丢证据。
- 移动失败（如目标已存在）MUST 忽略并继续；下一个 tick 会因"results 已存在"而幂等跳过。

### 12.2 results 的 GC（MUST）

`results/` **MUST NOT 被第三方搬动或删除**——它是幂等检查（§8.1）的唯一依据。搬走等于让重放检查失效，会导致已成功的命令被重新执行。

只有服务者自己可以清理，且 MUST 按以下顺序：

1. 写 `results/gc.json`：`{"cutoffUtc":"...","removedBefore":"<requestId前缀>","generatedAtUtc":"..."}`
2. 删除 `cutoffUtc` 之前完成的响应文件。

这样读者看到 gc.json 就知道"某时间点之前的结果已被回收"，而不是误判为"从没处理过"。

### 12.3 archive 的保留

- `archive/` 是唯一允许删除的位置（§12.1、§19）。
- 保留策略由实现决定（建议按日期目录整目录删除），MUST 有界。

---

## 13. 服务者处理循环（`Tick`）

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
```

`Tick` MUST 是可重入的安全点：每次调用要么把某条命令推进到"执行中"，要么写出一条终态，要么什么都不做。**它 MUST NOT 因为某条命令执行得久而阻塞。**

### 13.1 handler 与 Tick 并发推进

handler 在 `Tick` 之外执行（线程池、异步任务，或宿主自己的执行器），`Tick` 只负责启动与收割。

- in-flight 数量 MUST ≤ `MAX_INFLIGHT`（默认 4）。名额满时第 6 步直接跳过，不阻塞。
- `MAX_TICK_BATCH`（默认 16）限制的是**单轮启动多少条**，不是单轮跑完多少条。
- `Tick` 与 handler MUST 能并发推进；实现 MUST 用锁或不可变快照保护 in-flight 表，MUST NOT 让 handler 直接改 `Tick` 正在遍历的集合。
- **MUST NOT 并发调用 `Tick`。** 两个线程同时 Tick 会同时看到"无 result、无 claim"，然后各写一次，破坏 claim 的独占语义。这条约束无法在运行时便宜地检测，只能写成规则并由实现声明。
- `Tick` 的单次工作量 MUST 有界：至多启动 `MAX_TICK_BATCH` 条、至多收割当前 in-flight 数。

**为什么必须这样设计。** 如果 `Tick` 同步跑完 handler 再返回，会同时坏掉三件事：心跳（§6）在 handler 执行期间不更新，慢 handler 把端点自己判死；取消（§11.1）只在 Tick 开头检查，而 handler 运行期间根本进不了 Tick，取消永远送不到；claim 租约（§8.1）无法续约，任何超过 `CLAIM_WINDOW` 的 handler 都会被别的循环接管、并行执行两次。这三件事不是三个独立的 bug，是同一个设计错误的三个症状：**把"推进队列"和"执行任务"绑在同一根线程上**。

### 13.2 单条命令的启动与收割

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
- 收割路径 MUST 用 `try/catch` 包住每一步 I/O，MUST NOT 让一个 `IOException` 穿透 `Tick` 把整个服务循环打死。
- 但**写 `results/` 失败是一个例外：此时 MUST NOT 归档命令、MUST NOT 删 claim**。命令留在 `commands/`、claim 留到租约到期，下一轮按 `attempts + 1` 重来。先归档再删 claim 会让这条命令连证据一起消失——那才是真正不可恢复的丢件（§1 不变式 3）。宁可重做一次，不可无凭据地丢掉。

**HANDLER_TIMEOUT 的默认值是命令的剩余 deadline。** 也就是说，一条命令从投递到产出终态，最迟不超过它自己的 `deadlineUtc`——这与 §7.1 的语义一致：deadline 既是"最晚开始执行"，也是"最晚可以有结果"。实现 MAY 配置更短的 handler 时限。

---

## 14. 投递方语义

### 14.1 投递

- MUST 原子写（§3.3），MUST 不覆盖。
- MUST 在写入前完成 §7.3 的三项检查。

### 14.2 等待终态

调用方 MUST 按下述顺序判定，且 MUST 在 `timeout` 内完成：

1. 轮询 `results/<requestId>.json`；出现即返回。
2. 超时仍无结果 → 返回合成的 `unreachable`。

- 轮询间隔 SHOULD 从 10 ms 起，指数退避到 200 ms 上限。
- MUST NOT 无限等待。
- MUST NOT 在等待期间删除或修改命令文件（它属于服务者的处理流程）。

### 14.3 互调死锁（实现约束）

**调用方**（`Call`/`CallAsync` 的使用者）MUST 使用非阻塞等待（异步/让出线程），MUST NOT 在驱动 `Tick` 的线程上同步阻塞等待另一个端点。同步阻塞只在没有 `Tick` 循环的短命进程（CLI）上安全。

**handler 内部 MUST NOT 同步等待另一个端点。** 这条规则把调用链深度限制为 1，从结构上排除 A→B→C→A 环。

§13.1 之后 `Tick` 本身不再被 handler 阻塞，所以这条规则的理由变了——但结论没变。现在的风险不是"Tick 停了"，而是**在途名额被等待中的 handler 占满**：`MAX_INFLIGHT`（默认 4）是个很小的数，四个 handler 各自阻塞等待另一个端点，这个端点就再也启动不了第五条命令。它不会死锁整个进程，但会让端点对外表现为"活着但什么都不做"——比崩溃更难诊断。

异步 handler 在 `await` 另一个端点时同样占着名额，只是不占线程。需要长链路协作的宿主 MUST 自行调大 `MAX_INFLIGHT`，或者把协作拆成两次独立投递。

---

## 15. 原因码总表

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

---

## 16. 参数与默认值

| 参数 | 默认值 | 可配置 | 约束 |
|---|---|---|---|
| `PROTOCOL_VERSION` | `1` | MUST NOT | 提升版本需要新文档 |
| `HEARTBEAT_ALIVE` | 5 s | MAY | 必须 < `HEARTBEAT_DEAD` |
| `HEARTBEAT_DEAD` | 15 s | MAY | |
| `DEFAULT_DEADLINE` | 10 s | MAY | 按调用可覆盖（§7.1） |
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

---

## 17. 安全要求

1. **不做鉴权。能写这个目录的进程就是可信的。** `issuedBy` 只用于审计与回信，不是凭据。
2. **`endpointId` 校验是强制的**（§4.1），这是唯一的路径拼接入口。
3. **root MUST NOT 位于多用户共享的临时目录**（`%TEMP%`、`/tmp`）。其他用户能往 `commands/` 投东西，而协议没有鉴权。多用户机器上 root MUST 位于用户私有目录。
4. **root SHOULD NOT 位于云同步目录**（OneDrive / Dropbox / iCloud）。同步客户端会在 rename 前后插入干扰，直接破坏 §3.3 的原子性。
5. **payload 不透明**。Courier MUST NOT 解析或校验 payload 内容；业务校验在 handler 里做。
6. **必须有 DoS 上限**：`MAX_TICK_BATCH`、`MAX_COMMAND_FILE_BYTES`、`MAX_COMMANDS_BACKPRESSURE` 全部生效。
7. **快通道引入文件系统之外的信任边界**。命名管道没有"能写这个目录才可信"的等价机制，同机任何进程都能连那个管道名。声明 `hot` 的端点 MUST 自行校验调用方，或只暴露无需鉴权的只读状态。

---

## 18. 一致性清单

一份实现要声称"符合 Courier v1"，下列每一条都必须是可验证的：

**编码与原子性**
- [ ] 所有写入走"同目录临时文件 → flush → rename"（§3.3）
- [ ] 命令与 claim 用不覆盖语义创建（§3.3）
- [ ] 读 JSON 对 `IOException` 有界重试（§3.4）
- [ ] JSON 忽略未知字段（§3.1）

**标识与版本**
- [ ] `endpointId` 非法时拒绝启动（§4.1）
- [ ] `requestId` 含随机段（§4.2）
- [ ] 版本不匹配时投递方不写任何文件（§4.4）

**路由与所有权**
- [ ] 投递方从不写 `results/`、`claims/`、`heartbeat`（§2）
- [ ] 端点退出不删目录（§2、§6）

**生命周期**
- [ ] 每条命令最终产出 results 或 deadletter（§1 不变式 3）
- [ ] claim 过期后先查 results 再重做（§8、§13）
- [ ] 续约失败（租约被接管）时放弃执行且不写 `results/`（§8.1）
- [ ] `attempts` 记在 claim 而非命令里（§8）
- [ ] 超过 `deadlineUtc` 的命令不执行（§7.1）
- [ ] 超限命令先看大小再读（§7.2）
- [ ] 归档顺序是"先写结果，后移命令"（§12.1）
- [ ] `results/` 不被第三方搬动，GC 先写墓碑（§12.2）

**语义**
- [ ] `rejected` 与 `failed` 严格分开（§9.1）
- [ ] `unreachable` 不落盘（§9.2）
- [ ] 只使用 §15 的原因码
- [ ] `Tick` 不并发（§13）

**并发模型**
- [ ] `Tick` 不等 handler，慢 handler 期间心跳照常更新（§6、§13.1）
- [ ] handler 收到 `CancellationToken`，取消能在 handler 执行期间送达（§11.1）
- [ ] 在途 handler 续约 claim，租约不假过期（§8.1）
- [ ] 在途数量受 `MAX_INFLIGHT` 限制（§13.1）
- [ ] 超过 `HANDLER_TIMEOUT` 立即写 `failed/handler-timeout`，不等 handler 返回（§13.2）
- [ ] 收割路径的 I/O 失败不穿透 `Tick`（§13.2）
- [ ] 第一次 `Tick` 之后再注册能力被拒绝（§5）

**互操作**
- [ ] 与另一份实现双向对拍：命令、响应、死信、取消、进度格式完全一致

---

## 19. 非目标（明确不做，实现 MUST NOT 自行扩展）

下列能力不属于 Courier v1。实现 MUST NOT 以"可选"名义引入。

1. 网络传输。跨机器请挂载共享目录。
2. 多写者事务、分布式一致性。
3. 高频数据面（> 约 100 msg/s）。数据面由端点自行声明的快通道承担（§5.2），Courier 只记录声明。
4. 鉴权。能写这个目录的进程即视为可信（§17 第 1 条）。
5. payload 的 schema 校验。payload 对 Courier 是不透明字符串。
6. MCP 适配层。与 MCP 是替代关系。
7. 跨语言 SDK 统一化。正式实现只有 C#，其余语言走 CLI 或 stdio 助手。
8. 绑定任何宿主框架类型。
9. 传输抽象层（如 `IMessageTransport`）。
10. 消息过滤与订阅。收件人在写入时由目录确定（§2 不变式 2）。
11. 注册中心与服务发现协议。`endpoints/` 目录即名单。

