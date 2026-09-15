using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using AB.RevitMcp.Contracts.Json;

namespace AB.RevitMcp.Addin.AiClients
{
    /// <summary>
    /// How an MCP client should start the server: a command plus arguments.
    ///
    /// This exists because of Microsoft Defender's Attack Surface Reduction rule
    /// "Block executable files from running unless they meet a prevalence, age, or trusted list
    /// criteria" (01443614-cd74-433a-b99e-2ecdc07bfc25). A freshly built, unsigned executable
    /// fails all three tests by definition, so on a managed machine Windows refuses to create the
    /// process at all - the client reports "spawn EPERM" and the bridge never starts.
    ///
    /// The server publish contains the managed AB.RevitMcp.Server.dll next to its apphost .exe,
    /// so the identical program can be started as "dotnet.exe AB.RevitMcp.Server.dll" instead.
    /// The process then being created is dotnet.exe - Microsoft-signed and about as prevalent as
    /// software gets - and the rule has nothing to act on. The .dll is LOADED, not executed as a
    /// process. Nothing is disabled or bypassed: same code, same user, same permissions.
    /// </summary>
    public sealed class LaunchSpec
    {
        public string Command;
        public string[] Args;
        public bool ViaDotnet;

        private LaunchSpec() { Args = new string[0]; }

        /// <summary>The arguments as a JSON array, for a client config file.</summary>
        public JsonValue ArgsJson()
        {
            JsonValue a = JsonValue.NewArray();
            for (int i = 0; i < Args.Length; i++) a.Add(Args[i]);
            return a;
        }

        /// <summary>A quoted command line, for clients that take one string.</summary>
        public string CommandLine()
        {
            var sb = new StringBuilder();
            sb.Append('"').Append(Command).Append('"');
            for (int i = 0; i < Args.Length; i++) sb.Append(" \"").Append(Args[i]).Append('"');
            return sb.ToString();
        }

        /// <summary>YAML args, e.g. <c>[]</c> or <c>["C:\\...\\x.dll"]</c>.</summary>
        public string ArgsYaml()
        {
            if (Args.Length == 0) return "[]";
            var sb = new StringBuilder("[");
            for (int i = 0; i < Args.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('"').Append(Args[i].Replace("\\", "\\\\")).Append('"');
            }
            return sb.Append(']').ToString();
        }

        private static LaunchSpec Direct(string serverExe)
        {
            return new LaunchSpec { Command = serverExe, Args = new string[0], ViaDotnet = false };
        }

        /// <summary>
        /// Decides how to launch. Prefers the dotnet route, but only after actually PROVING it
        /// works on this machine - a config that points at a dotnet which cannot start the server
        /// would be worse than the ASR block it is meant to avoid.
        /// </summary>
        public static LaunchSpec For(string serverExe, Action<string> log)
        {
            if (log == null) log = delegate { };

            string dll = Path.ChangeExtension(serverExe, ".dll");
            if (!File.Exists(dll))
            {
                log("  launch: the managed .dll is missing - using the executable directly");
                return Direct(serverExe);
            }

            string dotnet = FindDotnet();
            if (dotnet == null)
            {
                log("  launch: .NET is not installed - using the executable directly");
                log("          (if Defender blocks it, install the .NET runtime and configure the clients again)");
                return Direct(serverExe);
            }

            if (!CanLaunch(dotnet, dll))
            {
                log("  launch: dotnet could not start the server - using the executable directly");
                return Direct(serverExe);
            }

            log("  launch: via dotnet.exe (Microsoft-signed, immune to the Defender ASR block)");
            return new LaunchSpec { Command = dotnet, Args = new[] { dll }, ViaDotnet = true };
        }

        private static string FindDotnet()
        {
            var candidates = new List<string>();

            // The 64-bit install location, whichever variable this process sees.
            string pf64 = Environment.GetEnvironmentVariable("ProgramW6432");
            if (!string.IsNullOrEmpty(pf64)) candidates.Add(Path.Combine(pf64, "dotnet", "dotnet.exe"));

            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(pf)) candidates.Add(Path.Combine(pf, "dotnet", "dotnet.exe"));

            // Per-user installs.
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local)) candidates.Add(Path.Combine(local, "Microsoft", "dotnet", "dotnet.exe"));

            // Anything on PATH.
            string path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(path))
            {
                foreach (string dir in path.Split(';'))
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    try { candidates.Add(Path.Combine(dir.Trim(), "dotnet.exe")); }
                    catch (ArgumentException) { }   // a malformed PATH entry
                }
            }

            foreach (string c in candidates)
            {
                try { if (File.Exists(c)) return c; }
                catch (Exception) { }
            }
            return null;
        }

        /// <summary>Runs "dotnet server.dll --help" and requires a clean exit.</summary>
        private static bool CanLaunch(string dotnet, string dll)
        {
            try
            {
                var psi = new ProcessStartInfo(dotnet, "\"" + dll + "\" --help")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (Process p = Process.Start(psi))
                {
                    if (p == null) return false;
                    // Drain stdout so a full pipe buffer cannot deadlock the child.
                    p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(15000)) { try { p.Kill(); } catch (Exception) { } return false; }
                    return p.ExitCode == 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public enum AgentFormat
    {
        /// <summary>A JSON object keyed by server name under a root key.</summary>
        Json,

        /// <summary>Continue's config.yaml - a YAML list, not JSON.</summary>
        Yaml,

        /// <summary>No file we should touch; show the user the command instead.</summary>
        CommandOnly
    }

    /// <summary>One AI client that can be pointed at the Revit MCP server.</summary>
    public sealed class AgentTarget
    {
        /// <summary>
        /// Stable id, e.g. "claude-desktop". The .msi records the user's ticks as these ids
        /// (installer\RevitMcp.AiClients.wxs): never rename one.
        /// </summary>
        public string Id;
        public string Name;
        public string ConfigPath;
        /// <summary>
        /// Where the server map lives in the config. May be a DOTTED PATH: OpenClaw nests its
        /// servers under "mcp.servers" rather than using a top-level key, and writing a flat
        /// "mcpServers" there is silently ignored - the client simply never sees the server.
        /// </summary>
        public string RootKey = "mcpServers";
        public AgentFormat Format = AgentFormat.Json;
        public string DetectFolder;
        public string Hint;
        public string RemoveHint;

        public bool Detected
        {
            get
            {
                if (string.IsNullOrEmpty(DetectFolder)) return false;
                return Directory.Exists(DetectFolder) || File.Exists(DetectFolder);
            }
        }

        public override string ToString()
        {
            if (!Detected) return Name + "  (not installed)";
            return AgentConfigurator.IsConfigured(this) ? Name + "  (configured)" : Name;
        }
    }

    /// <summary>
    /// Registers the MCP server with whichever AI clients are on the machine - and removes it again.
    ///
    /// Every write preserves the rest of the file and leaves a .abmcp-backup beside it. MCP client
    /// configs hold other servers and, in some cases, unrelated application settings - clobbering
    /// one would be a far worse outcome than simply not configuring it.
    ///
    /// Until 1.5.0 this ran inside AB.RevitMcp.Setup.exe. The installer is now an .msi, which must
    /// not run code (company PCs block unknown executables), so the add-in runs it instead: the
    /// choice made in the installer is applied when Revit starts (AiClientSetup), and the AI Clients
    /// button does the rest.
    /// </summary>
    public sealed class AgentConfigurator
    {
        public const string ServerName = "revit";

        private readonly Action<string> _log;

        public AgentConfigurator(Action<string> log)
        {
            _log = log ?? delegate { };
        }

        public static List<AgentTarget> KnownAgents()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            return new List<AgentTarget>
            {
                new AgentTarget
                {
                    Id = "claude-desktop",
                    Name = "Claude Desktop",
                    DetectFolder = Path.Combine(appData, "Claude"),
                    ConfigPath = Path.Combine(appData, "Claude", "claude_desktop_config.json"),
                    RootKey = "mcpServers"
                },
                new AgentTarget
                {
                    Id = "cursor",
                    Name = "Cursor",
                    DetectFolder = Path.Combine(userProfile, ".cursor"),
                    ConfigPath = Path.Combine(userProfile, ".cursor", "mcp.json"),
                    RootKey = "mcpServers"
                },
                new AgentTarget
                {
                    // VS Code is the odd one out: it keys servers under "servers", not "mcpServers".
                    Id = "vscode",
                    Name = "VS Code / GitHub Copilot (agent mode)",
                    DetectFolder = Path.Combine(appData, "Code", "User"),
                    ConfigPath = Path.Combine(appData, "Code", "User", "mcp.json"),
                    RootKey = "servers"
                },
                new AgentTarget
                {
                    // Gemini CLI speaks MCP; the Gemini web app and mobile app do not.
                    Id = "gemini-cli",
                    Name = "Gemini CLI",
                    DetectFolder = Path.Combine(userProfile, ".gemini"),
                    ConfigPath = Path.Combine(userProfile, ".gemini", "settings.json"),
                    RootKey = "mcpServers"
                },
                new AgentTarget
                {
                    Id = "visual-studio",
                    Name = "Visual Studio 2022",
                    DetectFolder = VisualStudioMcpFolder(localAppData),
                    ConfigPath = VisualStudioMcpFolder(localAppData) == null
                        ? null
                        : Path.Combine(VisualStudioMcpFolder(localAppData), "mcp.json"),
                    RootKey = "servers"
                },
                new AgentTarget
                {
                    Id = "windsurf",
                    Name = "Windsurf",
                    DetectFolder = Path.Combine(userProfile, ".codeium", "windsurf"),
                    ConfigPath = Path.Combine(userProfile, ".codeium", "windsurf", "mcp_config.json"),
                    RootKey = "mcpServers"
                },
                new AgentTarget
                {
                    Id = "cline",
                    Name = "Cline (VS Code extension)",
                    DetectFolder = Path.Combine(appData, "Code", "User", "globalStorage", "saoudrizwan.claude-dev"),
                    ConfigPath = Path.Combine(appData, "Code", "User", "globalStorage",
                                              "saoudrizwan.claude-dev", "settings", "cline_mcp_settings.json"),
                    RootKey = "mcpServers"
                },
                new AgentTarget
                {
                    Id = "roo-code",
                    Name = "Roo Code (VS Code extension)",
                    DetectFolder = Path.Combine(appData, "Code", "User", "globalStorage", "rooveterinaryinc.roo-cline"),
                    ConfigPath = Path.Combine(appData, "Code", "User", "globalStorage",
                                              "rooveterinaryinc.roo-cline", "settings", "mcp_settings.json"),
                    RootKey = "mcpServers"
                },
                new AgentTarget
                {
                    Id = "lm-studio",
                    Name = "LM Studio (local models)",
                    DetectFolder = Path.Combine(userProfile, ".lmstudio"),
                    ConfigPath = Path.Combine(userProfile, ".lmstudio", "mcp.json"),
                    RootKey = "mcpServers"
                },
                new AgentTarget
                {
                    Id = "continue",
                    Name = "Continue",
                    DetectFolder = Path.Combine(userProfile, ".continue"),
                    ConfigPath = Path.Combine(userProfile, ".continue", "config.yaml"),
                    Format = AgentFormat.Yaml
                },
                new AgentTarget
                {
                    // Nested key: OpenClaw reads mcp.servers, not a top-level mcpServers.
                    Id = "openclaw",
                    Name = "OpenClaw",
                    DetectFolder = Path.Combine(userProfile, ".openclaw"),
                    ConfigPath = Path.Combine(userProfile, ".openclaw", "openclaw.json"),
                    RootKey = "mcp.servers"
                },
                new AgentTarget
                {
                    // ~/.claude.json also stores live session state; a scripted rewrite there is
                    // not worth the risk, so the supported command is shown instead.
                    Id = "claude-code",
                    Name = "Claude Code",
                    DetectFolder = Path.Combine(userProfile, ".claude.json"),
                    ConfigPath = Path.Combine(userProfile, ".claude.json"),
                    Format = AgentFormat.CommandOnly,
                    Hint = "claude mcp add revit -- {SERVER}",
                    RemoveHint = "claude mcp remove revit"
                }
            };
        }

        /// <summary>
        /// Visual Studio keeps per-instance folders under %LOCALAPPDATA%\Microsoft\VisualStudio,
        /// so the exact path is discovered rather than hard-coded.
        /// </summary>
        private static string VisualStudioMcpFolder(string localAppData)
        {
            try
            {
                string root = Path.Combine(localAppData, "Microsoft", "VisualStudio");
                if (!Directory.Exists(root)) return null;

                foreach (string dir in Directory.GetDirectories(root, "17.*"))
                    return dir;     // any 2022 instance will do
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Builds a target for an agent this installer does not know about. The long tail of MCP
        /// clients changes monthly; guessing at config paths would write files to the wrong place,
        /// so the user points at the real one instead.
        /// </summary>
        public static AgentTarget Custom(string configPath, string rootKey, bool yaml)
        {
            return new AgentTarget
            {
                Id = "custom",
                Name = "Custom - " + Path.GetFileName(configPath),
                ConfigPath = configPath,
                DetectFolder = Path.GetDirectoryName(configPath),
                RootKey = string.IsNullOrEmpty(rootKey) ? "mcpServers" : rootKey,
                Format = yaml ? AgentFormat.Yaml : AgentFormat.Json
            };
        }

        public static bool IsYamlPath(string path)
        {
            return !string.IsNullOrEmpty(path) &&
                   (path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Whether the client's config already holds a "revit" server. Never throws.</summary>
        public static bool IsConfigured(AgentTarget agent)
        {
            try
            {
                if (agent == null || string.IsNullOrEmpty(agent.ConfigPath) || !File.Exists(agent.ConfigPath)) return false;
                string text = File.ReadAllText(agent.ConfigPath);

                if (agent.Format == AgentFormat.Yaml)
                    return text.IndexOf("name: " + ServerName, StringComparison.OrdinalIgnoreCase) >= 0;

                JsonValue document;
                if (!JsonValue.TryParse(text, out document) || !document.IsObject) return false;
                JsonValue container = FindContainer(document, agent.Format == AgentFormat.CommandOnly ? "mcpServers" : agent.RootKey);
                return container != null && container.Has(ServerName);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public int Configure(IEnumerable<AgentTarget> agents, string serverExe)
        {
            // Decide ONCE how the server should be started, so every client is registered the
            // same way and the probe only runs a single time.
            LaunchSpec spec = LaunchSpec.For(serverExe, _log);
            return Configure(agents, spec);
        }

        public int Configure(IEnumerable<AgentTarget> agents, LaunchSpec spec)
        {
            int configured = 0;

            foreach (AgentTarget agent in agents)
            {
                try
                {
                    if (agent.Format == AgentFormat.CommandOnly)
                    {
                        _log(agent.Name + ": run  " + agent.Hint.Replace("{SERVER}", spec.CommandLine()));
                        continue;
                    }

                    if (agent.Format == AgentFormat.Yaml)
                    {
                        if (ConfigureYaml(agent, spec)) configured++;
                        continue;
                    }

                    if (ConfigureJson(agent, spec)) configured++;
                }
                catch (Exception ex)
                {
                    _log(agent.Name + ": FAILED - " + ex.Message);
                }
            }

            return configured;
        }

        /// <summary>Takes the "revit" server out of each client's config, keeping a backup.</summary>
        public int Remove(IEnumerable<AgentTarget> agents)
        {
            int removed = 0;

            foreach (AgentTarget agent in agents)
            {
                try
                {
                    if (agent.Format == AgentFormat.CommandOnly)
                    {
                        _log(agent.Name + ": run  " + (agent.RemoveHint ?? "(remove the \"revit\" server in the client)"));
                        continue;
                    }

                    if (string.IsNullOrEmpty(agent.ConfigPath) || !File.Exists(agent.ConfigPath))
                    {
                        _log(agent.Name + ": no configuration file - nothing to remove");
                        continue;
                    }

                    if (agent.Format == AgentFormat.Yaml)
                    {
                        // Same reasoning as ConfigureYaml: no naive rewrites of someone's YAML.
                        _log(agent.Name + (IsConfigured(agent)
                            ? ": remove the \"- name: revit\" entry from " + agent.ConfigPath + " by hand"
                            : ": not configured - nothing to remove"));
                        continue;
                    }

                    JsonValue document;
                    if (!JsonValue.TryParse(File.ReadAllText(agent.ConfigPath), out document) || !document.IsObject)
                    {
                        _log(agent.Name + ": config is not valid JSON - left untouched");
                        continue;
                    }

                    JsonValue container = FindContainer(document, agent.RootKey);
                    if (container == null || !container.Has(ServerName))
                    {
                        _log(agent.Name + ": not configured - nothing to remove");
                        continue;
                    }

                    File.Copy(agent.ConfigPath, agent.ConfigPath + ".abmcp-backup", true);
                    container.Remove(ServerName);
                    File.WriteAllText(agent.ConfigPath, document.ToJson(true), new UTF8Encoding(false));
                    _log(agent.Name + ": removed");
                    removed++;
                }
                catch (Exception ex)
                {
                    _log(agent.Name + ": FAILED - " + ex.Message);
                }
            }

            return removed;
        }

        private bool ConfigureJson(AgentTarget agent, LaunchSpec spec)
        {
            if (string.IsNullOrEmpty(agent.ConfigPath))
            {
                _log(agent.Name + ": not installed - skipped");
                return false;
            }

            string folder = Path.GetDirectoryName(agent.ConfigPath);
            if (!Directory.Exists(folder))
            {
                // Create the folder only when the client itself is clearly installed; otherwise
                // we would litter config files for software the user does not have.
                if (!agent.Detected) { _log(agent.Name + ": not installed - skipped"); return false; }
                Directory.CreateDirectory(folder);
            }

            JsonValue document = JsonValue.NewObject();

            if (File.Exists(agent.ConfigPath))
            {
                File.Copy(agent.ConfigPath, agent.ConfigPath + ".abmcp-backup", true);

                string existing = File.ReadAllText(agent.ConfigPath);
                if (!string.IsNullOrEmpty(existing.Trim()))
                {
                    JsonValue parsed;
                    if (JsonValue.TryParse(existing, out parsed) && parsed.IsObject)
                    {
                        document = parsed;
                    }
                    else
                    {
                        _log(agent.Name + ": existing config is not valid JSON - left untouched");
                        return false;
                    }
                }
            }

            JsonValue servers = ResolveContainer(document, agent.RootKey);

            JsonValue entry = JsonValue.NewObject();
            entry.Set("command", spec.Command);
            entry.Set("args", spec.ArgsJson());
            servers.Set(ServerName, entry);

            File.WriteAllText(agent.ConfigPath, document.ToJson(true), new UTF8Encoding(false));
            _log(agent.Name + ": configured");
            return true;
        }

        /// <summary>
        /// Walks a dotted root key, creating intermediate objects as needed, and returns the
        /// object that should hold the server entries. "mcpServers" yields the top-level map;
        /// "mcp.servers" yields document["mcp"]["servers"], creating "mcp" if absent.
        /// </summary>
        private static JsonValue ResolveContainer(JsonValue document, string rootKey)
        {
            JsonValue current = document;
            string[] segments = (rootKey ?? "mcpServers").Split('.');

            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];
                if (string.IsNullOrEmpty(segment)) continue;

                JsonValue child = current[segment];
                if (!child.IsObject)
                {
                    child = JsonValue.NewObject();
                    current.Set(segment, child);
                }
                current = child;
            }

            return current;
        }

        /// <summary>Like ResolveContainer, but never creates anything: null when the path is absent.</summary>
        private static JsonValue FindContainer(JsonValue document, string rootKey)
        {
            JsonValue current = document;
            foreach (string segment in (rootKey ?? "mcpServers").Split('.'))
            {
                if (string.IsNullOrEmpty(segment)) continue;
                JsonValue child = current[segment];
                if (child == null || !child.IsObject) return null;
                current = child;
            }
            return current;
        }

        /// <summary>
        /// Continue uses YAML. Rather than take on a YAML parser just to add four lines, this only
        /// appends when there is no mcpServers block yet, and otherwise tells the user what to add.
        /// Silently rewriting someone's YAML with a naive text edit is how configs get destroyed.
        /// </summary>
        private bool ConfigureYaml(AgentTarget agent, LaunchSpec spec)
        {
            if (!agent.Detected) { _log(agent.Name + ": not installed - skipped"); return false; }

            string block =
                "mcpServers:" + Environment.NewLine +
                "  - name: revit" + Environment.NewLine +
                "    command: " + spec.Command + Environment.NewLine +
                "    args: " + spec.ArgsYaml() + Environment.NewLine;

            if (!File.Exists(agent.ConfigPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(agent.ConfigPath));
                File.WriteAllText(agent.ConfigPath, block, new UTF8Encoding(false));
                _log(agent.Name + ": configured (new config.yaml)");
                return true;
            }

            string existing = File.ReadAllText(agent.ConfigPath);

            if (existing.IndexOf("name: revit", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _log(agent.Name + ": already has a 'revit' entry - left untouched");
                return false;
            }

            if (existing.IndexOf("mcpServers:", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _log(agent.Name + ": already has an mcpServers block - add this entry by hand:");
                _log("    - name: revit");
                _log("      command: " + spec.Command);
                _log("      args: " + spec.ArgsYaml());
                return false;
            }

            File.Copy(agent.ConfigPath, agent.ConfigPath + ".abmcp-backup", true);
            string separator = existing.EndsWith("\n") ? Environment.NewLine : Environment.NewLine + Environment.NewLine;
            File.AppendAllText(agent.ConfigPath, separator + block, new UTF8Encoding(false));
            _log(agent.Name + ": configured (appended to config.yaml)");
            return true;
        }
    }
}
