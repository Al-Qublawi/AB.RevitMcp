// AB Adv Tools shared kit - see AdvToolsBrand.cs for how the kit is shared.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ABAdvTools
{
    /// <summary>
    /// Suite-wide preferences shared by every AB add-in on this machine, in
    /// %LOCALAPPDATA%\AB Adv Tools\settings.ini. Today that is only whether release
    /// notifications are shown. Missing or unreadable file = defaults.
    /// </summary>
    internal static class AdvToolsSettings
    {
        private const string CheckForUpdatesKey = "CheckForUpdates";
        private static readonly object Gate = new object();

        public static string FilePath
        {
            get { return Path.Combine(AdvToolsBrand.DataRoot, "settings.ini"); }
        }

        /// <summary>
        /// Whether add-ins look for new releases on GitHub at startup. On by default. A manual
        /// "Check for Updates" from the ribbon always runs, whatever this says.
        /// </summary>
        public static bool CheckForUpdates
        {
            get
            {
                string value;
                if (!Read().TryGetValue(CheckForUpdatesKey, out value)) return true;
                return !value.Equals("false", StringComparison.OrdinalIgnoreCase) && value != "0";
            }
            set
            {
                Dictionary<string, string> values = Read();
                values[CheckForUpdatesKey] = value ? "true" : "false";
                Write(values);
            }
        }

        private static Dictionary<string, string> Read()
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                lock (Gate)
                {
                    if (!File.Exists(FilePath)) return values;
                    foreach (string raw in File.ReadAllLines(FilePath))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                }
            }
            catch { }
            return values;
        }

        private static void Write(Dictionary<string, string> values)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# AB Adv Tools settings, shared by every AB add-in. Safe to delete.");
                foreach (KeyValuePair<string, string> pair in values)
                    sb.AppendLine(pair.Key + "=" + pair.Value);

                lock (Gate)
                {
                    Directory.CreateDirectory(AdvToolsBrand.DataRoot);
                    File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                AdvToolsLog.Warn("Could not save settings: " + ex.Message);
            }
        }
    }
}
