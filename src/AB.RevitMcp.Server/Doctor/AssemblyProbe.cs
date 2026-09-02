using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace AB.RevitMcp.Server.Doctor
{
    /// <summary>What an assembly declares, read from metadata without ever loading it.</summary>
    public sealed class AssemblyFacts
    {
        public string Path;
        public string Name;
        public Version Version;
        public List<string> References = new List<string>();
        public bool ReferencesNetStandardFacade;
        public bool IsNetFramework;
        public bool IsNetCore;
        public string ReadError;

        public string RuntimeLabel
        {
            get
            {
                if (IsNetFramework) return ".NET Framework";
                if (IsNetCore) return ".NET (Core)";
                return "unknown";
            }
        }
    }

    /// <summary>
    /// Metadata-only assembly inspection.
    ///
    /// This is the safe way to answer "will Revit be able to load this?". Actually loading the
    /// assembly would drag in RevitAPI/RevitAPIUI and their native resource DLLs, which only
    /// resolve inside Revit.exe - outside it, Windows shows modal error dialogs.
    /// </summary>
    public static class AssemblyProbe
    {
        public static AssemblyFacts Read(string path)
        {
            var facts = new AssemblyFacts { Path = path };

            try
            {
                using (FileStream stream = File.OpenRead(path))
                using (var pe = new PEReader(stream))
                {
                    if (!pe.HasMetadata)
                    {
                        facts.ReadError = "not a managed assembly";
                        return facts;
                    }

                    MetadataReader reader = pe.GetMetadataReader();

                    AssemblyDefinition definition = reader.GetAssemblyDefinition();
                    facts.Name = reader.GetString(definition.Name);
                    facts.Version = definition.Version;

                    foreach (AssemblyReferenceHandle handle in reader.AssemblyReferences)
                    {
                        AssemblyReference reference = reader.GetAssemblyReference(handle);
                        string name = reader.GetString(reference.Name);
                        facts.References.Add(name);

                        if (string.Equals(name, "netstandard", StringComparison.OrdinalIgnoreCase))
                            facts.ReferencesNetStandardFacade = true;

                        // Which runtime an assembly targets is reliably indicated by its core
                        // reference: .NET Framework binds mscorlib, .NET (Core) binds System.Runtime.
                        if (string.Equals(name, "mscorlib", StringComparison.OrdinalIgnoreCase))
                            facts.IsNetFramework = true;
                        if (string.Equals(name, "System.Runtime", StringComparison.OrdinalIgnoreCase))
                            facts.IsNetCore = true;
                    }

                    // A netstandard2.0 library references ONLY the facade - neither core library.
                    if (facts.ReferencesNetStandardFacade && !facts.IsNetFramework && !facts.IsNetCore)
                    {
                        facts.IsNetFramework = false;
                        facts.IsNetCore = false;
                    }
                }
            }
            catch (BadImageFormatException ex)
            {
                facts.ReadError = "not a valid PE/managed image: " + ex.Message;
            }
            catch (IOException ex)
            {
                facts.ReadError = "could not read: " + ex.Message;
            }
            catch (UnauthorizedAccessException ex)
            {
                facts.ReadError = "access denied: " + ex.Message;
            }

            return facts;
        }
    }
}
