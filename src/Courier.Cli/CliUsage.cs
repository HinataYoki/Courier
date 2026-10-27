using System.IO;

namespace Courier.Cli
{
    /// <summary>用法说明与版本号。帮助文本本身也是给人读的输出，走调用方指定的流。</summary>
    internal static class CliUsage
    {
        /// <summary>CLI 自身的版本号；协议版本另见 <see cref="CourierProtocol.PROTOCOL_VERSION"/>。</summary>
        internal const string VERSION = "0.1.0";

        /// <summary>把完整用法写到指定流。</summary>
        /// <param name="writer">stdout（--help）或 stderr（用法错误）。</param>
        internal static void Write(TextWriter writer)
        {
            writer.Write(USAGE);
        }

        /// <summary>
        /// 用法全文。单独提出来，是为了让"写哪个流"与"写什么"分开：
        /// 帮助写 stdout，用法错误写 stderr，两边共用同一份文本。
        /// </summary>
        private const string USAGE =
            "courier - talk to Courier endpoints through a directory\n" +
            "\n" +
            "usage:\n" +
            "  courier endpoints [--json] [--root PATH] [--root-name NAME]\n" +
            "  courier describe <endpointId> [--json] [--root PATH] [--root-name NAME]\n" +
            "  courier call <endpointId> <channel>/<action> [--payload TEXT] [--timeout 10s] [--json] [--root PATH] [--root-name NAME]\n" +
            "  courier ps [--json] [--root PATH] [--root-name NAME]\n" +
            "  courier state <endpointId> <name> [--json|--raw] [--root PATH] [--root-name NAME]\n" +
            "  courier gc [<endpointId>] --older-than 1h [--json] [--root PATH] [--root-name NAME]\n" +
            "  courier progress <endpointId> <requestId> [--json|--raw] [--root PATH] [--root-name NAME]\n" +
            "  courier cancel <endpointId> <requestId> [--reason TEXT] [--json] [--root PATH] [--root-name NAME]\n" +
            "  courier version [--json]\n" +
            "  courier --help\n" +
            "\n" +
            "options:\n" +
            "  --root PATH      protocol root; default: the nearest directory named by --root-name, searching upward\n" +
            "  --root-name NAME directory name to search for when --root is absent; default: .courier\n" +
            "                   (also read from the COURIER_ROOT_NAME environment variable)\n" +
            "  --json           one line of compact JSON on stdout; errors become {\"error\":..,\"detail\":..} on stderr\n" +
            "  --payload TEXT   opaque string handed to the endpoint handler; use --payload=VALUE when it starts with -\n" +
            "  --timeout DUR    how long to wait for a response, e.g. 500ms, 10s, 1m; default 10s (capped at 300s)\n" +
            "  --older-than DUR how old a finished response must be before gc removes it, e.g. 1h, 30m, 7d\n" +
            "  --raw            state/progress: print the file as stored instead of the wrapped JSON;\n" +
            "                   cannot be combined with --json\n" +
            "  --reason TEXT    cancel: why, stored in the cancel file; default: requested (max 512 characters)\n" +
            "\n" +
            "Each command accepts only the options listed for it above: an option that does not apply is an\n" +
            "error rather than a silent no-op, so \"courier endpoints --timeout 5s\" fails instead of pretending.\n" +
            "\n" +
            "The directory layout inside the root is read from courier.layout.json when present, so an\n" +
            "embedding application can rename endpoints/, commands/, state/ and friends without a rebuild.\n" +
            "A layout file that cannot be parsed is an error (exit code 4), never a silent fallback to defaults.\n" +
            "\n" +
            "state reads the latest value an endpoint published under state/<name>.json.\n" +
            "It has no side effects and never waits: missing means the endpoint has not published yet, which\n" +
            "is not an error. --json prints {endpointId,name,published,updatedAtUtc,revision,payload} with the\n" +
            "fields the file lacks omitted, or {endpointId,name,published:false} when nothing was published;\n" +
            "--raw prints the file as is. An endpoint that is not registered at all is an error: the command\n" +
            "reports endpoint-not-found and exits 4, so \"no such endpoint\" cannot be mistaken for\n" +
            "\"registered but has not published this state yet\".\n" +
            "\n" +
            "gc removes results/<requestId>.json files that finished before the cutoff and are no longer in\n" +
            "flight. It writes results/gc.json first, so a later reader can tell \"collected\" from \"never seen\".\n" +
            "Without <endpointId> it sweeps every endpoint under the root. A failure on one endpoint does not\n" +
            "stop the sweep: the summary lists the failures, and deleted results still show up in the total.\n" +
            "The exit code is 0 only when every endpoint was collected.\n" +
            "\n" +
            "progress reads progress/<requestId>.json, the latest snapshot the serving endpoint overwrote\n" +
            "(SPEC §13.2). No file means \"no progress information\", not an error: it still exits 0.\n" +
            "cancel drops a cancels/<requestId>.json for the target endpoint to find on its next tick. It is\n" +
            "best effort: exit 0 only means the request was delivered, not that the command stopped.\n" +
            "\n" +
            "describe/endpoints also show the endpoint's optional \"capabilities\" array when it declares one.\n" +
            "A call whose result file is gone prints {\"reclaimed\":true} when results/gc.json proves it was\n" +
            "collected; the status stays \"unreachable\" and the exit code is unchanged.\n" +
            "\n" +
            "exit codes:\n" +
            "  0 ok   1 rejected   2 failed   3 unreachable\n" +
            "  4 usage/environment: bad arguments or options, no protocol root, an unreadable\n" +
            "    courier.layout.json, or an endpoint that is not registered under the root\n";
    }
}
