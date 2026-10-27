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
public abstract partial class TestRoot : IDisposable
{
    /// <summary>测试根所属目录的名字；根目录自身再按实例加 GUID 子目录。</summary>
    private const string ArtifactsDirectoryName = "courier-test-artifacts";

    /// <summary>
    /// 每个测试实例独占一个协议根。根建在用户私有的 artifacts 目录下，MUST NOT 落在共享临时目录
    /// （SPEC §20 第 3 条：其他用户能往 commands/ 投东西，而协议没有鉴权）。
    /// </summary>
    protected TestRoot()
    {
        Root = Path.Combine(ResolveArtifactsDirectory(), "courier-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>
    /// 解析 artifacts 父目录：优先 %LOCALAPPDATA%，退化到用户主目录，两者都是用户私有目录。
    /// </summary>
    /// <returns>artifacts 父目录的绝对路径。</returns>
    /// <exception cref="InvalidOperationException">两个候选目录都取不到时抛出，避免退化成相对路径。</exception>
    private static string ResolveArtifactsDirectory()
    {
        string userPrivate = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(userPrivate))
        {
            userPrivate = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (string.IsNullOrEmpty(userPrivate))
        {
            throw new InvalidOperationException(
                "无法定位用户私有目录（LOCALAPPDATA 与 UserProfile 都为空）；" +
                "SPEC §20 第 3 条禁止把测试根放进共享临时目录，请检查测试宿主的环境变量。");
        }

        return Path.Combine(userPrivate, ArtifactsDirectoryName);
    }

    /// <summary>本测试独占的协议根；协议目录（endpoints/、archive/）都在它下面。</summary>
    protected string Root { get; }

    protected CourierNode OpenNode(string endpointId, string? description = null)
    {
        return CourierNode.Open(Root, endpointId, description);
    }

    protected string EndpointsRoot
    {
        get { return Path.Combine(Root, CourierProtocol.ENDPOINTS_DIRECTORY); }
    }

    protected string EndpointDirectory(string endpointId)
    {
        return Path.Combine(EndpointsRoot, endpointId);
    }

    protected string EndpointJsonPath(string endpointId)
    {
        return Path.Combine(EndpointDirectory(endpointId), CourierProtocol.ENDPOINT_FILE_NAME);
    }

    protected string HeartbeatPath(string endpointId)
    {
        return Path.Combine(EndpointDirectory(endpointId), CourierProtocol.HEARTBEAT_FILE_NAME);
    }

    protected string CommandsDirectory(string endpointId)
    {
        return Path.Combine(EndpointDirectory(endpointId), CourierProtocol.COMMANDS_DIRECTORY);
    }

    protected string ClaimsDirectory(string endpointId)
    {
        return Path.Combine(EndpointDirectory(endpointId), CourierProtocol.CLAIMS_DIRECTORY);
    }

    protected string ResultsDirectory(string endpointId)
    {
        return Path.Combine(EndpointDirectory(endpointId), CourierProtocol.RESULTS_DIRECTORY);
    }

    protected string DeadletterDirectory(string endpointId)
    {
        return Path.Combine(EndpointDirectory(endpointId), CourierProtocol.DEADLETTER_DIRECTORY);
    }

    protected string CancelsDirectory(string endpointId)
    {
        return Path.Combine(EndpointDirectory(endpointId), CourierProtocol.CANCELS_DIRECTORY);
    }

    protected string ProgressDirectory(string endpointId)
    {
        return Path.Combine(EndpointDirectory(endpointId), CourierProtocol.PROGRESS_DIRECTORY);
    }

    protected string CommandsPath(string endpointId, string requestId)
    {
        return Path.Combine(CommandsDirectory(endpointId), requestId + CourierProtocol.JSON_EXTENSION);
    }

    protected string ClaimsPath(string endpointId, string requestId)
    {
        return Path.Combine(ClaimsDirectory(endpointId), requestId + CourierProtocol.JSON_EXTENSION);
    }

    protected string ResultsPath(string endpointId, string requestId)
    {
        return Path.Combine(ResultsDirectory(endpointId), requestId + CourierProtocol.JSON_EXTENSION);
    }

    protected string DeadletterPath(string endpointId, string requestId)
    {
        return Path.Combine(DeadletterDirectory(endpointId), requestId + CourierProtocol.JSON_EXTENSION);
    }

    protected string CancelsPath(string endpointId, string requestId)
    {
        return Path.Combine(CancelsDirectory(endpointId), requestId + CourierProtocol.JSON_EXTENSION);
    }

    protected string ProgressPath(string endpointId, string requestId)
    {
        return Path.Combine(ProgressDirectory(endpointId), requestId + CourierProtocol.JSON_EXTENSION);
    }

    /// <summary>
    /// 按 SPEC §9 手工投递一条命令（不经过 CourierNode，用于精确控制 deadline 与文件内容）。
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

    /// <summary>按 SPEC §10.1 写一个租约：expiresAtUtc 是到期时刻，不是认领时刻。</summary>
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
        WriteClaimFile(endpointId, requestId, DateTime.UtcNow + CourierProtocol.CLAIM_WINDOW, attempts);
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
        string[] files = Directory.GetFiles(directory, "*" + CourierProtocol.JSON_EXTENSION);
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
    /// 断言命令被归档到 archive/&lt;yyyy-MM-dd&gt;/&lt;endpointId&gt;/commands/&lt;requestId&gt;.json（SPEC §15.1），
    /// 并返回归档后的绝对路径。
    /// </summary>
    protected string AssertArchivedCommand(string endpointId, string requestId)
    {
        return AssertArchived(endpointId, requestId, CourierProtocol.COMMANDS_DIRECTORY);
    }

    /// <summary>
    /// 断言死信副本被归档到 archive/&lt;yyyy-MM-dd&gt;/&lt;endpointId&gt;/deadletter/&lt;requestId&gt;.json
    /// （SPEC §12、§15.1：在线死信可被清理，所以归档副本是证据链的另一半），并返回其绝对路径。
    /// </summary>
    protected string AssertArchivedDeadletter(string endpointId, string requestId)
    {
        return AssertArchived(endpointId, requestId, CourierProtocol.DEADLETTER_DIRECTORY);
    }

    /// <summary>
    /// 同一个 requestId 在 archive 下最多各有一份 commands 副本与 deadletter 副本（终态唯一，
    /// 但两份副本对应两个不同的东西），所以这里按子目录分别断言，不跨目录取总数。
    /// </summary>
    private string AssertArchived(string endpointId, string requestId, string subdirectory)
    {
        string archiveRoot = Path.Combine(Root, CourierProtocol.ARCHIVE_DIRECTORY);
        string[] matches = Directory.Exists(archiveRoot)
            ? Directory.GetFiles(archiveRoot, requestId + CourierProtocol.JSON_EXTENSION, SearchOption.AllDirectories)
            : Array.Empty<string>();

        var inSubdirectory = new System.Collections.Generic.List<string>();
        for (int index = 0; index < matches.Length; index++)
        {
            string[] parts = RelativeSegments(matches[index]);
            if (parts.Length == 5 && string.Equals(parts[3], subdirectory, StringComparison.Ordinal)) inSubdirectory.Add(matches[index]);
        }

        Assert.Single(inSubdirectory);

        string archived = inSubdirectory[0];
        string[] segments = RelativeSegments(archived);
        Assert.Equal(5, segments.Length);
        Assert.Equal(CourierProtocol.ARCHIVE_DIRECTORY, segments[0]);
        Assert.True(
            DateTime.TryParseExact(segments[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "归档日期目录不是 yyyy-MM-dd: " + segments[1]);
        Assert.Equal(endpointId, segments[2]);
        Assert.Equal(subdirectory, segments[3]);
        Assert.Equal(requestId + CourierProtocol.JSON_EXTENSION, segments[4]);
        return archived;
    }

    private string[] RelativeSegments(string path)
    {
        return Path.GetRelativePath(Root, path).Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// 在服务端持续 Tick 的同时异步调用它：长命宿主的标准用法（SPEC §17.3）。
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
    /// Windows 上句柄释放有延迟，删不掉就重试；重试用尽仍失败时保留目录、把路径写到 stderr，
    /// 而不是静默吞掉——残留的协议根会污染下一个测试的排查现场。
    /// </summary>
    void IDisposable.Dispose()
    {
        Exception? lastFailure = null;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!Directory.Exists(Root)) return;
                Directory.Delete(Root, true);
                return;
            }
            catch (IOException exception)
            {
                lastFailure = exception;
            }
            catch (UnauthorizedAccessException exception)
            {
                lastFailure = exception;
            }

            Thread.Sleep(20);
        }

        ReportResidualRoot(lastFailure);
    }

    /// <summary>
    /// 清理失败时的诊断出口：不抛异常（清理失败不该把已通过的结论改判为红），
    /// 但必须把保留目录的绝对路径与最后一次异常写给诊断通道，残留目录不再是无声无息的黑洞。
    /// </summary>
    /// <param name="lastFailure">最后一次删除尝试抛出的异常；从未抛过时为 null。</param>
    private void ReportResidualRoot(Exception? lastFailure)
    {
        string detail = lastFailure == null
            ? "目录仍然存在"
            : lastFailure.GetType().Name + ": " + lastFailure.Message;
        string message = "测试根清理失败，已保留现场目录: " + Root + "（" + detail +
            "）。若反复出现，请检查是否有未释放的 CourierNode 句柄仍在写该目录。";
        Console.Error.WriteLine(message);
    }
}
