# Deploying to other machines

## "Risky action blocked" / "Your administrator has blocked this action"

If Defender blocks `AB.RevitMcp.Setup.exe` with:

> **Blocked by:** Attack surface reduction
> **Rule:** Block executable files from running unless they meet a prevalence, age, or trusted list criteria

**this is not a malware detection.** Nothing was found in the file. It is an **Attack Surface
Reduction (ASR) policy rule** — GUID `01443614-cd74-433a-b99e-2ecdc07bfc25` — which blocks *any*
executable Microsoft has not seen widely enough, for long enough, or that is not on an allow list.

A freshly compiled, unsigned executable meets none of those criteria by definition. Your own build
of any tool would be blocked identically on a machine with that rule enforced.

Two things follow:

- **"Your administrator has blocked this action" means the machine is managed.** The rule came from
  Intune or Group Policy. The person sitting at that machine cannot lift it, and should not try to —
  the fix goes through whoever manages the fleet.
- **Reputation is earned by the certificate, not the file.** Allowlisting one build's hash works
  until you rebuild. Signing fixes it permanently.

---

## Fix 1 — sign the build (the real fix)

Once you have a code-signing certificate, signing is one flag:

```powershell
# certificate installed in CurrentUser\My (typical for a hardware token)
.\build\build-installer.ps1 -CertThumbprint A1B2C3D4E5F6...

# or a .pfx on disk
$pw = Read-Host -AsSecureString "PFX password"
.\build\build-installer.ps1 -CertPfx .\mycert.pfx -CertPassword $pw
```

This signs **both** the installer and the binaries inside it — the add-in DLLs Revit loads and the
MCP server executable your AI client launches. Signing only the installer would leave everything it
drops behind unsigned, which is the mistake that makes people think signing "didn't work".

Everything is SHA-256 and **timestamped**, so signatures stay valid after the certificate expires.

### Which certificate

| Type | Cost/yr | ASR / SmartScreen behaviour |
| --- | --- | --- |
| **OV** (standard) | ~$200–400 | Valid signature immediately, but **no reputation on day one** — ASR may still block early builds until downloads accumulate |
| **EV** (extended validation) | ~$400–700 | **Immediate SmartScreen reputation.** Ships on a hardware token / cloud HSM |
| Self-signed | free | Useless here — not trusted by anything outside machines you install the root on |

For distributing to colleagues on managed corporate machines, **EV is the one that actually solves
this on day one**. OV plus an IT allowlist (below) also works and is cheaper.

---

## Fix 2 — ask IT to allow it

Every build writes `dist\AB.RevitMcp.Setup.allowlist.txt` containing exactly what an administrator
needs: publisher, version, size, SHA-256, and signature status.

Send that file to IT and ask for **one** of these, best first:

1. **Allow by publisher certificate** — survives every future build. Only possible once signed.
2. **ASR exclusion for the installer path or hash** — Defender for Endpoint → ASR rule exclusions,
   or Intune → Endpoint security → Attack surface reduction.
3. **WDAC / AppLocker publisher rule**, if the estate uses those instead.

An administrator can also verify the file themselves:

```powershell
Get-FileHash .\AB.RevitMcp.Setup.exe -Algorithm SHA256
Get-AuthenticodeSignature .\AB.RevitMcp.Setup.exe | Format-List
```

> Do not ask users to turn ASR off, run as administrator, or "just allow it once" on a managed
> device. That rule exists because unsigned executables from a Downloads folder are exactly how
> most endpoint compromises start — the honest answer is to sign the software.

---

## Mark of the Web (a separate, smaller problem)

Anything downloaded or emailed carries a "downloaded from the internet" flag. That is **not** the
ASR block above, but it causes its own symptoms — Revit silently refusing to load the add-in being
the common one. On the receiving machine:

```powershell
Get-ChildItem "$env:LOCALAPPDATA\ABRevitMcp" -Recurse | Unblock-File
Get-ChildItem "$env:APPDATA\Autodesk\Revit\Addins" -Recurse -Filter "AB.RevitMcp.*" | Unblock-File
```

Copying over a network share or USB usually avoids it entirely.

---

## Deployment options that avoid the problem

**Internal file share instead of Downloads.** ASR's prevalence rule is aimed at the browser-download
path. A signed build on a share that IT already trusts is the normal enterprise route.

**Silent install from a managed script.** Once IT has allowed the publisher, roll it out with:

```
AB.RevitMcp.Setup.exe /silent
AB.RevitMcp.Setup.exe /silent /noagents    :: skip AI client registration
```

Exit code `0` = success, `1` = problems. A log is always written to
`%TEMP%\ABRevitMcp-Setup-*.log`.

**Build on the target machine.** With the .NET SDK and Revit installed, `INSTALL.bat` compiles
locally — locally compiled binaries in a user profile are not subject to the download-prevalence
rule. Practical for a handful of BIM workstations, not for a fleet.

---

## What this software actually does, for a security review

Worth having ready when IT asks:

- **Per-user only.** No admin rights, no `Program Files`, no registry, no services, no scheduled
  tasks. Everything lives in `%APPDATA%\Autodesk\Revit\Addins` and `%LOCALAPPDATA%\ABRevitMcp`.
- **No network access.** The bridge is a **named pipe**, restricted by ACL to the current Windows
  user. Nothing is sent off the machine. The optional HTTP transport is off by default, binds
  loopback only, and validates `Origin`.
- **No telemetry.** Logs are local newline-delimited JSON.
- **The AI does not run code.** Tools are 70 fixed, schema-validated operations. The one arbitrary
  code path (`revit_execute_code`) is disabled by default behind **three** independent gates: a
  server flag, a Revit-side setting, and a per-call confirmation.
- **Destructive operations require explicit confirmation** and most support a dry run that reports
  the blast radius and rolls back.
- **Fully open source** — the whole thing can be read and rebuilt from source.
