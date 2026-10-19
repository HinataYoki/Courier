# Courier

**用文件系统作为进程间信道的最小可靠基座。**

两端各自打开同一个目录：一端把消息写成文件，另一端按周期扫描、执行、把结果写回。没有端口、没有 socket、没有常驻 server、没有防火墙弹窗。任意一端崩溃重启，未完成的消息仍在磁盘上。

```
进程 A                        文件系统                      进程 B
CourierNode  ──command.json──▶  commands/  ──▶  CourierNode
             ◀─response.json──  results/   ◀──
```

> 名字取自"信使"。与商业通知服务 Courier.com 无关。

**当前状态：M0 已完成（协议规范定稿），M1 已完成（C# 实现 + `courier` CLI 双进程互调跑通）。** 本仓库交付 `SPEC.md` 协议规范、`src/Courier/` 的协议实现、`src/Courier.Cli/` 的 `courier` CLI、`tests/Courier.Tests/` 的测试套件、`tools/bench/` 的载体基准、`tools/e2e/` 的双进程端到端工具和 `tools/compat/` 的宿主兼容性验证工程。产品源码可在 `net10.0` 与 `netstandard2.1` 两个目标下编译，可整份拖进 Unity 等宿主工程。

---

## 文件地图

| 文件 | 是什么 | 什么时候读 |
|---|---|---|
| [SPEC.md](SPEC.md) | **协议规范 v1**，唯一有约束力 | 要写实现、要判断某行为是否合规 |
| [src/Courier](src/Courier) | C# 协议实现，单一编译单元、零依赖 | 要看实现或嵌进宿主 |
| [src/Courier.Cli](src/Courier.Cli) | `courier` 命令行工具 | 要从任何语言调用端点 |
| [tests/Courier.Tests](tests/Courier.Tests) | xUnit 测试套件 | 改完实现要验证时 |
| [tools/bench](tools/bench) | 载体往返延迟基准（跨进程） | 质疑"为什么文件协议慢"时 |
| [tools/e2e](tools/e2e) | 双进程端到端验证工具（`serve` / `call`） | 要证明两个真实进程能互调时 |
| [tools/compat](tools/compat) | 宿主兼容性验证：`netstandard2.1` 编译同一份产品源码 | 改完产品库、准备嵌进 Unity 等宿主时 |

冲突时以 SPEC.md 为准。

---

## 解决什么问题

编辑器插件、命令行工具、桌面应用、本地 AI Agent 之间需要双向调用时，业界默认答案是"开一个 socket"或"起一个 MCP server"。这个默认答案在以下场景代价过高：

| 问题 | socket / server 方案 | Courier |
|---|---|---|
| 需要额外进程常驻 | 是 | 否，只需要一个目录 |
| 端口被占用 / 权限不足 | 常见失败 | 不存在该概念 |
| 接收端未启动 | 连接失败，需重试协商 | 消息留在目录里，启动后自然消费 |
| 发送端崩溃 | 连接断开，请求丢失 | 消息已落盘，重启后仍在 |
| 事后取证 | 需要抓包或额外日志 | 目录本身就是审计日志 |
| 跨语言 | 每端都要有对应语言的协议栈 | 两端只约定文件格式，一端甚至可以是 CLI |

代价是延迟。默认轮询 200 ms，**不为高频数据面设计**。它面向控制面：命令、查询、任务投递。

---

## 典型形态：给 Agent 提供一整套能力

```
┌─────────────┐   内嵌 CourierNode    ┌──────────────┐   CLI 调用   ┌───────┐
│   你的软件   │ ───────────────────▶ │  .courier/   │ ◀──────────  │ Agent │
│              │    Handle(...)       │  （目录）     │   读写文件    │       │
└─────────────┘                       └──────────────┘              └───────┘
```

```bash
$ courier endpoints
app-a    (desktop-tool)  alive, heartbeat 1.2s ago

$ courier describe app-a --json
{"endpointId":"app-a","actions":[{"channel":"document","action":"open","summary":"打开文档"}]}

$ courier call app-a document/open --payload '{"path":"Main.doc"}'
{"status":"ok","payload":{"path":"Main.doc","openedAtUtc":"..."}}

$ courier ps
app-a  20261020T031500Z-1a2b3c4d  owner=app-a  claimed 0.4s ago  attempt 1
```

| | 内嵌 MCP server | Courier + CLI |
|---|---|---|
| 软件侧改造量 | 协议实现、传输层、会话管理 | 一个 `Handle` 调用 + 一个 `Tick` 调用 |
| Agent 侧接入 | 每个客户端都要配 MCP 连接 | 任何能执行命令的 Agent |
| 软件未运行时 | 连接失败，模糊错误 | `endpoints` 明确报告无活端点 |
| 调用历史 | 需要额外日志 | `archive/` 即完整审计 |

**与 MCP 是替代关系，不是叠加关系。** MCP 把软件变成 server；Courier 让软件还是一个软件。

---

## 七条设计目标

1. **两端对等**。没有客户端/服务端角色差异，同一个 `CourierNode` 既能被调用也能发起调用。
2. **落盘即承诺**。出现在 `commands/` 的命令必须产出终态证据：响应或死信。不允许静默丢失。
3. **可观察**。不启动任何工具，用 `ls`、`cat` 就能看懂系统在做什么。
4. **零依赖**。不引用第三方包，不引用宿主框架 API，不带 JSON 库。
5. **单一编译单元**。一份源码可直接放进宿主工程，无条件编译。
6. **对话从发现开始**。先列端点、再看能力、后调用，无需预置知识。
7. **协议优先于实现**。产品是磁盘上的协议，不是某个语言的库。

完整非目标清单见 [SPEC.md §19](SPEC.md)。

---

## 三档接入，只有一档叫"嵌入"

| 档 | 形态 | 谁用 | 代码量 | 需要嵌进宿主 |
|---|---|---|---|---|
| **C** | 源码移植 | 被调用方（Host） | 300–600 行 | 是（源码须能在宿主的 API 兼容级别下编译，见 [tools/compat](tools/compat)） |
| **B** | stdio 助手子进程 | 被调用方（Host） | 约 50–150 行 | 否 |
| **A** | CLI 子进程 | 调用方、观察方 | 0 行 | 否 |

三档的能力差只有一项：C 档的 handler 能直接读写宿主的活对象。A/B 两档的 handler 在助手进程里，碰不到宿主内存。

**"不支持某语言"实际只意味 handler 不能直接碰宿主内存，不意味不能用。** 非 C# 语言走 A/B 两档即可：端点由别的进程承载，调用方只需会读写文件。

---

## 实测：为什么文件协议不该做数据面

本机两个真实进程 ping-pong，200 字节载荷（Windows / .NET 10 / Release）：

| 载体 | 单次往返 | 吞吐 |
|---|---|---|
| 文件：临时文件 + rename + 轮询 + delete | **1243 µs** | **805 往返/s** |
| 命名管道（Unix 上映射为 UDS） | 8.9 µs | 113k 往返/s |
| 文件映射环形缓冲 | 1.3 µs | 793k 往返/s |
| 混合：映射放数据 + 管道敲门铃 | 8.8 µs | 114k 往返/s |

多次运行的波动在 ±10% 以内，量级关系稳定。

**文件协议比管道慢约 150 倍，比映射慢约 1000 倍。** 这是"每次通信都要过 VFS、要落盘、要能被 `ls` 看见"的结构性代价，不是实现没优化。

因此：**Courier 负责"可靠地不丢"，快通道负责"快"，两者用一个可选字段对接，而不是用一个抽象层对接。** 数据面走命名管道或共享内存，Courier 只在 `endpoint.json` 里用可选 `hot` 字段声明它存在——**只声明，不抽象**，协议不解释这个字段的内容，也不会替任何人转发数据。

复现：`cd tools/bench && dotnet run -c Release -- file`（`file` / `mmap` / `pipe` / `hybrid`）。

产品源码的宿主兼容性另有一个编译验证：`dotnet build tools/compat/Courier.Compat.csproj -c Release`，它用 `netstandard2.1` + `LangVersion 9.0` 把 `src/Courier/*.cs` 再编一遍。

---

## 路线图

| 里程碑 | 内容 |
|---|---|
| M0 | SPEC.md 定稿（本文档已完成） |
| M1 | C# `CourierNode` 最小可用：两个进程互相调用成功（**已完成**） |
| M2 | 崩溃恢复：claim 过期、deadletter、过期命令拒绝 |
| M2.5 | stdio 助手：非 C# 宿主完成一次往返 |
| M3 | `courier` CLI（已随 M1 交付 `endpoints`/`describe`/`call`/`ps`/`version`） |
| M4 | 归档与 GC |
| M5 | 两个 C# 软件互调完整示例 |
| M6 | 任意语言 Host 演示（Python） |
| M7 | Python C 档实现，与 C# 双向对拍 |
| M8 | 体积预算守卫 |
| M9 | 三方（含环）互调压测 |
| M10 | 发现语义验证 |
| M11 | 缺口定稿 |
| M12 | 慢 handler 压测 |
| M13 | 快通道声明与回落 |

**现在处于 M1 之后。** 下一步是 M2（真进程崩溃恢复验证）与 M4（归档与 GC）。

---

## 许可

MIT，见 [LICENSE](LICENSE)。
