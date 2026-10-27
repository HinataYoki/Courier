<h1 align="center">Courier</h1>

<p align="center"><strong>让两个本地进程，通过一个目录互相调用。</strong></p>

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-2ea44f?style=flat-square" alt="License"></a>
  <img src="https://img.shields.io/badge/.NET-10-512BD4?style=flat-square" alt=".NET">
  <img src="https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-lightgrey?style=flat-square" alt="Platform">
</p>

---

Courier 是一套**用文件系统当信道的本地进程间调用协议**，外加一份开箱即用的 C# 实现和一个 `courier` 命令行工具。

一端把请求写成文件，另一端按周期扫描、执行、把结果写回同一个目录。没有端口、没有 socket、没有常驻服务端、没有防火墙弹窗。任意一端崩溃重启，未完成的消息仍然在磁盘上。

```
进程 A（调用方）                 目录（root）                   进程 B（你的软件）
courier call  ──command.json──▶  endpoints/app-a/commands/  ──▶  CourierNode 的 handler
              ◀─response.json──  endpoints/app-a/results/   ◀──
                                  endpoints/app-a/state/         ← 只发布，不等待回执
```

| 适合 | 不适合 |
|---|---|
| 桌面软件 / 编辑器插件 / 本地 Agent 之间的控制面调用 | 高频数据面（音视频、每帧同步、大块二进制） |
| 需要「调用历史可离线取证」的自动化 | 跨机器通信（把目录挂成共享盘即可，协议本身不做网络） |
| 被调用方是 GUI 进程，不方便长连接或开端口 | 需要 exactly-once 副作用的场景（投递是至少一次） |

> 名字取自「信使」。

---

## 目录

- [快速开始](#快速开始)
- [工作原理](#工作原理)
- [命令](#命令)
- [嵌入到你的软件](#嵌入到你的软件)
- [目录与配置](#目录与配置)
- [退出码与错误](#退出码与错误)
- [常见问题](#常见问题)
- [性能与适用边界](#性能与适用边界)
- [许可](#许可)

---

## 快速开始

需要 .NET 10 SDK。下面两条命令都不必先构建，`dotnet run` 会自己编译。

**终端 1 —— 起一个演示端点**（仓库自带的示例宿主，root 就是当前目录下的 `.courier`）：

```bash
dotnet run --project tools/e2e -c Release -- serve .courier app-a
```

它打印 `READY .courier` 之后开始按 20 ms 节奏服务，注册了三个动作：`document/open`（打开文档）、`demo/echo`（回显）、`demo/slow`（睡 payload 毫秒）。

**终端 2 —— 在同一个目录（或它的任意子目录）里调用它**：

```bash
dotnet run --project src/Courier.Cli -c Release -- endpoints
# app-a    (e2e host)  alive, heartbeat 0.2s ago

dotnet run --project src/Courier.Cli -c Release -- call app-a demo/echo --payload "hello world" --json
# {"status":"ok","requestId":"20261027T152452Z-e230613a","endpointId":"app-a","channel":"demo","action":"echo","durationMs":61,"payload":"hello world"}
```

`requestId` 同时就是证据文件名：`.courier/endpoints/app-a/results/20261027T152452Z-e230613a.json`。打开它，能看到这次调用完整的终态记录。

**想少打几个字**：构建一次 CLI，把产物放进 PATH，后文的 `courier` 就指它。

```bash
dotnet build src/Courier.Cli/Courier.Cli.csproj -c Release
# 产物：src/Courier.Cli/bin/Release/net10.0/courier.exe（Linux / macOS 无 .exe）
courier endpoints
```

---

## 工作原理

root 下面一个端点一个目录，各管各的：

```
.courier/
├── endpoints/app-a/
│   ├── endpoint.json     能力表：我是谁、能做什么（软件没运行时也能读）
│   ├── heartbeat         心跳时间戳（判断活着 / 卡住 / 已退出）
│   ├── commands/         待处理请求（调用方写，服务者读）
│   ├── claims/           正在处理的请求的租约
│   ├── results/          终态证据（每条命令一个文件；gc.json 是回收墓碑）
│   ├── deadletter/       重试耗尽或格式非法的请求
│   ├── cancels/          取消请求
│   ├── progress/         进度快照
│   └── state/            状态发布的最新值
└── archive/              已归档的命令与死信（<日期>/<端点>/…）
```

**所有跨进程通信都只发生在这套目录里**，所以 `ls`、`cat`、`git diff` 就是调试器：某条命令卡在哪、谁认领了它、上次执行返回了什么，都在磁盘上。

---

## 命令

| 命令 | 作用 |
|---|---|
| `courier endpoints` | 列出 root 下所有端点与活性 |
| `courier describe <id>` | 看一个端点声明了哪些动作与状态 |
| `courier call <id> <channel>/<action>` | 调用一个动作，等结果 |
| `courier state <id> <name>` | 读端点发布的状态最新值 |
| `courier ps` | 看现在有哪些请求正在被处理 |
| `courier progress <id> <requestId>` | 看一条在途命令的进度 |
| `courier cancel <id> <requestId>` | 请求取消一条命令 |
| `courier gc --older-than <时长>` | 清理过期的结果文件 |
| `courier version` | 版本号与协议版本 |
| `courier --help` | 完整用法 |

通用选项：`--json`（一行紧凑 JSON，便于机器消费）、`--root PATH`（指定协议根目录）、`--root-name NAME`（改零配置发现时的目录名，默认 `.courier`）。每个命令只接受它自己列出的选项——不适用的选项会报错，而不是被静默忽略。

### 看看有什么、能做什么

```bash
$ courier endpoints
app-a    (e2e host)  alive, heartbeat 0.2s ago

$ courier describe app-a
demo/echo  回显 payload
demo/slow  慢任务：payload 是毫秒数
document/open  打开文档
state clock  服务循环最后一次 Tick 的 UTC 时刻
```

`describe` 只读 `endpoint.json`，所以**软件没在运行也能查能力**——这也是「先发现、后调用」的兑现方式。`--json` 会把动作、状态、可选的能力自述（`capabilities`）和 `metadata` 一起输出。

### 调用

```bash
$ courier call app-a document/open --payload '{"path":"Main.doc"}'
{"opened":true,"payload":"{\"path\":\"Main.doc\"}"}

$ courier call app-a demo/slow --payload 300 --timeout 5s --json
{"status":"ok","requestId":"20261027T152452Z-...","endpointId":"app-a","channel":"demo","action":"slow","durationMs":370,"payload":"slept:300"}
```

不带 `--json` 时，成功只把 handler 返回的 payload 打到 stdout——可以直接嵌进 shell 管道。失败与超时走 stderr，退出码说明是哪一种。

`--payload` 是不透明字符串，Courier 不解释它。值以 `-` 开头时用等号写法：`--payload=-x`。

**超时**：`--timeout` 默认 10s，上限 300s。它同时写进命令的 `deadlineUtc`，所以超时有两种可观察结果——端点活着但 handler 没在期限内跑完，得到 `failed / handler-timeout`（退出码 2）；端点根本没响应，调用方自己等满，得到 `unreachable`（退出码 3）。两者都表示「这次没拿到结果」，不表示命令一定没被执行：端点晚一点处理完，结果仍然会出现在 `results/` 里。响应要么完整存在，要么不存在，不会落半条。

### 状态发布

```bash
$ courier state app-a clock --json
{"endpointId":"app-a","name":"clock","published":true,"updatedAtUtc":"2026-10-27T15:14:35.8159801Z","revision":11,"payload":"2026-10-27T15:14:35Z"}

$ courier state app-a nope --json
{"endpointId":"app-a","name":"nope","published":false}
```

状态是**覆盖写的最新值**，没有回执、不产生历史。`published:false` 只表示「还没发布过」，不是错误（退出码 0）；但端点根本不存在会报 `endpoint-not-found`，两者刻意区分。`--raw` 输出状态文件的原文。

### 看正在跑什么、取消、进度

```bash
$ courier ps
app-a  20261027T151602Z-9cb4c1e9  owner=app-a  lease 8.9s left  attempt 1

$ courier cancel app-a 20261027T151602Z-9cb4c1e9 --reason "user aborted"
cancel requested for 20261027T151602Z-9cb4c1e9 on app-a (best effort; the endpoint reads it on its next tick)

$ courier progress app-a 20261027T151602Z-9cb4c1e9 --json
{"endpointId":"app-a","requestId":"20261027T151602Z-9cb4c1e9","published":true,"percent":40,"message":"第 4 步"}
```

- `ps` 读的是租约：`lease 8.9s left` 会走到 `lease expired`，那时下个 Tick 就会有别的执行者接管（所以同一条命令**可能执行两次**，见下文）。
- `cancel` 是**尽力而为**：它只是把取消意图放进 `cancels/`，目标端点下个 Tick 读到后把信号交给 handler 的 `CancellationToken`。handler 完全可以忽略它——所以「请求已投递」不等于「命令已停止」，退出码只表示前者。
- `progress` 没有文件时输出 `published:false` 并退出 0：没写进度是合法状态，不是错误。

### 清理结果

```bash
$ courier gc --older-than 7d --json
{"removed":12,"endpoints":2,"failed":0,"cutoffUtc":"2026-10-20T15:15:13Z","results":[{"endpointId":"app-a","removed":12,"cutoffUtc":"2026-10-20T15:15:13Z","removedBefore":"20261020T041500Z-9f8e7d6c","generatedAtUtc":"2026-10-20T15:15:13Z"}]}
```

（数字随实际回收量变化；这里只是形状示例。）

只删**已完成**的响应，在途命令不动。删之前先写 `results/gc.json` 墓碑，所以之后调用方看到「结果文件不存在」时能区分「被回收了」（`reclaimed:true`）和「从来没处理过」。不带端点 id 就清理 root 下全部端点；某个端点失败不会中断其余端点，但退出码会是 2。

---

## 嵌入到你的软件

引用 `src/Courier`（或把源码拖进宿主工程），注册几个 handler，然后按你自己的节奏调 `Tick()`：

```csharp
using Courier;

using (CourierNode node = CourierNode.Open(".courier", "my-tool", "我的桌面工具"))
{
    // 同步 handler：payload 进，字符串出。
    node.Handle("document", "open", "打开文档", payload => "{\"opened\":true}");

    // 带上下文的异步 handler：能拿到 requestId、attempt、调用方标识，也能响应取消。
    node.HandleAsync("job", "run", "跑一个长任务", async (CourierCall call, CancellationToken token) =>
    {
        for (int step = 0; step < 10; step++)
        {
            token.ThrowIfCancellationRequested();
            node.ReportProgress(call.RequestId, step * 10, "第 " + step + " 步");
            await Task.Delay(500, token);
        }

        return "done";
    });

    node.DeclareState("clock", "最后一次 Tick 的 UTC 时刻");

    while (true)
    {
        node.Tick();                                              // 收命令、跑收割、写心跳
        node.PublishState("clock", "\"" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") + "\"");
        Thread.Sleep(50);
    }
}
```

几条要点：

- **`Tick()` 是宿主驱动的**，库自己不起线程。Tick 不等 handler 跑完（非阻塞泵），所以慢任务不会把端点自己拖成 `dead`；建议 20–200 ms 一次。小工具里可以直接 `while (true) { node.Tick(); Thread.Sleep(50); }`，WinForms / WPF / Unity 里挂到已有的定时器或 `Update` 上更自然。
- **handler 抛异常**会被记为 `failed / handler-error`，异常不会穿透到宿主。业务上的「不成功」更建议放在 payload 里（例如返回 `{"ok":false,"reason":"..."}`），这样 `status` 仍是 `ok`，调用方拿到的是数据而不是错误。
- **投递是至少一次**。服务者崩溃、租约过期被接管，都可能让同一条命令执行两次。需要恰好一次的副作用，请用 `call.RequestId` 自己做去重——它就是结果文件名，天然稳定。
- **`CourierNode` 也能当调用方**：`node.Call(...)` / `node.CallAsync(...)` / `node.Endpoints()`。两端对等，没有客户端/服务端角色之分。
- **源码兼容**：库可在 `net10.0` 与 `netstandard2.1`（C# 9）下编译，零第三方依赖，不引用宿主框架 API。整份 `src/Courier/` 可以直接放进 Unity 之类的宿主工程。

### 常用开关

```csharp
new CourierNodeOptions
{
    AcquireHostLock = true,        // 一个端点目录只允许一个服务者（推荐：GUI 软件可能被双击两次）
    PublishRootRegistry = true,    // 维护 <root>/endpoint.json 的端点摘要，供不能列目录的宿主快速发现
    ArchiveRetention = TimeSpan.FromDays(30),   // 归档保留期，默认 14 天；0 = 不清理
    DeadletterRetention = TimeSpan.FromDays(30),// 在线死信保留期，默认 14 天
    EnableHotChannel = false       // 关掉同机内存叫醒旁路，只走纯文件通道（默认开）
}
```

默认值刻意偏保守：`host.lock` 与 root 注册表默认关闭（不影响既有宿主），归档与死信清理默认开启（14 天），热通道默认开启但只在 Windows 生效、失败自动退回文件通道。

---

## 目录与配置

**root 怎么找**：`--root PATH` 直接指定；否则从当前目录向上找名为 `.courier` 的目录（`--root-name` 或环境变量 `COURIER_ROOT_NAME` 可换名字）。所以只要在一个 root 的任意子目录里，命令都能用。

**改目录名**：在 root 下放一个 `courier.layout.json`，把目录名换成你软件里更合适的名字，不用重新编译：

```json
{
  "version": 1,
  "endpoints": "nodes",
  "commands": "inbox",
  "results": "outbox",
  "state": "snapshots",
  "endpointFile": "self.json",
  "heartbeatFile": "pulse"
}
```

没写的键保持默认。`version` 目前只接受 1。**布局文件写坏了会直接报 `layout-invalid`（退出码 4），不会静默退回默认布局**——否则「名字写错」会表现成「目录是空的」，比报错难查得多。

---

## 退出码与错误

| 退出码 | 含义 |
|---|---|
| `0` | 成功（`status=ok`，或查询类命令正常返回） |
| `1` | `rejected`：命令被拒，没被执行（未知动作、格式非法、队列满…） |
| `2` | `failed`：执行了但失败（handler 抛异常、超时、被取消） |
| `3` | `unreachable`：没等到结果（端点没在跑，或超过 `--timeout`） |
| `4` | 用法 / 环境错误：参数不合法、找不到 root、布局文件读不懂、端点根本没注册 |

`--json` 模式下错误是一行 JSON 到 stderr：

```json
{"error":"endpoint-not-found","detail":"no endpoint named \"nope\" under ...\\endpoints (it has never started, or its endpoint.json is gone)"}
```

| `error` | 什么时候出现 |
|---|---|
| `invalid-argument` | 参数、选项组合不合法 |
| `invalid-endpoint-id` / `invalid-request-id` / `invalid-state-name` | 标识符不符合协议正则 |
| `invalid-duration` | `--timeout` / `--older-than` 不是合法时长 |
| `endpoint-not-found` | 该端点在 root 下不存在 |
| `root-not-found` | 指定的 root 目录不存在 |
| `layout-invalid` | `courier.layout.json` 解析失败 |
| `gc-failed` / `cancel-failed` | 清理或投递取消时磁盘写失败 |
| `unknown-command` / `internal-error` | 未知命令 / 未预期异常 |

---

## 常见问题

**端点显示 `stale` 或 `dead`。**

`alive` 是心跳 5 秒内、`stale` 是 15 秒内、`dead` 是超过 15 秒。`stale` 常见于宿主把 `Tick()` 卡住了（例如在 UI 线程上同步等结果）；`dead` 说明进程已退出或不再 Tick。心跳只是活性提示，不是权威——能力表 `endpoint.json` 一直可读。

**`call` 等了很久然后 `unreachable`。**

三种可能：端点没在运行；`Tick()` 没被驱动；`--timeout` 比 handler 的实际耗时短。先 `courier endpoints` 看活性，再 `courier ps` 看它是不是正卡在别的任务上。

**多个实例打开同一个端点。**

默认允许（测试、把 `CourierNode` 当无状态客户端时是合理的），但两个服务者会重复执行命令。宿主可能被启动两次时，请设 `AcquireHostLock = true`：第二个实例会在 `Open` 时抛 `CourierHostLockedException`，而不是安静地互相覆盖。

**结果文件不见了。**

先看同目录的 `gc.json`：`removedBefore` 之前的 requestId 是被 `courier gc` 回收了，`call --json` 会给出 `"reclaimed":true`。这不是错误：读方应该能把「已回收」与「从未处理」分开。

**能不能跨机器？**

不能。协议只认目录。要跨机器，把 root 挂成共享盘——但要注意共享文件系统的原子 rename 语义未必满足要求，只在本地文件系统上验证过。

---

## 性能与适用边界

文件协议比内存通道慢一到三个数量级，这是结构性的：每次通信都要过 VFS、要能被 `ls` 看见。本机实测（Windows / .NET 10 / 200 字节载荷，两个真实进程）：

| 载体 | 单次往返 |
|---|---|
| 文件：临时文件 + rename + 轮询 + delete | 1243 µs |
| 命名管道 / Unix domain socket | 8.9 µs |
| 文件映射环形缓冲（同进程空转） | 1.3 µs |

`courier call` 的端到端耗时主要在进程启动上：本机 15 次采样 `durationMs` 中位约 50 ms（含起 CLI 进程、投递、等待、读结果）。**所以它面向控制面：命令、查询、任务投递，不是数据面。**

如果确实需要更低的单次延迟，实现里有一条可选的同机内存「叫醒」旁路：命令仍然先落盘、终态仍然写 `results/`，旁路只负责把对方从轮询里叫醒，失败就自动退回文件通道。它不改变任何可观察行为，Windows 之外不生效，也可以用 `EnableHotChannel = false` 关掉。

---

## 许可

MIT，见 [LICENSE](LICENSE)。
