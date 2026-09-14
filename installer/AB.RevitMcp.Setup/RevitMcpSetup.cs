using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using ABAdvTools.Setup;

namespace AB.RevitMcp.Setup
{
    /// <summary>
    /// What the AB Revit MCP Bridge installs, and how its earlier releases are recognised.
    ///
    /// Everything is per user - no elevation, no Program Files, no services:
    ///   Revit add-in  %APPDATA%\Autodesk\Revit\Addins\&lt;year&gt;\AB.RevitMcp.addin + ABRevitMcp\
    ///   MCP server    %LOCALAPPDATA%\ABRevitMcp\Server\
    /// then the server is registered with the AI clients the user ticks.
    ///
    /// The add-in deliberately does not live in %LOCALAPPDATA%: endpoint protection commonly blocks
    /// DLL loads from there, and Revit then reports "the assembly does not exist" about a file that
    /// is plainly present.
    ///
    /// Earlier versions it recognises:
    ///   - a bridge written by AB.RevitMcp.Setup 1.x or build\install.ps1 (same locations)
    ///   - an install made by this installer, through its Apps and Features entry
    /// </summary>
    internal sealed class RevitMcpSetup : SetupProduct
    {
        private const string DataFolderName = "ABRevitMcp";
        private const string ServerProcessName = "AB.RevitMcp.Server";
        private static readonly string[] ServerFolders = { "Server", "Doctor", "bin", "endpoints" };

        private readonly RevitAddin _revit = new RevitAddin
        {
            ManifestFileName = "AB.RevitMcp.addin",
            FolderName = DataFolderName,
            AssemblyFileName = "AB.RevitMcp.Addin.dll",
            AddInName = "AB MCP AI Bridge",
            FullClassName = "AB.RevitMcp.Addin.App",
            // Revit keys the registration off this id. It must never change.
            AddInId = "7f3c9a12-5d84-4b1e-9c67-2a8e5f0d41b3",
            VendorId = "ABLOTFY",
            VendorDescription = "Abdullah Lotfy - https://www.linkedin.com/in/abdullahalqublawi/"
        };

        private readonly bool _skipClients;
        private CheckedListBox _agentList;
        private List<AgentTarget> _chosenAgents = new List<AgentTarget>();

        public RevitMcpSetup(string[] args)
        {
            _skipClients = args != null && Array.Exists(args, delegate (string a)
            {
                string flag = a.TrimStart('/', '-').ToLowerInvariant();
                return flag == "noclients" || flag == "no-clients" || flag == "noagents";
            });
        }

        public override string Id { get { return "RevitMcp"; } }
        public override string Name { get { return "AB Revit MCP Bridge"; } }
        public override string GitHubRepository { get { return "AB.RevitMcp"; } }
        public override InstallScope DefaultScope { get { return InstallScope.CurrentUser; } }
        public override bool ScopeSelectable { get { return false; } }
        public override string[] BlockingProcesses { get { return new[] { AutodeskLocator.RevitProcess }; } }

        /// <summary>1.x let you carry on with Revit open and skipped only the releases it had locked.</summary>
        public override bool AllowInstallWhileHostRunning { get { return true; } }

        /// <summary>1.x wrote %TEMP%\ABRevitMcp-Setup-*.log; deployment scripts may look for it.</summary>
        public override string LogFilePrefix { get { return "ABRevitMcp"; } }

        public override string Description
        {
            get
            {
                return "Lets any MCP-compatible AI client - Claude, Cursor, VS Code, a local model - safely query " +
                       "and edit the open Revit model. Installs the Revit add-in and the MCP server for you.";
            }
        }

        public override string HostSummary
        {
            get { return "Autodesk Revit " + Payload.YearRange("Revit"); }
        }

        public override string NextSteps
        {
            get
            {
                return "Start Revit, open the AB Adv Tools tab and press Start Bridge on the MCP Bridge panel, " +
                       "then restart your AI client so it picks up the server.";
            }
        }

        public override string ElevationReason(InstallScope scope, IList<HostTarget> targets)
        {
            return null;   // per user, always
        }

        private static string InstallRoot(SetupContext ctx)
        {
            return Path.Combine(ctx.LocalAppData, DataFolderName);
        }

        private static string ServerExe(SetupContext ctx)
        {
            return Path.Combine(InstallRoot(ctx), "Server", ServerProcessName + ".exe");
        }

        // ------------------------------------------------------------ targets

        public override List<HostTarget> FindTargets(SetupContext ctx)
        {
            List<HostTarget> targets = AutodeskLocator.FindRevit(2019, 2035);
            foreach (HostTarget target in targets)
            {
                target.PayloadAvailable = Payload.Has(RevitAddin.PayloadName(target.Year));
                if (!target.PayloadAvailable) target.Note = "this installer has no build for " + target.Year;
            }
            return targets;
        }

        // ------------------------------------------------------------ earlier versions

        public override List<ExistingInstall> FindExistingInstalls(SetupContext ctx)
        {
            var found = new List<ExistingInstall>();

            ExistingInstall registered = RegisteredInstallFor(ctx, InstallScope.CurrentUser);
            if (registered != null)
            {
                registered.Locations.Add(Path.Combine(InstallRoot(ctx), "Server"));
                found.Add(registered);
            }
            else
            {
                List<int> years = _revit.InstalledYears(ctx, InstallScope.CurrentUser);
                bool server = File.Exists(ServerExe(ctx));
                if (years.Count > 0 || server)
                {
                    string version = _revit.InstalledVersion(ctx, InstallScope.CurrentUser) ??
                                     FileOps.VersionOf(ServerExe(ctx));

                    var earlier = new ExistingInstall
                    {
                        Key = "files-currentuser",
                        ProductName = Name,
                        Version = version,
                        InstalledBy = "AB.RevitMcp.Setup 1.x or install.ps1",
                        Scope = InstallScope.CurrentUser,
                        NeedsElevation = false,
                        Remove = delegate (SetupContext c) { RemoveFiles(c, InstallScope.CurrentUser); }
                    };
                    foreach (int year in years)
                        earlier.Locations.Add(_revit.BinaryFolder(ctx, InstallScope.CurrentUser, year));
                    if (server) earlier.Locations.Add(Path.GetDirectoryName(ServerExe(ctx)));
                    found.Add(earlier);
                }
            }

            // Never installed that way by any release, but a hand copy would load twice.
            List<int> machineYears = _revit.InstalledYears(ctx, InstallScope.AllUsers);
            if (machineYears.Count > 0)
            {
                var machine = new ExistingInstall
                {
                    Key = "files-allusers",
                    ProductName = Name,
                    Version = _revit.InstalledVersion(ctx, InstallScope.AllUsers),
                    InstalledBy = "Copied files",
                    Scope = InstallScope.AllUsers,
                    NeedsElevation = true,
                    Remove = delegate (SetupContext c) { _revit.Remove(c, InstallScope.AllUsers); }
                };
                foreach (int year in machineYears)
                    machine.Locations.Add(_revit.BinaryFolder(ctx, InstallScope.AllUsers, year));
                found.Add(machine);
            }

            return found;
        }

        // ------------------------------------------------------------ install / remove

        public override void Install(SetupContext ctx, InstallScope scope, IList<HostTarget> targets)
        {
            StopRunningServers(ctx);

            string serverFolder = Path.GetDirectoryName(ServerExe(ctx));
            string locked = FileOps.FirstLockedFile(serverFolder);
            if (locked != null)
                throw new InvalidOperationException("'" + locked + "' is in use. Close your AI client and run setup again.");

            int files = Payload.Extract("Server", serverFolder, true);
            ctx.Log("   MCP server  ->  " + serverFolder + "  (" + files + " files)");

            // A Revit release that is open keeps its add-in locked: skip it, install the others, and
            // say so - exactly what the 1.x installer did.
            int installed = 0;
            foreach (HostTarget target in targets)
            {
                if (_revit.Install(ctx, InstallScope.CurrentUser, target.Year, true)) installed++;
            }

            if (installed == 0)
                throw new InvalidOperationException("No Revit release was installed. Close Revit and run setup again.");
        }

        public override void RemoveFiles(SetupContext ctx, InstallScope scope)
        {
            if (scope == InstallScope.AllUsers)
            {
                _revit.Remove(ctx, InstallScope.AllUsers, true);
                return;
            }

            StopRunningServers(ctx);
            _revit.Remove(ctx, InstallScope.CurrentUser, true);

            // Program folders only: logs and settings under the same root are kept.
            foreach (string folder in ServerFolders)
                FileOps.RemoveDirectory(ctx, Path.Combine(InstallRoot(ctx), folder));
        }

        public override void AfterInstall(SetupContext ctx, InstallPlan plan)
        {
            if (_chosenAgents.Count == 0) return;

            if (ctx.IsSandbox)
            {
                ctx.Log("   SANDBOX: skipped configuring " + _chosenAgents.Count + " AI client(s) - their config files are real.");
                return;
            }

            ctx.Log("Configuring AI clients...");
            new AgentConfigurator(delegate (string line) { ctx.Log("   " + line); })
                .Configure(_chosenAgents, ServerExe(ctx));
        }

        /// <summary>
        /// The MCP server is a child process of whatever AI client is open, so during an upgrade it
        /// is very often running and holding its own files. Stopping it is safe: it is stateless and
        /// the client relaunches it on the next tool call.
        /// </summary>
        private static void StopRunningServers(SetupContext ctx)
        {
            if (ctx.IsSandbox) return;   // never touch a real process from a test run

            Process[] running;
            try { running = Process.GetProcessesByName(ServerProcessName); }
            catch (Exception) { return; }
            if (running.Length == 0) return;

            ctx.Log("   stopping " + running.Length + " running MCP server process(es); your AI client relaunches it on demand");
            foreach (Process process in running)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    ctx.Log("   could not stop PID " + process.Id + ": " + ex.Message);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        // ------------------------------------------------------------ AI client options

        public override Control CreateOptionsControl(int width)
        {
            // Built at its final size, so nothing inside moves when it is placed on the page.
            var panel = new Panel { Size = new Size(width, 156) };

            var label = new Label
            {
                Text = "AI agents to configure automatically (existing configs are backed up):",
                AutoSize = true,
                Location = new Point(0, 6)
            };

            var addCustom = new Button
            {
                Text = "Add custom agent...",
                Size = new Size(170, 26),
                Location = new Point(width - 172, 0)
            };

            _agentList = new CheckedListBox
            {
                Location = new Point(2, 32),
                Size = new Size(width - 4, 122),
                CheckOnClick = true,
                IntegralHeight = false,
                BorderStyle = BorderStyle.FixedSingle
            };

            // Every client we know how to configure is listed; the ones actually present are ticked,
            // so it is obvious what is supported rather than silently hiding options.
            foreach (AgentTarget agent in AgentConfigurator.KnownAgents())
                _agentList.Items.Add(agent, agent.Detected && !_skipClients);

            addCustom.Click += delegate { AddCustomAgent(panel.FindForm()); };

            panel.Controls.AddRange(new Control[] { label, addCustom, _agentList });
            return panel;
        }

        public override void CaptureOptions()
        {
            _chosenAgents = new List<AgentTarget>();

            if (_agentList != null)
            {
                foreach (object item in _agentList.CheckedItems)
                {
                    var agent = item as AgentTarget;
                    if (agent != null) _chosenAgents.Add(agent);
                }
                return;
            }

            // Silent: configure every client actually present, unless /noclients.
            if (_skipClients) return;
            foreach (AgentTarget agent in AgentConfigurator.KnownAgents())
                if (agent.Detected) _chosenAgents.Add(agent);
        }

        /// <summary>
        /// Lets the user point at any MCP config file this installer does not know. The set of MCP
        /// clients changes constantly, and guessing paths would write files to the wrong place.
        /// </summary>
        private void AddCustomAgent(IWin32Window owner)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Select the MCP configuration file for your agent";
                dialog.Filter = "MCP config (*.json;*.yaml;*.yml)|*.json;*.yaml;*.yml|All files (*.*)|*.*";
                dialog.CheckFileExists = false;   // the file may not exist yet
                if (dialog.ShowDialog(owner) != DialogResult.OK) return;

                string path = dialog.FileName;
                bool yaml = path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                            path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

                string rootKey = "mcpServers";
                if (!yaml)
                {
                    DialogResult answer = MessageBox.Show(owner,
                        "Which key does this client use for its server list?" + Environment.NewLine + Environment.NewLine +
                        "Yes  =  \"mcpServers\"   (Claude, Cursor, Windsurf, Cline, LM Studio, most clients)" + Environment.NewLine +
                        "No   =  \"servers\"      (VS Code, Visual Studio)",
                        Name, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

                    if (answer == DialogResult.Cancel) return;
                    if (answer == DialogResult.No) rootKey = "servers";

                    // Some clients nest the map (OpenClaw uses mcp.servers). Writing a flat key into a
                    // nested config is silently ignored, which looks exactly like "it does not work".
                    string typed = Microsoft.VisualBasic.Interaction.InputBox(
                        "Key holding the server map." + Environment.NewLine +
                        "Use a dotted path if the client nests it, e.g. mcp.servers",
                        Name, rootKey);
                    if (!string.IsNullOrEmpty(typed)) rootKey = typed.Trim();
                }

                _agentList.Items.Add(AgentConfigurator.Custom(path, rootKey, yaml), true);
            }
        }

        // ------------------------------------------------------------ after install

        public override IList<SetupAction> FinishActions
        {
            get { return new[] { new SetupAction("Verify installation", Verify) }; }
        }

        /// <summary>1.x had Verify beside Install at all times: check an existing install without reinstalling.</summary>
        public override IList<SetupAction> ToolActions
        {
            get { return new[] { new SetupAction("Verify", Verify) }; }
        }

        public override void AfterUninstall(SetupContext ctx)
        {
            ctx.Log("Done. Remove the \"revit\" entry from your AI client config if you no longer want it.");
        }

        /// <summary>Runs the server's own doctor, which checks the whole chain end to end.</summary>
        private static void Verify(SetupContext ctx)
        {
            string exe = ServerExe(ctx);
            if (!File.Exists(exe))
            {
                ctx.Log("The MCP server is not installed.");
                return;
            }

            ctx.Log(string.Empty);
            ctx.Log("=== Verifying ===");
            try
            {
                var info = new ProcessStartInfo(exe, "--doctor --no-ping")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process process = Process.Start(info))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(30000);
                    foreach (string line in output.Split('\n')) ctx.Log(line.TrimEnd('\r'));
                }
            }
            catch (Exception ex)
            {
                ctx.Log("Could not run the verification: " + ex.Message);
            }
        }
    }
}
