using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Courier
{
    /// <summary>
    /// 私有命名空间的持有者。创建方给对象加 SDDL（只允许 SY 与当前用户），打开方靠
    /// "边界描述符里的 SID 集合 + 别名"进入同一个命名空间。
    /// <para>
    /// 这不是加密，也挡不住已经拿到 root 读权限的进程 —— 那本来就在信任边界内（SPEC §20）。
    /// 它挡住的是同一次登录里"猜名字"的陌生进程：私有命名空间里的对象在内核命名空间不可枚举。
    /// </para>
    /// </summary>
    internal sealed partial class CourierHotNamespace : IDisposable
    {
        private const uint TOKEN_QUERY = 0x0008;
        private const int TOKEN_USER_CLASS = 1;

        /// <summary>取当前进程的伪句柄；伪句柄不能被 CloseHandle，所以这里不配对。</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentProcess();

        /// <summary>关内核句柄；对本文件里每一个真实句柄（token、SID、边界描述符、命名空间）是唯一归还路径。</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>开本进程的访问令牌，用来读出自己的用户 SID（私有命名空间按 SID 隔离）。</summary>
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        /// <summary>取令牌信息；这里只用它读 <c>TokenUser</c>，缓冲区由调用方按两次调用（先问长度再取值）准备。</summary>
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr buffer, int length, out int returned);

        /// <summary>把 SID 二进制转成 <c>S-1-5-...</c> 文本；返回的文本要 LocalFree。</summary>
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr text);

        /// <summary>把 <c>S-1-5-...</c> 文本转回 SID 二进制；返回的 SID 要 LocalFree。</summary>
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ConvertStringSidToSidW(string text, out IntPtr sid);

        /// <summary>把 SDDL 文本转成安全描述符；私有命名空间用它声明"只允许本用户打开"。</summary>
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, out uint size);

        /// <summary>建一个边界描述符；同一个边界里的命名空间只能被同一个用户看到。</summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateBoundaryDescriptorW(string name, uint flags);

        // 注意：AddSIDToBoundaryDescriptor 在 kernel32.dll，不在 advapi32.dll。
        /// <summary>把本用户 SID 加进边界，令命名空间只对本用户可见；必须传 ref，函数会改写句柄。</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AddSIDToBoundaryDescriptor(ref IntPtr boundary, IntPtr sid);

        /// <summary>在边界里建私有命名空间；宿主用它在内核对象名里占住一个别人抢不走的前缀。</summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreatePrivateNamespaceW(IntPtr attributes, IntPtr boundary, string alias);

        /// <summary>打开已存在的私有命名空间；客户端走这一条，失败通常意味着宿主已经退出。</summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenPrivateNamespaceW(IntPtr boundary, string alias);

        /// <summary>关私有命名空间句柄；<c>flags</c> 传 0 表示保留命名空间内的既有对象。</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ClosePrivateNamespace(IntPtr handle, uint flags);

        /// <summary>释放 Win32 分配的缓冲区（SID 文本、SID 二进制、安全描述符都要经它归还）。</summary>
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        /// <summary>Win32 <c>SECURITY_ATTRIBUTES</c> 的托管镜像；布局必须逐字段对齐，顺序不能改。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public int bInheritHandle;
        }

        /// <summary>本进程用户的 SID 文本；取不到时为 null（此时热通道不可用）。</summary>
        private static readonly string? sSidText = TryLoadSidText();

        /// <summary>驻留的 SID 指针；AddSIDToBoundaryDescriptor 只记指针不复制，绝不能释放。</summary>
        private static IntPtr sSid;

        private static bool sSidReady;

        private readonly IntPtr mHandle;
        private bool mDisposed;

        /// <summary>包装一个已经打开的私有命名空间句柄；归还由 <c>Dispose</c> 负责。</summary>
        private CourierHotNamespace(IntPtr handle)
        {
            mHandle = handle;
        }

        /// <summary>命名空间别名；拼对象全名时用 <c>alias + "\" + 短名</c>。</summary>
        internal string Alias { get; private set; } = string.Empty;

        /// <summary>当前用户是否可以参与私有命名空间（拿得到 SID 才可能）。</summary>
        internal static bool HasIdentity
        {
            get { return EnsureSid(); }
        }

        /// <summary>创建（或接管）命名空间；失败返回 null，调用方据此关闭热通道。</summary>
        /// <param name="rootDirectory">协议根目录。</param>
        /// <param name="endpointId">端点标识。</param>
        /// <param name="alias">命名空间别名。</param>
        /// <returns>命名空间，或 <c>null</c>。</returns>
        internal static CourierHotNamespace? Create(string rootDirectory, string endpointId, string alias)
        {
            if (!CourierHot.IsSupported || !EnsureSid()) return null;

            IntPtr boundary = IntPtr.Zero;
            IntPtr descriptor = IntPtr.Zero;
            try
            {
                boundary = MakeBoundary(CourierHot.BoundaryName(rootDirectory, endpointId));
                if (boundary == IntPtr.Zero) return null;

                string sddl = "D:P(A;;GA;;;SY)(A;;GA;;;" + sSidText + ")";
                uint size;
                if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out descriptor, out size)) return null;

                SECURITY_ATTRIBUTES attributes = new SECURITY_ATTRIBUTES();
                attributes.nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES));
                attributes.lpSecurityDescriptor = descriptor;
                attributes.bInheritHandle = 0;

                IntPtr pointer = Marshal.AllocHGlobal(attributes.nLength);
                IntPtr handle;
                try
                {
                    Marshal.StructureToPtr(attributes, pointer, false);
                    handle = CreatePrivateNamespaceW(pointer, boundary, alias);
                }
                finally
                {
                    Marshal.FreeHGlobal(pointer);
                }

                if (handle == IntPtr.Zero) return null;
                var result = new CourierHotNamespace(handle);
                result.Alias = alias;
                return result;
            }
            catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException || caught is InvalidOperationException)
            {
                return null;
            }
            finally
            {
                if (descriptor != IntPtr.Zero) LocalFree(descriptor);
                if (boundary != IntPtr.Zero) CloseHandle(boundary);
            }
        }

        /// <summary>打开已存在的命名空间；失败返回 null（上一代宿主的残留、或宿主还没起来）。</summary>
        /// <param name="rootDirectory">协议根目录。</param>
        /// <param name="endpointId">端点标识。</param>
        /// <param name="alias">命名空间别名。</param>
        /// <returns>命名空间，或 <c>null</c>。</returns>
        internal static CourierHotNamespace? Open(string rootDirectory, string endpointId, string alias)
        {
            if (!CourierHot.IsSupported || !EnsureSid()) return null;

            IntPtr boundary = IntPtr.Zero;
            try
            {
                boundary = MakeBoundary(CourierHot.BoundaryName(rootDirectory, endpointId));
                if (boundary == IntPtr.Zero) return null;
                IntPtr handle = OpenPrivateNamespaceW(boundary, alias);
                if (handle == IntPtr.Zero) return null;
                var result = new CourierHotNamespace(handle);
                result.Alias = alias;
                return result;
            }
            catch (Exception caught) when (caught is IOException || caught is UnauthorizedAccessException || caught is InvalidOperationException)
            {
                return null;
            }
            finally
            {
                if (boundary != IntPtr.Zero) CloseHandle(boundary);
            }
        }

        /// <summary>建出带当前用户 SID 的边界描述符；失败返回 <see cref="IntPtr.Zero"/>。</summary>
        /// <param name="name">边界名字。</param>
        /// <returns>边界句柄（由调用方 CloseHandle）。</returns>
        private static IntPtr MakeBoundary(string name)
        {
            IntPtr boundary = CreateBoundaryDescriptorW(name, 0);
            if (boundary == IntPtr.Zero) return IntPtr.Zero;
            if (!AddSIDToBoundaryDescriptor(ref boundary, sSid))
            {
                CloseHandle(boundary);
                return IntPtr.Zero;
            }

            return boundary;
        }

        /// <summary>把 SID 文本解析成驻留指针；只尝试一次，失败后不再重试。</summary>
        /// <returns>拿到可用 SID 时为 <c>true</c>。</returns>
        private static bool EnsureSid()
        {
            if (sSidReady) return sSid != IntPtr.Zero;
            if (sSidText == null) return false;
            sSidReady = true;
            IntPtr sid;
            if (!ConvertStringSidToSidW(sSidText, out sid))
            {
                sSid = IntPtr.Zero;
                return false;
            }

            sSid = sid;
            return true;
        }

        /// <summary>读当前进程令牌里的用户 SID 文本；任何一步失败都返回 null（热通道整体退场）。</summary>
        /// <returns>SID 文本（形如 <c>S-1-5-21-...</c>），或 <c>null</c>。</returns>
        private static string? TryLoadSidText()
        {
            if (!CourierHot.IsSupported) return null;

            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token)) return null;
            try
            {
                int size;
                GetTokenInformation(token, TOKEN_USER_CLASS, IntPtr.Zero, 0, out size);
                if (size <= 0) return null;
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!GetTokenInformation(token, TOKEN_USER_CLASS, buffer, size, out size)) return null;
                    IntPtr text;
                    if (!ConvertSidToStringSidW(Marshal.ReadIntPtr(buffer), out text)) return null;
                    try
                    {
                        return Marshal.PtrToStringUni(text);
                    }
                    finally
                    {
                        LocalFree(text);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }

        /// <summary>关闭命名空间句柄；幂等。</summary>
        public void Dispose()
        {
            if (mDisposed) return;
            mDisposed = true;
            if (mHandle != IntPtr.Zero) ClosePrivateNamespace(mHandle, 0);
        }
    }

    /// <summary>
    /// 把"进程退出"这个内核事件包成可等待句柄。<c>Process.WaitHandle</c> 在 .NET 里不公开，
    /// 只能自己接住 <see cref="WaitHandle.SafeWaitHandle"/>（它的 setter 是 protected），
    /// 这样才能把它和门铃放进同一个 <see cref="WaitHandle.WaitAny(WaitHandle[], int)"/>。
    /// </summary>
    internal sealed class ProcessExitHandle : WaitHandle
    {
        /// <summary>接管一个已经打开的进程句柄；句柄所有权转给本对象。</summary>
        /// <param name="native">由 <c>OpenProcess(SYNCHRONIZE, ...)</c> 得到的句柄。</param>
        internal ProcessExitHandle(IntPtr native)
        {
            SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(native, true);
        }
    }
}
