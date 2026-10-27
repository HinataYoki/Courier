using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Courier.Tests;

/// <summary>
/// <c>results/</c> 回收与墓碑 <c>results/gc.json</c>（SPEC §15.2）。
/// <para>
/// 这一类测试的每个用例都自己造响应文件（而不是跑一遍真实投递），因为要精确控制
/// <c>completedAtUtc</c>：GC 的判定只看它，跟文件时间戳无关。
/// </para>
/// </summary>
public sealed class CourierGcTests : TestRoot
{
    private string GcPath(string endpointId)
    {
        return Path.Combine(ResultsDirectory(endpointId), CourierResultsGc.TOMBSTONE_FILE_NAME);
    }

    /// <summary>写一条完成时刻可指定的响应；返回它的 requestId。</summary>
    private string WriteResponse(string endpointId, DateTime completedAtUtc)
    {
        string requestId = CourierProtocol.NewRequestId();
        WriteResponseWithId(endpointId, requestId, completedAtUtc);
        return requestId;
    }

    /// <summary>写一条指定 requestId 的响应，用于需要精确控制"序数大小关系"的用例。</summary>
    private void WriteResponseWithId(string endpointId, string requestId, DateTime completedAtUtc)
    {
        var response = new CourierResponse(
            requestId, CourierProtocol.STATUS_OK, "done", null, false, completedAtUtc);
        CourierIO.WriteAtomic(ResultsPath(endpointId, requestId), response.ToJson(), true);
    }

    /// <summary>SPEC §15.2：cutoff 之前完成的响应被删，之后的一律保留。</summary>
    [Fact]
    public void Collect_只删cutoff之前完成的结果()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            DateTime now = DateTime.UtcNow;
            string old1 = WriteResponse("worker", now - TimeSpan.FromMinutes(10));
            string old2 = WriteResponse("worker", now - TimeSpan.FromMinutes(5));
            string fresh = WriteResponse("worker", now - TimeSpan.FromSeconds(1));

            int removed = node.CollectResults(now - TimeSpan.FromMinutes(1));

            Assert.Equal(2, removed);
            Assert.False(File.Exists(ResultsPath("worker", old1)));
            Assert.False(File.Exists(ResultsPath("worker", old2)));
            Assert.True(File.Exists(ResultsPath("worker", fresh)));
        }
    }

    /// <summary>SPEC §15.2：墓碑三元组齐全，removedBefore 是删除后仍存在的最小 requestId。</summary>
    [Fact]
    public void Collect_墓碑字段正确_removedBefore取幸存者最小值()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            DateTime now = DateTime.UtcNow;
            WriteResponse("worker", now - TimeSpan.FromMinutes(10));
            WriteResponse("worker", now - TimeSpan.FromMinutes(9));
            // requestId 以定长时间戳开头，所以"最小"就是"最早"。写两条幸存的，取序数小的那条。
            string survivor1 = WriteResponse("worker", now - TimeSpan.FromSeconds(30));
            string survivor2 = WriteResponse("worker", now - TimeSpan.FromSeconds(20));
            string expected = string.CompareOrdinal(survivor1, survivor2) < 0 ? survivor1 : survivor2;

            DateTime cutoff = now - TimeSpan.FromMinutes(1);
            Assert.Equal(2, node.CollectResults(cutoff));

            CourierGcTombstone? tombstone = CourierResultsGc.TryReadTombstone(ResultsDirectory("worker"));
            Assert.NotNull(tombstone);
            Assert.Equal(cutoff, tombstone!.CutoffUtc);
            Assert.Equal(expected, tombstone.RemovedBefore);
            Assert.True(tombstone.GeneratedAtUtc > DateTime.UtcNow - TimeSpan.FromMinutes(1));

            // 墓碑自己的字段名与 SPEC 完全一致，不多不少。
            Dictionary<string, object?> document = ReadJsonObject(GcPath("worker"));
            Assert.Equal(new[] { "cutoffUtc", "generatedAtUtc", "removedBefore" }, Keys(document));
        }
    }

    /// <summary>SPEC §15.2：全部删光时 removedBefore 是空串——不得猜一个不存在的 ID。</summary>
    [Fact]
    public void Collect_全部删除_removedBefore为空串()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            DateTime now = DateTime.UtcNow;
            WriteResponse("worker", now - TimeSpan.FromMinutes(10));
            WriteResponse("worker", now - TimeSpan.FromMinutes(9));

            Assert.Equal(2, node.CollectResults(now));

            CourierGcTombstone? tombstone = CourierResultsGc.TryReadTombstone(ResultsDirectory("worker"));
            Assert.NotNull(tombstone);
            Assert.Equal(string.Empty, tombstone!.RemovedBefore);
        }
    }

    /// <summary>SPEC §15.2：命令还在 <c>commands/</c> 或 claim 还在 <c>claims/</c> 的请求是"在途"，绝不删。</summary>
    [Fact]
    public void Collect_在途命令绝不删除()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            DateTime now = DateTime.UtcNow;
            string inCommands = WriteResponse("worker", now - TimeSpan.FromHours(1));
            CourierIO.WriteAtomic(CommandsPath("worker", inCommands), "{}", true);

            string inClaims = WriteResponse("worker", now - TimeSpan.FromHours(1));
            WriteFreshClaimFile("worker", inClaims, 1);

            string finished = WriteResponse("worker", now - TimeSpan.FromHours(1));

            Assert.Equal(1, node.CollectResults(now));

            Assert.True(File.Exists(ResultsPath("worker", inCommands)));
            Assert.True(File.Exists(ResultsPath("worker", inClaims)));
            Assert.False(File.Exists(ResultsPath("worker", finished)));
            // 幸存者只有那两条在途的：removedBefore 取它们当中序数小的那个。
            string expected = string.CompareOrdinal(inCommands, inClaims) < 0 ? inCommands : inClaims;
            Assert.Equal(expected, CourierResultsGc.TryReadTombstone(ResultsDirectory("worker"))!.RemovedBefore);
        }
    }

    /// <summary>SPEC §15.2：幂等——第二次调用不报错、不破坏墓碑，也不会把 gc.json 自己删掉。</summary>
    [Fact]
    public void Collect_二次调用_幂等且不删墓碑()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            DateTime now = DateTime.UtcNow;
            WriteResponse("worker", now - TimeSpan.FromMinutes(10));
            Assert.Equal(1, node.CollectResults(now));

            Assert.Equal(0, node.CollectResults(now));
            Assert.Equal(0, node.CollectResults(now.AddMinutes(1)));

            Assert.True(File.Exists(GcPath("worker")));
            Assert.NotNull(CourierResultsGc.TryReadTombstone(ResultsDirectory("worker")));
        }
    }

    /// <summary>SPEC §15.2：没有任何可删的东西时也写墓碑，统计为 0。</summary>
    [Fact]
    public void Collect_没有可删结果_仍然写墓碑且返回零()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            string fresh = WriteResponse("worker", DateTime.UtcNow);
            Assert.Equal(0, node.CollectResults(DateTime.UtcNow - TimeSpan.FromMinutes(1)));

            Assert.True(File.Exists(GcPath("worker")));
            Assert.Equal(fresh, CourierResultsGc.TryReadTombstone(ResultsDirectory("worker"))!.RemovedBefore);
        }
    }

    /// <summary>SPEC §15.2：解析不出来的响应不是"已完成的响应"，删掉它就等于删掉唯一证据。</summary>
    [Fact]
    public void Collect_损坏的响应文件_不删除()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            string requestId = CourierProtocol.NewRequestId();
            CourierIO.WriteAtomic(ResultsPath("worker", requestId), "{ broken", true);

            Assert.Equal(0, node.CollectResults(DateTime.UtcNow));
            Assert.True(File.Exists(ResultsPath("worker", requestId)));
        }
    }

    /// <summary>SPEC §15.2：results/ 里的非协议文件（名字不是 requestId）绝不碰。</summary>
    [Fact]
    public void Collect_非requestId文件_不删除()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            string stray = Path.Combine(ResultsDirectory("worker"), "notes.json");
            CourierIO.WriteAtomic(stray, "{\"hello\":1}", true);

            Assert.Equal(0, node.CollectResults(DateTime.UtcNow));
            Assert.True(File.Exists(stray));
        }
    }

    /// <summary>SPEC §15.2：cutoff 必须是 UTC，非 UTC 的入参属于编程错误。</summary>
    [Fact]
    public void CollectResults_非UTC的cutoff_抛出ArgumentException()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            Assert.Throws<ArgumentException>(() => node.CollectResults(DateTime.Now));
            Assert.Throws<ArgumentException>(() => node.CollectResults(DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified)));
        }
    }

    /// <summary>SPEC §15.2：Dispose 之后不得再回收。</summary>
    [Fact]
    public void CollectResults_Dispose之后_抛出ObjectDisposedException()
    {
        CourierNode node = OpenNode("worker");
        node.Dispose();
        Assert.Throws<ObjectDisposedException>(() => node.CollectResults(DateTime.UtcNow));
    }

    /// <summary>SPEC §15.2：results/ 不存在时不是错误，返回 0 且不写墓碑。</summary>
    [Fact]
    public void Collect_results目录不存在_返回零且不写墓碑()
    {
        Directory.CreateDirectory(EndpointDirectory("worker"));
        Assert.Equal(0, CourierResultsGc.Collect(
            Path.Combine(EndpointDirectory("worker"), "results"), null, null, DateTime.UtcNow));
        Assert.False(File.Exists(GcPath("worker")));
    }

    /// <summary>
    /// SPEC §15.2 的读者条款：墓碑把"已被回收"与"从没处理过"分开。
    /// 空 removedBefore 表示当时的回收把结果删光了，比它小的空格情形也一律算已回收。
    /// </summary>
    [Fact]
    public void IsPossiblyReclaimed_按墓碑的removedBefore区分()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            // 还没有墓碑：缺失的结果只能当成"从没处理过"，不能断言被回收（§3.7）。
            Assert.False(CourierResultsGc.IsPossiblyReclaimed(ResultsDirectory("worker"), "20260101T000000Z-01234567"));

            DateTime now = DateTime.UtcNow;
            // requestId 的随机段占同一个秒内的排序，所以这里用两条手写的、序数确定的 id：
            // 小的一条会因 completedAtUtc 早而被删，大的一条幸存。
            const string deleted = "20260101T000000Z-aaaaaaaa";
            const string survivor = "20260101T000000Z-bbbbbbbb";
            WriteResponseWithId("worker", deleted, now - TimeSpan.FromMinutes(10));
            WriteResponseWithId("worker", survivor, now - TimeSpan.FromSeconds(5));

            Assert.Equal(1, node.CollectResults(now - TimeSpan.FromMinutes(1)));
            Assert.False(File.Exists(ResultsPath("worker", deleted)));

            // removedBefore 是幸存者；比它小的（刚被删的那条）算已回收，它自己不算。
            Assert.Equal(survivor, CourierResultsGc.TryReadTombstone(ResultsDirectory("worker"))!.RemovedBefore);
            Assert.True(CourierResultsGc.IsPossiblyReclaimed(ResultsDirectory("worker"), deleted));
            Assert.False(CourierResultsGc.IsPossiblyReclaimed(ResultsDirectory("worker"), survivor));
        }
    }

    /// <summary>SPEC §15.2：removedBefore 是空串（全删光）时，任何缺失的结果都可能已被回收。</summary>
    [Fact]
    public void IsPossiblyReclaimed_全删光时一律算已回收()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            DateTime now = DateTime.UtcNow;
            WriteResponse("worker", now - TimeSpan.FromMinutes(10));
            Assert.Equal(1, node.CollectResults(now));
            Assert.Equal(string.Empty, CourierResultsGc.TryReadTombstone(ResultsDirectory("worker"))!.RemovedBefore);

            Assert.True(CourierResultsGc.IsPossiblyReclaimed(ResultsDirectory("worker"), CourierProtocol.NewRequestId()));
        }
    }

    /// <summary>
    /// SPEC §11.2、§15.2：等到超时仍无响应时 status 仍是 unreachable，但墓碑能说明"它可能已被回收"。
    /// 这不是第四个终态、也不是原因码，只是一个诊断标记。
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task CallAsync_结果已被回收_仍是unreachable但带reclaimed标记()
    {
        using (CourierNode node = OpenNode("worker"))
        {
            node.Handle("document", "open", "打开文档", handlerPayload => "opened");

            // 造现场：本端点曾经处理过东西，结果已被回收（全部删光 → removedBefore 是空串）。
            DateTime now = DateTime.UtcNow;
            WriteResponse("worker", now - TimeSpan.FromMinutes(10));
            Assert.Equal(1, node.CollectResults(now));

            // 不 Tick，handler 不会跑，命令留在 commands/ 里直到调用方超时。
            CourierResponse response = await node.CallAsync("worker", "document", "open", "x", TimeSpan.FromMilliseconds(120));

            Assert.Equal(CourierProtocol.STATUS_UNREACHABLE, response.Status);
            Assert.Null(response.Reason);
            Assert.True(response.Reclaimed, "墓碑说明结果已被回收时，调用方应能看到这个诊断标记");
        }
    }

    /// <summary>
    /// SPEC §11、§15.2：墓碑标记是调用方的诊断量，MUST NOT 落盘——<c>ToJson</c> 的字段集合里没有它。
    /// 用例名按"方法_条件_预期"给出被测方法与前提，避免只写断言结论。
    /// </summary>
    [Fact]
    public void ToJson_响应带墓碑标记_不写出reclaimed字段()
    {
        var response = new CourierResponse(
            "20260101T000000Z-01234567", CourierProtocol.STATUS_UNREACHABLE, string.Empty, null, false, DateTime.UtcNow, reclaimed: true);
        Assert.True(response.Reclaimed);

        Dictionary<string, object?> document = ReadJsonObjectText(response.ToJson());
        Assert.False(document.ContainsKey("reclaimed"));
    }

    private static Dictionary<string, object?> ReadJsonObjectText(string text)
    {
        Dictionary<string, object?>? document;
        Assert.True(CourierJson.TryParseObject(text, out document), "无法解析 JSON: " + text);
        Assert.NotNull(document);
        return document!;
    }

    private static string[] Keys(Dictionary<string, object?> document)
    {
        var keys = new string[document.Count];
        document.Keys.CopyTo(keys, 0);
        Array.Sort(keys, StringComparer.Ordinal);
        return keys;
    }
}
