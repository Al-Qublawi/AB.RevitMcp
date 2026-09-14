// AB Adv Tools shared kit - installer engine. See SetupProduct.cs for the overview.
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ABAdvTools.Setup
{
    /// <summary>File helpers with the failure modes of add-in folders in mind: read-only flags,
    /// DLLs locked by a running Revit or Navisworks, and files that simply are not there.</summary>
    internal static class FileOps
    {
        /// <summary>
        /// Deletes a folder tree - or nothing. Every DLL is checked for a lock first, so a copy that a
        /// running Revit or Navisworks still has open is left whole rather than half deleted.
        /// </summary>
        public static void DeleteDirectory(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

            string locked = FirstLockedFile(folder);
            if (locked != null)
                throw new IOException("'" + locked + "' is in use - close the application using it. Nothing in that folder was removed.");

            foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); }
                catch { }
            }
            Directory.Delete(folder, true);
        }

        public static void DeleteFile(string file)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return;
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }

        /// <summary>Deletes a folder if it exists and logs it. Returns true when something was removed.</summary>
        public static bool RemoveDirectory(SetupContext ctx, string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return false;
            DeleteDirectory(folder);
            ctx.Log("   removed " + folder);
            return true;
        }

        public static bool RemoveFile(SetupContext ctx, string file)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return false;
            DeleteFile(file);
            ctx.Log("   removed " + file);
            return true;
        }

        /// <summary>Removes a folder only if nothing is left in it - never someone else's files.</summary>
        public static void RemoveIfEmpty(string folder)
        {
            try
            {
                if (Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length == 0)
                    Directory.Delete(folder);
            }
            catch { }
        }

        /// <summary>
        /// The first file in a folder tree that cannot be opened for writing, or null. Used to
        /// refuse an install up front rather than fail half way and leave a broken add-in.
        /// </summary>
        public static string FirstLockedFile(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;

            foreach (string file in Directory.GetFiles(folder, "*.dll", SearchOption.AllDirectories))
            {
                try
                {
                    using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                }
                catch (IOException) { return file; }
                catch (UnauthorizedAccessException) { return file; }
            }
            return null;
        }

        /// <summary>Three-part version of a DLL or EXE, or null when it cannot be read.</summary>
        public static string VersionOf(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(file);
                string text = !string.IsNullOrEmpty(info.ProductVersion) ? info.ProductVersion : info.FileVersion;
                if (string.IsNullOrEmpty(text)) return null;

                int plus = text.IndexOf('+');
                if (plus > 0) text = text.Substring(0, plus);

                Version parsed;
                return System.Version.TryParse(text, out parsed)
                    ? parsed.ToString(Math.Min(3, Math.Max(2, CountParts(text))))
                    : text;
            }
            catch
            {
                return null;
            }
        }

        private static int CountParts(string text)
        {
            return text.Split('.').Length;
        }

        /// <summary>The highest version among several copies of the same file.</summary>
        public static string HighestVersion(System.Collections.Generic.IEnumerable<string> files)
        {
            string best = null;
            foreach (string file in files)
            {
                string v = VersionOf(file);
                if (v == null) continue;
                if (best == null || UpdateChecker.Compare(v, best) > 0) best = v;
            }
            return best;
        }

        /// <summary>Writes text as UTF-8 without a byte order mark - what Autodesk manifest parsers expect.</summary>
        public static void WriteText(string file, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, content, new UTF8Encoding(false));
        }

        public static string XmlEscape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }
    }
}
