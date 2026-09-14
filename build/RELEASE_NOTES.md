Universal MCP server + Revit add-in. Any MCP-compatible AI client — Claude Desktop, Claude Code,
Cursor, VS Code, Windsurf, Cline, LM Studio, OpenClaw, or a local Qwen/Llama/DeepSeek behind an
MCP agent — can safely query, create and modify Autodesk Revit models.

**Revit 2020 – 2026 · 78 tools · metric in, metric out · no AI vendor lock-in.**

## What's new in 1.4.0

### One ribbon tab for every AB add-in

The bridge now lives on the **AB Adv Tools** tab, shared with every other AB add-in for Revit, as the
**MCP Bridge** panel. Start/Stop Bridge, Status, Tools, Copy config, Open logs, Auto-start and Code exec
are all unchanged. At the end of the tab is one **AB Adv Tools** panel for the whole suite:

- **About** lists every AB tool loaded in Revit, with its version.
- **Check for Updates** checks them all.
- **LinkedIn**.

### Release notifications

Once a day at most, in the background, the add-in checks this repository's latest release and tells
you **once per new version**, when Revit is idle with a view open. The only thing sent is an
anonymous request for the latest release. Turn it off in **AB Adv Tools › About**.

### The installer finds earlier versions

`AB.RevitMcp.Setup.exe` now finds an earlier copy of the bridge, lists it, and removes it before
installing unless you untick it. It also registers in Apps and Features (per user, still no
administrator rights) and adds `/uninstall`, `/scan` and `/log:<file>`.

Everything the 1.3 installer did is still there: the Revit and AI-agent lists, **Add custom agent…**,
**Verify** at any time, carrying on with Revit open (locked releases are skipped and reported), `/silent`,
`/noclients`, and the `%TEMP%\ABRevitMcp-Setup-*.log` log.

The installer now carries every Revit release from 2020 to 2026, whichever releases the build machine
had installed.

**Full history:** [CHANGELOG.md](https://github.com/Al-Qublawi/AB.RevitMcp/blob/main/CHANGELOG.md)

---

> **The installer is not code-signed.** On a managed machine, Defender's Attack Surface Reduction
> rule can block it. See [docs/DEPLOYMENT.md](https://github.com/Al-Qublawi/AB.RevitMcp/blob/main/docs/DEPLOYMENT.md).
