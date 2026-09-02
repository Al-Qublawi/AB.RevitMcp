using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AB.RevitMcp.Setup
{
    internal static class Program
    {
        /// <summary>
        /// Entry point. Runs the window by default; /silent installs every supported Revit release
        /// found on the machine without any UI, for IT deployment.
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
            // Must be wired up BEFORE any method that touches a Contracts type is JIT-compiled,
            // which is why the real body lives in Run() with inlining suppressed.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbeddedAssembly;
            return Run(args);
        }

        /// <summary>Serves AB.RevitMcp.Contracts out of this executable's own resources.</summary>
        private static Assembly ResolveEmbeddedAssembly(object sender, ResolveEventArgs args)
        {
            string requested = new AssemblyName(args.Name).Name;
            if (requested != "AB.RevitMcp.Contracts") return null;

            using (Stream stream = Assembly.GetExecutingAssembly()
                       .GetManifestResourceStream("AB.RevitMcp.Setup.Lib.AB.RevitMcp.Contracts.dll"))
            {
                if (stream == null) return null;
                var bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int n = stream.Read(bytes, read, bytes.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                return Assembly.Load(bytes);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string[] args)
        {
            bool silent = args != null && args.Any(a =>
                string.Equals(a, "/silent", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "/S", StringComparison.Ordinal));

            bool skipAgents = args != null && args.Any(a =>
                string.Equals(a, "/noclients", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "--no-clients", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "/noagents", StringComparison.OrdinalIgnoreCase));

            if (silent) return RunSilent(!skipAgents);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
            return 0;
        }

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        private const int AttachParentProcess = -1;

        private static int RunSilent(bool configureAgents)
        {
            // A WinExe has no console of its own, so unattended output would vanish entirely.
            // Attach to the calling console when there is one, and always keep a log file so a
            // deployment run can be inspected afterwards either way.
            bool hasConsole = false;
            try { hasConsole = AttachConsole(AttachParentProcess); } catch (Exception) { }

            string logPath = Path.Combine(Path.GetTempPath(),
                "ABRevitMcp-Setup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            var lines = new System.Collections.Generic.List<string>();

            Action<string> log = delegate (string text)
            {
                lines.Add(text ?? string.Empty);
                if (hasConsole) Console.WriteLine(text);
            };

            log(Branding.ProductName + " " + Branding.Version + " - silent install");
            log("by " + Branding.Author + "  (" + Branding.LinkedInUrl + ")");
            log(string.Empty);

            var installer = new BundleInstaller(log);
            var targets = installer.DiscoverRevit(null).Where(t => t.PayloadAvailable).ToList();

            int exitCode;
            if (targets.Count == 0)
            {
                log("No supported Revit installation was found.");
                exitCode = 1;
            }
            else
            {
                // Silent runs configure every AI client actually present on the machine.
                var agents = configureAgents
                    ? AgentConfigurator.KnownAgents().Where(a => a.Detected).ToList()
                    : new System.Collections.Generic.List<AgentTarget>();

                bool ok = installer.Install(targets, agents);
                log(string.Empty);
                log(ok ? "Installed." : "Finished with problems - see above.");
                exitCode = ok ? 0 : 1;
            }

            try
            {
                File.WriteAllLines(logPath, lines.ToArray());
                if (hasConsole) Console.WriteLine("Log: " + logPath);
            }
            catch (Exception) { }

            return exitCode;
        }
    }
}
