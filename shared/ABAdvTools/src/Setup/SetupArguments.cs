// AB Adv Tools shared kit - installer engine. See SetupProduct.cs for the overview.
using System;
using System.Collections.Generic;
using System.Text;

namespace ABAdvTools.Setup
{
    /// <summary>
    /// Command line, identical for every AB setup:
    ///
    ///   (none)              the setup window
    ///   /silent             install every supported release with no UI, removing earlier versions
    ///   /uninstall          remove the add-in (with /silent: no UI)
    ///   /allusers           install for all users (needs an elevated prompt when silent)
    ///   /currentuser        install for the current user only
    ///   /keepold            silent install that leaves earlier versions in place (not recommended)
    ///   /targets:A,B        only these releases, e.g. /targets:Revit2024,Revit2026
    ///   /log:&lt;file&gt;         also write the log to this file
    ///   /scan               report detected releases and earlier versions, change nothing
    ///   /sandbox:&lt;folder&gt;   redirect every file and registry write into a folder (testing)
    ///   /interactive        with /uninstall: always show the window (Apps and Features uses this)
    ///   /render:&lt;folder&gt;    save pictures of every setup page to a folder, change nothing (testing)
    ///
    /// Internal, used when setup restarts itself elevated:
    ///   /autorun /remove:k1,k2 /appdata:&lt;path&gt; /localappdata:&lt;path&gt;
    /// </summary>
    internal sealed class SetupArguments
    {
        public bool Silent;
        public bool Uninstall;
        public bool AutoRun;
        public bool Scan;
        public bool KeepOld;
        public bool Interactive;
        public string RenderFolder;
        public InstallScope? Scope;
        public string Sandbox;
        public string LogFile;
        public string UserAppData;
        public string UserLocalAppData;
        public List<string> Targets;
        public List<string> Remove;

        public static SetupArguments Parse(string[] args)
        {
            var parsed = new SetupArguments();
            if (args == null) return parsed;

            foreach (string raw in args)
            {
                if (string.IsNullOrEmpty(raw)) continue;

                string arg = raw.TrimStart('/', '-');
                string name = arg;
                string value = null;

                int split = arg.IndexOfAny(new[] { ':', '=' });
                if (split > 0)
                {
                    name = arg.Substring(0, split);
                    value = arg.Substring(split + 1).Trim('"');
                }

                switch (name.ToLowerInvariant())
                {
                    case "silent":
                    case "quiet":
                    case "s":
                        parsed.Silent = true; break;
                    case "uninstall":
                    case "remove-all":
                        parsed.Uninstall = true; break;
                    case "autorun": parsed.AutoRun = true; break;
                    case "scan": parsed.Scan = true; break;
                    case "keepold": parsed.KeepOld = true; break;
                    case "interactive": parsed.Interactive = true; break;
                    case "render": parsed.RenderFolder = value; break;
                    case "allusers": parsed.Scope = InstallScope.AllUsers; break;
                    case "currentuser": parsed.Scope = InstallScope.CurrentUser; break;
                    case "sandbox": parsed.Sandbox = value; break;
                    case "log": parsed.LogFile = value; break;
                    case "appdata": parsed.UserAppData = value; break;
                    case "localappdata": parsed.UserLocalAppData = value; break;
                    case "targets": parsed.Targets = SplitList(value); break;
                    case "remove": parsed.Remove = SplitList(value); break;
                }
            }
            return parsed;
        }

        /// <summary>The arguments that recreate a choice in an elevated copy of setup.</summary>
        public static string ForElevatedRestart(bool uninstall, InstallScope scope, IEnumerable<string> targets,
                                                IEnumerable<string> remove, SetupContext ctx, string logFile)
        {
            var sb = new StringBuilder();
            sb.Append("/autorun");
            if (uninstall) sb.Append(" /uninstall");
            else sb.Append(scope == InstallScope.AllUsers ? " /allusers" : " /currentuser");

            if (targets != null) sb.Append(" \"/targets:" + string.Join(",", new List<string>(targets).ToArray()) + "\"");
            if (remove != null) sb.Append(" \"/remove:" + string.Join(",", new List<string>(remove).ToArray()) + "\"");

            // An administrator account's profile is not the user's. Carry the user's folders over.
            sb.Append(" \"/appdata:" + ctx.AppData + "\"");
            sb.Append(" \"/localappdata:" + ctx.LocalAppData + "\"");
            if (!string.IsNullOrEmpty(logFile)) sb.Append(" \"/log:" + logFile + "\"");
            return sb.ToString();
        }

        private static List<string> SplitList(string value)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(value)) return list;
            foreach (string part in value.Split(','))
            {
                string trimmed = part.Trim();
                if (trimmed.Length > 0) list.Add(trimmed);
            }
            return list;
        }
    }
}
