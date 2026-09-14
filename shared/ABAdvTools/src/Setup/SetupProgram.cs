// AB Adv Tools shared kit - installer engine. See SetupProduct.cs for the overview.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ABAdvTools.Setup
{
    /// <summary>
    /// Entry point shared by every AB setup executable:
    ///
    ///     [STAThread]
    ///     static int Main(string[] args) { return SetupProgram.Run(new MyProduct(), args); }
    /// </summary>
    internal static class SetupProgram
    {
        public const int ExitOk = 0;
        public const int ExitFailed = 1;
        public const int ExitHostRunning = 2;
        public const int ExitNothingToInstall = 3;
        public const int ExitElevationRequired = 740;   // ERROR_ELEVATION_REQUIRED

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        public static int Run(SetupProduct product, string[] rawArgs)
        {
            SetupArguments args = SetupArguments.Parse(rawArgs);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (!string.IsNullOrEmpty(args.RenderFolder)) return SetupForm.RenderPages(product, args);

            // An add-in whose earlier installer removed silently on a bare /uninstall keeps doing so.
            bool legacySilentUninstall = args.Uninstall && !args.Interactive && product.UninstallSwitchIsSilent;

            if (args.Silent || args.Scan || legacySilentUninstall) return RunHeadless(product, args);

            Application.Run(new SetupForm(product, args));
            return ExitOk;
        }

        // ------------------------------------------------------------ headless

        private static int RunHeadless(SetupProduct product, SetupArguments args)
        {
            // A WinExe has no console, so unattended output would vanish. Attach to the caller's
            // console when there is one, and always keep a log file.
            bool console = false;
            try { console = AttachConsole(-1); } catch { }

            string logFile = !string.IsNullOrEmpty(args.LogFile)
                ? args.LogFile
                : Path.Combine(Path.GetTempPath(),
                    product.LogFilePrefix + "-Setup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");

            var lines = new List<string>();
            Action<string> log = delegate (string line)
            {
                lines.Add(line);
                if (console) Console.WriteLine(line);
            };

            int exit;
            try
            {
                var ctx = new SetupContext(log, args.Sandbox, args.UserAppData, args.UserLocalAppData);
                exit = args.Scan ? Scan(ctx, product)
                     : args.Uninstall ? SilentUninstall(ctx, product, args)
                     : SilentInstall(ctx, product, args);
            }
            catch (Exception ex)
            {
                log("FAILED: " + ex);
                exit = ExitFailed;
            }

            log("Exit code " + exit.ToString(CultureInfo.InvariantCulture));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logFile)));
                File.WriteAllLines(logFile, lines.ToArray(), Encoding.UTF8);
                if (console) Console.WriteLine("Log: " + logFile);
            }
            catch { }

            // Double-clicked or run from a shortcut with no console, a failure would otherwise pass
            // without a trace. Earlier AB installers said so in a message box; keep doing that.
            if (exit != ExitOk && !console && !args.Silent && !args.Scan)
            {
                int from = Math.Max(0, lines.Count - 12);
                MessageBox.Show(string.Join(Environment.NewLine, lines.GetRange(from, lines.Count - from).ToArray()) +
                                Environment.NewLine + Environment.NewLine + "Log: " + logFile,
                                product.Name + " Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return exit;
        }

        private static int Scan(SetupContext ctx, SetupProduct product)
        {
            ctx.Log(product.Name + " " + product.Version + " setup - scan only, nothing is changed.");
            ctx.Log(string.Empty);

            ctx.Log("Releases on this computer:");
            List<HostTarget> targets = product.FindTargets(ctx);
            if (targets.Count == 0) ctx.Log("  (none)");
            foreach (HostTarget target in targets)
                ctx.Log("  " + (target.PayloadAvailable ? "[supported]   " : "[no build]    ") + target.DisplayName +
                        "  " + target.InstallDirectory);

            ctx.Log(string.Empty);
            ctx.Log("Copies of " + product.Name + " already installed:");
            List<ExistingInstall> existing = product.FindExistingInstalls(ctx);
            if (existing.Count == 0) ctx.Log("  (none)");
            foreach (ExistingInstall install in existing)
            {
                ctx.Log("  " + install.Title + "  |  " + install.InstalledBy + "  |  " + install.ScopeText +
                        (install.NeedsElevation ? "  |  removal needs administrator" : string.Empty) +
                        "  |  key " + install.Key);
                foreach (string location in install.Locations) ctx.Log("      " + location);
            }

            string running = SetupEngine.RunningHost(ctx, product);
            ctx.Log(string.Empty);
            ctx.Log(running == null ? "No blocking application is running." : running + " is running.");
            return ExitOk;
        }

        private static int SilentInstall(SetupContext ctx, SetupProduct product, SetupArguments args)
        {
            string running = SetupEngine.RunningHost(ctx, product);
            if (running != null)
            {
                ctx.Log(running + " is running. Close it and run setup again.");
                return ExitHostRunning;
            }

            var plan = new InstallPlan { Scope = args.Scope ?? product.DefaultScope };
            if (!product.ScopeSelectable) plan.Scope = product.DefaultScope;

            foreach (HostTarget target in product.FindTargets(ctx))
            {
                if (!target.PayloadAvailable) continue;
                if (args.Targets != null && !Contains(args.Targets, target.Key)) continue;
                plan.Targets.Add(target);
            }

            if (plan.Targets.Count == 0)
            {
                ctx.Log("No supported release was found (or none matched /targets).");
                return ExitNothingToInstall;
            }

            if (!args.KeepOld)
            {
                foreach (ExistingInstall old in product.FindExistingInstalls(ctx))
                {
                    if (args.Remove != null && !Contains(args.Remove, old.Key)) continue;
                    plan.Remove.Add(old);
                }
            }

            string reason = SetupEngine.ElevationReason(ctx, product, plan);
            if (reason != null)
            {
                ctx.Log("Administrator rights are needed to " + reason + ". Run setup from an elevated prompt.");
                return ExitElevationRequired;
            }

            product.CaptureOptions();
            return SetupEngine.Install(ctx, product, plan) ? ExitOk : ExitFailed;
        }

        private static int SilentUninstall(SetupContext ctx, SetupProduct product, SetupArguments args)
        {
            string running = SetupEngine.RunningHost(ctx, product);
            if (running != null)
            {
                ctx.Log(running + " is running. Close it and run setup again.");
                return ExitHostRunning;
            }

            // A scope on the command line narrows removal to it. So does a bare /uninstall for an add-in
            // whose earlier installer removed only the current user's copy unless told /allusers.
            InstallScope? scope = args.Scope;
            if (scope == null && !args.Silent && product.UninstallSwitchIsSilent) scope = product.DefaultScope;

            var installs = new List<ExistingInstall>();
            foreach (ExistingInstall install in product.FindExistingInstalls(ctx))
            {
                if (args.Remove != null && !Contains(args.Remove, install.Key)) continue;
                if (scope != null && install.Scope != scope.Value) continue;
                installs.Add(install);
            }

            if (installs.Count == 0)
            {
                ctx.Log(product.Name + " is not installed.");
                return ExitOk;
            }

            string reason = SetupEngine.ElevationReasonForRemoval(ctx, installs);
            if (reason != null)
            {
                ctx.Log("Administrator rights are needed to " + reason + ". Run setup from an elevated prompt.");
                return ExitElevationRequired;
            }

            return SetupEngine.Uninstall(ctx, product, installs) ? ExitOk : ExitFailed;
        }

        internal static bool Contains(IEnumerable<string> list, string value)
        {
            foreach (string item in list)
            {
                if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
