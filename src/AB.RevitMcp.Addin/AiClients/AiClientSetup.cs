using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Threading;
using AB.RevitMcp.Addin.Bridge;
using AB.RevitMcp.Addin.Ui;
using AB.RevitMcp.Contracts.Protocol;
using Microsoft.Win32;

namespace AB.RevitMcp.Addin.AiClients
{
    /// <summary>
    /// Applies the AI-client choice made in the installer, the first time Revit starts after it.
    ///
    /// The .msi cannot configure AI clients itself: that needs code, and a package that runs code
    /// is what company PCs with Defender's attack surface reduction rules block. So its "AI clients"
    /// page only records the ticks, in
    ///     HKCU\Software\AB Adv Tools\Installer\RevitMcp\AiClients
    ///         Token       new on every install or repair
    ///         Clients     ticked ids, "claude-desktop;cursor;"
    ///         CustomPath  a custom client's config file, or empty
    ///         CustomKey   the key holding its server list
    /// (see installer\RevitMcp.AiClients.wxs), and this applies them once per token. Revit itself
    /// is trusted by those PCs, so running the configurator from here is not blocked.
    /// </summary>
    internal static class AiClientSetup
    {
        private const string ChoiceKey = @"Software\AB Adv Tools\Installer\RevitMcp\AiClients";

        /// <summary>The last token applied, beside the bridge's other settings.</summary>
        private static string AppliedTokenFile
        {
            get { return Path.Combine(IpcConstants.DataRoot, "ai-clients-applied.txt"); }
        }

        internal sealed class Choice
        {
            public string Token;
            public List<AgentTarget> Agents = new List<AgentTarget>();
        }

        /// <summary>Where the installed MCP server lives, whatever the layout.</summary>
        public static string ServerPath
        {
            get { return RibbonBuilder.ExpectedServerPath(); }
        }

        /// <summary>The installer's choice, if there is one not yet applied. Never throws.</summary>
        public static Choice ReadPending()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ChoiceKey))
                {
                    if (key == null) return null;

                    string token = key.GetValue("Token") as string;
                    if (string.IsNullOrEmpty(token)) return null;

                    string applied = File.Exists(AppliedTokenFile) ? File.ReadAllText(AppliedTokenFile).Trim() : string.Empty;
                    if (string.Equals(applied, token.Trim(), StringComparison.Ordinal)) return null;

                    var choice = new Choice { Token = token.Trim() };

                    var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string id in ((key.GetValue("Clients") as string) ?? string.Empty).Split(';'))
                        if (id.Trim().Length > 0) ids.Add(id.Trim());

                    foreach (AgentTarget agent in AgentConfigurator.KnownAgents())
                        if (ids.Contains(agent.Id)) choice.Agents.Add(agent);

                    string customPath = ((key.GetValue("CustomPath") as string) ?? string.Empty).Trim().Trim('"');
                    if (customPath.Length > 0)
                    {
                        string customKey = ((key.GetValue("CustomKey") as string) ?? string.Empty).Trim();
                        choice.Agents.Add(AgentConfigurator.Custom(customPath, customKey, AgentConfigurator.IsYamlPath(customPath)));
                    }

                    return choice;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Called once Revit is up. Configures on a worker thread - the launch probe starts a
        /// process and can take seconds - then shows what happened on Revit's UI thread.
        /// </summary>
        public static void ApplyPendingInBackground(Dispatcher uiDispatcher)
        {
            if (ReadPending() == null) return;

            ThreadPool.QueueUserWorkItem(delegate
            {
                var lines = new List<string>();
                Choice choice = null;

                // Two Revit releases starting together must not both rewrite the same config files.
                using (var mutex = new Mutex(false, @"Local\ABRevitMcp.AiClients"))
                {
                    bool owned;
                    try { owned = mutex.WaitOne(TimeSpan.FromMinutes(2)); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (!owned) return;

                    try
                    {
                        choice = ReadPending();       // the other Revit may have applied it meanwhile
                        if (choice == null) return;

                        if (choice.Agents.Count > 0)
                        {
                            string server = ServerPath;
                            if (string.IsNullOrEmpty(server) || !File.Exists(server))
                            {
                                lines.Add("The MCP server was not found (expected at " + server + "). Repair AB Revit MCP Bridge in Apps and Features.");
                            }
                            else
                            {
                                lines.Add("Server: " + server);
                                new AgentConfigurator(lines.Add).Configure(choice.Agents, server);
                            }
                        }

                        MarkApplied(choice.Token);
                    }
                    catch (Exception ex)
                    {
                        lines.Add("Configuring AI clients failed: " + ex.Message);
                    }
                    finally
                    {
                        mutex.ReleaseMutex();
                    }
                }

                BridgeService service = BridgeService.Current;
                if (service != null)
                {
                    foreach (string line in lines) service.Log.Info("AI clients (installer choice): " + line);
                }

                if (choice == null || choice.Agents.Count == 0 || uiDispatcher == null) return;

                try
                {
                    uiDispatcher.BeginInvoke(new Action(delegate
                    {
                        AiClientsForm.ShowResults(
                            "Revit configured the AI clients you chose when installing. Restart those clients so they pick up the Revit MCP server.",
                            lines);
                    }));
                }
                catch (Exception) { }
            });
        }

        private static void MarkApplied(string token)
        {
            try
            {
                Directory.CreateDirectory(IpcConstants.DataRoot);
                File.WriteAllText(AppliedTokenFile, token);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Runs the server's own doctor, which checks the whole chain end to end - the check
        /// Setup.exe's Verify button ran. Started the way AI clients start it, so a Defender block
        /// on the executable shows up here too. Blocking: call it from a worker thread.
        /// </summary>
        public static void Verify(Action<string> log)
        {
            string exe = ServerPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                log("The MCP server is not installed (expected at " + exe + ").");
                return;
            }

            LaunchSpec spec = LaunchSpec.For(exe, log);

            // With the bridge running, the doctor can ping it as well.
            BridgeService service = BridgeService.Current;
            string doctorArgs = service != null && service.IsRunning ? "--doctor" : "--doctor --no-ping";

            string arguments = string.Empty;
            foreach (string a in spec.Args) arguments += "\"" + a + "\" ";
            arguments += doctorArgs;

            log(string.Empty);
            log("=== Verifying: " + spec.CommandLine() + " " + doctorArgs);
            try
            {
                var info = new ProcessStartInfo(spec.Command, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process process = Process.Start(info))
                {
                    // Both pipes drained at once, so a chatty stderr cannot fill and stall the child.
                    var errorsRead = process.StandardError.ReadToEndAsync();
                    string output = process.StandardOutput.ReadToEnd();
                    string errors = errorsRead.Result;
                    process.WaitForExit(30000);
                    foreach (string line in output.Split('\n')) log(line.TrimEnd('\r'));
                    foreach (string line in errors.Split('\n')) if (line.Trim().Length > 0) log(line.TrimEnd('\r'));
                }
            }
            catch (Exception ex)
            {
                log("Could not run the verification: " + ex.Message);
            }
        }
    }
}
