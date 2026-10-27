using System;
using System.Threading;
using System.Threading.Tasks;

namespace Courier
{
    /// <summary>
    /// <see cref="CourierNode"/> 的内部类型：能力条目、in-flight 条目、热等待登记与上下文兜底。
    /// </summary>
    public sealed partial class CourierNode
    {
        /// <summary>SPEC §14：一个已声明的状态名。声明只描述"会发布什么"，值在 state/ 下按需读取。</summary>
        private sealed class StateEntry
        {
        /// <summary>登记的是一条状态声明：名字、说明与声明方元数据，值本身不缓存。</summary>
            internal StateEntry(string name, string summary, string? metadata)
            {
                Name = name;
                Summary = summary;
                Metadata = metadata;
            }

            internal string Name { get; }
            internal string Summary { get; }

            /// <summary>SPEC §5.1：可选自由 JSON 对象；没有时为 null（写 endpoint.json 时字段省略）。</summary>
            internal string? Metadata { get; }
        }

        private sealed class HandlerEntry
        {
        /// <summary>只收 payload 的旧式处理器（源码兼容保留）。</summary>
            internal HandlerEntry(string channel, string action, string summary, string? metadata, Func<string, CancellationToken, Task<string>> handler)
            {
                Channel = channel;
                Action = action;
                Summary = summary;
                Metadata = metadata;
                Handler = handler;
                CallAware = null;
            }

        /// <summary>带 <see cref="CourierCall"/> 的处理器；<c>callAware</c> 非 null 时优先于旧式处理器。</summary>
            internal HandlerEntry(string channel, string action, string summary, string? metadata, Func<CourierCall, CancellationToken, Task<string>> handler)
            {
                Channel = channel;
                Action = action;
                Summary = summary;
                Metadata = metadata;
                // 两个委托只保留一个：CallAware 存在时 Handler 是它的去上下文化包装，
                // 这样执行路径只需要判断一次"要不要构造上下文"，而不必维护两条独立代码路径。
                CallAware = handler;
                Handler = (payload, token) => handler(NoContext(payload), token);
            }

            internal string Channel { get; }
            internal string Action { get; }
            internal string Summary { get; }

            /// <summary>SPEC §5.1：可选自由 JSON 对象；没有时为 null（写 endpoint.json 时字段省略）。</summary>
            internal string? Metadata { get; }

            /// <summary>不带上下文的处理函数；<see cref="CallAware"/> 存在时它是包装后的版本，永远不为 null。</summary>
            internal Func<string, CancellationToken, Task<string>> Handler { get; }

            /// <summary>需要 <see cref="CourierCall"/> 的处理函数；注册的是旧式重载时为 null。</summary>
            internal Func<CourierCall, CancellationToken, Task<string>>? CallAware { get; }
        }

        /// <summary>
        /// 给旧式 handler 兜一个"只有 payload"的上下文。它永远不该被走到——执行路径在
        /// <see cref="HandlerEntry.CallAware"/> 非 null 时直接调用那个委托；这里只是让字段非空。
        /// </summary>
        private static CourierCall NoContext(string payload)
        {
            return new CourierCall(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, DateTime.MinValue, DateTime.MinValue, 0, DateTime.MinValue, payload);
        }

        /// <summary>
        /// SPEC §16.2 的中间态：一条已经通过前置检查、但还没有派发 handler 的命令。
        /// <para>
        /// 它把"这次执行是第几次、claim 能不能覆盖、命令本身是什么"打包在一起，
        /// 免得接纳流程的每一步都把这四样东西挂在参数表上。
        /// </para>
        /// </summary>
        private sealed class PendingCommand
        {
        /// <summary>已经通过准入检查、即将启动的一条命令；从 <c>AdmitCommand</c> 交到 <c>LaunchHandler</c> 之间只读。</summary>
            internal PendingCommand(string requestId, string commandPath, string claimPath, int attempts, bool takeOver, CourierCommand command)
            {
                RequestId = requestId;
                CommandPath = commandPath;
                ClaimPath = claimPath;
                Attempts = attempts;
                TakeOver = takeOver;
                Command = command;
            }

            /// <summary>命令标识，与 commands/ 下的文件名一致（SPEC §4.2）。</summary>
            internal string RequestId { get; }

            /// <summary>commands/ 下的命令文件路径。</summary>
            internal string CommandPath { get; }

            /// <summary>claims/ 下这条命令的租约文件路径。</summary>
            internal string ClaimPath { get; }

            /// <summary>本次执行是第几次（SPEC §10.1）；首次为 1，接管过期租约时递增。</summary>
            internal int Attempts { get; }

            /// <summary>是否接管了一条已存在的 claim；决定写租约时能否覆盖。</summary>
            internal bool TakeOver { get; }

            /// <summary>解析后的命令模型。</summary>
            internal CourierCommand Command { get; }
        }

        /// <summary>SPEC §16.1：一条正在执行的命令。字段只由 Tick 线程或 Dispose 改写，读写都在 mGate 之外时靠 volatile 语义的引用发布保证可见。</summary>
        private sealed class InflightEntry
        {
        /// <summary>在途命令的一条账：任务、取消源、截止时间与是否已放弃等待。</summary>
            internal InflightEntry(string requestId, string commandPath, DateTime deadlineUtc, CancellationTokenSource source, Task<string> task)
            {
                RequestId = requestId;
                CommandPath = commandPath;
                DeadlineUtc = deadlineUtc;
                Source = source;
                Task = task;
                LastRenewedUtc = DateTime.UtcNow;
            }

            internal string RequestId { get; }
            internal string CommandPath { get; }
            internal DateTime DeadlineUtc { get; }
            internal CancellationTokenSource Source { get; }
            internal Task<string> Task { get; }
            internal DateTime LastRenewedUtc { get; set; }
            internal bool Abandoned { get; set; }
        }
    }
}
