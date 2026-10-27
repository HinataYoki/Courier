using System;
using System.Collections.Generic;
using System.IO;

namespace Courier
{
    /// <summary>
    /// 协议根目录的布局（SPEC §4）：接入软件可以决定根目录叫什么、各子目录叫什么，
    /// 而不是被 <c>.courier</c> 与固定子目录名绑死。
    /// <para>
    /// 布局有两个来源，优先级从高到低：
    /// <list type="number">
    /// <item>调用方直接传入的实例（<c>CourierNode.Open(root, endpointId, description, layout)</c>）。</item>
    /// <item>根目录下的 <c>courier.layout.json</c>（见 <see cref="Load"/>）。</item>
    /// <item><see cref="Default"/>：与 v2 协议逐字节相同的默认名字。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 所有子目录名都必须是相对子路径：不能是绝对路径、不能含 <c>..</c>、不能为空。
    /// 这条校验不是洁癖 —— 子目录名会直接拼进路径，非法值等于让接入方把文件写到根目录之外。
    /// </para>
    /// </summary>
    public sealed class CourierLayout
    {
        /// <summary>根目录下用于声明自定义布局的文件名。它的存在本身就是"这里是协议根"的标记。</summary>
        internal const string LAYOUT_FILE_NAME = "courier.layout.json";

        /// <summary>零配置发现时向上查找的目录名（CLI 的 <c>--root-name</c> 可以覆盖）。</summary>
        internal const string DEFAULT_ROOT_NAME = ".courier";

        /// <summary>热通道账本目录的默认名。它记录"哪些消息走了内存"，属于本机的实现细节而非协议条款，因此不在 SPEC §19 的常量表里。</summary>
        internal const string HOT_EVIDENCE_DEFAULT = "hot";

        private string mEndpoints = CourierProtocol.ENDPOINTS_DIRECTORY;
        private string mCommands = CourierProtocol.COMMANDS_DIRECTORY;
        private string mClaims = CourierProtocol.CLAIMS_DIRECTORY;
        private string mResults = CourierProtocol.RESULTS_DIRECTORY;
        private string mDeadletter = CourierProtocol.DEADLETTER_DIRECTORY;
        private string mCancels = CourierProtocol.CANCELS_DIRECTORY;
        private string mProgress = CourierProtocol.PROGRESS_DIRECTORY;
        private string mState = CourierProtocol.STATE_DIRECTORY;
        private string mArchive = CourierProtocol.ARCHIVE_DIRECTORY;
        private string mHotEvidence = HOT_EVIDENCE_DEFAULT;
        private string mEndpointFileName = CourierProtocol.ENDPOINT_FILE_NAME;
        private string mHeartbeatFileName = CourierProtocol.HEARTBEAT_FILE_NAME;

        /// <summary>创建一个与 v2 协议完全一致的默认布局。</summary>
        public CourierLayout()
        {
        }

        /// <summary>
        /// 默认布局：与 v2 协议逐字节相同的名字。
        /// <para>
        /// 每次返回新实例，而不是共享一个可变单例 —— 否则任何一处 <c>Default.CommandsDirectory = ...</c>
        /// 都会悄悄改掉全进程的默认值，这类 bug 极难定位。
        /// </para>
        /// </summary>
        public static CourierLayout Default
        {
            get { return new CourierLayout(); }
        }

        /// <summary>endpoints/ 下的子路径：每个端点一个目录。</summary>
        public string EndpointsDirectory
        {
            get { return mEndpoints; }
            set { mEndpoints = ValidateSubpath(value, nameof(EndpointsDirectory)); }
        }

        /// <summary>命令投递目录。</summary>
        public string CommandsDirectory
        {
            get { return mCommands; }
            set { mCommands = ValidateSubpath(value, nameof(CommandsDirectory)); }
        }

        /// <summary>租约目录。</summary>
        public string ClaimsDirectory
        {
            get { return mClaims; }
            set { mClaims = ValidateSubpath(value, nameof(ClaimsDirectory)); }
        }

        /// <summary>终态证据目录。</summary>
        public string ResultsDirectory
        {
            get { return mResults; }
            set { mResults = ValidateSubpath(value, nameof(ResultsDirectory)); }
        }

        /// <summary>死信目录。</summary>
        public string DeadletterDirectory
        {
            get { return mDeadletter; }
            set { mDeadletter = ValidateSubpath(value, nameof(DeadletterDirectory)); }
        }

        /// <summary>取消请求目录。</summary>
        public string CancelsDirectory
        {
            get { return mCancels; }
            set { mCancels = ValidateSubpath(value, nameof(CancelsDirectory)); }
        }

        /// <summary>进度目录。</summary>
        public string ProgressDirectory
        {
            get { return mProgress; }
            set { mProgress = ValidateSubpath(value, nameof(ProgressDirectory)); }
        }

        /// <summary>状态发布目录。</summary>
        public string StateDirectory
        {
            get { return mState; }
            set { mState = ValidateSubpath(value, nameof(StateDirectory)); }
        }

        /// <summary>归档目录。</summary>
        public string ArchiveDirectory
        {
            get { return mArchive; }
            set { mArchive = ValidateSubpath(value, nameof(ArchiveDirectory)); }
        }

        /// <summary>端点目录下的热通道账本目录。</summary>
        public string HotEvidenceDirectory
        {
            get { return mHotEvidence; }
            set { mHotEvidence = ValidateSubpath(value, nameof(HotEvidenceDirectory)); }
        }

        /// <summary>端点描述文件名（不随目录变化）。</summary>
        public string EndpointFileName
        {
            get { return mEndpointFileName; }
            set { mEndpointFileName = ValidateFileName(value, nameof(EndpointFileName)); }
        }

        /// <summary>心跳文件名（不随目录变化）。</summary>
        public string HeartbeatFileName
        {
            get { return mHeartbeatFileName; }
            set { mHeartbeatFileName = ValidateFileName(value, nameof(HeartbeatFileName)); }
        }

        /// <summary>把相对子路径拼到根目录上。</summary>
        /// <param name="rootDirectory">根目录。</param>
        /// <param name="subpath">布局里的相对子路径。</param>
        /// <returns>拼好的路径。</returns>
        internal static string Combine(string rootDirectory, string subpath)
        {
            return Path.Combine(rootDirectory, subpath);
        }

        /// <summary>
        /// 读取根目录下的 <c>courier.layout.json</c>；文件不存在时返回 <see cref="Default"/>。
        /// <para>
        /// 解析失败绝不静默回落到默认值：那会让"布局写错了"表现为"读到了空目录"，
        /// 排查成本远高于当场报错。所以这里抛 <see cref="InvalidDataException"/>。
        /// </para>
        /// </summary>
        /// <param name="rootDirectory">协议根目录。</param>
        /// <returns>非 null 的布局。</returns>
        internal static CourierLayout Load(string rootDirectory)
        {
            // §3.6 的边界只有这里知道：每个宿主都在启动时经由此处拿到 root × layout 组合，
            // 登记之后 CourierIO 的每次落盘才能判断目标路径是否逃逸或穿过重解析点。
            CourierFsGuard.RegisterRoot(rootDirectory);
            string path = Path.Combine(rootDirectory, LAYOUT_FILE_NAME);
            string? text;
            if (!CourierIO.TryReadAllText(path, out text)) return LoadOrDefaultWithMissingCheck(path, rootDirectory);

            Dictionary<string, object?>? parsed;
            if (!CourierJson.TryParseObject(text, out parsed))
            {
                throw new InvalidDataException(LAYOUT_FILE_NAME + " is not a JSON object: " + path);
            }

            long? version = CourierJson.GetInteger(parsed, "version");
            if (version != null && version.Value != 1)
            {
                throw new InvalidDataException(LAYOUT_FILE_NAME + " declares unsupported version " + version.Value + ": " + path);
            }

            var layout = new CourierLayout();
            layout.Apply(parsed, "endpoints", value => layout.EndpointsDirectory = value);
            layout.Apply(parsed, "commands", value => layout.CommandsDirectory = value);
            layout.Apply(parsed, "claims", value => layout.ClaimsDirectory = value);
            layout.Apply(parsed, "results", value => layout.ResultsDirectory = value);
            layout.Apply(parsed, "deadletter", value => layout.DeadletterDirectory = value);
            layout.Apply(parsed, "cancels", value => layout.CancelsDirectory = value);
            layout.Apply(parsed, "progress", value => layout.ProgressDirectory = value);
            layout.Apply(parsed, "state", value => layout.StateDirectory = value);
            layout.Apply(parsed, "archive", value => layout.ArchiveDirectory = value);
            layout.Apply(parsed, "hotEvidence", value => layout.HotEvidenceDirectory = value);
            layout.Apply(parsed, "endpointFile", value => layout.EndpointFileName = value);
            layout.Apply(parsed, "heartbeatFile", value => layout.HeartbeatFileName = value);
            return layout;
        }

        /// <summary>
        /// <see cref="CourierIO.TryReadAllText"/> 失败后的分流：真的没有布局文件才回落默认值。
        /// </summary>
        /// <remarks>
        /// 契约是"解析失败绝不静默回落"，而 TryReadAllText 把"文件不存在"与"文件存在但读不到"
        /// （被独占占用、ACL 拒绝、路径过长）都报成 false。前者回落是对的，后者回落会让"布局读不到"
        /// 表现为"读到了默认布局"——宿主以为自己用的是标准目录，实际写盘位置与预期的完全不符，
        /// 这种错排查起来比当场报错贵得多，所以后者一律抛 <see cref="InvalidDataException"/>。
        /// </remarks>
        /// <param name="path">布局文件路径。</param>
        /// <param name="rootDirectory">协议根目录，仅用于组织错误消息。</param>
        /// <returns>布局文件确实不存在时的默认布局。</returns>
        /// <exception cref="InvalidDataException">布局文件存在但读不到时抛出。</exception>
        private static CourierLayout LoadOrDefaultWithMissingCheck(string path, string rootDirectory)
        {
            if (Directory.Exists(path) || File.Exists(path))
            {
                throw new InvalidDataException(
                    LAYOUT_FILE_NAME + " exists under " + rootDirectory + " but could not be read: " + path
                    + " (close the process that holds it, fix its permissions, or delete it to use the default layout)");
            }

            return Default;
        }

        /// <summary>读取布局，失败时返回 false 并把原因写进 detail（CLI 用它生成结构化错误）。</summary>
        /// <param name="rootDirectory">协议根目录。</param>
        /// <param name="layout">解析成功时的布局。</param>
        /// <param name="detail">失败原因。</param>
        /// <returns>是否成功。</returns>
        internal static bool TryLoad(string rootDirectory, out CourierLayout layout, out string? detail)
        {
            try
            {
                layout = Load(rootDirectory);
                detail = null;
                return true;
            }
            catch (Exception caught) when (caught is IOException || caught is InvalidDataException || caught is ArgumentException || caught is UnauthorizedAccessException)
            {
                layout = Default;
                detail = caught.Message;
                return false;
            }
        }

        /// <summary>读一个可选字段并交给 setter；字段缺失就保留默认值，setter 抛参数异常时转成布局文件无效。</summary>
        /// <param name="parsed">布局文件解析结果。</param>
        /// <param name="name">字段名。</param>
        /// <param name="set">把文本写进对应属性的委托。</param>
        /// <exception cref="InvalidDataException">值不合法（消息里带字段名与原因）。</exception>
        private void Apply(Dictionary<string, object?> parsed, string name, Action<string> set)
        {
            string? value = CourierJson.GetString(parsed, name);
            if (value == null) return;
            try
            {
                set(value);
            }
            catch (ArgumentException caught)
            {
                throw new InvalidDataException(LAYOUT_FILE_NAME + ": \"" + name + "\" " + caught.Message, caught);
            }
        }

        /// <summary>校验一个子目录名：非空、不是绝对路径、不含分隔符、拒绝 <c>.</c>/<c>..</c> 与首尾空白（SPEC §3.6）。</summary>
        /// <param name="value">布局文件里读到的原始文本。</param>
        /// <param name="name">字段名，用于错误消息。</param>
        /// <returns>去空白后的子目录名。</returns>
        /// <exception cref="ArgumentException">不合规。</exception>
        private static string ValidateSubpath(string? value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("must not be empty", name);
            }

            string trimmed = value.Trim();
            if (Path.IsPathRooted(trimmed))
            {
                throw new ArgumentException("must be a relative path", name);
            }

            string[] parts = trimmed.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                throw new ArgumentException("must not be empty", name);
            }

            for (int index = 0; index < parts.Length; index++)
            {
                string part = parts[index];
                if (part == "." || part == "..")
                {
                    throw new ArgumentException("must not contain \".\" or \"..\" segments", name);
                }

                if (!CourierProtocol.IsValidName(part))
                {
                    throw new ArgumentException("must match ^[A-Za-z0-9_.-]{1,64}$ per segment", name);
                }
            }

            return trimmed;
        }

        /// <summary>校验一个文件名字段：非空、必须落在当前目录、不含分隔符与 <c>..</c> 片段。</summary>
        /// <param name="value">布局文件里读到的原始文本。</param>
        /// <param name="name">字段名，用于错误消息。</param>
        /// <returns>去空白后的文件名。</returns>
        /// <exception cref="ArgumentException">不合规。</exception>
        private static string ValidateFileName(string? value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("must not be empty", name);
            }

            string trimmed = value.Trim();
            if (trimmed == "." || trimmed == ".." || trimmed.IndexOf('/') >= 0 || trimmed.IndexOf('\\') >= 0 || Path.IsPathRooted(trimmed))
            {
                throw new ArgumentException("must be a bare file name", name);
            }

            if (!CourierProtocol.IsValidName(trimmed))
            {
                throw new ArgumentException("must match ^[A-Za-z0-9_.-]{1,64}$", name);
            }

            return trimmed;
        }
    }
}
