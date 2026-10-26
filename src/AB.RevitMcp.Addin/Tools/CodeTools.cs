using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using AB.RevitMcp.Addin.Bridge;
using AB.RevitMcp.Addin.Revit;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;
using AB.RevitMcp.Contracts.Tools;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
#if !NET8_0_OR_GREATER
using System.CodeDom.Compiler;
using Microsoft.CSharp;
#endif

namespace AB.RevitMcp.Addin.Tools
{
    /// <summary>
    /// The escape hatch: compile and run arbitrary Revit API C#.
    ///
    /// Three independent gates guard it (server flag, ribbon switch for this Revit session,
    /// per-call confirm - see CodeGates), because unlike every other tool here it has no schema to
    /// validate against and no bound on what it can do to the model.
    ///
    /// WHERE THE SNIPPET IS COMPILED
    ///   Revit 2020-2024 (.NET Framework): here, by the in-box CSharpCodeProvider (C# 5), as always.
    ///   Revit 2025+ (.NET 8 / .NET 10): by the MCP SERVER, which ships Roslyn in its own process
    ///     and sends the compiled assembly in the request payload. Nothing compiler-related ever
    ///     loads into Revit.exe - which matters, because other add-ins already load their own
    ///     Roslyn there (pyRevit 4.11, another bridge 4.8) and a third copy bound by simple name
    ///     would pick up whichever came first.
    ///
    /// TRANSACTION MODES
    ///   "active" (default) - the snippet runs inside a transaction group + transaction on the
    ///                        active document: one undo step, rolled back on any exception.
    ///   "none"             - nothing is opened; doc may be null. For code that opens, changes,
    ///                        saves and closes BACKGROUND documents with transactions of its own,
    ///                        which an open transaction on the active document gets in the way of.
    /// </summary>
    public static class CodeTools
    {
        /// <summary>Gate 2: the ribbon switch for this Revit session, or AB_REVITMCP_ALLOW_CODE in Revit's environment.</summary>
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

        private static string GateSource()
        {
            string env = null;
            try { env = Environment.GetEnvironmentVariable("AB_REVITMCP_ALLOW_CODE"); } catch (Exception) { }
            return string.IsNullOrEmpty(env)
                ? "ribbon switch (this Revit session only)"
                : "AB_REVITMCP_ALLOW_CODE=" + env + " in Revit's environment (overrides the ribbon switch)";
        }

#if NET8_0_OR_GREATER
        private const string CompiledBy = "the MCP server (Roslyn)";
#else
        private const string CompiledBy = "Revit (in-box CodeDom compiler, C# 5)";
#endif

        /// <summary>Gate 2 and compiler, for revit_bridge_status. Safe off the UI thread.</summary>
        public static JsonValue GateStatus()
        {
            return J.O(
                "tools", J.A(ToolCatalog.ExecuteCodeToolName, ToolCatalog.RunAssemblyMethodToolName),
                "gate2RevitSwitch", IsEnabled() ? "on" : "off",
                "gate2Source", GateSource(),
                "snippetsCompiledBy", CompiledBy,
                "note", "Gate 1 is the MCP server's --allow-code-execution flag (reported by the server); " +
                        "gate 3 is \"confirm\": true on each call.");
        }

        /// <summary>
        /// Answers bridge/compile-context: what the MCP server needs to compile for this Revit.
        /// Runs OFF the UI thread - reflection over loaded assemblies only, no Revit API calls.
        /// </summary>
        public static JsonValue CompileContext()
        {
            JsonValue o = J.O(
                "addinVersion", typeof(CodeTools).Assembly.GetName().Version.ToString(),
                "revitRelease", Compat.RevitReleaseName,
                "codeExecutionAllowed", IsEnabled(),
                "gate2Source", GateSource(),
                "templateType", CodeTemplate.TypeName);
#if NET8_0_OR_GREATER
            o.Set("compileMode", "server");
            o.Set("runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);

            // Reference the framework the running Revit actually uses (2026.5 runs .NET 10 even
            // though the add-in targets net8), WPF beside it, and Revit's own API assemblies.
            var dirs = new List<string>();
            AddDir(dirs, typeof(object).Assembly.Location);
            AddDir(dirs, typeof(System.Windows.Window).Assembly.Location);
            o.Set("referenceDirectories", J.AStrings(dirs));
            o.Set("referenceFiles", J.AStrings(new[]
            {
                typeof(Document).Assembly.Location,
                typeof(UIApplication).Assembly.Location
            }));
#else
            o.Set("compileMode", "in-process");
#endif
            return o;
        }

#if NET8_0_OR_GREATER
        private static void AddDir(List<string> dirs, string assemblyLocation)
        {
            if (string.IsNullOrEmpty(assemblyLocation)) return;
            string dir = Path.GetDirectoryName(assemblyLocation);
            foreach (string d in dirs) if (string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)) return;
            dirs.Add(dir);
        }
#endif

        // ==================================================================
        //  revit_execute_code
        // ==================================================================
        public static JsonValue ExecuteCode(ToolContext ctx)
        {
            // Gate 2 again - the router checked it, but this handler must never run without it.
            if (!IsEnabled())
                throw new ToolException(BridgeErrorCodes.GateClosed,
                    CodeGates.Refusal(ctx.Tool.Name, 2, CodeGates.State.Open, CodeGates.State.Closed, CodeGates.State.Open));

            string code = Args.Str(ctx.Args, "code", true);
            string description = Args.Str(ctx.Args, "description");
            List<string> extraUsings = Args.StrList(ctx.Args, "usings", 30);
            string mode = Args.Str(ctx.Args, "transaction", false, "active", new[] { "active", "none" });
            int maxInline = Args.Int(ctx.Args, "maxInlineChars", false, IpcConstants.DefaultInlineTextChars, 1000, 1000000);

            if (code.Trim().Length == 0)
                throw ToolException.Invalid("code", "no code was supplied.");

            ctx.Log.Warn("revit_execute_code invoked", J.O(
                "description", description,
                "codeLength", code.Length,
                "transaction", mode,
                "revit", Compat.RevitReleaseName));

            Func<UIApplication, Document, object> run = Prepare(ctx, code, extraUsings);

            if (mode == "active")
            {
                // Exactly the pre-1.6.0 behaviour: one transaction group (one undo step) around one
                // transaction on the active document, rolled back on any exception.
                return ToolRouter.RunTransacted(ctx, ctx.Tool, delegate (ToolContext c)
                {
                    object returned = null;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    c.InTransaction(string.IsNullOrEmpty(description) ? "Execute code" : description, delegate
                    {
                        returned = Invoke(run, c.UiApp, c.Doc, false);
                    });
                    sw.Stop();
                    return J.O(
                        "executed", true,
                        "description", description,
                        "transaction", "active",
                        "durationMs", Metric.R(sw.Elapsed.TotalMilliseconds, 1),
                        "returned", Describe(returned, c.Doc, 0, maxInline, c),
                        "compiledBy", CompiledBy,
                        "note", "Compiled and run against the active model in one transaction. Undo with Ctrl+Z in Revit.");
                });
            }

            Document doc = ctx.TryGetDoc();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            object result = Invoke(run, ctx.UiApp, doc, true);
            watch.Stop();

            return J.O(
                "executed", true,
                "description", description,
                "transaction", "none",
                "durationMs", Metric.R(watch.Elapsed.TotalMilliseconds, 1),
                "returned", Describe(result, doc, 0, maxInline, ctx),
                "compiledBy", CompiledBy,
                "note", "Run with no transaction opened by the bridge. Whatever the code committed itself " +
                        "stays committed; documents it opened stay open unless it closed them.");
        }

        /// <summary>Calls the compiled snippet, surfacing the snippet's own exception rather than the reflection wrapper.</summary>
        private static object Invoke(Func<UIApplication, Document, object> run, UIApplication app, Document doc, bool noTransaction)
        {
            try
            {
                return run(app, doc);
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                throw new ToolException(BridgeErrorCodes.RevitApi,
                    "The code compiled but threw at runtime: " + inner.GetType().Name + ": " + inner.Message +
                    (noTransaction ? " (transaction \"none\": anything the code committed before this stays committed)" : string.Empty),
                    J.O("exception", inner.GetType().FullName, "stack", inner.StackTrace));
            }
        }

#if NET8_0_OR_GREATER
        /// <summary>Revit 2025+: load the assembly the MCP server compiled.</summary>
        private static Func<UIApplication, Document, object> Prepare(ToolContext ctx, string code, List<string> usings)
        {
            JsonValue payload = ctx.Payload ?? JsonValue.Null;
            string base64 = payload["assembly"].AsString(null);
            if (string.IsNullOrEmpty(base64))
            {
                throw new ToolException(BridgeErrorCodes.InvalidArgument,
                    "This call reached Revit " + Compat.RevitReleaseName + " without a compiled assembly. Revit 2025 " +
                    "and newer have no C# compiler of their own; the AB Revit MCP server compiles the snippet " +
                    "(server 1.6.0 or newer). Point the AI client at an up-to-date AB.RevitMcp.Server and retry.");
            }

            byte[] bytes;
            try { bytes = Convert.FromBase64String(base64); }
            catch (FormatException) { throw ToolException.Invalid("payload", "the compiled assembly is not valid base64."); }

            string expected = payload["sha256"].AsString(null);
            if (!string.IsNullOrEmpty(expected) && !string.Equals(expected, DevAssemblies.Sha256(bytes), StringComparison.OrdinalIgnoreCase))
                throw ToolException.Invalid("payload", "the compiled assembly does not match its SHA-256.");

            // Assembly.Load(byte[]) gives each snippet its own load context; RevitAPI and the
            // framework resolve to the copies Revit already has loaded.
            Assembly assembly = Assembly.Load(bytes);
            return Bind(assembly);
        }
#else
        /// <summary>Revit 2020-2024: compile here with the in-box compiler, exactly as before 1.6.0.</summary>
        private static Func<UIApplication, Document, object> Prepare(ToolContext ctx, string code, List<string> usings)
        {
            int preludeLines;
            string source = CodeTemplate.Build(code, usings, out preludeLines);

            using (var provider = new CSharpCodeProvider())
            {
                CompilerResults results = provider.CompileAssemblyFromSource(BuildCompilerParameters(), source);

                if (results.Errors.HasErrors)
                {
                    JsonValue errors = JsonValue.NewArray();
                    foreach (CompilerError error in results.Errors)
                    {
                        if (error.IsWarning) continue;
                        errors.Add(J.O(
                            "line", CodeTemplate.SnippetLine(error.Line, preludeLines),
                            "code", error.ErrorNumber,
                            "message", error.ErrorText));
                        if (errors.Count >= 25) break;
                    }

                    throw new ToolException(BridgeErrorCodes.InvalidArgument,
                        "The code did not compile. Line numbers are relative to your snippet.",
                        J.O("errors", errors,
                            "note", "The snippet is the body of: object Execute(UIApplication uiApp, " +
                                    "Document doc). Revit " + Compat.RevitReleaseName + " compiles C# 5."));
                }

                return Bind(results.CompiledAssembly);
            }
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
            var wanted = new[] { "mscorlib", "System", "System.Core", "RevitAPI", "RevitAPIUI" };

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

        private static Func<UIApplication, Document, object> Bind(Assembly assembly)
        {
            Type type = assembly.GetType(CodeTemplate.TypeName);
            MethodInfo method = type != null ? type.GetMethod(CodeTemplate.MethodName) : null;
            if (method == null)
                throw new ToolException(BridgeErrorCodes.Internal, "The generated script type was not found.");

            return delegate (UIApplication app, Document doc)
            {
                object instance = Activator.CreateInstance(type);
                return method.Invoke(instance, new object[] { app, doc });
            };
        }

        // ==================================================================
        //  Returned values
        // ==================================================================

        /// <summary>
        /// Turns whatever the code returned into JSON. Revit objects are reduced to something
        /// readable rather than reflected over, and collections are capped - code that returns
        /// every element in the model must not blow the frame budget. A top-level string longer
        /// than maxInline is written in full to a file and shortened inline.
        /// </summary>
        internal static JsonValue Describe(object value, Document doc, int depth, int maxInline, ToolContext ctx)
        {
            if (value == null) return JsonValue.Null;
            if (depth > 3) return J.S(value.ToString());

            string text = value as string;
            if (text != null)
                return depth == 0 && text.Length > maxInline ? LongText(text, maxInline, ctx) : J.S(text);
            if (value is bool) return J.B((bool)value);
            if (value is int || value is long || value is short || value is byte)
                return J.N(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            if (value is double || value is float || value is decimal)
                return J.N(Convert.ToDouble(value, CultureInfo.InvariantCulture));

            var elementId = value as ElementId;
            if (elementId != null)
            {
                JsonValue o = J.O("elementId", Compat.IdValue(elementId));
                Element referenced = null;
                try { referenced = doc != null ? doc.GetElement(elementId) : null; } catch (Exception) { }
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
                    if (count < 100) { array.Add(Describe(item, doc, depth + 1, maxInline, ctx)); count++; }
                }
                if (total > count)
                    return J.O("items", array, "returned", count, "total", total,
                               "truncated", true,
                               "hint", "Return a projection or a count instead of every element.");
                return array;
            }

            return J.S(value.ToString());
        }

        /// <summary>Full text to %LOCALAPPDATA%\ABRevitMcp\results, the start and the end inline.</summary>
        private static JsonValue LongText(string text, int maxInline, ToolContext ctx)
        {
            string path = null;
            try
            {
                Directory.CreateDirectory(IpcConstants.ResultsDirectory);
                string id = ctx != null && !string.IsNullOrEmpty(ctx.RequestId)
                    ? ctx.RequestId.Substring(0, Math.Min(8, ctx.RequestId.Length)) : "result";
                string tool = ctx != null && ctx.Tool != null ? ctx.Tool.Name.Replace("revit_", "") : "result";
                path = Path.Combine(IpcConstants.ResultsDirectory,
                    DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + tool + "-" + id + ".txt");
                File.WriteAllText(path, text, new UTF8Encoding(false));
            }
            catch (Exception) { path = null; }

            int head = maxInline * 6 / 10;
            int tail = maxInline - head;
            string shortened = text.Substring(0, head) +
                               "\n\n... [" + (text.Length - head - tail).ToString("N0", CultureInfo.InvariantCulture) +
                               " characters left out here" + (path != null ? " - the full text is in the file" : string.Empty) +
                               "] ...\n\n" + text.Substring(text.Length - tail);

            return J.O(
                "text", shortened,
                "length", text.Length,
                "truncated", true,
                "fullTextFile", path,
                "note", path != null
                    ? "Read fullTextFile for the whole text."
                    : "The full text could not be written to " + IpcConstants.ResultsDirectory + ".");
        }
    }
}
