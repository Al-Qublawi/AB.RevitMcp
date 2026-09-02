using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using AB.RevitMcp.Contracts.Json;

namespace AB.RevitMcp.Setup
{
    /// <summary>Author and product identity, mirrored from the add-in.</summary>
    public static class Branding
    {
        public const string Author = "Abdullah Lotfy";
        public const string LinkedInUrl = "https://www.linkedin.com/in/abdullahalqublawi/";
        public const string LinkedInCaption = "Abdullah Lotfy - LinkedIn";
        public const string ProductName = "AB Revit MCP Bridge";
        public const string Version = "1.1.0";
    }

    /// <summary>One installable Revit release found on this machine.</summary>
    public sealed class RevitTarget
    {
        public int Version;
        public string InstallFolder;
        public bool PayloadAvailable;

        public override string ToString()
        {
            return PayloadAvailable
                ? "Revit " + Version
                : "Revit " + Version + "  (not supported by this build)";
        }
    }

    /// <summary>
    /// Does the actual work: unpacks the embedded payload, writes .addin manifests and optionally
    /// registers the MCP server with the AI clients present on the machine.
    ///
    /// Everything is per-user. No elevation, no Program Files, no registry, no services.
    /// </summary>
    public sealed class BundleInstaller
    {
        private const string PayloadPrefix = "AB.RevitMcp.Setup.Payload.";
        private const string ServerPayload = PayloadPrefix + "Server.zip";
        private const string AddInFileName = "AB.RevitMcp.addin";
        private const string AddInFolderName = "ABRevitMcp";

        private readonly Action<string> _log;

        public BundleInstaller(Action<string> log)
        {
            _log = log ?? delegate { };
        }

        public static string InstallRoot
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ABRevitMcp"); }
        }

        public static string ServerFolder { get { return Path.Combine(InstallRoot, "Server"); } }
        public static string ServerExe { get { return Path.Combine(ServerFolder, "AB.RevitMcp.Server.exe"); } }

        public static string AddInsRoot
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Autodesk", "Revit", "Addins");
            }
        }

        // ==================================================================
        //  discovery
        // ==================================================================

        /// <summary>Revit releases installed here, flagged with whether this build ships for them.</summary>
        public List<RevitTarget> DiscoverRevit(string programRoot)
        {
            var found = new List<RevitTarget>();
            if (string.IsNullOrEmpty(programRoot)) programRoot = @"C:\Program Files\Autodesk";
            if (!Directory.Exists(programRoot)) return found;

            HashSet<int> payloads = AvailablePayloadVersions();

            foreach (string dir in Directory.GetDirectories(programRoot, "Revit *"))
            {
                string leaf = Path.GetFileName(dir);
                string yearText = leaf.Substring("Revit ".Length).Trim();
                int year;
                if (!int.TryParse(yearText, out year)) continue;

                // "Revit Content 2020" and similar are not Revit itself.
                if (!File.Exists(Path.Combine(dir, "RevitAPI.dll"))) continue;

                found.Add(new RevitTarget
                {
                    Version = year,
                    InstallFolder = dir,
                    PayloadAvailable = payloads.Contains(year)
                });
            }

            found.Sort(delegate (RevitTarget a, RevitTarget b) { return a.Version.CompareTo(b.Version); });
            return found;
        }

        private HashSet<int> AvailablePayloadVersions()
        {
            var versions = new HashSet<int>();
            foreach (string name in Assembly.GetExecutingAssembly().GetManifestResourceNames())
            {
                if (!name.StartsWith(PayloadPrefix, StringComparison.Ordinal)) continue;
                if (name == ServerPayload) continue;

                string middle = name.Substring(PayloadPrefix.Length);           // "Revit2024.zip"
                if (middle.EndsWith(".zip", StringComparison.Ordinal))
                    middle = middle.Substring(0, middle.Length - 4);
                if (middle.StartsWith("Revit", StringComparison.Ordinal))
                    middle = middle.Substring("Revit".Length);

                int year;
                if (int.TryParse(middle, out year)) versions.Add(year);
            }
            return versions;
        }

        public bool HasServerPayload()
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceNames()
                .Any(n => n == ServerPayload);
        }

        // ==================================================================
        //  install
        // ==================================================================

        public bool Install(IEnumerable<RevitTarget> targets, IEnumerable<AgentTarget> agents)
        {
            bool ok = true;

            if (!StopRunningServers()) ok = false;

            // ---- server ----
            if (HasServerPayload())
            {
                try
                {
                    _log("Installing the MCP server...");
                    ExtractPayload(ServerPayload, ServerFolder, true);
                    _log("   " + ServerFolder);
                }
                catch (Exception ex)
                {
                    _log("   FAILED: " + ex.Message);
                    ok = false;
                }
            }
            else
            {
                _log("WARNING: this installer carries no MCP server payload.");
                ok = false;
            }

            // ---- add-in, one folder + manifest per Revit release ----
            int installed = 0;
            foreach (RevitTarget target in targets)
            {
                if (!target.PayloadAvailable) continue;

                string addInDir = Path.Combine(AddInsRoot, target.Version.ToString());
                string assemblyDir = Path.Combine(addInDir, AddInFolderName);

                try
                {
                    _log("Installing for Revit " + target.Version + "...");

                    // Check writability BEFORE touching anything. Revit locks the add-in DLL while
                    // it is open, and a half-completed extraction would leave a broken install
                    // behind - worse than not installing at all.
                    string blocker = FirstLockedFile(assemblyDir);
                    if (blocker != null)
                    {
                        _log("   SKIPPED: '" + Path.GetFileName(blocker) + "' is locked, so Revit " +
                             target.Version + " is still running.");
                        _log("   Close it and run this installer again. Nothing was changed.");
                        ok = false;
                        continue;
                    }

                    // Overwrite in place rather than deleting the folder first: if anything does go
                    // wrong the previous install survives intact.
                    ExtractPayload(PayloadPrefix + "Revit" + target.Version + ".zip", assemblyDir, false);
                    WriteManifest(addInDir);
                    _log("   " + assemblyDir);
                    installed++;
                }
                catch (IOException ex)
                {
                    _log("   FAILED: " + ex.Message);
                    _log("   Close Revit " + target.Version + " and run this installer again.");
                    ok = false;
                }
                catch (Exception ex)
                {
                    _log("   FAILED: " + ex.Message);
                    ok = false;
                }
            }

            if (installed == 0)
            {
                _log("No Revit release was installed.");
                ok = false;
            }

            if (agents != null)
            {
                var list = new List<AgentTarget>(agents);
                if (list.Count > 0)
                {
                    _log("Configuring AI clients...");
                    new AgentConfigurator(delegate (string line) { _log("   " + line); })
                        .Configure(list, ServerExe);
                }
            }

            return ok;
        }

        /// <summary>
        /// The MCP server is a child process of whatever AI client is open, so it is very often
        /// running during an upgrade and holding its own files. Stopping it is safe - it is
        /// stateless and the client relaunches it on the next tool call.
        /// </summary>
        private bool StopRunningServers()
        {
            Process[] running;
            try { running = Process.GetProcessesByName("AB.RevitMcp.Server"); }
            catch (Exception) { return true; }

            if (running.Length == 0) return true;

            _log("Stopping " + running.Length + " running MCP server process(es)...");
            bool ok = true;
            foreach (Process process in running)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    _log("   could not stop PID " + process.Id + ": " + ex.Message);
                    ok = false;
                }
                finally
                {
                    try { process.Dispose(); } catch (Exception) { }
                }
            }
            _log("   your AI client will relaunch it on the next Revit tool call");
            return ok;
        }

        /// <summary>
        /// Returns the first existing file in the folder that cannot be opened for writing, or null
        /// if everything is writable. Used to refuse an install rather than start one that will
        /// fail half way.
        /// </summary>
        private static string FirstLockedFile(string folder)
        {
            if (!Directory.Exists(folder)) return null;

            foreach (string file in Directory.GetFiles(folder))
            {
                try
                {
                    using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                    }
                }
                catch (IOException)
                {
                    return file;
                }
                catch (UnauthorizedAccessException)
                {
                    return file;
                }
            }
            return null;
        }

        private static void ExtractPayload(string resourceName, string targetFolder, bool clean)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null) throw new FileNotFoundException("Payload '" + resourceName + "' is missing from this installer.");

                if (clean && Directory.Exists(targetFolder))
                {
                    try { Directory.Delete(targetFolder, true); }
                    catch (IOException) { /* fall through - individual writes will report the lock */ }
                }
                Directory.CreateDirectory(targetFolder);

                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;   // directory entry

                        string destination = Path.GetFullPath(Path.Combine(targetFolder, entry.FullName));

                        // Zip-slip guard: never let an entry escape the target folder.
                        string root = Path.GetFullPath(targetFolder);
                        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Payload entry '" + entry.FullName + "' escapes the target folder.");

                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        entry.ExtractToFile(destination, true);
                    }
                }
            }
        }

        private static void WriteManifest(string addInDir)
        {
            Directory.CreateDirectory(addInDir);

            // A RELATIVE assembly path, resolved by Revit against the manifest folder. The add-in
            // deliberately does not live in %LOCALAPPDATA%: endpoint protection commonly blocks DLL
            // loads from there, and Revit then reports "the assembly does not exist" about a file
            // that is plainly present.
            string manifest =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + Environment.NewLine +
                "<RevitAddIns>" + Environment.NewLine +
                "  <AddIn Type=\"Application\">" + Environment.NewLine +
                "    <Name>AB MCP AI Bridge</Name>" + Environment.NewLine +
                "    <Assembly>" + AddInFolderName + "\\AB.RevitMcp.Addin.dll</Assembly>" + Environment.NewLine +
                "    <AddInId>7f3c9a12-5d84-4b1e-9c67-2a8e5f0d41b3</AddInId>" + Environment.NewLine +
                "    <FullClassName>AB.RevitMcp.Addin.App</FullClassName>" + Environment.NewLine +
                "    <VendorId>ABLOTFY</VendorId>" + Environment.NewLine +
                "    <VendorDescription>" + Branding.Author + " - " + Branding.LinkedInUrl +
                "</VendorDescription>" + Environment.NewLine +
                "  </AddIn>" + Environment.NewLine +
                "</RevitAddIns>" + Environment.NewLine;

            // UTF-8 without a BOM, matching how every mainstream add-in ships its manifest.
            File.WriteAllText(Path.Combine(addInDir, AddInFileName), manifest, new UTF8Encoding(false));
        }

        // ==================================================================
        //  verify / uninstall
        // ==================================================================

        public string RunDoctor()
        {
            if (!File.Exists(ServerExe)) return "The MCP server is not installed.";

            try
            {
                var psi = new ProcessStartInfo(ServerExe, "--doctor --no-ping")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(30000);
                    return output;
                }
            }
            catch (Exception ex)
            {
                return "Could not run the verification: " + ex.Message;
            }
        }

        public void Uninstall()
        {
            StopRunningServers();

            if (Directory.Exists(AddInsRoot))
            {
                foreach (string versionDir in Directory.GetDirectories(AddInsRoot))
                {
                    string manifest = Path.Combine(versionDir, AddInFileName);
                    if (File.Exists(manifest))
                    {
                        try { File.Delete(manifest); _log("Removed manifest for Revit " + Path.GetFileName(versionDir)); }
                        catch (Exception ex) { _log("Could not remove " + manifest + ": " + ex.Message); }
                    }

                    string assemblies = Path.Combine(versionDir, AddInFolderName);
                    if (Directory.Exists(assemblies))
                    {
                        try { Directory.Delete(assemblies, true); }
                        catch (Exception ex) { _log("Could not remove " + assemblies + ": " + ex.Message); }
                    }
                }
            }

            foreach (string folder in new[] { "Server", "Doctor", "bin", "endpoints" })
            {
                string path = Path.Combine(InstallRoot, folder);
                if (!Directory.Exists(path)) continue;
                try { Directory.Delete(path, true); _log("Removed " + path); }
                catch (Exception ex) { _log("Could not remove " + path + ": " + ex.Message); }
            }

            _log("Done. Remove the \"revit\" entry from your AI client config if you no longer want it.");
        }
    }
}
