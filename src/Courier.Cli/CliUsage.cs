using System.IO;

namespace Courier.Cli
{
    /// <summary>用法说明与版本号。帮助文本本身也是给人读的输出，走调用方指定的流。</summary>
    internal static class CliUsage
    {
        /// <summary>CLI 自身的版本号；协议版本另见 <see cref="CourierProtocol.ProtocolVersion"/>。</summary>
        internal const string Version = "0.1.0";

        /// <summary>把完整用法写到指定流。</summary>
        /// <param name="writer">stdout（--help）或 stderr（用法错误）。</param>
        internal static void Write(TextWriter writer)
        {
            writer.Write(
                "courier - talk to Courier endpoints through a directory\n" +
                "\n" +
                "usage:\n" +
                "  courier endpoints [--json] [--root PATH]\n" +
                "  courier describe <endpointId> [--json] [--root PATH]\n" +
                "  courier call <endpointId> <channel>/<action> [--payload TEXT] [--timeout 10s] [--json] [--root PATH]\n" +
                "  courier ps [--json] [--root PATH]\n" +
                "  courier state <endpointId> <name> [--json] [--root PATH]\n" +
                "  courier version [--json]\n" +
                "  courier --help\n" +
                "\n" +
                "options:\n" +
                "  --root PATH      protocol root; default: the nearest .courier directory, searching upward\n" +
                "  --json           one line of compact JSON on stdout; errors become {\"error\":..,\"detail\":..} on stderr\n" +
                "  --payload TEXT   opaque string handed to the endpoint handler\n" +
                "  --timeout DUR    how long to wait for a response, e.g. 500ms, 10s, 1m; default 10s\n" +
                "\n" +
                "state reads the latest value an endpoint published under state/<name>.json.\n" +
                "It has no side effects and never waits: missing means the endpoint has not published yet.\n" +
                "\n" +
                "exit codes:\n" +
                "  0 ok   1 rejected   2 failed   3 unreachable\n");
        }
    }
}
