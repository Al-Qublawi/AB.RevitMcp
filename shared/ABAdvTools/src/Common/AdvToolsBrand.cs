// ============================================================================
//  AB Adv Tools shared kit - part of every AB add-in.
//  Canonical copy: AB.AdvTools.Kit\src. Do not edit the vendored copy inside an
//  add-in repository; change the kit and run sync-kit.ps1 instead.
// ============================================================================
using System;
using System.Diagnostics;
using System.IO;

namespace ABAdvTools
{
    /// <summary>
    /// Suite-wide identity. Every AB add-in, in Revit and in Navisworks, puts its buttons on
    /// one ribbon tab with this name, and credits the same author the same way.
    ///
    /// Everything in the kit is internal and compiled INTO each add-in rather than shipped as a
    /// shared DLL. Two add-ins built against different kit versions can then load side by side
    /// in one Revit or Navisworks process without an assembly-version conflict - the classic way
    /// a shared helper library breaks the second add-in that uses it.
    /// </summary>
    internal static class AdvToolsBrand
    {
        /// <summary>Bumped whenever the kit changes; shown in the About dialog.</summary>
        public const string KitVersion = "2.0.0";

        public const string SuiteName = "AB Adv Tools";

        /// <summary>The one ribbon tab every AB add-in uses. Never change it: users look for it.</summary>
        public const string RibbonTabName = "AB Adv Tools";

        /// <summary>Title of the shared About / Updates / LinkedIn panel, always the last on the tab.</summary>
        public const string SharedPanelTitle = "AB Adv Tools";

        public const string Author = "Abdullah Lotfy";
        public const string LinkedInUrl = "https://www.linkedin.com/in/abdullahalqublawi/";
        public const string LinkedInCaption = "Abdullah Lotfy - LinkedIn";
        public const string GitHubOwner = "Al-Qublawi";

        private const int FirstYear = 2026;

        /// <summary>e.g. "© 2026 Abdullah Lotfy. All rights reserved."</summary>
        public static string Copyright
        {
            get
            {
                int now = DateTime.Now.Year;
                string years = now > FirstYear ? FirstYear + "-" + now : FirstYear.ToString();
                return "© " + years + " " + Author + ". All rights reserved.";
            }
        }

        /// <summary>%LOCALAPPDATA%\AB Adv Tools - shared settings, update state and logs.</summary>
        public static string DataRoot
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    SuiteName);
            }
        }

        /// <summary>
        /// Opens a web page in the default browser. UseShellExecute must be set explicitly: it
        /// defaults to false on .NET 8 (Revit 2025+), where Process.Start(url) otherwise throws.
        /// </summary>
        public static bool OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            try
            {
                var info = new ProcessStartInfo(url);
                info.UseShellExecute = true;
                Process.Start(info);
                return true;
            }
            catch (Exception ex)
            {
                AdvToolsLog.Warn("Could not open " + url + ": " + ex.Message);
                return false;
            }
        }

        public static bool OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                var info = new ProcessStartInfo("explorer.exe", "\"" + path + "\"");
                info.UseShellExecute = true;
                Process.Start(info);
                return true;
            }
            catch (Exception ex)
            {
                AdvToolsLog.Warn("Could not open folder " + path + ": " + ex.Message);
                return false;
            }
        }
    }
}
