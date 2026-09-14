// AB Adv Tools shared kit - see AdvToolsBrand.cs for how the kit is shared.
using System;
using System.Collections.Generic;
using System.Reflection;

namespace ABAdvTools
{
    /// <summary>A button an add-in contributes to the About dialog.</summary>
    internal sealed class AdvToolsAction
    {
        public AdvToolsAction(string text, Action run)
        {
            Text = text;
            Run = run;
        }

        public string Text { get; private set; }

        /// <summary>
        /// A framework delegate type on purpose: the About dialog may belong to another add-in's
        /// copy of the kit, and only framework types can be shared between those copies.
        /// </summary>
        public Action Run { get; private set; }
    }

    /// <summary>Which Autodesk application an add-in runs inside.</summary>
    internal enum AdvToolsHost
    {
        Revit,
        Navisworks
    }

    /// <summary>
    /// Identity of one AB add-in, as the suite needs to know it: what to call it, which version is
    /// running, and which GitHub repository its releases are published to.
    ///
    /// Each add-in creates exactly one of these at startup and hands it to RevitAdvTools or
    /// NavisworksAdvTools. Adding a new AB add-in to the suite starts here.
    /// </summary>
    internal sealed class AdvToolsProduct
    {
        public AdvToolsProduct(string id, string name, string tagline, AdvToolsHost host,
                               string gitHubRepository, Assembly versionSource)
            : this(id, name, tagline, host, AdvToolsBrand.GitHubOwner, gitHubRepository, VersionOf(versionSource))
        {
        }

        /// <summary>Rebuilds a product another add-in registered (see AdvToolsRegistry).</summary>
        internal AdvToolsProduct(string id, string name, string tagline, AdvToolsHost host,
                                 string gitHubOwner, string gitHubRepository, string version)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException("id");
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException("name");

            Id = id;
            Name = name;
            Tagline = tagline ?? string.Empty;
            Host = host;
            GitHubOwner = string.IsNullOrEmpty(gitHubOwner) ? AdvToolsBrand.GitHubOwner : gitHubOwner;
            GitHubRepository = gitHubRepository ?? string.Empty;
            Version = string.IsNullOrEmpty(version) ? "0.0.0" : version;
        }

        /// <summary>Stable key, e.g. "ClashApprover". Used for state files; never rename.</summary>
        public string Id { get; private set; }

        /// <summary>Display name, e.g. "AB Clash Approver".</summary>
        public string Name { get; private set; }

        public string Tagline { get; private set; }

        public AdvToolsHost Host { get; private set; }

        /// <summary>Three-part version of the running build, read from the assembly.</summary>
        public string Version { get; private set; }

        public string GitHubOwner { get; private set; }

        /// <summary>Repository name only, e.g. "AB.ClashApprover.Navisworks".</summary>
        public string GitHubRepository { get; private set; }

        /// <summary>
        /// Optional add-in level switch for automatic checks, on top of the suite setting - for an
        /// add-in that already had its own "check for updates" option before joining the suite.
        /// </summary>
        public Func<bool> AutomaticChecksAllowed { get; set; }

        /// <summary>
        /// Extra lines for this tool in the About dialog - what an add-in's own About used to say
        /// (tool count, host version, where its logs are). Evaluated each time About opens, so it
        /// can report live state. Optional.
        /// </summary>
        public Func<string> Details { get; set; }

        /// <summary>
        /// Where the add-in's own switch for release checks lives, added to the update notice,
        /// e.g. "the SwitchBack Settings dialog". Optional.
        /// </summary>
        public string UpdateSettingsHint { get; set; }

        private readonly List<AdvToolsAction> _actions = new List<AdvToolsAction>();

        /// <summary>Buttons shown in About while this tool is selected, e.g. "Open the log folder".</summary>
        public IList<AdvToolsAction> Actions
        {
            get { return _actions; }
        }

        public AdvToolsProduct AddAction(string text, Action run)
        {
            if (!string.IsNullOrEmpty(text) && run != null) _actions.Add(new AdvToolsAction(text, run));
            return this;
        }

        public bool HasRepository
        {
            get { return GitHubRepository.Length > 0; }
        }

        public string RepositoryUrl
        {
            get { return "https://github.com/" + GitHubOwner + "/" + GitHubRepository; }
        }

        /// <summary>Always lands on the newest release, whose assets include the installer.</summary>
        public string LatestReleasePageUrl
        {
            get { return RepositoryUrl + "/releases/latest"; }
        }

        public string LatestReleaseApiUrl
        {
            get { return "https://api.github.com/repos/" + GitHubOwner + "/" + GitHubRepository + "/releases/latest"; }
        }

        /// <summary>
        /// The version comes from the compiled assembly, never a hand-maintained constant. A
        /// constant drifts - one add-in reported 1.1.0 for two releases after it shipped 1.3.0 -
        /// and a drifted version makes every update check claim an update is available.
        /// </summary>
        internal static string VersionOf(Assembly assembly)
        {
            try
            {
                if (assembly == null) return "0.0.0";

                var informational = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    assembly, typeof(AssemblyInformationalVersionAttribute));
                if (informational != null && !string.IsNullOrEmpty(informational.InformationalVersion))
                {
                    // Strip SourceLink's "+commit" suffix.
                    string text = informational.InformationalVersion;
                    int plus = text.IndexOf('+');
                    if (plus > 0) text = text.Substring(0, plus);
                    return text;
                }

                Version v = assembly.GetName().Version;
                return v == null ? "0.0.0" : v.ToString(3);
            }
            catch
            {
                return "0.0.0";
            }
        }
    }
}
