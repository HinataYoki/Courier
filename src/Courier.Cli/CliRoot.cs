using System;
using System.IO;

namespace Courier.Cli
{
    /// <summary>
    /// 协议根目录的解析：零配置向上查找一个约定目录名，或使用 --root 覆盖。
    /// <para>
    /// 向上查找是 CLI 存在的理由之一：Agent 在任意子目录里执行 `courier` 都应该能用，
    /// 而不是先被要求 cd 到某个固定位置。
    /// </para>
    /// <para>
    /// 目录名不是协议的一部分。<see cref="DIRECTORY_NAME"/> 只是"没人指定时"的默认值；
    /// 接入软件可以把自己的约定名（.agent、.kiro、workspace/courier …）通过 --root-name
    /// 或环境变量 COURIER_ROOT_NAME 传进来，CLI 不需要为每家软件改一次源码。
    /// </para>
    /// </summary>
    internal static class CliRoot
    {
        /// <summary>零配置时约定的目录名；等价于 <see cref="CourierLayout.DEFAULT_ROOT_NAME"/>。</summary>
        internal const string DIRECTORY_NAME = ".courier";

        /// <summary>替代 <see cref="DIRECTORY_NAME"/> 的环境变量名；--root-name 的优先级高于它。</summary>
        internal const string ROOT_NAME_VARIABLE = "COURIER_ROOT_NAME";

        /// <summary>--root / --root-name 本身写错了（空值、互相冲突、不是一个合法的目录名或路径）。</summary>
        internal const string ERROR_INVALID_ARGUMENT = "invalid-argument";

        /// <summary>参数没错，但这台机器的磁盘上没有对应的协议根目录。</summary>
        internal const string ERROR_NOT_FOUND = "root-not-found";

        /// <summary>
        /// 解析出协议根目录。
        /// <para>
        /// --root 与 --root-name 互斥：前者直接给出根，后者的作用只是"向上查找时换个名字"，
        /// 同时给出时后者必然不生效——静默忽略会让人以为查找路径被改了，所以这里直接判错。
        /// 空值同理：`--root \"\"` 以前会悄悄退化成向上查找，等于把一个写错的参数当成没写。
        /// </para>
        /// <para>
        /// 失败分两类并给出不同的错误码：参数写错了（<see cref="ERROR_INVALID_ARGUMENT"/>，改参数就能解决）
        /// 与目录确实不存在（<see cref="ERROR_NOT_FOUND"/>，要先把根建出来或 cd 到它下面）。
        /// </para>
        /// </summary>
        /// <param name="explicitRoot">--root 的值；为 null 时从当前目录向上查找协议目录。</param>
        /// <param name="rootName">
        /// --root-name 的值：向上查找时用的目录名。为 null 时读环境变量
        /// <see cref="ROOT_NAME_VARIABLE"/>，再没有就用 <see cref="DIRECTORY_NAME"/>。
        /// </param>
        /// <param name="root">解析成功时的绝对路径。</param>
        /// <param name="detail">解析失败时的人类可读原因。</param>
        /// <param name="errorCode">解析失败时的机器可判别错误码。</param>
        /// <returns>是否解析成功。失败是正常返回值，不是异常。</returns>
        internal static bool TryResolve(
            string? explicitRoot,
            string? rootName,
            out string root,
            out string? detail,
            out string errorCode)
        {
            root = string.Empty;
            errorCode = ERROR_NOT_FOUND;

            if (explicitRoot != null)
            {
                if (explicitRoot.Trim().Length == 0)
                {
                    detail = "--root must not be empty (omit it to search upward for the root directory)";
                    errorCode = ERROR_INVALID_ARGUMENT;
                    return false;
                }

                if (rootName != null)
                {
                    detail = "--root and --root-name cannot be combined: --root already names the protocol root, "
                        + "so --root-name would have no effect";
                    errorCode = ERROR_INVALID_ARGUMENT;
                    return false;
                }

                return TryResolveExplicit(explicitRoot, out root, out detail, out errorCode);
            }

            if (rootName != null && rootName.Trim().Length == 0)
            {
                detail = "--root-name must not be empty (omit it to use " + DIRECTORY_NAME + ", or name a single directory)";
                errorCode = ERROR_INVALID_ARGUMENT;
                return false;
            }

            string name;
            if (!TryResolveRootName(rootName, out name, out detail))
            {
                errorCode = ERROR_INVALID_ARGUMENT;
                return false;
            }

            string? discovered = Discover(name);
            if (discovered == null)
            {
                detail = "no protocol root found: no " + name + " directory in "
                    + CurrentDirectory() + " or any parent directory (use --root <path> to point at one, "
                    + "or --root-name <name> to look for a different directory name)";
                return false;
            }

            root = discovered;
            return true;
        }

        /// <summary>把 --root 的原文解析成绝对路径，并要求目录确实存在。</summary>
        /// <param name="explicitRoot">非空的 --root 原文。</param>
        /// <param name="root">解析成功时的绝对路径。</param>
        /// <param name="detail">失败原因。</param>
        /// <param name="errorCode">失败时的错误码：路径本身非法 vs 目录不存在。</param>
        /// <returns>目录是否存在。</returns>
        private static bool TryResolveExplicit(string explicitRoot, out string root, out string? detail, out string errorCode)
        {
            root = string.Empty;
            detail = null;
            errorCode = ERROR_NOT_FOUND;
            string candidate;
            try
            {
                candidate = Path.GetFullPath(explicitRoot);
            }
            catch (Exception caught) when (caught is ArgumentException || caught is NotSupportedException)
            {
                detail = "the path given to --root is not a valid path: " + explicitRoot;
                errorCode = ERROR_INVALID_ARGUMENT;
                return false;
            }

            if (!Directory.Exists(candidate))
            {
                detail = "no protocol root at " + candidate + ": the directory does not exist";
                return false;
            }

            root = candidate;
            return true;
        }

        /// <summary>
        /// 决定向上查找用的目录名：--root-name &gt; 环境变量 &gt; <see cref="DIRECTORY_NAME"/>。
        /// </summary>
        /// <param name="rootName">--root-name 的原始值，可为 null。</param>
        /// <param name="name">解析出的目录名。</param>
        /// <param name="detail">失败原因。</param>
        /// <returns>名字是否可用。</returns>
        internal static bool TryResolveRootName(string? rootName, out string name, out string? detail)
        {
            detail = null;
            string? value = rootName;
            string origin = "--root-name";
            if (value == null)
            {
                value = Environment.GetEnvironmentVariable(ROOT_NAME_VARIABLE);
                origin = ROOT_NAME_VARIABLE;
                if (string.IsNullOrWhiteSpace(value)) value = null;
            }

            if (value == null)
            {
                name = DIRECTORY_NAME;
                return true;
            }

            string trimmed = value.Trim();
            if (trimmed.Length != value.Length)
            {
                // 首尾空白不是"无害的空格"：Windows 会在拼路径时把它吃掉，于是 --root-name \".courier \"
                // 看着指向一个目录、实际指向另一个，而查找结果会静默变成"找不到"。
                name = string.Empty;
                detail = origin + " must not start or end with whitespace, got \"" + value + "\"";
                return false;
            }

            if (!IsValidDirectoryName(trimmed))
            {
                // 这是"路径片段"而不是路径：允许 '.'，不允许分隔符、盘符与 ..，也不允许尾随点。
                name = string.Empty;
                detail = origin + " must be a single directory name without separators and must not end with a dot, got \"" + trimmed + "\"";
                return false;
            }

            name = trimmed;
            return true;
        }

        /// <summary>
        /// 向上查找用的目录名是否合法：单段、非空、非 . / ..、不含分隔符、不是绝对路径、不以点结尾。
        /// <para>
        /// 尾随点必须拒掉：Windows 在解析路径时会把 \"foo.\" 折成 \"foo\"，于是名字写错一个点，
        /// 查找的却是另一个目录，而结果只表现为\"找不到\"。
        /// </para>
        /// </summary>
        /// <param name="value">候选目录名。</param>
        /// <returns>是否可直接 Path.Combine 到一个目录后面。</returns>
        internal static bool IsValidDirectoryName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (value == "." || value == "..") return false;
            if (value!.IndexOf('/') >= 0 || value.IndexOf('\\') >= 0) return false;
            if (value.IndexOf(':') >= 0) return false;
            if (value[value.Length - 1] == '.') return false;
            try
            {
                if (Path.IsPathRooted(value)) return false;
            }
            catch (ArgumentException)
            {
                return false;
            }

            return true;
        }

        /// <summary>从当前目录向上找第一个名为 <paramref name="directoryName"/> 的目录。</summary>
        /// <param name="directoryName">目录名（已校验）。</param>
        /// <returns>绝对路径；一路到盘根都没找到时为 null。</returns>
        private static string? Discover(string directoryName)
        {
            string? current = CurrentDirectory();
            while (current != null && current.Length > 0)
            {
                string candidate = Path.Combine(current, directoryName);
                if (Directory.Exists(candidate))
                {
                    try
                    {
                        return Path.GetFullPath(candidate);
                    }
                    catch (Exception caught) when (caught is ArgumentException || caught is NotSupportedException)
                    {
                        return null;
                    }
                }

                DirectoryInfo? parent = Directory.GetParent(current);
                if (parent == null) return null;
                current = parent.FullName;
            }

            return null;
        }

        /// <summary>当前工作目录；取不到时退回空串（此时向上查找直接结束）。</summary>
        /// <returns>绝对路径或空串。</returns>
        private static string CurrentDirectory()
        {
            try
            {
                return Directory.GetCurrentDirectory();
            }
            catch (IOException)
            {
                return string.Empty;
            }
        }
    }
}
