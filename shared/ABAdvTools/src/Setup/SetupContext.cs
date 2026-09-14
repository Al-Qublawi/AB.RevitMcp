// AB Adv Tools shared kit - installer engine. See SetupProduct.cs for the overview.
using System;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace ABAdvTools.Setup
{
    /// <summary>
    /// Where things go on this computer, and how setup reports what it does.
    ///
    /// Every path and registry key the engine or a product touches comes through here, never from
    /// Environment directly. That buys two things:
    ///   - a sandbox (/sandbox:&lt;folder&gt;) that redirects every write - files and registry - into
    ///     one folder, so the whole install / detect / upgrade / uninstall cycle can be exercised
    ///     on a machine where Revit or Navisworks is open and real installs must not be touched
    ///   - correct per-user folders when setup has restarted itself elevated: an administrator
    ///     account's %APPDATA% is not the user's, so the original values travel on the command line
    /// </summary>
    internal sealed class SetupContext
    {
        private const string UninstallSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        private const string SandboxRegistryRoot = @"Software\ABAdvTools.Sandbox";

        private readonly Action<string> _log;

        public SetupContext(Action<string> log, string sandboxRoot, string userAppData, string userLocalAppData)
        {
            _log = log ?? delegate { };
            SandboxRoot = string.IsNullOrEmpty(sandboxRoot) ? null : Path.GetFullPath(sandboxRoot);

            if (SandboxRoot != null)
            {
                AppData = Path.Combine(SandboxRoot, @"User\AppData\Roaming");
                LocalAppData = Path.Combine(SandboxRoot, @"User\AppData\Local");
                ProgramData = Path.Combine(SandboxRoot, "ProgramData");
                ProgramFiles = Path.Combine(SandboxRoot, "Program Files");
            }
            else
            {
                AppData = !string.IsNullOrEmpty(userAppData)
                    ? userAppData
                    : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                LocalAppData = !string.IsNullOrEmpty(userLocalAppData)
                    ? userLocalAppData
                    : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            }
        }

        public string SandboxRoot { get; private set; }

        public bool IsSandbox
        {
            get { return SandboxRoot != null; }
        }

        /// <summary>%APPDATA% of the user setup is installing for.</summary>
        public string AppData { get; private set; }

        /// <summary>%LOCALAPPDATA% of the user setup is installing for.</summary>
        public string LocalAppData { get; private set; }

        public string ProgramData { get; private set; }

        public string ProgramFiles { get; private set; }

        public void Log(string line)
        {
            try { _log(line ?? string.Empty); }
            catch { }
        }

        /// <summary>
        /// Something went wrong but the rest can carry on - e.g. one Revit release is running and
        /// holds its files while the others install. The run then finishes "with problems".
        /// </summary>
        public void Problem(string line)
        {
            ProblemCount++;
            Log("   PROBLEM: " + line);
        }

        public int ProblemCount { get; private set; }

        public void ResetProblems()
        {
            ProblemCount = 0;
        }

        /// <summary>
        /// Maps a real absolute path - typically inside an Autodesk install folder - into the
        /// sandbox. Outside a sandbox the path is returned unchanged.
        /// </summary>
        public string Redirect(string realPath)
        {
            if (!IsSandbox || string.IsNullOrEmpty(realPath)) return realPath;

            string full = Path.GetFullPath(realPath);
            if (full.StartsWith(SandboxRoot, StringComparison.OrdinalIgnoreCase)) return full;

            string root = Path.GetPathRoot(full) ?? string.Empty;
            string drive = root.TrimEnd('\\', ':');
            return Path.Combine(SandboxRoot, "Drives", drive, full.Substring(root.Length));
        }

        /// <summary>Folder the Autodesk Application Plugins bundles live in for a scope.</summary>
        public string ApplicationPluginsFolder(InstallScope scope)
        {
            return Path.Combine(scope == InstallScope.AllUsers ? ProgramData : AppData,
                                "Autodesk", "ApplicationPlugins");
        }

        /// <summary>Revit's add-in manifest folder for a release and scope.</summary>
        public string RevitAddinsFolder(InstallScope scope, int year)
        {
            return Path.Combine(scope == InstallScope.AllUsers ? ProgramData : AppData,
                                "Autodesk", "Revit", "Addins", year.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>Where setup keeps its own copy, so Apps and Features can run the uninstaller.</summary>
        public string SetupFolder(InstallScope scope, string productId)
        {
            return scope == InstallScope.AllUsers
                ? Path.Combine(ProgramFiles, AdvToolsBrand.SuiteName, productId)
                : Path.Combine(LocalAppData, AdvToolsBrand.SuiteName, "Setup", productId);
        }

        // ------------------------------------------------------------ registry

        /// <summary>Opens the Apps and Features key for a scope. Null when it cannot be opened.</summary>
        public RegistryKey OpenUninstallRoot(InstallScope scope, bool writable)
        {
            try
            {
                if (IsSandbox)
                {
                    string path = SandboxRegistryRoot + @"\" + SandboxName() + @"\" + scope;
                    return writable
                        ? Registry.CurrentUser.CreateSubKey(path)
                        : Registry.CurrentUser.OpenSubKey(path);
                }

                RegistryKey hive = scope == InstallScope.AllUsers
                    ? RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    : RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);

                return writable ? hive.CreateSubKey(UninstallSubKey) : hive.OpenSubKey(UninstallSubKey);
            }
            catch (Exception ex)
            {
                Log("Could not open the uninstall registry for " + scope + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>Removes the sandbox's registry keys. Test helper.</summary>
        public void DeleteSandboxRegistry()
        {
            if (!IsSandbox) return;
            try { Registry.CurrentUser.DeleteSubKeyTree(SandboxRegistryRoot + @"\" + SandboxName(), false); }
            catch { }
        }

        private string SandboxName()
        {
            // One registry subtree per sandbox folder, so parallel test runs do not collide.
            return Path.GetFileName(SandboxRoot.TrimEnd('\\'));
        }

        // ------------------------------------------------------------ elevation

        public static bool IsElevated
        {
            get
            {
                try
                {
                    using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    {
                        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                    }
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
