using System;
using System.IO;

namespace AB.RevitMcp.Contracts.Protocol
{
    /// <summary>
    /// Names, paths and limits shared by the pipe server (inside Revit) and the pipe client
    /// (inside the MCP server process).
    /// </summary>
    public static class IpcConstants
    {
        /// <summary>Wire protocol revision. Bumped on any breaking change to the frame or envelope.</summary>
        public const int ProtocolVersion = 1;

        /// <summary>Base pipe name. The effective name is "<c>{Base}.{revitVersion}.{sessionPid}</c>".</summary>
        public const string PipeNamePrefix = "AB.RevitMcp.Bridge";

        /// <summary>Environment variable that pins the client to one specific pipe.</summary>
        public const string PipeNameEnvVar = "AB_REVITMCP_PIPE";

        /// <summary>Environment variable that pins the client to one Revit release (e.g. "2024").</summary>
        public const string RevitVersionEnvVar = "AB_REVITMCP_REVIT_VERSION";

        /// <summary>Environment variable overriding the per-request timeout, in milliseconds.</summary>
        public const string TimeoutEnvVar = "AB_REVITMCP_TIMEOUT_MS";

        /// <summary>
        /// Default per-request budget: Revit must answer within this or the call is cancelled.
        /// Comfortable for interactive queries and ordinary edits, which finish in tens of
        /// milliseconds. Genuinely long operations declare their own budget instead - see
        /// <see cref="LongRunningTimeoutMs"/> and <c>ToolDescriptor.TimeoutMs</c>.
        /// </summary>
        public const int DefaultRequestTimeoutMs = 30000;

        /// <summary>
        /// Budget for tools that drive a whole Revit export or a model-wide sweep. Exporting a
        /// sheet set to PDF or DWG is minutes of work on a real project, and it is not something
        /// the caller can page or shrink, so the request must be allowed to run to completion.
        /// </summary>
        public const int LongRunningTimeoutMs = 300000;   // 5 minutes

        /// <summary>
        /// Hard ceiling a client may request. Above <see cref="LongRunningTimeoutMs"/> so an
        /// operator can raise the budget further with --timeout for an unusually large export
        /// without recompiling.
        /// </summary>
        public const int MaxRequestTimeoutMs = 600000;    // 10 minutes

        /// <summary>Time allowed for the named-pipe connection handshake.</summary>
        public const int ConnectTimeoutMs = 3000;

        /// <summary>Maximum single frame size (8 MB) - guards against a corrupt length prefix.</summary>
        public const int MaxFrameBytes = 8 * 1024 * 1024;

        /// <summary>Default page size for list-style tools.</summary>
        public const int DefaultPageLimit = 50;

        /// <summary>Absolute ceiling on a single page, regardless of what the caller asks for.</summary>
        public const int MaxPageLimit = 500;

        /// <summary>Reserved tool names handled by the bridge itself rather than the Revit tool registry.</summary>
        public const string OpPing = "bridge/ping";
        public const string OpDescribe = "bridge/describe";
        public const string OpStatus = "bridge/status";

        public static string BuildPipeName(string revitVersion, int processId)
        {
            return PipeNamePrefix + "." + (string.IsNullOrEmpty(revitVersion) ? "0000" : revitVersion) + "." + processId;
        }

        /// <summary>%LOCALAPPDATA%\ABRevitMcp - root for endpoint discovery files and logs.</summary>
        public static string DataRoot
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
                return Path.Combine(local, "ABRevitMcp");
            }
        }

        /// <summary>Directory holding one JSON descriptor per live Revit bridge.</summary>
        public static string EndpointDirectory { get { return Path.Combine(DataRoot, "endpoints"); } }

        /// <summary>Directory holding newline-delimited JSON logs.</summary>
        public static string LogDirectory { get { return Path.Combine(DataRoot, "logs"); } }
    }
}
