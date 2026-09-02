using System;
using System.Collections.Generic;
using System.Globalization;
using AB.RevitMcp.Contracts.Protocol;

namespace AB.RevitMcp.Server
{
    public enum TransportKind { Stdio, Http }

    /// <summary>Command-line and environment configuration for the MCP server process.</summary>
    public sealed class ServerOptions
    {
        public TransportKind Transport = TransportKind.Stdio;
        public int HttpPort = 3333;
        public string HttpHost = "127.0.0.1";
        public string PipeName;
        public string RevitVersion;
        public int RequestTimeoutMs = IpcConstants.DefaultRequestTimeoutMs;
        public bool Verbose;
        public bool ShowHelp;
        public bool PrintTools;
        public string PrintConfigClient;
        public bool RunDoctor;
        public bool AllowDestructive = true;
        /// <summary>Gate 1 of 3 for revit_execute_code. Off unless explicitly requested.</summary>
        public bool AllowCodeExecution;

        public static ServerOptions Parse(string[] args, out string error)
        {
            error = null;
            var options = new ServerOptions();

            // Environment first - MCP clients configure servers through "env" blocks.
            options.PipeName = Environment.GetEnvironmentVariable(IpcConstants.PipeNameEnvVar);
            options.RevitVersion = Environment.GetEnvironmentVariable(IpcConstants.RevitVersionEnvVar);

            string timeoutEnv = Environment.GetEnvironmentVariable(IpcConstants.TimeoutEnvVar);
            int parsedTimeout;
            if (!string.IsNullOrEmpty(timeoutEnv) &&
                int.TryParse(timeoutEnv, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedTimeout) &&
                parsedTimeout > 0)
            {
                options.RequestTimeoutMs = Math.Min(parsedTimeout, IpcConstants.MaxRequestTimeoutMs);
            }

            string allowCodeEnv = Environment.GetEnvironmentVariable("AB_REVITMCP_ALLOW_CODE");
            if (!string.IsNullOrEmpty(allowCodeEnv) &&
                (allowCodeEnv == "1" || string.Equals(allowCodeEnv, "true", StringComparison.OrdinalIgnoreCase)))
            {
                options.AllowCodeExecution = true;
            }

            string readOnlyEnv = Environment.GetEnvironmentVariable("AB_REVITMCP_READONLY");
            if (!string.IsNullOrEmpty(readOnlyEnv) &&
                (readOnlyEnv == "1" || string.Equals(readOnlyEnv, "true", StringComparison.OrdinalIgnoreCase)))
            {
                options.AllowDestructive = false;
            }

            for (int i = 0; args != null && i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg.ToLowerInvariant())
                {
                    case "--stdio":
                        options.Transport = TransportKind.Stdio;
                        break;

                    case "--http":
                        options.Transport = TransportKind.Http;
                        break;

                    case "--port":
                        {
                            int port;
                            if (i + 1 >= args.Length ||
                                !int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out port) ||
                                port < 1 || port > 65535)
                            {
                                error = "--port requires a number between 1 and 65535.";
                                return options;
                            }
                            options.HttpPort = port;
                            options.Transport = TransportKind.Http;
                            break;
                        }

                    case "--host":
                        if (i + 1 >= args.Length) { error = "--host requires a value."; return options; }
                        options.HttpHost = args[++i];
                        break;

                    case "--pipe":
                        if (i + 1 >= args.Length) { error = "--pipe requires a pipe name."; return options; }
                        options.PipeName = args[++i];
                        break;

                    case "--revit-version":
                        if (i + 1 >= args.Length) { error = "--revit-version requires a value, e.g. 2024."; return options; }
                        options.RevitVersion = args[++i];
                        break;

                    case "--timeout":
                        {
                            int timeout;
                            if (i + 1 >= args.Length ||
                                !int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out timeout) ||
                                timeout < 1000)
                            {
                                error = "--timeout requires a value in milliseconds (minimum 1000).";
                                return options;
                            }
                            options.RequestTimeoutMs = Math.Min(timeout, IpcConstants.MaxRequestTimeoutMs);
                            break;
                        }

                    case "--read-only":
                        options.AllowDestructive = false;
                        break;

                    case "--allow-code-execution":
                        options.AllowCodeExecution = true;
                        break;

                    case "--print-tools":
                        options.PrintTools = true;
                        break;

                    case "--doctor":
                        options.RunDoctor = true;
                        break;

                    // Consumed by the doctor itself; accepted here so parsing does not fail.
                    case "--no-ping":
                    case "--doctor-help":
                        break;

                    case "--revit-root":
                        if (i + 1 >= args.Length) { error = "--revit-root requires a folder."; return options; }
                        i++;
                        break;

                    case "--print-config":
                        // Optional client name; defaults to printing every client.
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                        {
                            options.PrintConfigClient = args[++i];
                            if (!ConfigTemplates.IsKnown(options.PrintConfigClient))
                            {
                                error = "Unknown client '" + options.PrintConfigClient + "'. Known: " +
                                        ConfigTemplates.KnownClientList() + ".";
                                return options;
                            }
                        }
                        else options.PrintConfigClient = "all";
                        break;

                    case "--verbose":
                    case "-v":
                        options.Verbose = true;
                        break;

                    case "--help":
                    case "-h":
                    case "/?":
                        options.ShowHelp = true;
                        break;

                    default:
                        error = "Unknown argument '" + arg + "'. Run with --help for usage.";
                        return options;
                }
            }

            return options;
        }

        public static string HelpText()
        {
            var lines = new List<string>
            {
                "AB Revit MCP Server - Model Context Protocol bridge for Autodesk Revit 2020-2026",
                "",
                "USAGE",
                "  AB.RevitMcp.Server.exe [options]",
                "",
                "TRANSPORT",
                "  --stdio                 Speak MCP over stdin/stdout (default; what MCP clients use).",
                "  --http                  Serve Streamable HTTP instead.",
                "  --port <n>              HTTP port (default 3333). Implies --http.",
                "  --host <addr>           HTTP bind address (default 127.0.0.1 - loopback only).",
                "",
                "TARGETING REVIT",
                "  --pipe <name>           Connect to one specific bridge pipe instead of auto-discovering.",
                "  --revit-version <yyyy>  Only connect to this Revit release, e.g. 2024.",
                "  --timeout <ms>          Per-request budget (default " + IpcConstants.DefaultRequestTimeoutMs +
                    ", maximum " + IpcConstants.MaxRequestTimeoutMs + ").",
                "",
                "SAFETY",
                "  --allow-code-execution  Expose revit_execute_code, which compiles and runs",
                "                          arbitrary C# against the model. Off by default; the Revit",
                "                          add-in must ALSO enable it, and every call needs confirm.",
                "  --read-only             Refuse every write and destructive tool. They are still",
                "                          advertised, but calling one returns an error.",
                "",
                "OTHER",
                "  --verbose               Log protocol traffic to stderr.",
                "  --print-tools           Print the tool catalogue as Markdown and exit.",
                "  --print-config [client] Print ready-to-paste MCP client configuration and exit.",
                "  --doctor                Verify the installation without starting Revit, and exit.",
                "                          client: " + ConfigTemplates.KnownClientList() + ".",
                "  --help                  Show this text.",
                "",
                "ENVIRONMENT",
                "  " + IpcConstants.PipeNameEnvVar + "        same as --pipe",
                "  " + IpcConstants.RevitVersionEnvVar + "   same as --revit-version",
                "  " + IpcConstants.TimeoutEnvVar + "     same as --timeout",
                "  AB_REVITMCP_READONLY       set to 1 for --read-only",
                "",
                "NOTES",
                "  stdout carries the MCP protocol and nothing else. All logging goes to stderr.",
                "  Revit does not need to be running when this server starts - it connects on demand."
            };
            return string.Join(Environment.NewLine, lines);
        }
    }
}
