using System;
using System.Collections.Generic;
using System.IO;

namespace Courier
{
    /// <summary>
    /// root 级注册表 <c>&lt;root&gt;/endpoint.json</c> 的维护（SPEC §7）。
    /// <para>
    /// 这一份是**端点清单**，与 <c>endpoints/&lt;id&gt;/endpoint.json</c>（单个端点的能力目录）同名但不同文件、
    /// 不同含义。它存在的理由只有一个：让没有目录列举能力的宿主一次读到"这个 root 下现在有哪些端点"。
    /// <b>它不是存活权威</b>——权威永远是端点自己写的 <c>heartbeat</c>；条目里的 <c>state</c> 是允许过期的快照。
    /// </para>
    /// <para>
    /// 多个端点会同时写同一个文件，所以写必须是"短持锁 + 重读-合并-整体原子替换"，而不是
    /// "读自己的旧内容再补一条"：后者在并发下会让一个端点的条目覆盖掉另一个的。
    /// <c>registry.lock</c> 是这条集合更新路径的**实现细节，不是协议要求**——SPEC §7 只要求
    /// "整体原子替换 + 按 endpointId 升序"，怎么协调多个写者由实现决定。
    /// </para>
    /// </summary>
    internal static class CourierRootRegistry
    {
        /// <summary>协调多写者的锁文件名。它是实现细节，不是协议的一部分。</summary>
        internal const string LOCK_FILE_NAME = "registry.lock";

        /// <summary>注册表文件名，与端点目录下的文件同名但不同含义（SPEC §7）。</summary>
        internal const string REGISTRY_FILE_NAME = "endpoint.json";

        /// <summary>
        /// 把本端点写进注册表（或更新它的快照），再从注册表里剔除已经退出的端点。
        /// </summary>
        /// <param name="rootDirectory">协议根目录。</param>
        /// <param name="layout">生效的布局，用它的端点目录名做剔除判定。</param>
        /// <param name="entry">本端点的条目。</param>
        /// <param name="prune">
        /// true 时剔除"端点目录已不存在"的条目（Dispose 用）；false 时只做 upsert，
        /// 因为一个正在运行的发现在注册表里有些它自己还没见过的端点，删掉它们就变成互相打架。
        /// </param>
        /// <returns>是否真的写了文件；抢不到锁时返回 false，由调用方下一轮再来。</returns>
        internal static bool Publish(string rootDirectory, CourierLayout layout, CourierRegistryEntry entry, bool prune)
        {
            string registryPath = Path.Combine(rootDirectory, REGISTRY_FILE_NAME);
            string lockPath = Path.Combine(rootDirectory, LOCK_FILE_NAME);

            // 短持有：从重读到落盘之间不能让别的端点插进来，所以锁要覆盖整段；但它绝不含任何 I/O 等待，
            // 拿不到立刻放弃，服务循环一天都不会为它停一下（SPEC §16：Tick MUST NOT 被阻塞）。
            FileStream? guard = CourierHostLock.TryAcquire(lockPath);
            if (guard == null) return false;

            try
            {
                List<CourierRegistryEntry> entries = ReadEntries(registryPath, rootDirectory, layout);

                int existing = -1;
                for (int index = 0; index < entries.Count; index++)
                {
                    if (string.Equals(entries[index].EndpointId, entry.EndpointId, StringComparison.Ordinal))
                    {
                        existing = index;
                        break;
                    }
                }

                if (existing >= 0) entries[existing] = entry;
                else entries.Add(entry);

                if (prune) entries = PruneMissing(entries, rootDirectory, layout);

                // SPEC §7：条目 MUST 按 endpointId 序数升序。排序在合并之后做，一次就好。
                entries.Sort(CompareEntries);
                Write(registryPath, entries);
                return true;
            }
            finally
            {
                guard.Dispose();
            }
        }

        /// <summary>从注册表里移除一个端点（端点退出时调用）。文件不存在或本来就没有它时什么都不做。</summary>
        /// <param name="rootDirectory">协议根目录。</param>
        /// <param name="layout">生效的布局。</param>
        /// <param name="endpointId">要移除的端点。</param>
        /// <returns>是否真的写了文件。</returns>
        internal static bool Remove(string rootDirectory, CourierLayout layout, string endpointId)
        {
            string registryPath = Path.Combine(rootDirectory, REGISTRY_FILE_NAME);
            string lockPath = Path.Combine(rootDirectory, LOCK_FILE_NAME);

            FileStream? guard = CourierHostLock.TryAcquire(lockPath);
            if (guard == null) return false;

            try
            {
                List<CourierRegistryEntry> entries = ReadEntries(registryPath, rootDirectory, layout);
                int removed = entries.RemoveAll(candidate => string.Equals(candidate.EndpointId, endpointId, StringComparison.Ordinal));
                // 没写过这个端点，也没有别人的条目要修剪：不动文件，免得每次退出都刷新一次 generatedAtUtc。
                if (removed == 0 && !File.Exists(registryPath)) return false;

                entries = PruneMissing(entries, rootDirectory, layout);
                entries.Sort(CompareEntries);
                Write(registryPath, entries);
                return true;
            }
            finally
            {
                guard.Dispose();
            }
        }

        /// <summary>
        /// 直接读注册表；文件不存在、不可解析或版本不符时返回 null。
        /// <para>读方 MUST NOT 因为注册表坏了就认为"这个 root 下没有端点"——调用方要退回到列目录（SPEC §7）。</para>
        /// </summary>
        /// <param name="rootDirectory">协议根目录。</param>
        /// <returns>按 endpointId 升序的条目；读不到时为 null。</returns>
        internal static List<CourierRegistryEntry>? TryRead(string rootDirectory)
        {
            List<CourierRegistryEntry> entries;
            return TryReadRegistry(Path.Combine(rootDirectory, REGISTRY_FILE_NAME), out entries) ? entries : null;
        }

        /// <summary>
        /// 读并校验一份注册表文件：不存在、不可解析、版本不符或缺少 <c>endpoints</c> 数组时返回 false。
        /// </summary>
        /// <param name="registryPath">注册表全路径。</param>
        /// <param name="entries">成功时按 endpointId 升序的条目；失败时为空列表。</param>
        /// <returns>这份注册表是否可读且与当前协议版本相容。</returns>
        /// <remarks>
        /// 读注册表的**唯一**入口。合并路径（<see cref="Publish"/>、<see cref="Remove"/>）必须走这里而不是
        /// 自己解析一遍：否则一份未来格式的注册表会被当成当前版本理解，然后被整体重写，把不认识的条目
        /// 悄悄抹掉（SPEC §7：读不懂就重建，不能硬解释）。
        /// </remarks>
        private static bool TryReadRegistry(string registryPath, out List<CourierRegistryEntry> entries)
        {
            entries = new List<CourierRegistryEntry>();
            string? text;
            if (!CourierIO.TryReadAllText(registryPath, out text)) return false;

            Dictionary<string, object?>? document;
            if (!CourierJson.TryParseObject(text, out document)) return false;

            // SPEC §7：版本不匹配就当这份注册表读不懂，退化为列目录，而不是硬解释一份未来格式。
            long? version = CourierJson.GetInteger(document, "protocolVersion");
            if (version == null || version.Value != CourierProtocol.PROTOCOL_VERSION) return false;

            List<object?>? rawEntries = CourierJson.GetArray(document, "endpoints");
            if (rawEntries == null) return false;

            for (int index = 0; index < rawEntries.Count; index++)
            {
                Dictionary<string, object?>? item = rawEntries[index] as Dictionary<string, object?>;
                if (item == null) continue;
                CourierRegistryEntry? entry = CourierRegistryEntry.TryParse(item);
                // 单条坏条目只是少看到一条，不该让整份注册表作废。
                if (entry != null) entries.Add(entry);
            }

            entries.Sort(CompareEntries);
            return true;
        }

        /// <summary>
        /// 读端点名单：注册表可用就用它，否则退回列 <c>endpoints/</c> 目录（SPEC §7 的首选路径）。
        /// </summary>
        /// <param name="rootDirectory">协议根目录。</param>
        /// <param name="layout">生效的布局。</param>
        /// <returns>按 endpointId 升序的条目；两种情况都读不到时返回空列表。</returns>
        internal static List<CourierRegistryEntry> ReadListing(string rootDirectory, CourierLayout layout)
        {
            List<CourierRegistryEntry>? registry = TryRead(rootDirectory);
            if (registry != null && registry.Count > 0) return registry;

            // 退化路径：列目录。这也是"注册表只写了一半"时的正确答案——宁可多看到几个端点，
            // 也不要因为注册表缺了某一条就报告"没有端点"。
            return RebuildFromDirectories(rootDirectory, layout);
        }

        /// <summary>
        /// 读出现有注册表的条目，供"重读-合并-整体替换"使用；读不懂时从目录重建。
        /// </summary>
        /// <param name="registryPath">注册表全路径。</param>
        /// <param name="rootDirectory">协议根目录。</param>
        /// <param name="layout">生效的布局。</param>
        /// <returns>可以拿来做合并起点的条目列表。</returns>
        /// <remarks>
        /// 版本判定必须与 <see cref="TryRead"/> 一致：一份 <c>protocolVersion</c> 不是 2 的注册表对我们来说是
        /// 未来格式，它的条目集合我们读不全，**MUST NOT** 拿来做合并起点——那样整体重写会顺手删掉不认识的条目。
        /// 这种情形退回列目录（SPEC §7 的"读不懂就退化为列目录"），代价是一轮之内丢了别人的快照字段，
        /// 下一轮由各自的心跳写回来。
        /// </remarks>
        private static List<CourierRegistryEntry> ReadEntries(string registryPath, string rootDirectory, CourierLayout layout)
        {
            List<CourierRegistryEntry> existing;
            if (TryReadRegistry(registryPath, out existing)) return existing;

            // 注册表坏了、版本不符或不存在：从目录重建。合并不能以"读不到"为由把别人的条目全丢掉。
            return RebuildFromDirectories(rootDirectory, layout);
        }

        /// <summary>扫描 endpoints/ 目录重建条目表；注册表损坏或缺失时的退化路径（SPEC §7）。</summary>
        private static List<CourierRegistryEntry> RebuildFromDirectories(string rootDirectory, CourierLayout layout)
        {
            var entries = new List<CourierRegistryEntry>();
            IReadOnlyList<CourierEndpointInfo> endpoints = CourierEndpointReader.ReadAll(CourierLayout.Combine(rootDirectory, layout.EndpointsDirectory));
            for (int index = 0; index < endpoints.Count; index++)
            {
                CourierEndpointInfo info = endpoints[index];
                entries.Add(new CourierRegistryEntry(info.EndpointId, info.Description, info.SessionId, info.State));
            }

            return entries;
        }

        /// <summary>剔掉目录已经不在的条目，避免增量合并把死端点永远留在文件里。</summary>
        private static List<CourierRegistryEntry> PruneMissing(List<CourierRegistryEntry> entries, string rootDirectory, CourierLayout layout)
        {
            string endpointsRoot = CourierLayout.Combine(rootDirectory, layout.EndpointsDirectory);
            var kept = new List<CourierRegistryEntry>(entries.Count);
            for (int index = 0; index < entries.Count; index++)
            {
                CourierRegistryEntry entry = entries[index];
                // 端点目录是"这个端点存在过"的唯一持久证据（SPEC §2：退出不删目录）。目录没了，条目就该没了。
                if (Directory.Exists(Path.Combine(endpointsRoot, entry.EndpointId))) kept.Add(entry);
            }

            return kept;
        }

        /// <summary>整体原子替换 root endpoint.json；条目按 endpointId 序数升序（SPEC §7 的排序要求）。</summary>
        private static void Write(string registryPath, List<CourierRegistryEntry> entries)
        {
            var parts = new List<string>(entries.Count);
            for (int index = 0; index < entries.Count; index++) parts.Add(entries[index].ToJson());

            string json = new CourierJsonObject()
                .AddNumber("protocolVersion", CourierProtocol.PROTOCOL_VERSION)
                .AddString("generatedAtUtc", CourierProtocol.FormatUtc(DateTime.UtcNow))
                .AddRaw("endpoints", CourierJson.Array(parts))
                .Build();

            // SPEC §7：MUST 整体原子替换，MUST NOT 增量追加。
            CourierIO.WriteAtomic(registryPath, json, true);
        }

        /// <summary>排序比较器；用 <see cref="StringComparer.Ordinal"/> 而不是区域性比较，保证跨机器一致。</summary>
        private static int CompareEntries(CourierRegistryEntry left, CourierRegistryEntry right)
        {
            return string.CompareOrdinal(left.EndpointId, right.EndpointId);
        }
    }

    /// <summary>root 注册表里的一条端点条目（SPEC §7）。字段严格是这四个，多一个都不是协议定义的。</summary>
    internal sealed class CourierRegistryEntry
    {
        /// <summary>一条注册表条目：SPEC §7 允许的四个字段之外不写任何东西。</summary>
        internal CourierRegistryEntry(string endpointId, string description, string sessionId, string state)
        {
            EndpointId = endpointId;
            Description = description ?? string.Empty;
            SessionId = sessionId ?? string.Empty;
            State = state ?? string.Empty;
        }

        internal string EndpointId { get; }
        internal string Description { get; }
        internal string SessionId { get; }

        /// <summary>写这条时的心跳快照（SPEC §6）。允许过期，读方 MUST NOT 把它当权威。</summary>
        internal string State { get; }

        /// <summary>从解析结果取一条条目；缺 endpointId 或 state 不在三个取值里时返回 null。</summary>
        internal static CourierRegistryEntry? TryParse(Dictionary<string, object?> source)
        {
            string? endpointId = CourierJson.GetString(source, "endpointId");
            if (endpointId == null || !CourierProtocol.IsValidEndpointId(endpointId)) return null;
            return new CourierRegistryEntry(
                endpointId,
                CourierJson.GetString(source, "description") ?? string.Empty,
                CourierJson.GetString(source, "sessionId") ?? string.Empty,
                CourierJson.GetString(source, "state") ?? string.Empty);
        }

        /// <summary>序列化成单行 JSON，字段顺序固定为 endpointId/description/sessionId/state。</summary>
        internal string ToJson()
        {
            return new CourierJsonObject()
                .AddString("endpointId", EndpointId)
                .AddString("description", Description)
                .AddString("sessionId", SessionId)
                .AddString("state", State)
                .Build();
        }
    }
}
