using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using AB.RevitMcp.Contracts.Json;

namespace AB.RevitMcp.Server
{
    /// <summary>
    /// Emits ready-to-paste MCP client configuration for this exact executable.
    ///
    /// The server knows its own absolute path, so the snippet it prints is always correct - no
    /// placeholder for the user to substitute and get wrong.
    /// </summary>
    public static class ConfigTemplates
    {
        public static string ExecutablePath()
        {
            try
            {
                string path = Process_MainModuleFileName();
                if (!string.IsNullOrEmpty(path)) return path;
            }
            catch (Exception) { }

            try
            {
                string asm = Assembly.GetEntryAssembly() != null ? Assembly.GetEntryAssembly().Location : null;
                if (!string.IsNullOrEmpty(asm))
                {
                    string exe = Path.ChangeExtension(asm, ".exe");
                    if (File.Exists(exe)) return exe;
                    return asm;
                }
            }
            catch (Exception) { }

            return @"C:\Users\<YOU>\AppData\Local\ABRevitMcp\Server\AB.RevitMcp.Server.exe";
        }

        private static string Process_MainModuleFileName()
        {
            using (System.Diagnostics.Process p = System.Diagnostics.Process.GetCurrentProcess())
            {
                return p.MainModule != null ? p.MainModule.FileName : null;
            }
        }

        private static readonly string[] KnownClients =
        {
            "claude-desktop", "claude-code", "cursor", "vscode", "visualstudio", "gemini-cli",
            "windsurf", "cline", "continue", "lmstudio", "openclaw", "local", "generic", "http", "all"
        };

        public static bool IsKnown(string client)
        {
            for (int i = 0; i < KnownClients.Length; i++)
                if (string.Equals(KnownClients[i], client, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static string KnownClientList() { return string.Join(", ", KnownClients); }

        public static string Render(string client)
        {
            string exe = ExecutablePath();
            var sb = new StringBuilder();

            if (string.IsNullOrEmpty(client)) client = "all";

            bool all = string.Equals(client, "all", StringComparison.OrdinalIgnoreCase);

            sb.AppendLine("AB Revit MCP - client configuration");
            sb.AppendLine("Server executable: " + exe);
            sb.AppendLine();
            sb.AppendLine("MCP is a feature of the CLIENT, not of the model. Any model the client can");
            sb.AppendLine("drive - Claude, GPT, Gemini, DeepSeek, Qwen, a local Llama - can use these tools,");
            sb.AppendLine("provided the client itself speaks MCP.");
            sb.AppendLine();

            if (all || Is(client, "claude-desktop")) Section(sb, "Claude Desktop",
                @"%APPDATA%\Claude\claude_desktop_config.json",
                StdioJson(exe, "mcpServers", false));

            if (all || Is(client, "claude-code")) Section(sb, "Claude Code",
                "run the command, or put .mcp.json in the project root",
                "claude mcp add revit \"" + exe + "\"\n\n" + StdioJson(exe, "mcpServers", true));

            if (all || Is(client, "cursor")) Section(sb, "Cursor",
                @"%USERPROFILE%\.cursor\mcp.json   (or .cursor\mcp.json in the project)",
                StdioJson(exe, "mcpServers", false));

            if (all || Is(client, "vscode")) Section(sb, "VS Code (GitHub Copilot agent mode)",
                @"%APPDATA%\Code\User\mcp.json   (or .vscode\mcp.json in the workspace)",
                StdioJson(exe, "servers", true));

            if (all || Is(client, "gemini-cli")) Section(sb, "Gemini CLI",
                @"%USERPROFILE%\.gemini\settings.json   (the Gemini web app has no MCP client)",
                StdioJson(exe, "mcpServers", false));

            if (all || Is(client, "visualstudio")) Section(sb, "Visual Studio 2022",
                @"%LOCALAPPDATA%\Microsoft\VisualStudio\17.x\mcp.json",
                StdioJson(exe, "servers", true));

            if (all || Is(client, "windsurf")) Section(sb, "Windsurf",
                @"%USERPROFILE%\.codeium\windsurf\mcp_config.json",
                StdioJson(exe, "mcpServers", false));

            if (all || Is(client, "cline")) Section(sb, "Cline / Roo Code (VS Code extension)",
                "MCP Servers panel -> Configure MCP Servers  (cline_mcp_settings.json)",
                StdioJson(exe, "mcpServers", false));

            if (all || Is(client, "continue")) Section(sb, "Continue",
                @"%USERPROFILE%\.continue\config.yaml   (YAML, not JSON)",
                "mcpServers:\n  - name: revit\n    command: " + exe + "\n    args: []");

            if (all || Is(client, "lmstudio")) Section(sb, "LM Studio (local models: Qwen, Llama, DeepSeek-R1 ...)",
                "Program -> Install -> Edit mcp.json",
                StdioJson(exe, "mcpServers", false));

            if (all || Is(client, "openclaw")) Section(sb, "OpenClaw",
                @"%USERPROFILE%\.openclaw\openclaw.json   -  NESTED under mcp.servers",
                NestedJson(exe, "mcp", "servers") + Environment.NewLine + Environment.NewLine +
                "Or let OpenClaw write it:  openclaw mcp add" + Environment.NewLine +
                "Verify with:               openclaw mcp probe");

            if (all || Is(client, "local")) Section(sb, "Local models - Qwen, Llama, DeepSeek, Mistral",
                "run the model inside an agent that speaks MCP; the model itself does not",
                string.Join(Environment.NewLine, new[]
                {
                    "Ollama and llama.cpp are model SERVERS, not MCP clients - they have no MCP",
                    "support and nothing to configure. Point an MCP-capable AGENT at your local",
                    "model instead:",
                    "",
                    "  LM Studio   built-in MCP, loads GGUF directly   --print-config lmstudio",
                    "  Cline       VS Code ext; can use an Ollama URL  --print-config cline",
                    "  Continue    VS Code/JetBrains; Ollama or any    --print-config continue",
                    "              OpenAI-compatible endpoint",
                    "  Goose, Jan, Cherry Studio, AnythingLLM, Msty - all speak MCP",
                    "",
                    "The agent gets every Revit tool; the model behind it only has to be good at",
                    "choosing them. A 7B model handles revit_list_levels fine; want something",
                    "larger before trusting it with revit_delete_elements.",
                    "",
                    "The config shape is identical everywhere:",
                    "",
                }) + StdioJson(exe, "mcpServers", false));

            if (all || Is(client, "generic")) Section(sb, "Any other stdio MCP client",
                "Chatbox, Cherry Studio, Jan, Goose, AnythingLLM, Msty, Zed, OpenAI Agents SDK, ...",
                StdioJson(exe, "mcpServers", false));

            if (all || Is(client, "http")) Section(sb, "HTTP transport (n8n, Open WebUI, remote clients)",
                "start the server yourself, then point the client at the URL",
                "  " + exe + " --http --port 3333\n\n" +
                "  Endpoint : http://127.0.0.1:3333/mcp   (POST JSON-RPC)\n" +
                "  Health   : http://127.0.0.1:3333/health\n\n" +
                "  Bound to loopback and Origin-checked. Do NOT expose this port to a network or a\n" +
                "  tunnel without putting authentication in front of it - anything that can reach it\n" +
                "  can modify the open Revit model.");

            sb.AppendLine("--- Clients with NO local MCP support " + new string('-', 30));
            sb.AppendLine("  ChatGPT (app/web)   OpenAI's MCP support is for REMOTE servers over HTTP/SSE.");
            sb.AppendLine("                      It never launches a local process, so there is no config");
            sb.AppendLine("                      file to write. Use --http and put authentication in front");
            sb.AppendLine("                      of it, or use the OpenAI Agents SDK locally (stdio).");
            sb.AppendLine("  Gemini app          No MCP client. Gemini CLI does - see above.");
            sb.AppendLine("  Microsoft Copilot   The consumer app has no MCP. GitHub Copilot in VS Code");
            sb.AppendLine("                      and Visual Studio does - both are listed above.");
            sb.AppendLine();
            sb.AppendLine("Useful arguments");
            sb.AppendLine("  --read-only              refuse every write and destructive tool");
            sb.AppendLine("  --revit-version 2024     pin one Revit release when several are open");
            sb.AppendLine("  --timeout 30000          raise the per-request budget (ms)");
            sb.AppendLine();
            sb.AppendLine("Example - a safe read-only entry alongside the full-access one:");
            sb.AppendLine();
            sb.AppendLine(ReadOnlyExample(exe));

            return sb.ToString();
        }

        private static bool Is(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        private static void Section(StringBuilder sb, string title, string where, string body)
        {
            sb.AppendLine("--- " + title + " " + new string('-', Math.Max(0, 66 - title.Length)));
            sb.AppendLine("  " + where);
            sb.AppendLine();
            foreach (string line in body.Split('\n')) sb.AppendLine("  " + line.TrimEnd());
            sb.AppendLine();
        }

        /// <summary>
        /// Builds a NESTED entry, e.g. OpenClaw's mcp.servers. Clients that nest silently ignore a
        /// flat top-level key, which looks identical to the connector simply not working.
        /// </summary>
        private static string NestedJson(string exe, string outerKey, string innerKey)
        {
            JsonValue entry = JsonValue.NewObject();
            entry.Set("command", exe);
            entry.Set("args", JsonValue.NewArray());

            JsonValue servers = JsonValue.NewObject();
            servers.Set("revit", entry);

            JsonValue outer = JsonValue.NewObject();
            outer.Set(innerKey, servers);

            JsonValue root = JsonValue.NewObject();
            root.Set(outerKey, outer);
            return root.ToJson(true);
        }

        /// <summary>Builds the stdio entry, using the shared JSON writer so escaping is always right.</summary>
        private static string StdioJson(string exe, string rootKey, bool includeType)
        {
            JsonValue entry = JsonValue.NewObject();
            if (includeType) entry.Set("type", "stdio");
            entry.Set("command", exe);
            entry.Set("args", JsonValue.NewArray());
            if (!includeType) entry.Set("env", JsonValue.NewObject());

            JsonValue servers = JsonValue.NewObject();
            servers.Set("revit", entry);

            JsonValue root = JsonValue.NewObject();
            root.Set(rootKey, servers);
            return root.ToJson(true);
        }

        private static string ReadOnlyExample(string exe)
        {
            JsonValue safe = JsonValue.NewObject();
            safe.Set("command", exe);
            safe.Set("args", J.A("--read-only"));

            JsonValue full = JsonValue.NewObject();
            full.Set("command", exe);
            full.Set("args", JsonValue.NewArray());

            JsonValue servers = JsonValue.NewObject();
            servers.Set("revit-readonly", safe);
            servers.Set("revit", full);

            JsonValue root = JsonValue.NewObject();
            root.Set("mcpServers", servers);
            return root.ToJson(true);
        }
    }
}
