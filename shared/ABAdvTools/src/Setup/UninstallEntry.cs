// AB Adv Tools shared kit - installer engine. See SetupProduct.cs for the overview.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace ABAdvTools.Setup
{
    /// <summary>What an AB setup recorded in Apps and Features when it installed.</summary>
    internal sealed class RegisteredInstall
    {
        public InstallScope Scope { get; set; }
        public string DisplayName { get; set; }
        public string Version { get; set; }
        public string InstallLocation { get; set; }

        /// <summary>Host releases the add-in was installed into, e.g. "Revit2024;Revit2026".</summary>
        public string Targets { get; set; }
    }

    /// <summary>
    /// The Apps and Features entry, under Uninstall\ABAdvTools.&lt;ProductId&gt;. It is how a later
    /// release recognises this one, and how a user removes the add-in the normal Windows way.
    /// Setup copies itself beside the entry so the uninstall command keeps working after the
    /// downloaded installer has been deleted.
    /// </summary>
    internal static class UninstallEntry
    {
        public static string KeyName(SetupProduct product)
        {
            return "ABAdvTools." + product.Id;
        }

        public static RegisteredInstall Read(SetupContext ctx, SetupProduct product, InstallScope scope)
        {
            using (RegistryKey root = ctx.OpenUninstallRoot(scope, false))
            {
                if (root == null) return null;
                using (RegistryKey key = root.OpenSubKey(KeyName(product)))
                {
                    if (key == null) return null;
                    return new RegisteredInstall
                    {
                        Scope = scope,
                        DisplayName = key.GetValue("DisplayName") as string,
                        Version = key.GetValue("DisplayVersion") as string,
                        InstallLocation = key.GetValue("InstallLocation") as string,
                        Targets = key.GetValue("ABAdvToolsTargets") as string
                    };
                }
            }
        }

        public static void Write(SetupContext ctx, SetupProduct product, InstallScope scope,
                                 string targets, long estimatedSizeKb)
        {
            string setupFolder = ctx.SetupFolder(scope, product.Id);
            string setupCopy = Path.Combine(setupFolder, product.SetupFileName);

            CopySelf(setupCopy);

            using (RegistryKey root = ctx.OpenUninstallRoot(scope, true))
            {
                if (root == null) throw new InvalidOperationException("Could not write the Apps and Features entry.");

                using (RegistryKey key = root.CreateSubKey(KeyName(product)))
                {
                    key.SetValue("DisplayName", product.ArpDisplayName);
                    key.SetValue("DisplayVersion", product.Version);
                    key.SetValue("Publisher", AdvToolsBrand.Author);
                    key.SetValue("DisplayIcon", setupCopy + ",0");
                    key.SetValue("InstallLocation", setupFolder);
                    key.SetValue("UninstallString", Quote(setupCopy) + " /uninstall /interactive");
                    key.SetValue("QuietUninstallString", Quote(setupCopy) + " /uninstall /silent");
                    key.SetValue("URLInfoAbout", product.RepositoryUrl);
                    key.SetValue("URLUpdateInfo", product.RepositoryUrl + "/releases/latest");
                    key.SetValue("HelpLink", AdvToolsBrand.LinkedInUrl);
                    key.SetValue("Comments", AdvToolsBrand.SuiteName + " - " + product.Description);
                    key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, Math.Max(1, estimatedSizeKb)), RegistryValueKind.DWord);
                    key.SetValue("ABAdvToolsTargets", targets ?? string.Empty);
                    key.SetValue("ABAdvToolsScope", scope.ToString());
                }
            }

            ctx.Log("   registered in Apps and Features (" + (scope == InstallScope.AllUsers ? "all users" : "current user") + ")");
        }

        public static void Delete(SetupContext ctx, SetupProduct product, InstallScope scope)
        {
            using (RegistryKey root = ctx.OpenUninstallRoot(scope, true))
            {
                if (root == null) return;
                if (root.OpenSubKey(KeyName(product)) == null) return;
                root.DeleteSubKeyTree(KeyName(product), false);
            }

            string setupFolder = ctx.SetupFolder(scope, product.Id);
            RemoveSetupCopy(ctx, setupFolder);
        }

        private static void CopySelf(string destination)
        {
            string self = Assembly.GetEntryAssembly() != null ? Assembly.GetEntryAssembly().Location : null;
            if (string.IsNullOrEmpty(self) || !File.Exists(self)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(destination));

            // Re-running the registered copy itself (a repair) must not try to overwrite itself.
            if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                return;

            File.Copy(self, destination, true);
        }

        /// <summary>
        /// Deletes setup's own copy. When that copy is the program running right now - the user
        /// clicked Uninstall in Apps and Features - Windows will not delete a running executable,
        /// so a hidden command waits for this process to exit and removes the folder then.
        /// </summary>
        private static void RemoveSetupCopy(SetupContext ctx, string setupFolder)
        {
            if (!Directory.Exists(setupFolder)) return;

            string self = Assembly.GetEntryAssembly() != null ? Assembly.GetEntryAssembly().Location : string.Empty;
            bool runningFromIt = !string.IsNullOrEmpty(self) &&
                                 Path.GetFullPath(self).StartsWith(Path.GetFullPath(setupFolder), StringComparison.OrdinalIgnoreCase);

            if (!runningFromIt)
            {
                try { FileOps.DeleteDirectory(setupFolder); }
                catch (Exception ex) { ctx.Log("   could not remove " + setupFolder + ": " + ex.Message); }
                FileOps.RemoveIfEmpty(Path.GetDirectoryName(setupFolder));
                return;
            }

            try
            {
                // Setup exits within a second of scheduling this; the ping is a portable ~3 s wait.
                string script = "/c ping -n 4 127.0.0.1 >nul & rmdir /s /q " + Quote(setupFolder);

                Process.Start(new ProcessStartInfo("cmd.exe", script)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch (Exception ex)
            {
                ctx.Log("   could not schedule removal of " + setupFolder + ": " + ex.Message);
            }
        }

        private static string Quote(string path)
        {
            return "\"" + path + "\"";
        }
    }
}
