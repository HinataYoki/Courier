using System;
using Xunit;

namespace Courier.Tests;

/// <summary>
/// SPEC §18 原因码总表：<c>(status, reason)</c> 是**配对**关系，不是两个独立的白名单。
/// 把 <c>failed</c> 配上 <c>stale</c>、把 <c>rejected</c> 配上 <c>handler-error</c> 在语义上就是错的
/// （§11.1：rejected 表示请求本身有问题、重试无意义；failed 表示请求合法、执行出错），
/// 但只查"reason 在表里"的断言会让这种串用照样通过。
/// </summary>
public abstract partial class TestRoot
{
    /// <summary>
    /// 服务者侧合法配对。<c>ok</c> 与 <c>unreachable</c> 不带 reason（SPEC §18 总表里是"—"），
    /// 所以它们不出现在这里，改由 <see cref="AssertStatusWithoutReason"/> 断言 reason 必须是 null。
    /// </summary>
    private static readonly (string Status, string[] Reasons)[] SpecStatusReasonTable =
    {
        (CourierProtocol.STATUS_REJECTED, new[]
        {
            CourierProtocol.REASON_STALE,
            CourierProtocol.REASON_UNKNOWN_ACTION,
            CourierProtocol.REASON_INVALID_COMMAND,
            CourierProtocol.REASON_CANCELLED_BEFORE_START,
            CourierProtocol.REASON_BACKPRESSURE
        }),
        (CourierProtocol.STATUS_FAILED, new[]
        {
            CourierProtocol.REASON_HANDLER_ERROR,
            CourierProtocol.REASON_HANDLER_TIMEOUT,
            CourierProtocol.REASON_CANCELLED
        })
    };

    /// <summary>
    /// 断言某条终态的 <paramref name="status"/> 与 <paramref name="reason"/> 构成 SPEC §18 允许的配对。
    /// 只校验配对，不校验"这个场景应该产生哪一个码"——后者由调用方先做精确断言。
    /// </summary>
    /// <param name="status">终态 status，取自 CourierProtocol.STATUS_*。</param>
    /// <param name="reason">终态 reason；为 null 或空串时断言失败。</param>
    protected static void AssertReasonInSpecTable(string? status, string? reason)
    {
        Assert.False(string.IsNullOrEmpty(reason), "status=" + status + " 的终态必须带 reason（SPEC §11）");
        Assert.NotNull(status);

        for (int index = 0; index < SpecStatusReasonTable.Length; index++)
        {
            (string tableStatus, string[] tableReasons) = SpecStatusReasonTable[index];
            if (!string.Equals(tableStatus, status, StringComparison.Ordinal)) continue;

            Assert.Contains(reason!, tableReasons);
            return;
        }

        Assert.Fail(
            "status " + status + " / reason " + reason +
            " 不是 SPEC §18 允许的配对：带 reason 的只有 rejected 与 failed，ok 与 unreachable 必须不带 reason。");
    }

    /// <summary>
    /// 断言 <paramref name="status"/> 这一档终态按 SPEC §18 总表 MUST NOT 携带 reason
    /// （<c>ok</c> 与调用方本地合成的 <c>unreachable</c>）。
    /// </summary>
    /// <param name="status">终态 status，取自 CourierProtocol.STATUS_*。</param>
    /// <param name="reason">终态 reason，必须为 null。</param>
    protected static void AssertStatusWithoutReason(string? status, string? reason)
    {
        Assert.True(
            string.Equals(status, CourierProtocol.STATUS_OK, StringComparison.Ordinal) ||
            string.Equals(status, CourierProtocol.STATUS_UNREACHABLE, StringComparison.Ordinal),
            "只有 ok 与 unreachable 不带 reason，这里收到的是 " + status);
        Assert.Null(reason);
    }

    /// <summary>
    /// 断言死信的原因码落在 SPEC §12 允许的集合里。死信没有 status 字段，所以按它的语义归属
    /// 分档：<c>invalid-command</c> 属于请求该被 rejected 的那一类，<c>handler-error</c> 属于执行失败那一类。
    /// </summary>
    /// <param name="reason">死信的 reason；为 null 时断言失败。</param>
    protected static void AssertDeadletterReasonInSpecTable(string? reason)
    {
        Assert.False(string.IsNullOrEmpty(reason), "deadletter 必须带 reason（SPEC §12）");
        Assert.True(
            string.Equals(reason, CourierProtocol.REASON_INVALID_COMMAND, StringComparison.Ordinal) ||
            string.Equals(reason, CourierProtocol.REASON_HANDLER_ERROR, StringComparison.Ordinal),
            "死信原因码 " + reason + " 不属于 SPEC §12 的三类情形（超限 / 非法命令 / attempts 超限）");
    }
}
