// AB Adv Tools shared kit - installer engine. See SetupProduct.cs for the overview.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace ABAdvTools.Setup
{
    /// <summary>
    /// Finds installed Revit and Navisworks releases.
    ///
    /// The product's own registry key first, then the conventional Program Files layout. Every
    /// candidate must contain the product's API assembly: that rules out the leftovers Autodesk
    /// uninstallers leave behind - a "Revit 2027" folder holding only setup files, or a
    /// "Navisworks Manage 2025" folder holding only a Plugins subfolder, both seen in the wild.
    /// </summary>
    internal static class AutodeskLocator
    {
        public const string RevitProcess = "Revit";
        public const string NavisworksProcess = "Roamer";

        private static readonly string[] NavisworksEditions = { "Manage", "Simulate" };

        public static List<HostTarget> FindRevit(int minYear, int maxYear)
        {
            var found = new List<HostTarget>();

            for (int year = minYear; year <= maxYear; year++)
            {
                string folder = RevitFromRegistry(year) ?? FromProgramFiles("Revit " + Y(year), "RevitAPI.dll");
                if (folder == null) continue;

                found.Add(new HostTarget { Host = HostKind.Revit, Year = year, InstallDirectory = folder });
            }
            return found;
        }

        public static List<HostTarget> FindNavisworks(int minYear, int maxYear)
        {
            var found = new List<HostTarget>();

            for (int year = minYear; year <= maxYear; year++)
            {
                foreach (string edition in NavisworksEditions)
                {
                    string folder = NavisworksFromRegistry(year, edition) ??
                                    FromProgramFiles("Navisworks " + edition + " " + Y(year), "Autodesk.Navisworks.Api.dll");
                    if (folder == null) continue;

                    found.Add(new HostTarget
                    {
                        Host = HostKind.Navisworks,
                        Year = year,
                        Edition = edition,
                        InstallDirectory = folder
                    });
                }
            }
            return found;
        }

        /// <summary>Autodesk numbers Navisworks releases as year minus 2003: 2026 is series Nw23.</summary>
        public static string NavisworksSeries(int year)
        {
            return "Nw" + (year - 2003).ToString(CultureInfo.InvariantCulture);
        }

        public static bool IsRunning(string processName)
        {
            try
            {
                Process[] running = Process.GetProcessesByName(processName);
                foreach (Process process in running) process.Dispose();
                return running.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static string RevitFromRegistry(int year)
        {
            try
            {
                using (RegistryKey hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey key = hive.OpenSubKey(@"SOFTWARE\Autodesk\Revit\" + Y(year)))
                {
                    if (key == null) return null;
                    foreach (string sub in key.GetSubKeyNames())
                    {
                        using (RegistryKey product = key.OpenSubKey(sub))
                        {
                            string location = product == null ? null : product.GetValue("InstallationLocation") as string;
                            string valid = Validate(location, "RevitAPI.dll");
                            if (valid != null) return valid;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static string NavisworksFromRegistry(int year, string edition)
        {
            try
            {
                string major = (year - 2003).ToString(CultureInfo.InvariantCulture);
                using (RegistryKey hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey key = hive.OpenSubKey(@"SOFTWARE\Autodesk\Navisworks " + edition + @"\" + major + @".0\Location"))
                {
                    return key == null ? null : Validate(key.GetValue("Path") as string, "Autodesk.Navisworks.Api.dll");
                }
            }
            catch
            {
                return null;
            }
        }

        private static string FromProgramFiles(string folderName, string apiFile)
        {
            foreach (Environment.SpecialFolder root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string programFiles = Environment.GetFolderPath(root);
                if (string.IsNullOrEmpty(programFiles)) continue;

                string valid = Validate(Path.Combine(programFiles, "Autodesk", folderName), apiFile);
                if (valid != null) return valid;
            }
            return null;
        }

        private static string Validate(string folder, string apiFile)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            try
            {
                folder = folder.TrimEnd('\\');
                return File.Exists(Path.Combine(folder, apiFile)) ? folder : null;
            }
            catch
            {
                return null;
            }
        }

        private static string Y(int year)
        {
            return year.ToString(CultureInfo.InvariantCulture);
        }
    }
}
