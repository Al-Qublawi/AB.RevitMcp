using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Tools;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AB.RevitMcp.Server.Bridge
{
    /// <summary>
    /// Compiles revit_execute_code snippets for Revit 2025 and newer, in THIS process.
    ///
    /// Revit 2025+ run on .NET 8 / .NET 10, which have no in-box C# compiler. Shipping Roslyn into
    /// Revit.exe would put a third copy beside the ones other add-ins already load there, so the
    /// server compiles instead and the add-in only loads the finished assembly. The references are
    /// the running Revit's own framework and API assemblies, which the bridge reports through
    /// bridge/compile-context; they are read as metadata only, never loaded or executed here.
    /// </summary>
    public static class SnippetCompiler
    {
        public sealed class Outcome
        {
            public bool Success;
            public byte[] Assembly;
            public string Sha256;
            public JsonValue Errors = JsonValue.NewArray();
            public string Failure;
        }

        /// <summary>e.g. "Roslyn 4.14.0.0 (C# 13.0)". Touching it loads Roslyn, so only call it when compiling.</summary>
        public static string CompilerDescription
        {
            get
            {
                return "Roslyn " + typeof(CSharpCompilation).Assembly.GetName().Version + " (C# " +
                       LanguageVersion.Latest.MapSpecifiedToEffectiveVersion().ToDisplayString() + ")";
            }
        }

        private static readonly object CacheLock = new object();
        private static string _cacheKey;
        private static List<MetadataReference> _cachedReferences;

        public static Outcome Compile(string code, IEnumerable<string> usings, JsonValue context)
        {
            var outcome = new Outcome();

            List<MetadataReference> references;
            string why;
            if (!TryReferences(context, out references, out why))
            {
                outcome.Failure = why;
                return outcome;
            }

            int preludeLines;
            string source = CodeTemplate.Build(code, usings, out preludeLines);

            SyntaxTree tree = CSharpSyntaxTree.ParseText(source,
                new CSharpParseOptions(LanguageVersion.Latest), "snippet.cs", Encoding.UTF8);

            CSharpCompilation compilation = CSharpCompilation.Create(
                "AbMcpSnippet_" + Guid.NewGuid().ToString("N"),
                new[] { tree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release,
                    platform: Platform.AnyCpu,
                    allowUnsafe: false));

            using (var stream = new MemoryStream())
            {
                Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(stream);

                if (!result.Success)
                {
                    foreach (Diagnostic d in result.Diagnostics)
                    {
                        if (d.Severity != DiagnosticSeverity.Error) continue;
                        FileLinePositionSpan span = d.Location.GetLineSpan();
                        outcome.Errors.Add(J.O(
                            "line", CodeTemplate.SnippetLine(span.StartLinePosition.Line + 1, preludeLines),
                            "column", span.StartLinePosition.Character + 1,
                            "code", d.Id,
                            "message", d.GetMessage()));
                        if (outcome.Errors.Count >= 25) break;
                    }
                    return outcome;
                }

                outcome.Assembly = stream.ToArray();
            }

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(outcome.Assembly);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                outcome.Sha256 = sb.ToString();
            }

            outcome.Success = true;
            return outcome;
        }

        /// <summary>
        /// Metadata references for the running Revit: every managed DLL in the framework folders it
        /// reported (the first file of a name wins), plus RevitAPI / RevitAPIUI. Cached per set.
        /// </summary>
        private static bool TryReferences(JsonValue context, out List<MetadataReference> references, out string why)
        {
            references = null;
            why = null;

            var dirs = new List<string>();
            foreach (JsonValue d in context["referenceDirectories"].Items) { string s = d.AsString(null); if (!string.IsNullOrEmpty(s)) dirs.Add(s); }
            var files = new List<string>();
            foreach (JsonValue f in context["referenceFiles"].Items) { string s = f.AsString(null); if (!string.IsNullOrEmpty(s)) files.Add(s); }

            if (dirs.Count == 0 || files.Count == 0)
            {
                why = "The Revit bridge did not report its reference assemblies.";
                return false;
            }

            string key = string.Join("|", dirs) + "||" + string.Join("|", files);
            lock (CacheLock)
            {
                if (_cacheKey == key && _cachedReferences != null)
                {
                    references = _cachedReferences;
                    return true;
                }

                var list = new List<MetadataReference>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string file in files)
                {
                    if (!File.Exists(file)) { why = "Reference '" + file + "' is not readable from the MCP server."; return false; }
                    if (seen.Add(Path.GetFileName(file))) list.Add(MetadataReference.CreateFromFile(file));
                }

                foreach (string dir in dirs)
                {
                    if (!Directory.Exists(dir)) { why = "Framework folder '" + dir + "' is not readable from the MCP server."; return false; }
                    foreach (string file in Directory.GetFiles(dir, "*.dll"))
                    {
                        if (seen.Contains(Path.GetFileName(file)) || !IsManagedAssembly(file)) continue;
                        seen.Add(Path.GetFileName(file));
                        list.Add(MetadataReference.CreateFromFile(file));
                    }
                }

                _cacheKey = key;
                _cachedReferences = list;
                references = list;
                return true;
            }
        }

        /// <summary>The framework folders also hold native DLLs (coreclr.dll, ...); only assemblies are references.</summary>
        private static bool IsManagedAssembly(string path)
        {
            try
            {
                using (FileStream stream = File.OpenRead(path))
                using (var pe = new PEReader(stream))
                {
                    if (!pe.HasMetadata) return false;
                    return pe.GetMetadataReader().IsAssembly;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
