# CourierStream：同机高频只读数据面

> 状态：实现说明。`CourierStream` 不改变 Courier 文件协议的 commands/results/state 语义，
> 也不属于协议层的快通道。它是实现私有、同机、单写者、只读消费的数据面。

## 1. 定位

Courier 的文件通道负责“可靠控制面”：

- 命令必须落盘，终态必须能在 `results/` 中取证；
- 进程崩溃后消息仍在磁盘上；
- 任意语言都能直接读写目录。

文件通道不适合每帧 trace、日志流、采样数据这类高频只读内容。`CourierStream` 为此提供：

- 宿主写入有界内存环；
- 同机读取端直接消费，不逐条写文件；
- 环满丢最旧记录，写入方永不阻塞；
- 记录带单调序号、UTC ticks 与 CRC32；
- 连接时返回最旧序号、写入 head、累计丢弃数，读方可判断是否漏读。

它不替代 `commands/` 与 `results/`。控制命令仍必须走文件协议；`CourierStream` 只承载可丢弃的观察数据。

## 2. 平台与传输

| 平台 | 默认传输 | 回退 |
|---|---|---|
| Windows | `mmap-ring`：命名共享内存 + 命名自动复位事件 | mmap 创建失败时回退 `unix-socket` |
| Linux | `unix-socket` | 无 |
| macOS | `unix-socket` | 无 |

`mmap-ring` 使用 pagefile-backed 命名节，不使用 file-backed 文件映射。共享内存只放环，
命名事件只负责唤醒读取端。

Unix Domain Socket 路径超过保守上限时，socket 会放到用户临时目录下的会话短目录，
实际目录记录在私有 descriptor 中；项目根路径很深时不需要改项目路径。

## 3. 目录与描述

每个流一个实现私有目录：

```text
<root>/endpoints/<endpointId>/streams/<streamName>/
├── descriptor.json
└── <sessionId>.sock        # 仅 unix-socket 且路径足够短时
```

`endpoint.json` 不增加 `stream` / `streams` / `hot` 字段。删除 `streams/` 只影响观察数据，
不影响 Courier 文件协议的任何命令、状态或终态证据。

### 3.1 mmap-ring descriptor

```json
{
  "version": 1,
  "transport": "mmap-ring",
  "stream": "trace",
  "sessionId": "8f2c41ab0d7e4b65a19c6d92f3e8a701",
  "hostPid": 41232,
  "capacity": 4096,
  "maxRecordBytes": 16384,
  "mapName": "Local\\CourierStream.<sessionId>.trace.map",
  "dataEventName": "Local\\CourierStream.<sessionId>.trace.data"
}
```

### 3.2 unix-socket descriptor

```json
{
  "version": 1,
  "transport": "unix-socket",
  "stream": "trace",
  "sessionId": "8f2c41ab0d7e4b65a19c6d92f3e8a701",
  "hostPid": 41232,
  "capacity": 4096,
  "maxRecordBytes": 16384,
  "socketFile": "8f2c41ab0d7e4b65a19c6d92f3e8a701.sock"
}
```

路径过长时额外出现：

```json
{
  "socketDirectory": "/tmp/courier-stream-8f2c41ab0d7e4b65a19c6d92f3e8a701"
}
```

## 4. 使用

宿主写入：

```csharp
using Courier;

using CourierNode node = CourierNode.Open(root, "my-editor");
using CourierStreamWriter writer = node.OpenStream(
    "trace",
    new CourierStreamOptions { Capacity = 4096, MaxRecordBytes = 16 * 1024 });

writer.TryWrite(Encoding.UTF8.GetBytes("{\"kind\":\"frame\",\"frame\":42}"));
```

读取端：

```csharp
using CourierStreamReader reader =
    CourierStreamReader.Open(root, "my-editor", "trace", sinceSequence: 100);

while (reader.TryRead(1000, out CourierStreamRecord record))
{
    // record.Sequence / record.TimestampUtcTicks / record.Payload
}
```

- `TryRead(0, ...)` 只检查当前已有数据。
- `TryRead(timeoutMs > 0, ...)` 最多等待该毫秒数。
- `TryRead(-1, ...)` 一直等待。
- 读取端一旦开始读一帧，就必须读完整帧；帧中途断开会让连接报错，不会把半帧留到下一次解析。
- 读取端断开不会停止宿主写入；重新打开可从当前最旧记录或指定序号继续追赶。

## 5. 语义

### 5.1 序号

- 从 1 开始单调递增。
- `OldestSequence`：连接时仍可使用的最旧记录。
- `NextSequence`：连接时宿主下一次写入将使用的序号。
- `SkippedBefore`：请求的 `sinceSequence` 早于最旧记录时，已经确认错过的数量。
- `DroppedBefore`：连接前宿主因环满丢弃的累计数量。

环满时每条新记录最多淘汰一条最旧记录，不会清空整环，也不会阻塞写入方。

### 5.2 容量

默认单流：

- `Capacity = 2048`
- `MaxRecordBytes = 8192`

容量与单条上限的乘积不得超过 256 MiB。超过上限的写入返回 `false`，不消耗序号。

### 5.3 并发

- 一个流一个写入者；多个线程可并发调用 writer，内部串行化。
- Unix Domain Socket 同时服务一个活动读者，后到的连接会在监听 backlog 中等待。
- Windows mmap-ring 允许多个只读读者各自追赶；写者只维护一个共享 head。

### 5.4 进程退出与陈旧描述

- 正常释放写入端会删除 descriptor、socket 文件与命名对象。
- 崩溃留下的 descriptor 在宿主 PID 已不存在时允许新会话接管。
- 描述仍指向存活进程时不得覆盖。

## 6. 安全边界

与 Courier 文件协议一致：能读 root 的进程视为可信。

- Windows 命名对象使用 `Local\` 会话命名空间，并带随机 `sessionId`。
- Unix socket 文件名含随机 `sessionId`。
- descriptor 是私有会合信息，不是协议能力，不具备鉴权承诺。
- 用户临时目录中的短 socket 目录只用于解决路径长度限制；目录名由随机 session 派生。

## 7. 故障与回退

| 场景 | 行为 |
|---|---|
| Windows mmap 创建失败 | 同一流自动回退 `unix-socket` |
| descriptor 缺失/损坏 | 读取端打开失败 |
| 读取端断开 | 宿主继续保留环并接受下一次连接 |
| 环满 | 丢最旧记录并累计 `DroppedCount` |
| 记录超过上限 | `TryWrite` 返回 `false` |
| 帧 CRC 错误 | 读取端抛 `InvalidDataException` |

`CourierStream` 不参与命令可靠性。控制面的失败、超时、取消、终态证据仍以 Courier 文件协议为准。

## 8. 验证

```bash
dotnet build src/Courier/Courier.csproj -c Release
dotnet build tools/compat/Courier.Compat.csproj -c Release
dotnet test tests/Courier.Tests/Courier.Tests.csproj -c Release --filter FullyQualifiedName~CourierStreamTests
```

测试覆盖：mmap/UDS 端到端、UDS 零超时立即读取、环满丢弃、序号追赶、超时、
断开重连、descriptor 清理、崩溃遗留接管、非法名称与描述。

当前运行验证环境为 Windows；Linux/macOS 的 UDS 路径已通过 `netstandard2.1 + C# 9`
编译，并在 Windows 上用 UDS 后端完成端到端测试，但尚未在真实 Linux/macOS Editor 上冒烟。
