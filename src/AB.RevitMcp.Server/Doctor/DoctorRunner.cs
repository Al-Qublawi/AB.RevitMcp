using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;
using AB.RevitMcp.Ipc;

namespace AB.RevitMcp.Server.Doctor
{
    /// <summary>
    /// Installation diagnostics: answers "will Revit load this, and can the MCP server reach it?"
    /// without launching Revit and without loading a single Revit binary.
    ///
    /// This lives INSIDE the server executable rather than as a separate tool so that a machine
    /// which has never had .NET installed can still run the diagnostic - the server ships
    /// self-contained, a second framework-dependent exe would not start at all.
    /// </summary>
    public static class DoctorRunner
    {
        private static int _failures;
        private static int _warnings;
        private static int _liveBridges;
        private static readonly List<string> Remedies = new List<string>();

        public static async Task<int> RunAsync(string[] args)
        {
            bool pingBridge = true;
            string revitRoot = @"C:\Program Files\Autodesk";

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--no-ping") pingBridge = false;
                else if (args[i] == "--revit-root" && i + 1 < args.Length) revitRoot = args[++i];
                else if (args[i] == "--doctor-help")
                {
                    Console.WriteLine("AB.RevitMcp.Server.exe --doctor [--no-ping] [--revit-root <path>]");
                    Console.WriteLine("  Verifies the installation without starting Revit.");
                    return 0;
                }
            }

            Header("AB Revit MCP - installation check");
            Console.WriteLine("  Host       : " + Environment.OSVersion.VersionString + "  (.NET " + Environment.Version + ")");
            Console.WriteLine("  User       : " + Environment.UserName);
            Console.WriteLine("  Install    : " + IpcConstants.DataRoot);
            Console.WriteLine();

            CheckAddins(revitRoot);
            CheckServer();
            if (pingBridge) await CheckLiveBridgeAsync().ConfigureAwait(false);

            Header("Result");
            if (_failures == 0 && _warnings == 0)
                Console.WriteLine(_liveBridges > 0
                    ? "  All checks passed, and " + _liveBridges + " Revit session(s) are live and answering. " +
                      "Point your AI client at the server and go."
                    : "  All checks passed. Start Revit, open the 'AB Adv Tools' tab and press Start Bridge.");
            else
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0} problem(s), {1} warning(s).", _failures, _warnings));

            if (Remedies.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("  What to do:");
                for (int i = 0; i < Remedies.Count; i++) Console.WriteLine("    " + (i + 1) + ". " + Remedies[i]);
            }
            Console.WriteLine();

            return _failures == 0 ? 0 : 1;
        }

        // ==================================================================
        //  Add-in per Revit release
        // ==================================================================
        private static void CheckAddins(string revitRoot)
        {
            Header("Revit add-in");

            string addinsRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Autodesk", "Revit", "Addins");

            if (!Directory.Exists(addinsRoot))
            {
                Fail("No Revit add-ins folder at " + addinsRoot,
                     "Install Revit, then run build\\install.ps1.");
                return;
            }

            int found = 0;

            foreach (string versionDir in Directory.GetDirectories(addinsRoot))
            {
                string version = Path.GetFileName(versionDir);
                string manifest = Path.Combine(versionDir, "AB.RevitMcp.addin");
                if (!File.Exists(manifest)) continue;

                found++;
                Console.WriteLine("  Revit " + version);

                int revitYear;
                int.TryParse(version, NumberStyles.Integer, CultureInfo.InvariantCulture, out revitYear);
                bool expectNetFramework = revitYear > 0 && revitYear < 2025;

                // ---- manifest ----
                string assemblyPath = null;
                try
                {
                    XDocument doc = XDocument.Load(manifest);
                    XElement addin = doc.Root != null ? doc.Root.Element("AddIn") : null;
                    if (addin == null) { Fail("    manifest has no <AddIn> element", "Re-run install.ps1."); continue; }

                    XElement assembly = addin.Element("Assembly");
                    XElement fullClass = addin.Element("FullClassName");
                    assemblyPath = assembly != null ? assembly.Value.Trim() : null;

                    if (string.IsNullOrEmpty(assemblyPath))
                    {
                        Fail("    manifest has no <Assembly> path", "Re-run install.ps1.");
                        continue;
                    }

                    // Revit resolves a relative <Assembly> against the manifest's own folder,
                    // which is the layout this installer uses.
                    if (!Path.IsPathRooted(assemblyPath))
                        assemblyPath = Path.GetFullPath(Path.Combine(versionDir, assemblyPath.Replace('/', '\\')));
                    if (fullClass == null || fullClass.Value.Trim() != "AB.RevitMcp.Addin.App")
                        Warn("    unexpected <FullClassName>: " + (fullClass == null ? "(missing)" : fullClass.Value));

                    Pass("    manifest parses");
                }
                catch (Exception ex)
                {
                    Fail("    manifest is not valid XML: " + ex.Message, "Re-run install.ps1 to rewrite it.");
                    continue;
                }

                // ---- the assembly the manifest points at ----
                if (!File.Exists(assemblyPath))
                {
                    Fail("    assembly missing: " + assemblyPath,
                         "Run build\\install.ps1 again - the manifest points at a file that is not there.");
                    continue;
                }
                Pass("    assembly present");

                // ---- the endpoint-protection trap ----
                // Bitdefender, CrowdStrike, Cortex and friends routinely block DLL loads out of
                // Local AppData. Revit then reports "the assembly does not exist" about a file that
                // is demonstrably on disk, which sends you hunting in entirely the wrong place.
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(localAppData) &&
                    assemblyPath.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase))
                {
                    Warn("    add-in loads from %LOCALAPPDATA% - endpoint protection often blocks DLLs there");
                    string relocate = "Re-run build\\install.ps1 to move the add-in beside its manifest under " +
                                      "%APPDATA%\\Autodesk\\Revit\\Addins, which security products already trust.";
                    if (!Remedies.Contains(relocate)) Remedies.Add(relocate);
                }

                // ---- the closure ----
                string dir = Path.GetDirectoryName(assemblyPath);
                string[] required = { "AB.RevitMcp.Addin.dll", "AB.RevitMcp.Contracts.dll", "AB.RevitMcp.Ipc.dll" };
                bool closureOk = true;

                foreach (string name in required)
                {
                    string p = Path.Combine(dir, name);
                    if (!File.Exists(p))
                    {
                        Fail("    missing dependency: " + name,
                             "Re-run build\\install.ps1; the add-in folder is incomplete.");
                        closureOk = false;
                        continue;
                    }

                    AssemblyFacts facts = AssemblyProbe.Read(p);
                    if (facts.ReadError != null)
                    {
                        Fail("    " + name + ": " + facts.ReadError, "Rebuild and reinstall.");
                        closureOk = false;
                        continue;
                    }

                    // THE bug this tool exists to catch.
                    if (expectNetFramework && facts.ReferencesNetStandardFacade)
                    {
                        Fail("    " + name + " references the 'netstandard' facade",
                             "Revit " + version + " runs on .NET Framework and cannot resolve that facade. " +
                             "Revit will report 'assembly does not exist' even though the file is present. " +
                             "AB.RevitMcp.Contracts must be built with a real net48 target.");
                        closureOk = false;
                    }

                    if (expectNetFramework && facts.IsNetCore)
                    {
                        Fail("    " + name + " is a .NET (Core) build but Revit " + version + " needs .NET Framework",
                             "Rebuild with -f net48 -p:RevitVersion=" + version + ".");
                        closureOk = false;
                    }
                    if (!expectNetFramework && facts.IsNetFramework)
                    {
                        Fail("    " + name + " is a .NET Framework build but Revit " + version + " needs .NET 8",
                             "Rebuild with -f net8.0-windows -p:RevitVersion=" + version + ".");
                        closureOk = false;
                    }
                }

                if (closureOk) Pass("    dependency closure is correct for " + (expectNetFramework ? "net48" : ".NET 8"));

                // ---- is this Revit release actually installed? ----
                string apiDll = Path.Combine(revitRoot, "Revit " + version, "RevitAPI.dll");
                if (!File.Exists(apiDll))
                    Warn("    Revit " + version + " is not installed here (manifest is harmless but unused)");

                Console.WriteLine();
            }

            if (found == 0)
                Fail("No AB.RevitMcp.addin manifest is installed for any Revit release.",
                     "Run build\\install.ps1.");
        }

        // ==================================================================
        //  MCP server
        // ==================================================================
        private static void CheckServer()
        {
            Header("MCP server");

            string exe = Path.Combine(IpcConstants.DataRoot, "Server", "AB.RevitMcp.Server.exe");
            if (!File.Exists(exe))
            {
                Fail("  server not found at " + exe, "Run build\\install.ps1.");
                return;
            }
            Pass("  server present: " + exe);

            // Launching with --print-tools is a safe, self-contained smoke test: it exercises the
            // whole contracts assembly and exits without opening a pipe or touching Revit.
            try
            {
                var psi = new ProcessStartInfo(exe, "--print-tools")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (Process process = Process.Start(psi))
                {
                    string stdout = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(20000);

                    if (process.HasExited && process.ExitCode == 0 && stdout.Contains("Revit MCP tool reference"))
                    {
                        int tools = 0;
                        foreach (string line in stdout.Split('\n'))
                            if (line.StartsWith("### `", StringComparison.Ordinal)) tools++;
                        Pass("  server runs and advertises " + tools + " tools");
                    }
                    else
                    {
                        Fail("  server exited with code " + (process.HasExited ? process.ExitCode.ToString() : "timeout"),
                             "The .NET 8 Desktop Runtime may be missing. Install it, or reinstall using the " +
                             "self-contained option so no runtime is required.");
                    }
                }
            }
            catch (Exception ex)
            {
                Fail("  server would not start: " + ex.Message,
                     "Install the .NET 8 Desktop Runtime, or reinstall with the self-contained option.");
            }

            Console.WriteLine();
        }

        // ==================================================================
        //  Live bridge
        // ==================================================================
        private static async Task CheckLiveBridgeAsync()
        {
            Header("Live Revit bridge");

            List<BridgeEndpoint> endpoints = EndpointRegistry.Discover(null);
            if (endpoints.Count == 0)
            {
                Console.WriteLine("  No Revit session is currently exposing the bridge.");
                Console.WriteLine("  That is expected if Revit is closed, or if you have not pressed Start Bridge yet.");
                Console.WriteLine();
                return;
            }

            foreach (BridgeEndpoint endpoint in endpoints)
            {
                Console.WriteLine("  Revit " + endpoint.RevitVersion + "  (pid " + endpoint.ProcessId + ")  " +
                                  (endpoint.DocumentTitle ?? "(no document)"));

                using (var client = new PipeClient())
                {
                    try
                    {
                        using (var cts = new CancellationTokenSource(5000))
                        {
                            await client.ConnectAsync(endpoint.PipeName, 3000, cts.Token).ConfigureAwait(false);

                            var request = new BridgeRequest
                            {
                                Id = Guid.NewGuid().ToString("N"),
                                Tool = IpcConstants.OpPing,
                                TimeoutMs = 5000,
                                ClientName = "doctor"
                            };

                            string reply = await client
                                .SendAsync(request.ToJson().ToJson(), 5000, cts.Token)
                                .ConfigureAwait(false);

                            JsonValue json;
                            if (JsonValue.TryParse(reply, out json) && json["ok"].AsBool(false))
                            {
                                _liveBridges++;
                                Pass("    ping ok - the bridge is live and answering");
                            }
                            else
                                Fail("    the bridge replied but not with a successful pong",
                                     "Stop and start the bridge from the AB Adv Tools ribbon tab.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Fail("    could not reach the pipe: " + ex.Message,
                             "Make sure Revit and this tool run as the same Windows user and at the " +
                             "same elevation level.");
                    }
                }
            }

            Console.WriteLine();
        }

        // ==================================================================
        //  output helpers
        // ==================================================================
        private static void Header(string title)
        {
            Console.WriteLine();
            Console.WriteLine("== " + title + " " + new string('=', Math.Max(0, 60 - title.Length)));
            Console.WriteLine();
        }

        private static void Pass(string message)
        {
            WriteColour(ConsoleColor.Green, "  OK   ");
            Console.WriteLine(message.TrimStart());
        }

        private static void Warn(string message)
        {
            _warnings++;
            WriteColour(ConsoleColor.Yellow, "  WARN ");
            Console.WriteLine(message.TrimStart());
        }

        private static void Fail(string message, string remedy)
        {
            _failures++;
            WriteColour(ConsoleColor.Red, "  FAIL ");
            Console.WriteLine(message.TrimStart());
            if (!string.IsNullOrEmpty(remedy) && !Remedies.Contains(remedy)) Remedies.Add(remedy);
        }

        private static void WriteColour(ConsoleColor colour, string text)
        {
            ConsoleColor previous = Console.ForegroundColor;
            try { Console.ForegroundColor = colour; Console.Write(text); }
            finally { Console.ForegroundColor = previous; }
        }
    }
}
