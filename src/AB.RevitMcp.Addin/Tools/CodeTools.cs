using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using AB.RevitMcp.Addin.Bridge;
using AB.RevitMcp.Addin.Revit;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;
using Autodesk.Revit.DB;
#if !NET8_0_OR_GREATER
using System.CodeDom.Compiler;
using Microsoft.CSharp;
#endif

namespace AB.RevitMcp.Addin.Tools
{
    /// <summary>
    /// The escape hatch: compile and run arbitrary Revit API C#.
    ///
    /// Three independent gates gate it (server flag, add-in setting, per-call confirm), because
    /// unlike every other tool here this one has no schema to validate against and no bound on
    /// what it can do to the model.
    ///
    /// Only .NET Framework builds can do this. Revit 2020-2024 host .NET Framework, whose in-box
    /// CSharpCodeProvider compiles without any extra dependency. Revit 2025+ run on .NET 8, where
    /// compilation would mean shipping Roslyn INTO the Revit process - the same class of assembly
    /// conflict that caused the original "assembly does not exist" failure, and Revit already
    /// loads its own Roslyn for Dynamo. Not worth it; those releases get a clear refusal.
    /// </summary>
    public static class CodeTools
    {
        /// <summary>Reads the add-in's own gate. Off unless explicitly enabled.</summary>
        public static bool IsEnabled()
        {
            try
            {
                string env = Environment.GetEnvironmentVariable("AB_REVITMCP_ALLOW_CODE");
                if (!string.IsNullOrEmpty(env))
                    return env == "1" || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase);
                return BridgeService.ReadAllowCodeExecution();
            }
            catch (Exception) { return false; }
        }

        public static JsonValue ExecuteCode(ToolContext ctx)
        {
            // Gate 2 of 3. (Gate 1 is the server's --allow-code-execution; gate 3 is the
            // confirm:true the router already enforced for every destructive tool.)
            if (!IsEnabled())
            {
                throw new ToolException(BridgeErrorCodes.ConfirmationRequired,
                    "Code execution is DISABLED in this Revit session. A human must turn it on from " +
                    "the AB MCP AI ribbon tab (\"Code exec\" button) before this tool will run. " +
                    "Prefer one of the other " + (Contracts.Tools.ToolCatalog.Count - 1) +
                    " tools - they are schema-validated and far safer.");
            }

            string code = Args.Str(ctx.Args, "code", true);
            string description = Args.Str(ctx.Args, "description");
            List<string> extraUsings = Args.StrList(ctx.Args, "usings", 30);

            if (code.Trim().Length == 0)
                throw ToolException.Invalid("code", "no code was supplied.");

            ctx.Log.Warn("revit_execute_code invoked", J.O(
                "description", description,
                "codeLength", code.Length,
                "revit", Compat.RevitReleaseName));

#if NET8_0_OR_GREATER
            throw new ToolException(BridgeErrorCodes.RevitApi,
                "Code execution is not available on Revit " + Compat.RevitReleaseName + ". " +
                "Revit 2025 and newer run on .NET 8, which has no in-box C# compiler, and shipping " +
                "a compiler into the Revit process risks assembly conflicts with Revit's own. " +
                "Use Revit 2020-2024 for this tool, or ask for a dedicated tool to be added.");
#else
            string source = BuildSource(code, extraUsings);

            using (var provider = new CSharpCodeProvider())
            {
                CompilerParameters parameters = BuildCompilerParameters();
                CompilerResults results = provider.CompileAssemblyFromSource(parameters, source);

                if (results.Errors.HasErrors)
                {
                    JsonValue errors = JsonValue.NewArray();
                    foreach (CompilerError error in results.Errors)
                    {
                        if (error.IsWarning) continue;
                        errors.Add(J.O(
                            "line", AdjustLine(error.Line),
                            "code", error.ErrorNumber,
                            "message", error.ErrorText));
                        if (errors.Count >= 25) break;
                    }

                    throw new ToolException(BridgeErrorCodes.InvalidArgument,
                        "The code did not compile. Line numbers are relative to your snippet.",
                        J.O("errors", errors,
                            "note", "The snippet is the body of: object Execute(UIApplication uiApp, " +
                                    "Document doc). A transaction is already open."));
                }

                object result = null;
                var sw = System.Diagnostics.Stopwatch.StartNew();

                ctx.InTransaction(string.IsNullOrEmpty(description) ? "Execute code" : description, delegate
                {
                    Type type = results.CompiledAssembly.GetType("AbMcpGenerated.Script");
                    if (type == null)
                        throw new ToolException(BridgeErrorCodes.Internal, "The generated script type was not found.");

                    object instance = Activator.CreateInstance(type);
                    MethodInfo method = type.GetMethod("Execute");

                    try
                    {
                        result = method.Invoke(instance, new object[] { ctx.UiApp, ctx.Doc });
                    }
                    catch (TargetInvocationException ex)
                    {
                        // Surface the real exception, not the reflection wrapper.
                        Exception inner = ex.InnerException ?? ex;
                        throw new ToolException(BridgeErrorCodes.RevitApi,
                            "The code compiled but threw at runtime: " +
                            inner.GetType().Name + ": " + inner.Message,
                            J.O("stack", inner.StackTrace));
                    }
                });

                sw.Stop();

                return J.O(
                    "executed", true,
                    "description", description,
                    "durationMs", Metric.R(sw.Elapsed.TotalMilliseconds, 1),
                    "returned", Describe(result, ctx.Doc, 0),
                    "note", "Compiled and run against the live model. Undo with Ctrl+Z in Revit.");
            }
#endif
        }

#if !NET8_0_OR_GREATER
        /// <summary>Number of template lines before the caller's first line, for error mapping.</summary>
        private static int _preludeLines;

        private static int AdjustLine(int compilerLine)
        {
            int adjusted = compilerLine - _preludeLines;
            return adjusted > 0 ? adjusted : compilerLine;
        }

        private static string BuildSource(string body, List<string> extraUsings)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using Autodesk.Revit.DB;");
            sb.AppendLine("using Autodesk.Revit.DB.Architecture;");
            sb.AppendLine("using Autodesk.Revit.DB.Mechanical;");
            sb.AppendLine("using Autodesk.Revit.DB.Plumbing;");
            sb.AppendLine("using Autodesk.Revit.DB.Electrical;");
            sb.AppendLine("using Autodesk.Revit.DB.Structure;");
            sb.AppendLine("using Autodesk.Revit.UI;");

            foreach (string ns in extraUsings)
            {
                if (string.IsNullOrEmpty(ns)) continue;
                // Keep the generated file well-formed no matter what arrives.
                if (ns.IndexOf(';') >= 0 || ns.IndexOf('{') >= 0) continue;
                sb.AppendLine("using " + ns + ";");
            }

            sb.AppendLine("namespace AbMcpGenerated {");
            sb.AppendLine("  public class Script {");
            sb.AppendLine("    public object Execute(UIApplication uiApp, Document doc) {");

            _preludeLines = 0;
            foreach (char c in sb.ToString()) if (c == '\n') _preludeLines++;

            sb.AppendLine(body);
            sb.AppendLine("    }");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static CompilerParameters BuildCompilerParameters()
        {
            var parameters = new CompilerParameters
            {
                GenerateInMemory = true,
                GenerateExecutable = false,
                TreatWarningsAsErrors = false,
                CompilerOptions = "/optimize /nowarn:1701,1702"
            };

            // Reference exactly what the running Revit already has loaded, so the generated code
            // binds against the same RevitAPI the add-in does - never a second copy.
            var wanted = new[]
            {
                "mscorlib", "System", "System.Core", "RevitAPI", "RevitAPIUI"
            };

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name;
                try { name = assembly.GetName().Name; }
                catch (Exception) { continue; }

                if (Array.IndexOf(wanted, name) < 0) continue;

                try
                {
                    if (assembly.IsDynamic) continue;
                    string location = assembly.Location;
                    if (string.IsNullOrEmpty(location)) continue;
                    if (!parameters.ReferencedAssemblies.Contains(location))
                        parameters.ReferencedAssemblies.Add(location);
                }
                catch (Exception) { }
            }

            return parameters;
        }
#endif

        /// <summary>
        /// Turns whatever the snippet returned into JSON. Revit objects are reduced to something
        /// readable rather than reflected over, and collections are capped - a snippet that
        /// returns every element in the model must not blow the frame budget.
        /// </summary>
        private static JsonValue Describe(object value, Document doc, int depth)
        {
            if (value == null) return JsonValue.Null;
            if (depth > 3) return J.S(value.ToString());

            if (value is string) return J.S((string)value);
            if (value is bool) return J.B((bool)value);
            if (value is int || value is long || value is short || value is byte)
                return J.N(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            if (value is double || value is float || value is decimal)
                return J.N(Convert.ToDouble(value, CultureInfo.InvariantCulture));

            var elementId = value as ElementId;
            if (elementId != null)
            {
                JsonValue o = J.O("elementId", Compat.IdValue(elementId));
                Element referenced = doc != null ? doc.GetElement(elementId) : null;
                if (referenced != null) o.Set("name", ElementSerializer.SafeName(referenced));
                return o;
            }

            var xyz = value as XYZ;
            if (xyz != null) return Metric.PointToMm(xyz);

            var element = value as Element;
            if (element != null)
                return ElementSerializer.Summarize(element, new SerializeOptions { IncludeLocation = true });

            var enumerable = value as IEnumerable;
            if (enumerable != null)
            {
                JsonValue array = JsonValue.NewArray();
                int count = 0, total = 0;
                foreach (object item in enumerable)
                {
                    total++;
                    if (count < 100) { array.Add(Describe(item, doc, depth + 1)); count++; }
                }
                if (total > count)
                    return J.O("items", array, "returned", count, "total", total,
                               "truncated", true,
                               "hint", "Return a projection or a count instead of every element.");
                return array;
            }

            return J.S(value.ToString());
        }
    }
}
