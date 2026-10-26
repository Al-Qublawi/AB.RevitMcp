using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AB.RevitMcp.Addin.Revit;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;

namespace AB.RevitMcp.Addin.Tools
{
    /// <summary>
    /// Loads development builds for revit_run_assembly_method - the pattern the AB add-ins' bridge
    /// recipes used by hand (tools\revit-bridge\run-all.cs), done once, properly:
    ///
    ///   * from BYTES, so the file is never locked and the next build can overwrite it;
    ///   * the .pdb beside it too, so a failing test reports file and line numbers;
    ///   * the same bytes again are REUSED - Revit never unloads an assembly;
    ///   * different bytes under an identity already loaded are REFUSED. Two copies of one
    ///     identity bind unpredictably (dependencies, WPF resources resolve by name), which is
    ///     why every dev build needs its own version - and why a forgotten version bump must be
    ///     an error rather than a test quietly running yesterday's code;
    ///   * other DLLs in the same folder are loaded on demand, by exact identity, when the
    ///     loaded code asks for them (run-all.cs hooked AssemblyResolve for Core by hand).
    /// </summary>
    internal static class DevAssemblies
    {
        private sealed class Loaded
        {
            public Assembly Assembly;
            public string Sha256;
            public string Path;
        }

        private static readonly object Lock = new object();
        private static readonly Dictionary<string, Loaded> ByFullName = new Dictionary<string, Loaded>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> Folders = new List<string>();
        private static bool _hooked;

        public static string Sha256(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>Loads (or reuses) the assembly at <paramref name="path"/>.</summary>
        public static Assembly Load(string path, out bool reused, out string sha256, out AssemblyName identity)
        {
            identity = ReadIdentity(path, "assemblyPath");
            byte[] bytes = File.ReadAllBytes(path);
            sha256 = Sha256(bytes);
            string folder = System.IO.Path.GetDirectoryName(path);

            lock (Lock)
            {
                if (!_hooked)
                {
                    AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
                    _hooked = true;
                }

                CheckFolderForStaleIdentities(folder);

                Loaded existing;
                if (ByFullName.TryGetValue(identity.FullName, out existing))
                {
                    if (string.Equals(existing.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        reused = true;
                        AddFolder(folder);
                        return existing.Assembly;
                    }
                    throw StaleIdentity(identity, path, existing.Path);
                }

                // Same identity loaded by Revit itself (an installed release of the add-in, say):
                // two copies would split every type in two.
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string fullName;
                    try { fullName = a.FullName; } catch (Exception) { continue; }
                    if (!string.Equals(fullName, identity.FullName, StringComparison.OrdinalIgnoreCase)) continue;
                    string where = null;
                    try { where = a.IsDynamic ? null : a.Location; } catch (Exception) { }
                    throw new ToolException(BridgeErrorCodes.InvalidArgument,
                        identity.Name + " " + identity.Version + " is already loaded in this Revit session" +
                        (string.IsNullOrEmpty(where) ? " (from memory)" : " from " + where) + ", not by this tool - " +
                        "probably the installed release. Give the dev build its own assembly version.",
                        J.O("assembly", identity.FullName, "alreadyLoadedFrom", where));
                }

                AddFolder(folder);
                Assembly assembly = LoadBytes(bytes, path);
                ByFullName[identity.FullName] = new Loaded { Assembly = assembly, Sha256 = sha256, Path = path };
                reused = false;
                return assembly;
            }
        }

        private static Assembly LoadBytes(byte[] bytes, string path)
        {
            string pdb = System.IO.Path.ChangeExtension(path, ".pdb");
            if (File.Exists(pdb))
            {
                try { return Assembly.Load(bytes, File.ReadAllBytes(pdb)); }
                catch (BadImageFormatException) { }   // a pdb the runtime cannot use - load without it
            }
            return Assembly.Load(bytes);
        }

        private static AssemblyName ReadIdentity(string path, string argument)
        {
            try
            {
                return AssemblyName.GetAssemblyName(path);
            }
            catch (BadImageFormatException)
            {
                throw ToolException.Invalid(argument, "'" + path + "' is not a .NET assembly.");
            }
            catch (FileNotFoundException)
            {
                throw ToolException.Invalid(argument, "'" + path + "' does not exist.");
            }
        }

        private static ToolException StaleIdentity(AssemblyName identity, string path, string loadedFrom)
        {
            return new ToolException(BridgeErrorCodes.InvalidArgument,
                identity.Name + " " + identity.Version + " was already loaded in this Revit session from DIFFERENT " +
                "bytes (" + loadedFrom + "). Revit never unloads an assembly, so a rebuilt dev copy needs a new " +
                "assembly version (e.g. .\\tools\\devbuild.ps1 -N <next number>) - otherwise the test would run the " +
                "old code.",
                J.O("assembly", identity.FullName, "path", path, "loadedFrom", loadedFrom));
        }

        /// <summary>A dependency in this folder that was rebuilt without a version bump is caught up front.</summary>
        private static void CheckFolderForStaleIdentities(string folder)
        {
            string[] files;
            try { files = Directory.GetFiles(folder, "*.dll"); } catch (Exception) { return; }

            foreach (string file in files)
            {
                AssemblyName name;
                try { name = AssemblyName.GetAssemblyName(file); } catch (Exception) { continue; }

                // Only identities this class already loaded can be stale; compare their bytes.
                Loaded existing;
                if (!ByFullName.TryGetValue(name.FullName, out existing)) continue;

                string sha;
                try { sha = Sha256(File.ReadAllBytes(file)); } catch (Exception) { continue; }
                if (!string.Equals(sha, existing.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw StaleIdentity(name, file, existing.Path);
            }
        }

        private static void AddFolder(string folder)
        {
            foreach (string f in Folders) if (string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)) return;
            Folders.Add(folder);
        }

        /// <summary>
        /// Resolves a dependency of a dev build: one already loaded by this class, or a DLL of exactly
        /// that identity in a dev folder. Anything else is left to Revit (returns null).
        /// </summary>
        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                var wanted = new AssemblyName(args.Name);
                lock (Lock)
                {
                    foreach (Loaded l in ByFullName.Values)
                    {
                        AssemblyName have = l.Assembly.GetName();
                        if (string.Equals(have.Name, wanted.Name, StringComparison.OrdinalIgnoreCase) &&
                            (wanted.Version == null || wanted.Version.Equals(have.Version)))
                            return l.Assembly;
                    }

                    foreach (string folder in Folders)
                    {
                        string candidate = System.IO.Path.Combine(folder, wanted.Name + ".dll");
                        if (!File.Exists(candidate)) continue;

                        AssemblyName name;
                        try { name = AssemblyName.GetAssemblyName(candidate); } catch (Exception) { continue; }
                        if (wanted.Version != null && !wanted.Version.Equals(name.Version)) continue;
                        if (ByFullName.ContainsKey(name.FullName)) continue;   // a different copy - see CheckFolder

                        byte[] bytes = File.ReadAllBytes(candidate);
                        Assembly assembly = LoadBytes(bytes, candidate);
                        ByFullName[name.FullName] = new Loaded { Assembly = assembly, Sha256 = Sha256(bytes), Path = candidate };
                        return assembly;
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>What has been loaded, for the tool's result.</summary>
        public static JsonValue Snapshot()
        {
            JsonValue list = JsonValue.NewArray();
            lock (Lock)
            {
                foreach (KeyValuePair<string, Loaded> kv in ByFullName)
                    list.Add(J.O("assembly", kv.Key, "path", kv.Value.Path));
            }
            return list;
        }
    }
}
