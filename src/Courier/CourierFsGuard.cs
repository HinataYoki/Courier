using System;
using System.Collections.Generic;
using System.IO;

namespace Courier
{
    /// <summary>
    /// 路径逃逸与重解析点校验（SPEC §3.6）。所有由布局派生的写盘路径在<strong>每次</strong>落盘前
    /// 都要过这里：目录可以在运行期间被别人换成符号链接，只在启动时校验一次等于没校验。
    /// <para>
    /// 校验分两步：先用 <see cref="Path.GetFullPath(string)"/> + <see cref="Path.GetRelativePath(string, string)"/> 做拼接后判定
    /// （§3.6 第 1–3 条），再对 root 之下、目标路径之上的<strong>已存在</strong>组件逐段查
    /// <see cref="FileAttributes.ReparsePoint"/>（§3.6 第 4 条）。
    /// </para>
    /// <para>
    /// 性能取舍：字符串判定是纯内存操作（微秒级），真正的成本是每段一次
    /// <see cref="File.GetAttributes(string)"/>。这里<strong>不做段缓存</strong>：单一写盘只拿得到目标路径，
    /// 想知道"边界在哪"必须先有 root，而 root 恰好是最容易被整体换成链接的那一个，
    /// 缓存它等于把 §3.6 第 5 条做过一次。实测一次完整校验（root + 三层目录共约 4–5 次 stat）
    /// 是几十微秒量级，与 <see cref="CourierIO.WriteAtomic"/> 自身的 CreateNew + rename（非 durable 约 0.1 ms、
    /// durable 约 1.5 ms）同阶或更小。
    /// </para>
    /// </summary>
    internal static class CourierFsGuard
    {
        /// <summary>
        /// 规范化一个本实现即将使用的绝对路径。<see cref="Path.GetFullPath(string)"/> 会消解 <c>.</c> 与 <c>..</c>，
        /// 之后再拿它去比对 root 前缀才是可信的（§3.6 第 1 条）。
        /// </summary>
        /// <param name="path">待规范化的路径，可为相对路径（相对进程当前目录）。</param>
        /// <returns>绝对路径。</returns>
        /// <exception cref="ArgumentException">路径为空或含非法字符。</exception>
        internal static string Normalize(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            return Path.GetFullPath(path);
        }

        /// <summary>
        /// 判定 <paramref name="candidate"/> 是否落在 <paramref name="rootDirectory"/> 之下，并按 §3.6 比对重解析点。
        /// <para>
        /// 只读路径（扫描、CLI 打不开的目录）用它就够了：它不缓存、不建目录，纯粹回答"能不能安全地碰这条路径"。
        /// </para>
        /// </summary>
        /// <param name="rootDirectory">协议根目录；相对路径按进程当前目录解析。</param>
        /// <param name="candidate">待判定的路径。</param>
        /// <param name="reason">不通过时给宿主看的原因（含逃逸/链接的说明），通过时为 null。</param>
        /// <returns>安全时为 true；逃逸或穿过重解析点时为 false。</returns>
        internal static bool IsSafe(string rootDirectory, string candidate, out string? reason)
        {
            reason = null;
            if (rootDirectory == null || candidate == null)
            {
                reason = "path check needs both a root and a candidate path";
                return false;
            }

            string fullRoot;
            string fullCandidate;
            try
            {
                fullRoot = Path.GetFullPath(rootDirectory);
                fullCandidate = Path.GetFullPath(candidate);
            }
            catch (Exception caught) when (caught is ArgumentException || caught is NotSupportedException || caught is PathTooLongException)
            {
                reason = "path is not a valid absolute path: " + caught.Message;
                return false;
            }

            return IsUnderRoot(fullRoot, fullCandidate, out reason) && HasNoReparsePoint(fullRoot, fullCandidate, out reason);
        }

        /// <summary>
        /// 拼接后校验（§3.6 第 1–3 条）：把 <paramref name="parts"/> 依次接到 <paramref name="rootDirectory"/> 上，
        /// 再用 <see cref="Path.GetRelativePath(string, string)"/> 判定结果是否仍在 root 之下。
        /// </summary>
        /// <param name="rootDirectory">协议根目录。</param>
        /// <param name="parts">相对片段，按顺序拼接。</param>
        /// <param name="fullPath">通过时的绝对路径。</param>
        /// <param name="reason">不通过时的原因。</param>
        /// <returns>是否仍在 root 之下。</returns>
        internal static bool TryResolve(string rootDirectory, IReadOnlyList<string> parts, out string fullPath, out string? reason)
        {
            fullPath = string.Empty;
            reason = null;
            string fullRoot;
            try
            {
                fullRoot = Path.GetFullPath(rootDirectory);
            }
            catch (Exception caught) when (caught is ArgumentException || caught is NotSupportedException || caught is PathTooLongException)
            {
                reason = "root directory is not a valid path: " + caught.Message;
                return false;
            }

            string combined = fullRoot;
            for (int index = 0; index < parts.Count; index++)
            {
                combined = Path.Combine(combined, parts[index]);
            }

            string fullCandidate;
            try
            {
                fullCandidate = Path.GetFullPath(combined);
            }
            catch (Exception caught) when (caught is ArgumentException || caught is NotSupportedException || caught is PathTooLongException)
            {
                reason = "path escapes the root or is not a valid path: " + caught.Message;
                return false;
            }

            if (!IsUnderRoot(fullRoot, fullCandidate, out reason)) return false;
            fullPath = fullCandidate;
            return true;
        }

        /// <summary>
        /// 判定 <paramref name="fullCandidate"/> 是否在 <paramref name="fullRoot"/> 之下。
        /// 两个参数必须已经过 <see cref="Path.GetFullPath(string)"/>。
        /// </summary>
        /// <remarks>
        /// 前缀比较 MUST 带结尾分隔符：不带分隔符时 <c>/root/app</c> 会被 <c>/root/app-evil</c> 骗过（§3.6 第 2 条）。
        /// 大小写敏感性跟着平台走（§3.6 第 3 条）：Windows 不敏感，其余平台敏感。
        /// </remarks>
        /// <param name="fullRoot">规范化的 root。</param>
        /// <param name="fullCandidate">规范化的候选路径。</param>
        /// <param name="reason">逃逸时给出的原因。</param>
        /// <returns>在 root 之下（含 root 本身）时为 true。</returns>
        private static bool IsUnderRoot(string fullRoot, string fullCandidate, out string? reason)
        {
            reason = null;
            string rootWithSeparator = AppendSeparator(fullRoot);
            if (string.Equals(fullCandidate, fullRoot, PathComparison)) return true;

            string relative = Path.GetRelativePath(fullRoot, fullCandidate);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                reason = "path escapes the protocol root (SPEC §3.6): " + fullCandidate + " is not under " + fullRoot;
                return false;
            }

            // 再用带分隔符的前缀比一次：GetRelativePath 在大小写不敏感平台上未必报告的差异由这里兜住。
            if (!fullCandidate.StartsWith(rootWithSeparator, PathComparison))
            {
                reason = "path escapes the protocol root (SPEC §3.6): " + fullCandidate + " is not under " + fullRoot;
                return false;
            }

            return true;
        }

        /// <summary>
        /// 逐段检查 root 及其下每一段<strong>已存在</strong>的路径组件是不是重解析点（§3.6 第 4 条）。
        /// 不存在的组件视为通过——它还没被创建，无从穿越。
        /// </summary>
        /// <param name="fullRoot">规范化的 root。</param>
        /// <param name="fullCandidate">规范化的候选路径，必须已在 root 之下。</param>
        /// <param name="reason">命中重解析点时的原因。</param>
        /// <returns>没有穿过任何重解析点时为 true。</returns>
        private static bool HasNoReparsePoint(string fullRoot, string fullCandidate, out string? reason)
        {
            reason = null;
            if (!IsReparsePointOfConcern(fullRoot, out reason)) return false;

            string relative = Path.GetRelativePath(fullRoot, fullCandidate);
            if (relative == "." || relative.Length == 0) return true;

            string current = fullRoot;
            string[] segments = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < segments.Length; index++)
            {
                current = Path.Combine(current, segments[index]);
                if (!IsReparsePointOfConcern(current, out reason)) return false;
            }

            return true;
        }

        /// <summary>检查单个组件；命中重解析点时写出原因，组件不存在时按通过处理。</summary>
        /// <param name="path">组件路径。</param>
        /// <param name="reason">命中时的原因。</param>
        /// <returns>安全时为 true。</returns>
        private static bool IsReparsePointOfConcern(string path, out string? reason)
        {
            reason = null;
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (Exception caught) when (caught is FileNotFoundException || caught is DirectoryNotFoundException)
            {
                // 还不存在：它不是链接，也就无从穿越（§3.6 第 4 条明确允许）。
                return true;
            }
            catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException || caught is NotSupportedException)
            {
                // 连属性都读不到就无法确认它不是链接：宁可拒绝（写不出去好过写到 root 外面）。
                reason = "cannot verify that " + path + " is not a reparse point (SPEC §3.6): " + caught.Message;
                return false;
            }

            if ((attributes & FileAttributes.ReparsePoint) == 0) return true;

            reason = "refusing to write through a reparse point (SPEC §3.6): " + path
                + " is a symbolic link, junction or mount point";
            return false;
        }

        /// <summary>已登记的协议根数量上限；超出后按登记顺序淘汰最旧的一个。</summary>
        private const int MAX_REGISTERED_ROOTS = 16;

        /// <summary>保护 <see cref="sRegisteredRoots"/> 的锁：节点线程、收尾线程与 GC 线程都会写盘。</summary>
        private static readonly object sRootLock = new object();

        /// <summary>已知的协议根（规范化后的绝对路径），按登记顺序排列。</summary>
        private static readonly List<string> sRegisteredRoots = new List<string>(MAX_REGISTERED_ROOTS);

        /// <summary>
        /// 登记一个协议根，登记后 <see cref="RequireSafeWrite"/> 才能判断某条待写路径的逃逸与重解析点。
        /// <para>
        /// 单次写盘只看得到目标路径，看不到 root；root 是唯一知道"边界在哪"的地方，所以由
        /// <see cref="CourierLayout.Load"/> / <see cref="CourierLayout.TryLoad"/> 在构造布局时登记——
        /// 那正是每个宿主拿到 root × layout 组合的唯一入口（节点启动、CLI 启动各一次）。
        /// 登记是幂等的，重复登记同一个 root 不会让表增长。
        /// </para>
        /// </summary>
        /// <param name="rootDirectory">协议根目录；空串或非法路径被忽略（布局加载处会另行报错）。</param>
        internal static void RegisterRoot(string rootDirectory)
        {
            if (string.IsNullOrEmpty(rootDirectory)) return;
            string full;
            try
            {
                full = Path.GetFullPath(rootDirectory);
            }
            catch (Exception caught) when (caught is ArgumentException || caught is NotSupportedException || caught is PathTooLongException)
            {
                // 非法 root 不在这里报错：布局加载路径会用 InvalidDataException 明确拒绝（§3.6 第 1 条）。
                return;
            }

            lock (sRootLock)
            {
                for (int index = 0; index < sRegisteredRoots.Count; index++)
                {
                    if (string.Equals(sRegisteredRoots[index], full, PathComparison)) return;
                }

                if (sRegisteredRoots.Count == MAX_REGISTERED_ROOTS) sRegisteredRoots.RemoveAt(0);
                sRegisteredRoots.Add(full);
            }
        }

        /// <summary>
        /// 找到包含 <paramref name="fullPath"/> 的最长已登记根，并把它挪到登记表末尾（最近使用）。
        /// </summary>
        /// <remarks>
        /// 这个"挪到末尾"不是修饰：登记表满了会淘汰**最旧的一条**，如果按先进先出算，
        /// 一个长期在线、每次落盘都在用自己 root 的节点反而最先被淘汰，之后它的所有写盘就
        /// 悄悄跳过了 §3.6 校验。按最近使用淘汰才是这张表该有的语义。
        /// </remarks>
        /// <param name="fullPath">已规范化的绝对路径。</param>
        /// <returns>匹配的根；未登记过匹配根时为 null。</returns>
        private static string? FindRoot(string fullPath)
        {
            string? best = null;
            int bestIndex = -1;
            lock (sRootLock)
            {
                for (int index = 0; index < sRegisteredRoots.Count; index++)
                {
                    string candidate = sRegisteredRoots[index];
                    if (!IsUnderRoot(candidate, fullPath, out _)) continue;
                    if (best == null || candidate.Length > best.Length)
                    {
                        best = candidate;
                        bestIndex = index;
                    }
                }

                if (bestIndex < 0) return null;
                if (bestIndex == sRegisteredRoots.Count - 1) return best;

                sRegisteredRoots.RemoveAt(bestIndex);
                sRegisteredRoots.Add(best!);
            }

            return best;
        }

        /// <summary>
        /// 落盘前校验（SPEC §3.6 第 5 条）：每次写入、每次建目录都要过这里，不能在启动时查一次就算。
        /// <para>
        /// 判定范围是从已登记 root 到目标路径：先做 <see cref="Path.GetFullPath(string)"/> 拼接后逃逸判定
        /// （第 1–3 条），再逐段查重解析点（第 4 条）。只检查 root 及其下组件——root 之上是宿主自己的选择，
        /// 不在协议职责内，对它们逐段做 stat 只会让重定向 profile 目录（如 OneDrive）误伤。
        /// </para>
        /// </summary>
        /// <param name="path">即将写入或创建的路径。</param>
        /// <exception cref="IOException">
        /// 路径逃逸出 root、穿过重解析点，或无法确认组件不是重解析点时抛出。
        /// 抛 <see cref="IOException"/> 而不是自定义类型，是为了让现有各处的 I/O 失败处理（
        /// 投递拒绝、能力注册失败、热描述符写失败）照常生效，异常不会穿到宿主。
        /// </exception>
        internal static void RequireSafeWrite(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception caught) when (caught is ArgumentException || caught is NotSupportedException || caught is PathTooLongException)
            {
                throw new IOException("refusing to write an unusable path (SPEC §3.6): " + path + " — fix the path or the layout", caught);
            }

            string? root = FindRoot(full);
            // 没有登记过 root 的调用方（直接调用 CourierIO 的测试、一次性脚本）无法判断边界，保持原行为。
            // 产品路径全部经由 CourierLayout.Load/TryLoad 登记，因此节点与 CLI 的每次落盘都被覆盖。
            if (root == null) return;

            if (!IsUnderRoot(root, full, out string? reason) || !HasNoReparsePoint(root, full, out reason))
            {
                throw new IOException(reason!);
            }
        }

        /// <summary>给路径补上结尾分隔符，避免前缀比较被同名前缀的兄弟目录骗过。</summary>
        /// <param name="path">目录路径。</param>
        /// <returns>以分隔符结尾的路径。</returns>
        private static string AppendSeparator(string path)
        {
            if (path.Length > 0 && (path[path.Length - 1] == Path.DirectorySeparatorChar || path[path.Length - 1] == Path.AltDirectorySeparatorChar)) return path;
            return path + Path.DirectorySeparatorChar;
        }

        /// <summary>§3.6 第 3 条：Windows 上大小写不敏感，其余平台敏感。</summary>
        internal static readonly StringComparison PathComparison =
            Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }
}
