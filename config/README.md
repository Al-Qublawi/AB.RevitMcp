# MCP client configuration templates

These are starting points. For a snippet with **your** absolute path already filled in, run:

```
%LOCALAPPDATA%\ABRevitMcp\Server\AB.RevitMcp.Server.exe --print-config
```

| File | Client | Root key |
| --- | --- | --- |
| `claude_desktop_config.json` | Claude Desktop | `mcpServers` |
| `claude_code.mcp.json` | Claude Code (`.mcp.json`) | `mcpServers` |
| `cursor_mcp.json` | Cursor | `mcpServers` |
| `vscode_mcp.json` | VS Code Copilot agent mode | **`servers`** |
| `windsurf_mcp_config.json` | Windsurf | `mcpServers` |
| `cline_mcp_settings.json` | Cline / Roo Code | `mcpServers` |
| `continue_config.yaml` | Continue | **YAML** |
| `generic_mcp.json` | Anything else, plus a read-only entry | `mcpServers` |

Full guidance, including the HTTP transport and the ChatGPT caveat: [../docs/CLIENTS.md](../docs/CLIENTS.md).

MCP is a client feature, not a model feature — any model your client can drive (DeepSeek, GPT,
Gemini, Qwen, a local Llama) gets all 46 Revit tools.
