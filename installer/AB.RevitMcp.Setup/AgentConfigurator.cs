using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AB.RevitMcp.Contracts.Json;

namespace AB.RevitMcp.Setup
{
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
            return Detected ? Name : Name + "  (not installed)";
        }
    }

    /// <summary>
    /// Registers the MCP server with whichever AI clients are on the machine.
    ///
    /// Every write preserves the rest of the file and leaves a .abmcp-backup beside it. MCP client
    /// configs hold other servers and, in some cases, unrelated application settings - clobbering
    /// one would be a far worse outcome than simply not configuring it.
    /// </summary>
    public sealed class AgentConfigurator
    {
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
                    Name = "Claude Desktop",
                    DetectFolder = Path.Combine(appData, "Claude"),
                    ConfigPath = Path.Combine(appData, "Claude", "claude_desktop_config.json"),
                    RootKey = "mcpServers"
                },
                new AgentTarget
                {
                    Name = "Cursor",
                    DetectFolder = Path.Combine(userProfile, ".cursor"),
                    ConfigPath = Path.Combine(userProfile, ".cursor", "mcp.json"),
                    RootKey = "mcpServers"
                },
                new AgentTarget
                {
                    // VS Code is the odd one out: it keys servers under "servers", not "mcpServers".
                    Name = "VS Code / GitHub Copilot (agent mode)",
                    DetectFolder = Path.Combine(appData, "Code", "User"),
                    ConfigPath = Path.Combine(appData, "Code", "User", "mcp.json"),
                    RootKey = "servers"
                },
                new AgentTarget
                {
                    // Gemini CLI speaks MCP; the Gemini web app and mobile app do not.
                    Name = "Gemini CLI",
                    DetectFolder = Path.Combine(userProfile, ".gemini"),
                    ConfigPath = Path.Combine(userProfile, ".gemini", "settings.json"),
                    RootKey = "mcpServers"
                },
                new AgentTarget
                {
                    Name = "Visual Studio 2022",
                    DetectFolder = VisualStudioMcpFolder(localAppData),
                    ConfigPath = VisualStudioMcpFolder(localAppData) == null
                        ? null
                        : Path.Combine(VisualStudioMcpFolder(localAppData), "mcp.json"),
                    RootKey = "servers"
                },
                new AgentTarget
                {
                    Name = "Windsurf",
                    DetectFolder = Path.Combine(userProfile, ".codeium", "windsurf"),
                    ConfigPath = Path.Combine(userProfile, ".codeium", "windsurf", "mcp_config.json"),
                    RootKey = "mcpServers"
                },
                new AgentTarget
                {
                    Name = "Cline (VS Code extension)",
                    DetectFolder = Path.Combine(appData, "Code", "User", "globalStorage", "saoudrizwan.claude-dev"),
                    ConfigPath = Path.Combine(appData, "Code", "User", "globalStorage",
                                              "saoudrizwan.claude-dev", "settings", "cline_mcp_settings.json"),
                    RootKey = "mcpServers"
                },
                new AgentTarget
                {
                    Name = "Roo Code (VS Code extension)",
                    DetectFolder = Path.Combine(appData, "Code", "User", "globalStorage", "rooveterinaryinc.roo-cline"),
                    ConfigPath = Path.Combine(appData, "Code", "User", "globalStorage",
                                              "rooveterinaryinc.roo-cline", "settings", "mcp_settings.json"),
                    RootKey = "mcpServers"
                },
                new AgentTarget
                {
                    Name = "LM Studio (local models)",
                    DetectFolder = Path.Combine(userProfile, ".lmstudio"),
                    ConfigPath = Path.Combine(userProfile, ".lmstudio", "mcp.json"),
                    RootKey = "mcpServers"
                },
                new AgentTarget
                {
                    Name = "Continue",
                    DetectFolder = Path.Combine(userProfile, ".continue"),
                    ConfigPath = Path.Combine(userProfile, ".continue", "config.yaml"),
                    Format = AgentFormat.Yaml
                },
                new AgentTarget
                {
                    // Nested key: OpenClaw reads mcp.servers, not a top-level mcpServers.
                    Name = "OpenClaw",
                    DetectFolder = Path.Combine(userProfile, ".openclaw"),
                    ConfigPath = Path.Combine(userProfile, ".openclaw", "openclaw.json"),
                    RootKey = "mcp.servers"
                },
                new AgentTarget
                {
                    // ~/.claude.json also stores live session state; a scripted rewrite there is
                    // not worth the risk, so the supported command is shown instead.
                    Name = "Claude Code",
                    DetectFolder = Path.Combine(userProfile, ".claude.json"),
                    Format = AgentFormat.CommandOnly,
                    Hint = "claude mcp add revit \"{SERVER}\""
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
                Name = "Custom - " + Path.GetFileName(configPath),
                ConfigPath = configPath,
                DetectFolder = Path.GetDirectoryName(configPath),
                RootKey = string.IsNullOrEmpty(rootKey) ? "mcpServers" : rootKey,
                Format = yaml ? AgentFormat.Yaml : AgentFormat.Json
            };
        }

        public int Configure(IEnumerable<AgentTarget> agents, string serverExe)
        {
            int configured = 0;

            foreach (AgentTarget agent in agents)
            {
                try
                {
                    if (agent.Format == AgentFormat.CommandOnly)
                    {
                        _log(agent.Name + ": run  " + agent.Hint.Replace("{SERVER}", serverExe));
                        continue;
                    }

                    if (agent.Format == AgentFormat.Yaml)
                    {
                        if (ConfigureYaml(agent, serverExe)) configured++;
                        continue;
                    }

                    if (ConfigureJson(agent, serverExe)) configured++;
                }
                catch (Exception ex)
                {
                    _log(agent.Name + ": FAILED - " + ex.Message);
                }
            }

            return configured;
        }

        private bool ConfigureJson(AgentTarget agent, string serverExe)
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
            entry.Set("command", serverExe);
            entry.Set("args", JsonValue.NewArray());
            servers.Set("revit", entry);

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

        /// <summary>
        /// Continue uses YAML. Rather than take on a YAML parser just to add four lines, this only
        /// appends when there is no mcpServers block yet, and otherwise tells the user what to add.
        /// Silently rewriting someone's YAML with a naive text edit is how configs get destroyed.
        /// </summary>
        private bool ConfigureYaml(AgentTarget agent, string serverExe)
        {
            if (!agent.Detected) { _log(agent.Name + ": not installed - skipped"); return false; }

            string block =
                "mcpServers:" + Environment.NewLine +
                "  - name: revit" + Environment.NewLine +
                "    command: " + serverExe + Environment.NewLine +
                "    args: []" + Environment.NewLine;

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
                _log("      command: " + serverExe);
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
