// AB Adv Tools shared kit - installer engine.
//
// One engine, one setup executable per add-in. An add-in's installer project contains a
// SetupProduct subclass (what to install, where, and how to recognise earlier versions), a
// three-line Program.Main calling SetupProgram.Run, and its payload zips. Everything else -
// the window, earlier-version detection and removal, elevation, Apps and Features, silent
// mode, logging - lives here and behaves identically for every AB add-in.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace ABAdvTools.Setup
{
    /// <summary>A button offered once installation has finished, e.g. "Verify installation".</summary>
    internal sealed class SetupAction
    {
        public SetupAction(string text, Action<SetupContext> run)
        {
            Text = text;
            Run = run;
        }

        public string Text { get; private set; }
        public Action<SetupContext> Run { get; private set; }
    }

    /// <summary>Everything that differs between one AB add-in's installer and another's.</summary>
    internal abstract class SetupProduct
    {
        // ------------------------------------------------------------ identity

        /// <summary>Stable id, e.g. "ClashApprover". Keys the Apps and Features entry; never rename.</summary>
        public abstract string Id { get; }

        /// <summary>e.g. "AB Clash Approver".</summary>
        public abstract string Name { get; }

        /// <summary>One sentence for the welcome page and Apps and Features.</summary>
        public abstract string Description { get; }

        /// <summary>e.g. "Autodesk Navisworks Manage 2026-2027".</summary>
        public abstract string HostSummary { get; }

        /// <summary>Repository name under the suite's GitHub account, e.g. "AB.ClashApprover.Navisworks".</summary>
        public abstract string GitHubRepository { get; }

        /// <summary>Name shown in Apps and Features.</summary>
        public virtual string ArpDisplayName
        {
            get { return Name; }
        }

        /// <summary>The setup executable's own version, which the build stamps with the add-in's.</summary>
        public virtual string Version
        {
            get { return AdvToolsProduct.VersionOf(Assembly.GetEntryAssembly() ?? typeof(SetupProduct).Assembly); }
        }

        public string RepositoryUrl
        {
            get { return "https://github.com/" + AdvToolsBrand.GitHubOwner + "/" + GitHubRepository; }
        }

        public virtual string SetupFileName
        {
            get
            {
                Assembly entry = Assembly.GetEntryAssembly();
                return entry != null ? Path.GetFileName(entry.Location) : Id + ".Setup.exe";
            }
        }

        // ------------------------------------------------------------ behaviour

        public abstract InstallScope DefaultScope { get; }

        /// <summary>False when the add-in can only go in one place (e.g. a Program Files plugin folder).</summary>
        public virtual bool ScopeSelectable
        {
            get { return true; }
        }

        /// <summary>Process names that lock the add-in's files while running: "Revit", "Roamer".</summary>
        public abstract string[] BlockingProcesses { get; }

        /// <summary>Installed host releases, each flagged with whether this installer carries a build for it.</summary>
        public abstract List<HostTarget> FindTargets(SetupContext ctx);

        /// <summary>Why administrator rights are needed for this choice, or null when they are not.</summary>
        public virtual string ElevationReason(InstallScope scope, IList<HostTarget> targets)
        {
            return scope == InstallScope.AllUsers ? "install for all users of this computer" : null;
        }

        /// <summary>
        /// Every copy of this add-in already on the computer: earlier MSIs, files left by earlier
        /// installers or scripts, and installs made by this engine (see RegisteredInstallFor).
        /// </summary>
        public abstract List<ExistingInstall> FindExistingInstalls(SetupContext ctx);

        /// <summary>Copies the add-in into the chosen releases. Throws on failure.</summary>
        public abstract void Install(SetupContext ctx, InstallScope scope, IList<HostTarget> targets);

        /// <summary>Removes the files this installer family puts down for one scope.</summary>
        public abstract void RemoveFiles(SetupContext ctx, InstallScope scope);

        /// <summary>
        /// Extra options on the welcome page, or null. Build the control at exactly
        /// <paramref name="width"/> pixels wide: the page does not resize it, because resizing a
        /// panel after its children are anchored moves right-anchored children off its edge.
        /// </summary>
        public virtual Control CreateOptionsControl(int width)
        {
            return null;
        }

        /// <summary>Reads the options control. Called on the UI thread just before work starts.</summary>
        public virtual void CaptureOptions()
        {
        }

        /// <summary>Runs after the files are in and Apps and Features is written.</summary>
        public virtual void AfterInstall(SetupContext ctx, InstallPlan plan)
        {
        }

        /// <summary>Runs after a successful removal, e.g. to say what the user may want to tidy by hand.</summary>
        public virtual void AfterUninstall(SetupContext ctx)
        {
        }

        /// <summary>Buttons offered once installation has finished.</summary>
        public virtual IList<SetupAction> FinishActions
        {
            get { return new SetupAction[0]; }
        }

        /// <summary>
        /// Buttons available on the welcome page at any time, without installing - e.g. verifying an
        /// existing install. Each runs with the log visible, then returns to the welcome page.
        /// </summary>
        public virtual IList<SetupAction> ToolActions
        {
            get { return new SetupAction[0]; }
        }

        /// <summary>
        /// When true, setup warns about a running host but lets the user carry on - for add-ins
        /// whose earlier installer allowed it. Per-release locks are then reported, not fatal.
        /// </summary>
        public virtual bool AllowInstallWhileHostRunning
        {
            get { return false; }
        }

        /// <summary>
        /// When true, a bare /uninstall removes without a window, as this add-in's earlier installer
        /// did; Apps and Features still gets the window through /interactive.
        /// </summary>
        public virtual bool UninstallSwitchIsSilent
        {
            get { return false; }
        }

        /// <summary>Headless log files are %TEMP%\&lt;prefix&gt;-Setup-&lt;time&gt;.log. Keep an earlier name if scripts look for it.</summary>
        public virtual string LogFilePrefix
        {
            get { return Id; }
        }

        /// <summary>Shown when installation succeeds.</summary>
        public virtual string NextSteps
        {
            get { return "Start the Autodesk application and open the " + AdvToolsBrand.RibbonTabName + " ribbon tab."; }
        }

        // ------------------------------------------------------------ helpers for subclasses

        /// <summary>
        /// Adds a target for every release this installer has a payload for but that is not installed
        /// here yet, ticked, so the add-in is already in place when that release is installed later.
        /// The 1.x MSIs of Clash Approver and SwitchBack worked this way; products whose earlier
        /// installers did should keep doing it. Only for layouts that need no install folder (a
        /// bundle, or a Revit Addins year folder).
        /// </summary>
        protected static void AddReleasesNotInstalled(List<HostTarget> targets, HostKind host, string payloadPrefix, string edition)
        {
            foreach (string name in Payload.Names())
            {
                int year;
                if (!name.StartsWith(payloadPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(name.Substring(payloadPrefix.Length), out year)) continue;

                bool present = targets.Exists(delegate (HostTarget t)
                {
                    return t.Host == host && t.Year == year &&
                           (host == HostKind.Revit || string.Equals(t.Edition, edition, StringComparison.OrdinalIgnoreCase));
                });
                if (present) continue;

                targets.Add(new HostTarget
                {
                    Host = host,
                    Year = year,
                    Edition = host == HostKind.Navisworks ? edition : null,
                    PayloadAvailable = true,
                    Note = "not installed on this computer yet - ready for when it is"
                });
            }

            targets.Sort(delegate (HostTarget a, HostTarget b)
            {
                int byHost = a.Host.CompareTo(b.Host);
                return byHost != 0 ? byHost : a.Year.CompareTo(b.Year);
            });
        }

        /// <summary>An install made by this engine, found through its Apps and Features entry.</summary>
        protected ExistingInstall RegisteredInstallFor(SetupContext ctx, InstallScope scope)
        {
            RegisteredInstall registered = UninstallEntry.Read(ctx, this, scope);
            if (registered == null) return null;

            var install = new ExistingInstall
            {
                Key = "setup-" + scope.ToString().ToLowerInvariant(),
                ProductName = Name,
                Version = registered.Version,
                InstalledBy = AdvToolsBrand.SuiteName + " setup",
                Scope = scope,
                NeedsElevation = scope == InstallScope.AllUsers,
                Remove = delegate (SetupContext c)
                {
                    RemoveFiles(c, scope);
                    UninstallEntry.Delete(c, this, scope);
                }
            };
            if (!string.IsNullOrEmpty(registered.Targets))
                install.Locations.Add("Installed for " + registered.Targets.Replace(";", ", "));
            return install;
        }

        /// <summary>An earlier release that shipped as an .msi. cleanup runs after msiexec, for leftovers.</summary>
        protected ExistingInstall MsiInstall(MsiProduct msi, Action<SetupContext> cleanup)
        {
            var install = new ExistingInstall
            {
                Key = "msi-" + msi.ProductCode.Trim('{', '}').ToLowerInvariant(),
                ProductName = Name,
                Version = TrimVersion(msi.Version),
                InstalledBy = "Windows Installer package (.msi)",
                Scope = msi.PerMachine ? InstallScope.AllUsers : InstallScope.CurrentUser,
                NeedsElevation = msi.PerMachine,
                Remove = delegate (SetupContext c)
                {
                    WindowsInstaller.Uninstall(c, msi);
                    if (cleanup != null) cleanup(c);
                }
            };
            install.Locations.Add(msi.Name + "  " + msi.ProductCode);
            return install;
        }

        /// <summary>"1.2.1.0" reads as "1.2.1".</summary>
        protected static string TrimVersion(string version)
        {
            if (string.IsNullOrEmpty(version)) return version;
            Version parsed;
            return System.Version.TryParse(version, out parsed) && parsed.Revision == 0 && parsed.Build >= 0
                ? parsed.ToString(3)
                : version;
        }
    }
}
