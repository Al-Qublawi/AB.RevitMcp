<#
.SYNOPSIS
    Walks through an AB .msi's pages with the default choices and saves a picture of each one.
    Called by Test-AdvToolsMsi.ps1 -Screens, on its dry-run copy - never on a real package.

.DESCRIPTION
    Opens the package with its full UI, and for every page: waits for it, saves it as a PNG
    (PrintWindow, so other windows on top do not matter), then presses Next. On the "Ready to
    install" page it saves the picture and presses Cancel - it never presses Install. The windows
    do appear on screen for a few seconds each.

    -ExitPage also shows the finish page ("installed", or "removed" with WixUI_InstallMode=Remove)
    by temporarily putting it first in the copy's page order.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Msi,
    [Parameter(Mandatory = $true)][string]$Folder,
    [string[]]$Properties = @(),
    [switch]$ExitPage,
    # With -ExitPage: 'Remove' or 'Repair' shows that version of the finish page.
    [string]$ExitMode,
    [int]$TimeoutSeconds = 240
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Folder | Out-Null

if (-not ('ABAdvTools.MsiPages' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace ABAdvTools
{
    public static class MsiPages
    {
        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

        private static string Text(IntPtr hwnd) { var sb = new StringBuilder(512); GetWindowText(hwnd, sb, 512); return sb.ToString(); }
        private static string Class(IntPtr hwnd) { var sb = new StringBuilder(128); GetClassName(hwnd, sb, 128); return sb.ToString(); }

        /// <summary>The visible top-level dialog of a process tree (msiexec runs the UI in the process we started).</summary>
        public static IntPtr FindDialog(int processId)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate (IntPtr hwnd, IntPtr l)
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == processId && IsWindowVisible(hwnd) && Class(hwnd) == "MsiDialogCloseClass") { found = hwnd; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>Every visible child control's text, joined - changes whenever the page changes.</summary>
        public static string Signature(IntPtr dialog)
        {
            var parts = new List<string>();
            EnumChildWindows(dialog, delegate (IntPtr hwnd, IntPtr l)
            {
                if (IsWindowVisible(hwnd)) parts.Add(Text(hwnd));
                return true;
            }, IntPtr.Zero);
            return string.Join("|", parts.ToArray());
        }

        public static IntPtr FindButton(IntPtr dialog, string caption)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(dialog, delegate (IntPtr hwnd, IntPtr l)
            {
                if (IsWindowVisible(hwnd) && IsWindowEnabled(hwnd) && Class(hwnd) == "Button" &&
                    Text(hwnd).Replace("&", "").Trim().StartsWith(caption, StringComparison.OrdinalIgnoreCase)) { found = hwnd; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public static bool Click(IntPtr dialog, string caption)
        {
            IntPtr button = FindButton(dialog, caption);
            if (button == IntPtr.Zero) return false;
            PostMessage(button, 0x00F5, IntPtr.Zero, IntPtr.Zero);   // BM_CLICK
            return true;
        }

        public static string Title(IntPtr dialog) { return Text(dialog); }

        public static void Save(IntPtr dialog, string file)
        {
            RECT r;
            GetWindowRect(dialog, out r);
            using (var bitmap = new Bitmap(Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top)))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    IntPtr hdc = g.GetHdc();
                    PrintWindow(dialog, hdc, 2);
                    g.ReleaseHdc(hdc);
                }
                bitmap.Save(file, ImageFormat.Png);
            }
        }
    }
}
'@
}

if ($ExitPage) {
    # Show the finish page first in this copy of the copy.
    $exitCopy = Join-Path (Split-Path -Parent $Msi) ('exitpage-' + [System.IO.Path]::GetFileName($Msi))
    Copy-Item $Msi $exitCopy -Force
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @([string]::Copy($exitCopy), 1))
    # The welcome page is skipped, and the finish page (normally shown on success, -1) shown instead.
    $statements = @("UPDATE ``InstallUISequence`` SET ``Condition``='0' WHERE ``Action``='ABWelcomeDlg'",
                    "UPDATE ``InstallUISequence`` SET ``Sequence``=1297, ``Condition``='1' WHERE ``Action``='ABExitDlg'")
    if ($ExitMode) { $statements += "INSERT INTO ``Property`` (``Property``, ``Value``) VALUES ('WixUI_InstallMode', '$ExitMode')" }
    foreach ($sql in $statements) {
        $v = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @($sql))
        $v.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $v, $null) | Out-Null
        $v.GetType().InvokeMember('Close', 'InvokeMethod', $null, $v, $null) | Out-Null
        [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($v) | Out-Null
    }
    $db.GetType().InvokeMember('Commit', 'InvokeMethod', $null, $db, $null) | Out-Null
    [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($db) | Out-Null
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    $Msi = $exitCopy
}

$process = Start-Process msiexec.exe -ArgumentList (@('/i', "`"$Msi`"") + $Properties) -PassThru
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$last = ''
$page = 0
$saved = @()

try {
    while ((Get-Date) -lt $deadline -and -not $process.HasExited) {
        $dialog = [ABAdvTools.MsiPages]::FindDialog($process.Id)
        if ($dialog -eq [IntPtr]::Zero) { [System.Threading.Thread]::Sleep(300); continue }

        $signature = [ABAdvTools.MsiPages]::Signature($dialog)
        if ($signature -eq $last) { [System.Threading.Thread]::Sleep(300); continue }
        [System.Threading.Thread]::Sleep(1500)   # let the page finish painting
        $signature = [ABAdvTools.MsiPages]::Signature($dialog)
        $last = $signature

        # Preparing / progress pages have no Next, Install, Finish or Yes: just wait them out.
        $isReady = [ABAdvTools.MsiPages]::FindButton($dialog, 'Install') -ne [IntPtr]::Zero
        $hasNext = [ABAdvTools.MsiPages]::FindButton($dialog, 'Next') -ne [IntPtr]::Zero
        $hasFinish = [ABAdvTools.MsiPages]::FindButton($dialog, 'Finish') -ne [IntPtr]::Zero
        $hasYes = [ABAdvTools.MsiPages]::FindButton($dialog, 'Yes') -ne [IntPtr]::Zero
        $hasAccept = [ABAdvTools.MsiPages]::FindButton($dialog, 'I have read') -ne [IntPtr]::Zero
        if ($hasAccept -and -not $hasNext) {
            # The read-me page: picture it as the user first sees it, then accept, then carry on.
            $page++
            $file = Join-Path $Folder ('{0:D2}.png' -f $page)
            [ABAdvTools.MsiPages]::Save($dialog, $file)
            $saved += $file
            [void][ABAdvTools.MsiPages]::Click($dialog, 'I have read')
            [System.Threading.Thread]::Sleep(800)
            [void][ABAdvTools.MsiPages]::Click($dialog, 'Next')
            continue
        }
        if (-not ($isReady -or $hasNext -or $hasFinish -or $hasYes)) { continue }

        $page++
        $file = Join-Path $Folder ('{0:D2}.png' -f $page)
        [ABAdvTools.MsiPages]::Save($dialog, $file)
        $saved += $file

        if ($hasYes) { [void][ABAdvTools.MsiPages]::Click($dialog, 'Yes') }          # "cancel setup?"
        elseif ($isReady) { [void][ABAdvTools.MsiPages]::Click($dialog, 'Cancel') }  # never Install
        elseif ($hasFinish) { [void][ABAdvTools.MsiPages]::Click($dialog, 'Finish') }
        else { [void][ABAdvTools.MsiPages]::Click($dialog, 'Next') }
    }
}
finally {
    if (-not $process.HasExited) {
        [System.Threading.Thread]::Sleep(2000)
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
}

Write-Host ("Saved {0} page(s) to {1}" -f $saved.Count, $Folder)
$saved
