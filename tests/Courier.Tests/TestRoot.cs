using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Courier.Tests;

/// <summary>
/// 每个测试实例自带一个临时协议根：root 之间不共享，Dispose 时递归删除。
/// 路径拼接与夹具读写集中在这里，测试正文只断言协议语义。
/// </summary>
public abstract class TestRoot : IDisposable
{
    /// <summary>SPEC §15 原因码总表。服务者侧的每一个原因码都必须在这里，多一个就说明实现私自加了码。</summary>
    private static readonly string[] SpecReasonTable =
    {
        CourierProtocol.ReasonStale,
        CourierProtocol.ReasonUnknownAction,
        CourierProtocol.ReasonInvalidCommand,
        CourierProtocol.ReasonCancelledBeforeStart,
        CourierProtocol.ReasonBackpressure,
        CourierProtocol.ReasonHandlerError,
        CourierProtocol.ReasonHandlerTimeout,
        CourierProtocol.ReasonCancelled
    };

    protected TestRoot()
    {
        Root = Path.Combine(Path.GetTempPath(), "courier-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>本测试独占的协议根；协议目录（endpoints/、archive/）都在它下面。</summary>
    protected string Root { get; }

    protected CourierNode OpenNode(string endpointId, string? description = null)
    {
        return CourierNode.Open(Root, endpointId, description);
    }

    protected string EndpointsRoot
    {
        get { return Path.Combine(Root, CourierProtocol.EndpointsDirectory); }
    }

    protected string EndpointDirectory(string endpointId)
    {
        return Path.Combine(EndpointsRoot, endpointId);
    }

    protected string EndpointJsonPath(string endpointId)
    {
        return Path.Combine(EndpointDirectory(endpointId), CourierProtocol.EndpointFileName);
    }

    protected string HeartbeatPath(string endpointId)
    {
        return Path.Combine(EndpointDirectory(endpointId), CourierProtocol.HeartbeatFileName);
    }

    protected string CommandsDirectory(string endpointId)
    {
        return Path.Combine(EndpointDirectory(endpointId), CourierProtocol.CommandsDirectory);
    }

    protected string ClaimsDirectory(string endpointId)
    {
        return Path.Combine(EndpointDirectory(endpointId), CourierProtocol.ClaimsDirectory);
    }

    protected string ResultsDirectory(string endpointId)
    {
        return Path.Combine(EndpointDirectory(endpointId), CourierProtocol.ResultsDirectory);
    }

    protected string DeadletterDirectory(string endpointId)
    {
        return Path.Combine(EndpointDirectory(endpointId), CourierProtocol.DeadletterDirectory);
    }

    protected string CancelsDirectory(string endpointId)
    {
        return Path.Combine(EndpointDirectory(endpointId), CourierProtocol.CancelsDirectory);
    }

    protected string CommandsPath(string endpointId, string requestId)
    {
        return Path.Combine(CommandsDirectory(endpointId), requestId + CourierProtocol.JsonExtension);
    }

    protected string ClaimsPath(string endpointId, string requestId)
    {
        return Path.Combine(ClaimsDirectory(endpointId), requestId + CourierProtocol.JsonExtension);
    }

    protected string ResultsPath(string endpointId, string requestId)
    {
        return Path.Combine(ResultsDirectory(endpointId), requestId + CourierProtocol.JsonExtension);
    }

    protected string DeadletterPath(string endpointId, string requestId)
    {
        return Path.Combine(DeadletterDirectory(endpointId), requestId + CourierProtocol.JsonExtension);
    }

    protected string CancelsPath(string endpointId, string requestId)
    {
        return Path.Combine(CancelsDirectory(endpointId), requestId + CourierProtocol.JsonExtension);
    }

    /// <summary>
    /// 按 SPEC §7 手工投递一条命令（不经过 CourierNode，用于精确控制 deadline 与文件内容）。
    /// </summary>
    /// <returns>生成的 requestId，即命令文件名。</returns>
    protected string DeliverCommand(
        string endpointId,
        string channel,
        string action,
        string payload,
        TimeSpan? lifetime = null,
        string issuedBy = "test-caller")
    {
        DateTime issuedAtUtc = DateTime.UtcNow;
        string requestId = CourierProtocol.NewRequestId();
        CourierCommand command = CourierCommand.Create(
            requestId,
            channel,
            action,
            payload,
            issuedBy,
            issuedAtUtc,
            issuedAtUtc + (lifetime ?? TimeSpan.FromSeconds(30)));
        WriteCommandFile(endpointId, requestId, command.ToJson());
        return requestId;
    }

    protected void WriteCommandFile(string endpointId, string fileName, string json)
    {
        Directory.CreateDirectory(CommandsDirectory(endpointId));
        CourierIO.WriteAtomic(CommandsPath(endpointId, fileName), json, true);
    }

    /// <summary>按 SPEC §8.1 写一个租约：expiresAtUtc 是到期时刻，不是认领时刻。</summary>
    /// <param name="endpointId">目标端点。</param>
    /// <param name="requestId">命令标识。</param>
    /// <param name="expiresAtUtc">租约到期时刻。</param>
    /// <param name="attempts">这是第几次认领。</param>
    protected void WriteClaimFile(string endpointId, string requestId, DateTime expiresAtUtc, int attempts)
    {
        Directory.CreateDirectory(ClaimsDirectory(endpointId));
        CourierClaim claim = CourierClaim.Create(endpointId, expiresAtUtc, attempts);
        CourierIO.WriteAtomic(ClaimsPath(endpointId, requestId), claim.ToJson(), true);
    }

    /// <summary>写一个"刚认领、租约还新鲜"的 claim：到期时刻为 now + window。</summary>
    /// <param name="endpointId">目标端点。</param>
    /// <param name="requestId">命令标识。</param>
    /// <param name="attempts">这是第几次认领。</param>
    protected void WriteFreshClaimFile(string endpointId, string requestId, int attempts)
    {
        WriteClaimFile(endpointId, requestId, DateTime.UtcNow + CourierProtocol.ClaimWindow, attempts);
    }

    /// <summary>写一个"持有者已失联"的 claim：到期时刻早于 now，可被下一个 Tick 接管。</summary>
    /// <param name="endpointId">目标端点。</param>
    /// <param name="requestId">命令标识。</param>
    /// <param name="attempts">这是第几次认领。</param>
    protected void WriteExpiredClaimFile(string endpointId, string requestId, int attempts)
    {
        WriteClaimFile(endpointId, requestId, DateTime.UtcNow - TimeSpan.FromSeconds(1), attempts);
    }

    protected void WriteResultFile(string endpointId, string requestId, string status, string payload, string? reason)
    {
        Directory.CreateDirectory(ResultsDirectory(endpointId));
        var response = new CourierResponse(requestId, status, payload, reason, false, DateTime.UtcNow);
        CourierIO.WriteAtomic(ResultsPath(endpointId, requestId), response.ToJson(), true);
    }

    protected void WriteCancelFile(string endpointId, string requestId)
    {
        Directory.CreateDirectory(CancelsDirectory(endpointId));
        string json = new CourierJsonObject()
            .AddString("requestId", requestId)
            .AddString("requestedAtUtc", CourierProtocol.FormatUtc(DateTime.UtcNow))
            .AddString("reason", "user-aborted")
            .Build();
        CourierIO.WriteAtomic(CancelsPath(endpointId, requestId), json, true);
    }

    /// <summary>
    /// 按 SPEC §6 的规范形式改写心跳：单个 UTC 时间戳字符串，无 JSON、无换行。
    /// 直接改文件内容即可在不 sleep 的前提下制造 alive/stale/dead。
    /// </summary>
    protected void WriteHeartbeat(string endpointId, DateTime utc)
    {
        WriteRawHeartbeat(endpointId, CourierProtocol.FormatUtc(utc));
    }

    /// <summary>直接写心跳文件的原始文本，用于测试读取侧对非规范线格式的容忍度。</summary>
    protected void WriteRawHeartbeat(string endpointId, string content)
    {
        Directory.CreateDirectory(EndpointDirectory(endpointId));
        CourierIO.WriteAtomic(HeartbeatPath(endpointId), content, true);
    }


    protected CourierResponse? ReadResponse(string endpointId, string requestId)
    {
        string? text;
        if (!CourierIO.TryReadAllText(ResultsPath(endpointId, requestId), out text)) return null;
        return CourierResponse.TryParse(text);
    }

    internal CourierDeadletter? ReadDeadletter(string endpointId, string requestId)
    {
        string? text;
        if (!CourierIO.TryReadAllText(DeadletterPath(endpointId, requestId), out text)) return null;
        return CourierDeadletter.TryParse(text);
    }

    internal CourierClaim? ReadClaim(string endpointId, string requestId)
    {
        string? text;
        if (!CourierIO.TryReadAllText(ClaimsPath(endpointId, requestId), out text)) return null;
        return CourierClaim.TryParse(text);
    }

    internal CourierCommand? ReadCommand(string endpointId, string requestId)
    {
        string? text;
        if (!CourierIO.TryReadAllText(CommandsPath(endpointId, requestId), out text)) return null;
        return CourierCommand.TryParse(text, requestId);
    }

    /// <summary>读取 endpoint.json 的解析结果；解析失败直接让测试失败。</summary>
    protected Dictionary<string, object?> ReadJsonObject(string path)
    {
        string text = ReadText(path);
        Dictionary<string, object?>? document;
        Assert.True(CourierJson.TryParseObject(text, out document), "无法解析 JSON: " + path);
        Assert.NotNull(document);
        return document!;
    }

    protected string ReadText(string path)
    {
        string? text;
        Assert.True(CourierIO.TryReadAllText(path, out text), "读取失败: " + path);
        Assert.NotNull(text);
        return text!;
    }

    /// <summary>目录下所有 *.json，按文件名序数升序（SPEC §3.5）。目录不存在时返回空。</summary>
    protected IReadOnlyList<string> ListFiles(string directory)
    {
        if (!Directory.Exists(directory)) return Array.Empty<string>();
        string[] files = Directory.GetFiles(directory, "*" + CourierProtocol.JsonExtension);
        Array.Sort(files, StringComparer.Ordinal);
        return files;
    }

    /// <summary>目录下所有 *.json 的 requestId（去掉扩展名的文件名），按序数升序。</summary>
    protected IReadOnlyList<string> RequestIds(string directory)
    {
        IReadOnlyList<string> files = ListFiles(directory);
        var ids = new List<string>(files.Count);
        for (int index = 0; index < files.Count; index++)
        {
            ids.Add(Path.GetFileNameWithoutExtension(files[index]));
        }
        return ids;
    }

    /// <summary>
    /// 断言命令被归档到 archive/&lt;yyyy-MM-dd&gt;/&lt;endpointId&gt;/commands/&lt;requestId&gt;.json（SPEC §12.1），
    /// 并返回归档后的绝对路径。
    /// </summary>
    protected string AssertArchivedCommand(string endpointId, string requestId)
    {
        string archiveRoot = Path.Combine(Root, CourierProtocol.ArchiveDirectory);
        string[] matches = Directory.Exists(archiveRoot)
            ? Directory.GetFiles(archiveRoot, requestId + CourierProtocol.JsonExtension, SearchOption.AllDirectories)
            : Array.Empty<string>();
        Assert.Single(matches);

        string archived = matches[0];
        string relative = Path.GetRelativePath(Root, archived);
        string[] segments = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(5, segments.Length);
        Assert.Equal(CourierProtocol.ArchiveDirectory, segments[0]);
        Assert.True(
            DateTime.TryParseExact(segments[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "归档日期目录不是 yyyy-MM-dd: " + segments[1]);
        Assert.Equal(endpointId, segments[2]);
        Assert.Equal(CourierProtocol.CommandsDirectory, segments[3]);
        Assert.Equal(requestId + CourierProtocol.JsonExtension, segments[4]);
        return archived;
    }

    /// <summary>断言 reason 严格取自 SPEC §15 总表，防止实现悄悄引入第四个原因码。</summary>
    protected static void AssertReasonInSpecTable(string? reason)
    {
        Assert.NotNull(reason);
        Assert.Contains(reason!, SpecReasonTable);
    }

    /// <summary>
    /// 在服务端持续 Tick 的同时异步调用它：长命宿主的标准用法（SPEC §14.3）。
    /// 调用方与被调用方必须是两个不同的 CourierNode 实例。
    /// </summary>
    protected static async Task<CourierResponse> CallWithTickingAsync(
        CourierNode server,
        CourierNode caller,
        string endpointId,
        string channel,
        string action,
        string? payload,
        TimeSpan timeout)
    {
        using var stop = new CancellationTokenSource();
        Task ticker = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                server.Tick();
                try
                {
                    await Task.Delay(10, stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        });

        try
        {
            return await caller.CallAsync(endpointId, channel, action, payload, timeout).ConfigureAwait(false);
        }
        finally
        {
            stop.Cancel();
            await ticker.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 显式接口实现：xUnit 只要求实例可转成 IDisposable，这样基类不会暴露一个待标记为测试的 public 方法。
    /// Windows 上句柄释放有延迟，删不掉就重试；仍失败则吞掉——清理失败不应掩盖测试结论。
    /// </summary>
    void IDisposable.Dispose()
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!Directory.Exists(Root)) return;
                Directory.Delete(Root, true);
                return;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            Thread.Sleep(20);
        }
    }
}
