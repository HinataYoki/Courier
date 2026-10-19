using System;
using System.IO;

namespace Courier.Cli
{
    /// <summary>
    /// 协议根目录的解析：零配置向上查找 <c>.courier</c>，或使用 --root 覆盖。
    /// <para>
    /// 向上查找是 CLI 存在的理由之一：Agent 在任意子目录里执行 `courier` 都应该能用，
    /// 而不是先被要求 cd 到某个固定位置。
    /// </para>
    /// </summary>
    internal static class CliRoot
    {
        /// <summary>零配置时约定的目录名。</summary>
        internal const string DirectoryName = ".courier";

        /// <summary>
        /// 解析出协议根目录。
        /// </summary>
        /// <param name="explicitRoot">--root 的值；为 null 时从当前目录向上查找 <c>.courier</c>。</param>
        /// <param name="root">解析成功时的绝对路径。</param>
        /// <param name="detail">解析失败时的人类可读原因。</param>
        /// <returns>是否解析成功。失败是正常返回值，不是异常。</returns>
        internal static bool TryResolve(string? explicitRoot, out string root, out string? detail)
        {
            root = string.Empty;
            detail = null;

            if (!string.IsNullOrEmpty(explicitRoot))
            {
                string candidate;
                try
                {
                    // IsNullOrEmpty 带 [NotNullWhen(true)]，编译器已把 explicitRoot 窄化为非空。
                    candidate = Path.GetFullPath(explicitRoot);
                }
                catch (Exception caught) when (caught is ArgumentException || caught is NotSupportedException)
                {
                    detail = "the path given to --root is not a valid path: " + explicitRoot;
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

            string? discovered = Discover();
            if (discovered == null)
            {
                detail = "no protocol root found: no " + DirectoryName + " directory in "
                    + CurrentDirectory() + " or any parent directory (use --root <path> to point at one)";
                return false;
            }

            root = discovered;
            return true;
        }

        private static string? Discover()
        {
            string? current = CurrentDirectory();
            while (current != null && current.Length > 0)
            {
                string candidate = Path.Combine(current, DirectoryName);
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
