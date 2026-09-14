// AB Adv Tools shared kit - installer engine. See SetupProduct.cs for the overview.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ABAdvTools.Setup
{
    /// <summary>
    /// A Revit add-in laid out the standard way: a manifest in Addins\&lt;year&gt;\ and the binaries
    /// in a subfolder beside it that the manifest points to with a relative path.
    ///
    ///     %APPDATA% or %PROGRAMDATA%\Autodesk\Revit\Addins\2026\MyTool.addin
    ///                                                           \MyTool\MyTool.dll ...
    ///
    /// Payloads are expected as "Revit&lt;year&gt;.zip" holding the contents of that subfolder.
    /// </summary>
    internal sealed class RevitAddin
    {
        /// <summary>Manifest file name, e.g. "ABSwitchBack.addin". Keep it stable across releases.</summary>
        public string ManifestFileName { get; set; }

        /// <summary>Subfolder beside the manifest, e.g. "ABSwitchBack".</summary>
        public string FolderName { get; set; }

        /// <summary>Main add-in assembly inside the subfolder, e.g. "ABSwitchBack.Revit.dll".</summary>
        public string AssemblyFileName { get; set; }

        public string AddInName { get; set; }
        public string FullClassName { get; set; }

        /// <summary>The add-in's GUID. Revit keys the registration off it: never change it.</summary>
        public string AddInId { get; set; }

        public string VendorId { get; set; }
        public string VendorDescription { get; set; }

        public static string PayloadName(int year)
        {
            return "Revit" + year.ToString(CultureInfo.InvariantCulture);
        }

        public string ManifestPath(SetupContext ctx, InstallScope scope, int year)
        {
            return Path.Combine(ctx.RevitAddinsFolder(scope, year), ManifestFileName);
        }

        public string BinaryFolder(SetupContext ctx, InstallScope scope, int year)
        {
            return Path.Combine(ctx.RevitAddinsFolder(scope, year), FolderName);
        }

        /// <summary>Revit years that have this add-in's manifest in a scope.</summary>
        public List<int> InstalledYears(SetupContext ctx, InstallScope scope)
        {
            var years = new List<int>();
            string root = Path.Combine(scope == InstallScope.AllUsers ? ctx.ProgramData : ctx.AppData, "Autodesk", "Revit", "Addins");
            if (!Directory.Exists(root)) return years;

            foreach (string folder in Directory.GetDirectories(root))
            {
                int year;
                if (!int.TryParse(Path.GetFileName(folder), NumberStyles.Integer, CultureInfo.InvariantCulture, out year)) continue;
                if (File.Exists(Path.Combine(folder, ManifestFileName)) || Directory.Exists(Path.Combine(folder, FolderName)))
                    years.Add(year);
            }
            years.Sort();
            return years;
        }

        public string InstalledVersion(SetupContext ctx, InstallScope scope)
        {
            var files = new List<string>();
            foreach (int year in InstalledYears(ctx, scope))
                files.Add(Path.Combine(BinaryFolder(ctx, scope, year), AssemblyFileName));
            return FileOps.HighestVersion(files);
        }

        /// <summary>
        /// Installs into one Revit release. Refuses up front if Revit has the files locked: throws, or
        /// with <paramref name="skipLocked"/> reports a problem, leaves that release as it was, and
        /// returns false so the other releases can still install.
        /// </summary>
        public bool Install(SetupContext ctx, InstallScope scope, int year, bool skipLocked = false)
        {
            string binaries = BinaryFolder(ctx, scope, year);

            string locked = FileOps.FirstLockedFile(binaries);
            if (locked != null)
            {
                string message = "Revit " + year + " skipped: '" + Path.GetFileName(locked) +
                                 "' is locked, so Revit " + year + " is still running. Close it and run setup again. Nothing was changed for it.";
                if (!skipLocked) throw new InvalidOperationException(message);
                ctx.Problem(message);
                return false;
            }

            int files = Payload.Extract(PayloadName(year), binaries, true);
            WriteManifest(ManifestPath(ctx, scope, year));
            ctx.Log("   Revit " + year + "  ->  " + binaries + "  (" + files + " files)");
            return true;
        }

        /// <summary>
        /// Removes the manifest and the binaries from every Revit release in a scope. With
        /// <paramref name="skipLocked"/>, a release whose files a running Revit holds is reported and
        /// left whole instead of stopping the rest.
        /// </summary>
        public void Remove(SetupContext ctx, InstallScope scope, bool skipLocked = false)
        {
            foreach (int year in InstalledYears(ctx, scope))
            {
                string binaries = BinaryFolder(ctx, scope, year);
                if (skipLocked && FileOps.FirstLockedFile(binaries) != null)
                {
                    ctx.Problem("Revit " + year + " is running and holds " + binaries + " - left in place.");
                    continue;
                }

                FileOps.RemoveFile(ctx, ManifestPath(ctx, scope, year));
                FileOps.RemoveDirectory(ctx, binaries);
            }
        }

        private void WriteManifest(string path)
        {
            // A relative assembly path, which Revit resolves against the manifest's folder.
            string manifest =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
                "<!-- Written by the " + FileOps.XmlEscape(AddInName) + " installer (" + AdvToolsBrand.SuiteName + "). -->\r\n" +
                "<RevitAddIns>\r\n" +
                "  <AddIn Type=\"Application\">\r\n" +
                "    <Name>" + FileOps.XmlEscape(AddInName) + "</Name>\r\n" +
                "    <Assembly>" + FileOps.XmlEscape(FolderName + "\\" + AssemblyFileName) + "</Assembly>\r\n" +
                "    <AddInId>" + AddInId + "</AddInId>\r\n" +
                "    <FullClassName>" + FileOps.XmlEscape(FullClassName) + "</FullClassName>\r\n" +
                "    <VendorId>" + FileOps.XmlEscape(VendorId) + "</VendorId>\r\n" +
                "    <VendorDescription>" + FileOps.XmlEscape(VendorDescription) + "</VendorDescription>\r\n" +
                "  </AddIn>\r\n" +
                "</RevitAddIns>\r\n";

            // UTF-8 without a BOM: some Revit releases ignore a manifest that has one.
            FileOps.WriteText(path, manifest);
        }
    }
}
