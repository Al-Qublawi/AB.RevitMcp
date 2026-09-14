// AB Adv Tools shared kit - installer engine. See SetupProduct.cs for the overview.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ABAdvTools.Setup
{
    /// <summary>
    /// An Autodesk application bundle for a Navisworks plugin, in
    /// %APPDATA% or %PROGRAMDATA%\Autodesk\ApplicationPlugins\&lt;Name&gt;.bundle.
    ///
    /// Each Navisworks release scans that folder at startup and loads the Contents\&lt;year&gt;
    /// build whose series matches its own, so one bundle serves every release side by side.
    /// Payloads are expected as "Navisworks&lt;year&gt;.zip", each holding the plugin DLL with its
    /// Images and en-US folders.
    /// </summary>
    internal sealed class NavisworksBundle
    {
        /// <summary>Folder name, e.g. "ABClashApprover.bundle". Keep it stable across releases.</summary>
        public string BundleName { get; set; }

        /// <summary>The plugin assembly's file name, e.g. "ABClashApprover.dll".</summary>
        public string ModuleFileName { get; set; }

        public string AppName { get; set; }
        public string Description { get; set; }

        /// <summary>Bundle codes written to PackageContents.xml. Keep them stable across releases.</summary>
        public string ProductCode { get; set; }
        public string UpgradeCode { get; set; }

        public static string PayloadName(int year)
        {
            return "Navisworks" + year.ToString(CultureInfo.InvariantCulture);
        }

        public string Folder(SetupContext ctx, InstallScope scope)
        {
            return Path.Combine(ctx.ApplicationPluginsFolder(scope), BundleName);
        }

        public bool Exists(SetupContext ctx, InstallScope scope)
        {
            return Directory.Exists(Folder(ctx, scope));
        }

        /// <summary>Highest plugin version found in the bundle, or null.</summary>
        public string InstalledVersion(SetupContext ctx, InstallScope scope)
        {
            string contents = Path.Combine(Folder(ctx, scope), "Contents");
            if (!Directory.Exists(contents)) return null;
            return FileOps.HighestVersion(Directory.GetFiles(contents, ModuleFileName, SearchOption.AllDirectories));
        }

        /// <summary>Writes a clean bundle holding exactly the chosen releases.</summary>
        public void Install(SetupContext ctx, InstallScope scope, IList<HostTarget> targets, string version)
        {
            string folder = Folder(ctx, scope);

            string locked = FileOps.FirstLockedFile(folder);
            if (locked != null)
                throw new InvalidOperationException("'" + locked + "' is in use. Close Navisworks and run setup again.");

            if (Directory.Exists(folder)) FileOps.DeleteDirectory(folder);

            var years = new SortedDictionary<int, List<string>>();
            foreach (HostTarget target in targets)
            {
                if (target.Host != HostKind.Navisworks) continue;
                List<string> editions;
                if (!years.TryGetValue(target.Year, out editions))
                {
                    editions = new List<string>();
                    years.Add(target.Year, editions);
                }
                if (!editions.Contains(target.Edition)) editions.Add(target.Edition);
            }

            if (years.Count == 0) throw new InvalidOperationException("No Navisworks release was selected.");

            foreach (int year in years.Keys)
            {
                string contents = Path.Combine(folder, "Contents", year.ToString(CultureInfo.InvariantCulture));
                int files = Payload.Extract(PayloadName(year), contents, true);
                ctx.Log("   Navisworks " + year + "  ->  " + contents + "  (" + files + " files)");
            }

            FileOps.WriteText(Path.Combine(folder, "PackageContents.xml"), PackageContents(years, version));
            ctx.Log("   PackageContents.xml written for " + years.Count + " release(s)");
        }

        public void Remove(SetupContext ctx, InstallScope scope)
        {
            FileOps.RemoveDirectory(ctx, Folder(ctx, scope));
        }

        private string PackageContents(SortedDictionary<int, List<string>> years, string version)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<!-- Written by the " + FileOps.XmlEscape(AppName) + " installer (" + AdvToolsBrand.SuiteName + "). -->");
            sb.AppendLine("<ApplicationPackage");
            sb.AppendLine("    SchemaVersion=\"1.0\"");
            sb.AppendLine("    ProductType=\"Application\"");
            sb.AppendLine("    Name=\"" + FileOps.XmlEscape(AppName) + "\"");
            sb.AppendLine("    AppVersion=\"" + FileOps.XmlEscape(version) + "\"");
            sb.AppendLine("    FriendlyVersion=\"" + FileOps.XmlEscape(version) + "\"");
            sb.AppendLine("    Description=\"" + FileOps.XmlEscape(Description) + "\"");
            sb.AppendLine("    Author=\"" + FileOps.XmlEscape(AdvToolsBrand.Author) + "\"");
            sb.AppendLine("    ProductCode=\"" + ProductCode + "\"");
            sb.AppendLine("    UpgradeCode=\"" + UpgradeCode + "\">");
            sb.AppendLine();
            sb.AppendLine("  <CompanyDetails Name=\"" + FileOps.XmlEscape(AdvToolsBrand.Author) + "\" />");
            sb.AppendLine();

            foreach (KeyValuePair<int, List<string>> year in years)
            {
                string y = year.Key.ToString(CultureInfo.InvariantCulture);
                string series = AutodeskLocator.NavisworksSeries(year.Key);

                var platforms = new List<string>();
                foreach (string edition in year.Value)
                    platforms.Add(string.Equals(edition, "Simulate", StringComparison.OrdinalIgnoreCase) ? "NAVSIM" : "NAVMAN");

                sb.AppendLine("  <Components Description=\"" + FileOps.XmlEscape(AppName) + " for Navisworks " + y + "\">");
                sb.AppendLine("    <RuntimeRequirements OS=\"Win64\" Platform=\"" + string.Join("|", platforms.ToArray()) +
                              "\" SeriesMin=\"" + series + "\" SeriesMax=\"" + series + "\" />");
                sb.AppendLine("    <ComponentEntry");
                sb.AppendLine("        AppName=\"" + FileOps.XmlEscape(AppName) + " " + y + "\"");
                sb.AppendLine("        AppType=\"ManagedPlugin\"");
                sb.AppendLine("        Version=\"" + FileOps.XmlEscape(version) + "\"");
                sb.AppendLine("        ModuleName=\"./Contents/" + y + "/" + ModuleFileName + "\"");
                sb.AppendLine("        AppDescription=\"" + FileOps.XmlEscape(Description) + "\" />");
                sb.AppendLine("  </Components>");
                sb.AppendLine();
            }

            sb.AppendLine("</ApplicationPackage>");
            return sb.ToString();
        }
    }
}
