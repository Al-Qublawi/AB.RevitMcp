// AB Adv Tools shared kit - see AdvToolsBrand.cs for how the kit is shared.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace ABAdvTools
{
    /// <summary>
    /// A deliberately tiny diagnostic log for the kit itself: ribbon merging, update checks and
    /// installer-visible events. Add-ins keep their own logs for their own work.
    ///
    /// %LOCALAPPDATA%\AB Adv Tools\Logs\advtools-yyyyMMdd.log, one file a day, a week kept.
    /// Several add-ins append to the same file from separate assemblies, so every write opens,
    /// appends and closes with shared access, and any failure is swallowed: logging must never
    /// be the reason a host application misbehaves.
    /// </summary>
    internal static class AdvToolsLog
    {
        private static readonly object Gate = new object();
        private static bool _pruned;

        /// <summary>Set once per add-in, e.g. "ClashApprover", so lines can be told apart.</summary>
        public static string Source { get; set; }

        public static string Directory
        {
            get { return Path.Combine(AdvToolsBrand.DataRoot, "Logs"); }
        }

        public static void Info(string message) { Write("INFO ", message); }
        public static void Warn(string message) { Write("WARN ", message); }

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", ex == null ? message : message + " " + ex.GetType().Name + ": " + ex.Message);
        }

        private static void Write(string level, string message)
        {
            try
            {
                string folder = Directory;
                System.IO.Directory.CreateDirectory(folder);

                string file = Path.Combine(folder,
                    "advtools-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");

                string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " +
                              level + " [" + (Source ?? "kit") + " pid " + CurrentPid() + "] " +
                              (message ?? string.Empty) + Environment.NewLine;

                byte[] bytes = Encoding.UTF8.GetBytes(line);

                lock (Gate)
                {
                    using (var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                    }

                    if (!_pruned)
                    {
                        _pruned = true;
                        Prune(folder);
                    }
                }
            }
            catch
            {
                // Never let a log line take anything down.
            }
        }

        private static void Prune(string folder)
        {
            try
            {
                DateTime cutoff = DateTime.Now.AddDays(-7);
                foreach (string file in System.IO.Directory.GetFiles(folder, "advtools-*.log"))
                {
                    if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
                }
            }
            catch { }
        }

        private static string CurrentPid()
        {
            try { return Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture); }
            catch { return "?"; }
        }
    }
}
