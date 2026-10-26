using System.Collections.Generic;
using System.Text;

namespace AB.RevitMcp.Contracts.Tools
{
    /// <summary>
    /// The source file a revit_execute_code snippet is compiled into. Shared so that the in-Revit
    /// compiler (Revit 2020-2024) and the MCP server's compiler (Revit 2025+) build exactly the
    /// same program from the same snippet, and report compile errors on the same line numbers.
    ///
    /// The snippet is the BODY of
    ///     public object Execute(UIApplication uiApp, Document doc)
    /// in class AbMcpGenerated.Script.
    /// </summary>
    public static class CodeTemplate
    {
        public const string TypeName = "AbMcpGenerated.Script";
        public const string MethodName = "Execute";

        /// <summary>Namespaces every snippet gets without asking.</summary>
        public static readonly string[] DefaultUsings =
        {
            "System",
            "System.Collections",
            "System.Collections.Generic",
            "System.Linq",
            "Autodesk.Revit.DB",
            "Autodesk.Revit.DB.Architecture",
            "Autodesk.Revit.DB.Mechanical",
            "Autodesk.Revit.DB.Plumbing",
            "Autodesk.Revit.DB.Electrical",
            "Autodesk.Revit.DB.Structure",
            "Autodesk.Revit.UI"
        };

        /// <summary>
        /// Builds the full source. <paramref name="preludeLines"/> is the number of template lines
        /// before the snippet's first line, for mapping compiler line numbers back to the snippet.
        /// </summary>
        public static string Build(string body, IEnumerable<string> extraUsings, out int preludeLines)
        {
            var sb = new StringBuilder();
            foreach (string ns in DefaultUsings) sb.Append("using ").Append(ns).Append(";\n");

            if (extraUsings != null)
            {
                foreach (string ns in extraUsings)
                {
                    if (string.IsNullOrEmpty(ns)) continue;
                    // Keep the generated file well-formed no matter what arrives.
                    if (ns.IndexOf(';') >= 0 || ns.IndexOf('{') >= 0 || ns.IndexOf('\n') >= 0) continue;
                    sb.Append("using ").Append(ns.Trim()).Append(";\n");
                }
            }

            sb.Append("namespace AbMcpGenerated {\n");
            sb.Append("  public class Script {\n");
            sb.Append("    public object Execute(UIApplication uiApp, Document doc) {\n");

            preludeLines = 0;
            for (int i = 0; i < sb.Length; i++) if (sb[i] == '\n') preludeLines++;

            sb.Append(body ?? string.Empty).Append('\n');
            sb.Append("    }\n");
            sb.Append("  }\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>Maps a 1-based line of the generated file to the snippet's own line numbering.</summary>
        public static int SnippetLine(int generatedLine, int preludeLines)
        {
            int adjusted = generatedLine - preludeLines;
            return adjusted > 0 ? adjusted : generatedLine;
        }
    }
}
