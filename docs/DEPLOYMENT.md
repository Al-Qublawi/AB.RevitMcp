# Deploying to other machines

## "Risky action blocked" / "Your administrator has blocked this action"

Up to 1.4.0 the bridge shipped as `AB.RevitMcp.Setup.exe`, and on managed machines Defender blocked
it with:

> **Blocked by:** Attack surface reduction
> **Rule:** Block executable files from running unless they meet a prevalence, age, or trusted list criteria

**That is not a malware detection.** It is an **Attack Surface Reduction (ASR) policy rule** — GUID
`01443614-cd74-433a-b99e-2ecdc07bfc25` — which blocks *any* executable Microsoft has not seen widely
enough, for long enough, or that is not on an allow list. A freshly compiled, unsigned executable
meets none of those criteria by definition.

**Since 1.5.0 the installer is `AB.RevitMcp-<version>.msi`.** The rule applies to executables; a
Windows Installer package is opened by `msiexec.exe`, which Windows trusts. The package contains no
custom action that runs code — `shared\ABAdvTools\msi\New-AdvToolsMsi.ps1` refuses to build one — so
there is nothing inside it for the rule to act on either. That is also why the installer does not
write the AI clients' configuration files itself: it records the choice, and the add-in applies it
inside Revit, which the machine already trusts.

Two places the rule can still matter:

- **The MCP server your AI client starts** is an executable. The add-in configures clients to start
  it as `dotnet.exe AB.RevitMcp.Server.dll` when .NET is installed (Microsoft-signed, so not blocked),
  and falls back to the `.exe` otherwise. **AI Clients → Verify** in Revit shows which is in use.
- **"Your administrator has blocked this action" means the machine is managed.** If a stricter policy
  (WDAC, AppLocker) blocks the package or the add-in itself, the fix goes through whoever manages the
  fleet — the person at that machine cannot lift it and should not try.

---

## Signing (optional, and still worth it)

Once you have a code-signing certificate, signing is one flag:

```powershell
# certificate installed in CurrentUser\My (typical for a hardware token)
.\build\build-installer.ps1 -CertThumbprint A1B2C3D4E5F6...

# or a .pfx on disk
$pw = Read-Host -AsSecureString "PFX password"
.\build\build-installer.ps1 -CertPfx .\mycert.pfx -CertPassword $pw
```

This signs the add-in DLLs Revit loads, the MCP server executable, and the `.msi`. A signature gives IT
one publisher to allow for every future build, instead of a hash per build, and lets the server `.exe`
run on machines without .NET.

Everything is SHA-256 and **timestamped**, so signatures stay valid after the certificate expires.

| Type | Cost/yr | ASR / SmartScreen behaviour |
| --- | --- | --- |
| **OV** (standard) | ~$200–400 | Valid signature immediately, but **no reputation on day one** |
| **EV** (extended validation) | ~$400–700 | **Immediate SmartScreen reputation.** Ships on a hardware token / cloud HSM |
| Self-signed | free | Useless here — not trusted by anything outside machines you install the root on |

---

## If IT asks for details

Every build writes `dist\AB.RevitMcp.allowlist.txt`: publisher, version, size, SHA-256 and signature
status of the `.msi`. An administrator can also check the file themselves:

```powershell
Get-FileHash .\AB.RevitMcp-1.5.0.msi -Algorithm SHA256
Get-AuthenticodeSignature .\AB.RevitMcp-1.5.0.msi | Format-List
```

> Do not ask users to turn ASR off, run as administrator, or "just allow it once" on a managed
> device.

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

## Rolling it out

Per user, so run it as the user (a login script, Intune "user" context, or by hand):

```
msiexec /i AB.RevitMcp-1.5.0.msi /qn                 :: AI clients found are configured when Revit starts
msiexec /i AB.RevitMcp-1.5.0.msi /qn NOCLIENTS=1     :: configure no AI client
msiexec /i AB.RevitMcp-1.5.0.msi /qn ALLRELEASES=1   :: every Revit release, installed or not
msiexec /i AB.RevitMcp-1.5.0.msi /l*v setup.log      :: with a log
msiexec /x AB.RevitMcp-1.5.0.msi /qn                 :: uninstall
```

Standard Windows Installer exit codes: `0` success, `3010` success but a restart finishes it (Revit
was open), `1602` cancelled, `1603` failed — see the log.

**Build on the target machine.** With the .NET SDK and Revit installed, `INSTALL.bat` compiles
locally. Practical for a handful of BIM workstations, not for a fleet.

---

## What this software actually does, for a security review

Worth having ready when IT asks:

- **Per-user only.** No admin rights, no `Program Files`, no services, no scheduled tasks. Files live
  in `%APPDATA%\Autodesk\Revit\Addins` and `%LOCALAPPDATA%\ABRevitMcp`; the installer keeps its own
  state under `HKCU\Software\AB Adv Tools\Installer\RevitMcp`.
- **The installer runs no code.** Windows Installer tables only — no DLL, EXE or script custom actions.
- **No network access.** The bridge is a **named pipe**, restricted by ACL to the current Windows
  user. Nothing is sent off the machine. The optional HTTP transport is off by default, binds
  loopback only, and validates `Origin`. The only outbound request is the once-a-day anonymous check
  for a newer GitHub release, which can be switched off.
- **No telemetry.** Logs are local newline-delimited JSON.
- **The AI does not run code.** Tools are fixed, schema-validated operations. The one arbitrary
  code path (`revit_execute_code`) is disabled by default behind **three** independent gates: a
  server flag, a Revit-side setting, and a per-call confirmation.
- **Destructive operations require explicit confirmation** and most support a dry run that reports
  the blast radius and rolls back.
- **Fully open source** — the whole thing can be read and rebuilt from source.
