// AB Adv Tools shared kit - installer engine. See SetupProduct.cs for the overview.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace ABAdvTools.Setup
{
    internal enum InstallScope
    {
        /// <summary>This Windows user only. No administrator rights.</summary>
        CurrentUser,

        /// <summary>Every user of the computer. Needs administrator rights.</summary>
        AllUsers
    }

    internal enum HostKind
    {
        Revit,
        Navisworks
    }

    /// <summary>One installed Autodesk release the add-in can be installed into.</summary>
    internal sealed class HostTarget
    {
        public HostKind Host { get; set; }
        public int Year { get; set; }

        /// <summary>"Manage" or "Simulate" for Navisworks; null for Revit.</summary>
        public string Edition { get; set; }

        public string InstallDirectory { get; set; }

        /// <summary>True when this installer carries a build for this release.</summary>
        public bool PayloadAvailable { get; set; }

        /// <summary>Why a release cannot be installed to, shown beside it.</summary>
        public string Note { get; set; }

        /// <summary>Stable key used on the command line, e.g. "Revit2024" or "NavisworksManage2026".</summary>
        public string Key
        {
            get
            {
                return Host == HostKind.Revit
                    ? "Revit" + Year.ToString(CultureInfo.InvariantCulture)
                    : "Navisworks" + (Edition ?? string.Empty) + Year.ToString(CultureInfo.InvariantCulture);
            }
        }

        public string DisplayName
        {
            get
            {
                return Host == HostKind.Revit
                    ? "Autodesk Revit " + Year.ToString(CultureInfo.InvariantCulture)
                    : "Autodesk Navisworks " + Edition + " " + Year.ToString(CultureInfo.InvariantCulture);
            }
        }

        public override string ToString()
        {
            return string.IsNullOrEmpty(Note) ? DisplayName : DisplayName + "   (" + Note + ")";
        }
    }

    /// <summary>
    /// A copy of the product already on this computer: an earlier MSI, files a previous installer
    /// or a developer script left behind, or an install made by this installer family.
    /// </summary>
    internal sealed class ExistingInstall
    {
        public ExistingInstall()
        {
            Locations = new List<string>();
        }

        /// <summary>Stable key used on the command line when setup restarts elevated.</summary>
        public string Key { get; set; }

        public string ProductName { get; set; }

        /// <summary>Best available version, or null when it cannot be told.</summary>
        public string Version { get; set; }

        /// <summary>How it got there: "Windows Installer package", "AB Adv Tools setup", "Copied files".</summary>
        public string InstalledBy { get; set; }

        public InstallScope Scope { get; set; }

        public List<string> Locations { get; private set; }

        public bool NeedsElevation { get; set; }

        /// <summary>Removes this copy. Throws on failure; the engine reports it.</summary>
        public Action<SetupContext> Remove { get; set; }

        public string Title
        {
            get { return ProductName + (string.IsNullOrEmpty(Version) ? string.Empty : " " + Version); }
        }

        public string ScopeText
        {
            get { return Scope == InstallScope.AllUsers ? "All users" : "Current user"; }
        }
    }

    /// <summary>Everything the user chose, handed from the UI (or the command line) to the engine.</summary>
    internal sealed class InstallPlan
    {
        public InstallPlan()
        {
            Targets = new List<HostTarget>();
            Remove = new List<ExistingInstall>();
        }

        public InstallScope Scope { get; set; }
        public List<HostTarget> Targets { get; private set; }
        public List<ExistingInstall> Remove { get; private set; }
    }
}
