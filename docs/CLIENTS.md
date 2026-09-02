# Connecting AI clients

## The thing to get straight first

**MCP is a feature of the client, not of the model.**

DeepSeek, GPT-4o, Gemini and Qwen cannot "support MCP" — a *model* only emits text and tool calls.
The application around it discovers the tools, runs the server process, and executes the calls. So
the question is never *"does DeepSeek work with this?"* but *"does the app I'm running DeepSeek in
speak MCP?"*

That is good news: any model your client can drive gets all 46 Revit tools for free. Put DeepSeek-R1
in Cline, or a local Qwen in LM Studio, and they drive Revit exactly as Claude does. Tool-calling
quality varies by model — that affects how well it *chooses* tools, not whether it can.

## Get the exact snippet for your machine

The server prints its own configuration, with its real absolute path already filled in:

```
%LOCALAPPDATA%\ABRevitMcp\Server\AB.RevitMcp.Server.exe --print-config
```

Add a client name to narrow it:

```
AB.RevitMcp.Server.exe --print-config cursor
```

Known names: `claude-desktop`, `claude-code`, `cursor`, `vscode`, `windsurf`, `cline`,
`continue`, `lmstudio`, `generic`, `http`, `all`.

## Client matrix

| Client | Transport | Config file | Root key |
| --- | --- | --- | --- |
| Claude Desktop | stdio | `%APPDATA%\Claude\claude_desktop_config.json` | `mcpServers` |
| Claude Code | stdio | `claude mcp add`, or `.mcp.json` in the project | `mcpServers` |
| Cursor | stdio | `%USERPROFILE%\.cursor\mcp.json` | `mcpServers` |
| VS Code (Copilot agent mode) | stdio | `%APPDATA%\Code\User\mcp.json` or `.vscode\mcp.json` | **`servers`** |
| Windsurf | stdio | `%USERPROFILE%\.codeium\windsurf\mcp_config.json` | `mcpServers` |
| Cline / Roo Code | stdio | MCP Servers panel → `cline_mcp_settings.json` | `mcpServers` |
| Continue | stdio | `%USERPROFILE%\.continue\config.yaml` | **YAML, not JSON** |
| LM Studio | stdio | Program → Install → Edit `mcp.json` | `mcpServers` |
| Zed, Goose, Chatbox, Cherry Studio | stdio | client-specific | `mcpServers` |
| OpenAI Agents SDK | stdio | in code (`MCPServerStdio`) | — |
| n8n, Open WebUI, remote clients | **HTTP** | point at a URL | — |

Two traps worth knowing: **VS Code uses `servers`, not `mcpServers`**, and **Continue uses YAML**.
Everything else is the same JSON block.

## The standard block

```json
{
  "mcpServers": {
    "revit": {
      "command": "C:\\Users\\<YOU>\\AppData\\Local\\ABRevitMcp\\Server\\AB.RevitMcp.Server.exe",
      "args": [],
      "env": {}
    }
  }
}
```

Backslashes must be doubled in JSON. `--print-config` handles that for you.

## Recommended: two entries, one read-only

Give the model a safe entry and an explicit full-access one. Day to day you use `revit-readonly`,
and switch deliberately when you want the model to change the model.

```json
{
  "mcpServers": {
    "revit-readonly": {
      "command": "C:\\Users\\<YOU>\\AppData\\Local\\ABRevitMcp\\Server\\AB.RevitMcp.Server.exe",
      "args": ["--read-only"]
    },
    "revit": {
      "command": "C:\\Users\\<YOU>\\AppData\\Local\\ABRevitMcp\\Server\\AB.RevitMcp.Server.exe",
      "args": []
    }
  }
}
```

In read-only mode every write and destructive tool is refused and its description is prefixed
`[DISABLED - this server runs in read-only mode]`, so the model can see it should not try.

Other arguments worth putting in `args`:

| Argument | Why |
| --- | --- |
| `--revit-version 2024` | Pin one release when several Revit versions are open at once. |
| `--timeout 60000` | Large models or slow operations (purge on a big file). Export already has its own 5-minute budget. |
| `--verbose` | Log every JSON-RPC method to stderr for debugging. |

## HTTP transport

For clients that want a URL rather than a child process (n8n, Open WebUI, a remote IDE):

```
AB.RevitMcp.Server.exe --http --port 3333
```

- Endpoint: `http://127.0.0.1:3333/mcp` (POST JSON-RPC)
- Health: `http://127.0.0.1:3333/health`

It binds **loopback only** and validates the `Origin` header against DNS rebinding.

> **Do not expose this port to a network or a tunnel without authentication in front of it.**
> There is no auth in the server itself. Anything that can reach that port can modify — and delete
> from — whatever Revit model is open. If you need remote access, put it behind a reverse proxy
> that authenticates, and strongly consider running with `--read-only`.

## ChatGPT

OpenAI's MCP support is for **remote** servers over HTTP/SSE (Agents SDK, and connectors in
ChatGPT's developer mode) — it will not spawn a local stdio process the way Claude Desktop and
Cursor do. Reaching a local Revit from ChatGPT therefore means exposing the HTTP transport off
this machine, which carries the risk in the box above. The OpenAI **Agents SDK** running locally is
the sane path: it supports stdio directly, so the model stays remote while the tool stays local.

## Auto-configure

The installer can write these files for you, backing up anything it touches
(`<file>.abmcp-backup`):

```powershell
.\build\install.ps1 -ConfigureClients
```

It configures Claude Desktop, Cursor and VS Code. It deliberately does **not** edit Claude Code's
`~/.claude.json`, because that file also holds live session state and a scripted rewrite risks
clobbering it — run `claude mcp add revit "<path>"` instead.

## Check it worked

```
%LOCALAPPDATA%\ABRevitMcp\Doctor\AB.RevitMcp.Doctor.exe
```

Then, in the client, ask it to call `revit_get_model_info`. If the bridge is not running you get a
clear message telling you to press **Start Bridge** on the AB MCP AI ribbon tab — Revit does not
need to be open when the client starts.
