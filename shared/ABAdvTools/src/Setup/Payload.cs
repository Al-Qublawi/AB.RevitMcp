// AB Adv Tools shared kit - installer engine. See SetupProduct.cs for the overview.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace ABAdvTools.Setup
{
    /// <summary>
    /// The add-in files travel inside the setup executable as embedded zips, so an installer is
    /// one file that can be copied anywhere. The project's build embeds payload\*.zip under
    /// "ABAdvTools.Payload.&lt;name&gt;.zip"; names are product-defined, e.g. "Revit2024" or
    /// "Navisworks2026".
    /// </summary>
    internal static class Payload
    {
        private const string Prefix = "ABAdvTools.Payload.";
        private const string Suffix = ".zip";

        public static HashSet<string> Names()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string resource in Assembly.GetExecutingAssembly().GetManifestResourceNames())
            {
                if (!resource.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                if (!resource.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) continue;
                names.Add(resource.Substring(Prefix.Length, resource.Length - Prefix.Length - Suffix.Length));
            }
            return names;
        }

        public static bool Has(string name)
        {
            return Names().Contains(name);
        }

        /// <summary>
        /// "2026-2027" for payloads named Navisworks2026 and Navisworks2027 - the releases this
        /// installer carries builds for, for the window's subtitle.
        /// </summary>
        public static string YearRange(string prefix)
        {
            var years = new List<int>();
            foreach (string name in Names())
            {
                int year;
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(name.Substring(prefix.Length), out year))
                    years.Add(year);
            }
            years.Sort();
            if (years.Count == 0) return string.Empty;
            return years.Count == 1 ? years[0].ToString() : years[0] + "-" + years[years.Count - 1];
        }

        /// <summary>
        /// Unpacks a payload into a folder and returns the number of files written.
        /// clean=true empties the folder first, so an upgrade never leaves a stale file behind.
        /// </summary>
        public static int Extract(string name, string targetFolder, bool clean)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Prefix + name + Suffix))
            {
                if (stream == null)
                    throw new InvalidOperationException("This installer does not contain the '" + name + "' payload.");

                if (clean && Directory.Exists(targetFolder)) FileOps.DeleteDirectory(targetFolder);
                Directory.CreateDirectory(targetFolder);

                string root = Path.GetFullPath(targetFolder).TrimEnd('\\') + "\\";
                int count = 0;

                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;   // a directory entry

                        string destination = Path.GetFullPath(Path.Combine(targetFolder, entry.FullName));

                        // Zip-slip guard: no entry may escape the target folder.
                        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Payload entry '" + entry.FullName + "' escapes its folder.");

                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        entry.ExtractToFile(destination, true);
                        count++;
                    }
                }
                return count;
            }
        }
    }
}
