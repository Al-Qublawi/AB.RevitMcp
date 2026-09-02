using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;

namespace AB.RevitMcp.Ipc
{
    /// <summary>
    /// Discovery. Each running Revit bridge drops a small JSON descriptor into
    /// %LOCALAPPDATA%\ABRevitMcp\endpoints so the MCP server can find it without the user having
    /// to configure a pipe name - and so several Revit versions can run side by side.
    /// </summary>
    public static class EndpointRegistry
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public static string FilePathFor(int processId)
        {
            return Path.Combine(IpcConstants.EndpointDirectory,
                "revit-" + processId.ToString(CultureInfo.InvariantCulture) + ".json");
        }

        /// <summary>Called by the add-in when the bridge starts.</summary>
        public static void Publish(BridgeEndpoint endpoint)
        {
            if (endpoint == null) throw new ArgumentNullException("endpoint");
            Directory.CreateDirectory(IpcConstants.EndpointDirectory);
            string path = FilePathFor(endpoint.ProcessId);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, endpoint.ToJson().ToJson(true), Utf8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);   // atomic-ish publish so a reader never sees a half file
        }

        /// <summary>Called by the add-in when the bridge stops or Revit shuts down.</summary>
        public static void Withdraw(int processId)
        {
            try
            {
                string path = FilePathFor(processId);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>
        /// Lists live bridges, newest first. Descriptors whose process is gone are deleted as a
        /// side effect, so a crashed Revit never leaves a stale endpoint behind.
        /// </summary>
        public static List<BridgeEndpoint> Discover(string revitVersionFilter = null)
        {
            var result = new List<BridgeEndpoint>();
            string dir = IpcConstants.EndpointDirectory;
            if (!Directory.Exists(dir)) return result;

            string[] files;
            try { files = Directory.GetFiles(dir, "revit-*.json"); }
            catch (IOException) { return result; }
            catch (UnauthorizedAccessException) { return result; }

            foreach (string file in files)
            {
                BridgeEndpoint ep = null;
                try
                {
                    JsonValue v;
                    if (JsonValue.TryParse(File.ReadAllText(file, Utf8), out v))
                        ep = BridgeEndpoint.FromJson(v);
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                if (ep == null) { TryDelete(file); continue; }
                if (!IsProcessAlive(ep.ProcessId)) { TryDelete(file); continue; }
                if (ep.ProtocolVersion != IpcConstants.ProtocolVersion) continue;
                if (!string.IsNullOrEmpty(revitVersionFilter) &&
                    !string.Equals(ep.RevitVersion, revitVersionFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                result.Add(ep);
            }

            result.Sort(delegate (BridgeEndpoint a, BridgeEndpoint b)
            {
                return b.StartedUtc.CompareTo(a.StartedUtc);
            });
            return result;
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static bool IsProcessAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    return !p.HasExited;
                }
            }
            catch (ArgumentException) { return false; }   // no such process
            catch (InvalidOperationException) { return false; }
            catch (Exception) { return true; }            // e.g. access denied - assume alive
        }
    }
}
