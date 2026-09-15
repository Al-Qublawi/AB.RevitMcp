Universal MCP server + Revit add-in. Any MCP-compatible AI client — Claude Desktop, Claude Code,
Cursor, VS Code, Windsurf, Cline, LM Studio, OpenClaw, or a local Qwen/Llama/DeepSeek behind an
MCP agent — can safely query, create and modify Autodesk Revit models.

**Revit 2020 – 2026 · 78 tools · metric in, metric out · no AI vendor lock-in.**

## What's new in 1.5.0

### The installer is an .msi: `AB.RevitMcp-1.5.0.msi`

1.4.0's `AB.RevitMcp.Setup.exe` was blocked on company PCs by Microsoft Defender's attack surface
reduction rule *"Block executable files from running unless they meet a prevalence, age, or trusted
list criteria"*. That rule stops unknown programs, not Windows Installer packages, and the new package
runs no program of its own.

Everything 1.4.0's installer offered is still there:

- **per user**, no administrator rights
- a tick per **Revit release**, the installed ones ticked — remembered for the next upgrade
- the **AI agents** to configure, the ones found ticked, plus a **custom agent** (any JSON or YAML MCP
  config, with its own server-map key)
- **earlier versions**: a copy installed by 1.4.0's `Setup.exe` is removed first unless you untick it
- Apps and Features, and silent deployment: `msiexec /i AB.RevitMcp-1.5.0.msi /qn` (`NOCLIENTS=1`
  configures no client)

### AI Clients and Verify, in Revit

Because the package runs no code, **Revit configures the AI clients you ticked**, the first time it
starts after installing, and shows you what it did. Restart the clients afterwards.

The new **AI Clients** and **Verify** buttons on the MCP Bridge panel do the rest at any time: configure
or remove the `revit` server, add a custom agent with a file browser, and run the server's doctor.
**Copy config** now writes the Defender-safe `dotnet.exe` launch form too.

If Setup.exe 1.4.0 is installed, just run the .msi: it replaces it. Close Revit first.

**Full history:** [CHANGELOG.md](https://github.com/Al-Qublawi/AB.RevitMcp/blob/main/CHANGELOG.md)
