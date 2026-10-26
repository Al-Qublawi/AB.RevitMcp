using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AB.RevitMcp.Addin.Bridge;
using AB.RevitMcp.Addin.Revit;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;
using AB.RevitMcp.Contracts.Tools;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AB.RevitMcp.Addin.Tools
{
    /// <summary>
    /// The development-test workflow of the AB add-ins, as tools: see which documents are open
    /// (and which one the human is looking at), run a DevHarness method of a dev build on
    /// background copies, and collect results of runs that take minutes.
    /// </summary>
    public static class DevTestTools
    {
        // ==================================================================
        //  revit_list_open_documents (read)
        // ==================================================================
        public static JsonValue ListOpenDocuments(ToolContext ctx)
        {
            UIApplication uiApp = ctx.UiApp;
            Autodesk.Revit.ApplicationServices.Application app = uiApp.Application;
            Document active = ctx.TryGetDoc();

            JsonValue docs = JsonValue.NewArray();
            foreach (Document d in app.Documents)
            {
                JsonValue o = JsonValue.NewObject();
                try { o.Set("title", d.Title); } catch (Exception) { }
                try { o.Set("path", string.IsNullOrEmpty(d.PathName) ? null : d.PathName); } catch (Exception) { }
                o.Set("isActive", IsSame(d, active));

                // A document opened by a script has no window - that is how a background test copy
                // is told apart from what the human has on screen.
                int windows = -1;
                try { windows = new UIDocument(d).GetOpenUIViews().Count; } catch (Exception) { }
                if (windows >= 0) o.Set("openWindows", windows);

                try { o.Set("isLinked", d.IsLinked); } catch (Exception) { }
                try { o.Set("isFamilyDocument", d.IsFamilyDocument); } catch (Exception) { }
                try { o.Set("isWorkshared", d.IsWorkshared); } catch (Exception) { }
                try { o.Set("isModified", d.IsModified); } catch (Exception) { }
                try { o.Set("isReadOnly", d.IsReadOnly); } catch (Exception) { }
                try { o.Set("isModifiable", d.IsModifiable); } catch (Exception) { }
                docs.Add(o);
            }

            return J.O(
                "revitVersion", app.VersionNumber,
                "revitBuild", app.VersionBuild,
                "activeDocument", active != null ? active.Title : null,
                "count", docs.Count,
                "documents", docs,
                "note", "openWindows 0 = opened in the background (no window). Leave the active document " +
                        "and anything with windows alone unless the human asked for it.");
        }

        internal static bool IsSame(Document a, Document b)
        {
            if (a == null || b == null) return false;
            try { return a.Equals(b); } catch (Exception) { return false; }
        }

        // ==================================================================
        //  revit_get_job_result (read) - answered by BridgeService off the UI thread
        // ==================================================================
        public static JsonValue GetJobResult(ToolContext ctx)
        {
            // BridgeService answers this tool before it is ever queued for the UI thread, so it
            // works while the job it asks about is still running. Reaching here means a client
            // bypassed that path.
            throw new ToolException(BridgeErrorCodes.Internal,
                ToolCatalog.GetJobResultToolName + " is answered by the bridge itself and should not reach the UI thread.");
        }

        // ==================================================================
        //  revit_run_assembly_method (destructive, triple-gated)
        // ==================================================================
        public static JsonValue RunAssemblyMethod(ToolContext ctx)
        {
            if (!CodeTools.IsEnabled())
                throw new ToolException(BridgeErrorCodes.GateClosed,
                    CodeGates.Refusal(ctx.Tool.Name, 2, CodeGates.State.Open, CodeGates.State.Closed, CodeGates.State.Open));

            // %LOCALAPPDATA% and friends are expanded, so a recipe file works on any machine.
            string path = Environment.ExpandEnvironmentVariables(Args.Str(ctx.Args, "assemblyPath", true));
            string typeName = Args.Str(ctx.Args, "typeName", true);
            string methodName = Args.Str(ctx.Args, "methodName", true);
            string description = Args.Str(ctx.Args, "description");
            bool allowActive = Args.Bool(ctx.Args, "allowActiveDocument", false);
            int maxInline = Args.Int(ctx.Args, "maxInlineChars", false, IpcConstants.DefaultInlineTextChars, 1000, 1000000);

            if (!Path.IsPathRooted(path))
                throw ToolException.Invalid("assemblyPath", "give the full path of the .dll.");
            if (!File.Exists(path))
                throw ToolException.Invalid("assemblyPath", "'" + path + "' does not exist.");

            // ---- the arguments, resolved BEFORE anything is loaded or run ----
            JsonValue specs = ctx.Args["arguments"];
            var values = new List<object>();
            var kinds = new List<string>();
            var documentsUsed = new List<string>();
            Document active = ctx.TryGetDoc();
            int index = 0;
            foreach (JsonValue spec in specs.IsArray ? specs.Items : new JsonValue[0])
            {
                object value;
                string kind = ResolveArgument(ctx, spec, index, active, allowActive, documentsUsed, out value);
                values.Add(value);
                kinds.Add(kind);
                index++;
            }

            ctx.Log.Warn("revit_run_assembly_method invoked", J.O(
                "description", description, "assemblyPath", path, "type", typeName, "method", methodName,
                "documents", J.AStrings(documentsUsed)));

            bool reused;
            string sha;
            AssemblyName identity;
            Assembly assembly = DevAssemblies.Load(path, out reused, out sha, out identity);

            Type type = assembly.GetType(typeName, false);
            if (type == null)
            {
                var names = new List<string>();
                try
                {
                    foreach (Type t in assembly.GetExportedTypes())
                        if (t.FullName.IndexOf(typeName.Substring(typeName.LastIndexOf('.') + 1), StringComparison.OrdinalIgnoreCase) >= 0)
                            names.Add(t.FullName);
                }
                catch (Exception) { }
                throw new ToolException(BridgeErrorCodes.NotFound,
                    "Type '" + typeName + "' was not found in " + identity.Name + " " + identity.Version +
                    ". Debug-only types (a DevHarness) exist only in a Debug build.").WithSuggestions(names);
            }

            MethodInfo method = PickMethod(type, methodName, values, kinds);
            object[] invokeArgs = ConvertArguments(method, values, kinds);

            object result;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                result = method.Invoke(null, invokeArgs);
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                throw new ToolException(BridgeErrorCodes.RevitApi,
                    type.Name + "." + method.Name + " threw: " + inner.GetType().Name + ": " + inner.Message +
                    " (no transaction is opened by this tool: anything the method committed before this stays committed)",
                    J.O("exception", inner.GetType().FullName, "stack", inner.StackTrace));
            }
            sw.Stop();

            return J.O(
                "executed", true,
                "description", description,
                "method", type.FullName + "." + method.Name + "(" + ParameterList(method) + ")",
                "assembly", J.O(
                    "name", identity.Name,
                    "version", identity.Version != null ? identity.Version.ToString() : null,
                    "path", path,
                    "sha256", sha,
                    "reusedFromEarlierRun", reused),
                "documents", J.AStrings(documentsUsed),
                "durationMs", Metric.R(sw.Elapsed.TotalMilliseconds, 1),
                "returned", CodeTools.Describe(result, active, 0, maxInline, ctx),
                "note", "No transaction was opened by the bridge; the method managed its own.");
        }

        private static string ResolveArgument(ToolContext ctx, JsonValue spec, int index, Document active,
                                              bool allowActive, List<string> documentsUsed, out object value)
        {
            string where = "arguments[" + index + "]";
            if (!spec.IsObject || spec.Count != 1)
                throw ToolException.Invalid(where, "give exactly one of document, uiApplication, application, " +
                                                   "string, path, number, integer, boolean, null.");

            foreach (KeyValuePair<string, JsonValue> member in spec.Members)
            {
                JsonValue v = member.Value;
                switch (member.Key)
                {
                    case "document":
                        value = FindDocument(ctx, v.AsString(""), where, active, allowActive);
                        documentsUsed.Add(((Document)value).Title);
                        return "document";
                    case "uiApplication":
                        value = ctx.UiApp;
                        return "uiApplication";
                    case "application":
                        value = ctx.UiApp.Application;
                        return "application";
                    case "string":
                        value = v.AsString("");
                        return "string";
                    case "path":
                        // Recipes are committed to repositories that keep user-profile paths out.
                        value = Environment.ExpandEnvironmentVariables(v.AsString(""));
                        return "string";
                    case "number":
                        value = v.AsDouble(0);
                        return "number";
                    case "integer":
                        value = v.AsLong(0);
                        return "integer";
                    case "boolean":
                        value = v.AsBool(false);
                        return "boolean";
                    case "null":
                        value = null;
                        return "null";
                }
            }
            throw ToolException.Invalid(where, "unknown argument kind.");
        }

        private static Document FindDocument(ToolContext ctx, string prefix, string where, Document active, bool allowActive)
        {
            if (string.IsNullOrEmpty(prefix)) throw ToolException.Invalid(where, "document needs a title prefix.");

            var matches = new List<Document>();
            var titles = new List<string>();
            foreach (Document d in ctx.UiApp.Application.Documents)
            {
                string title;
                try { if (d.IsLinked) continue; title = d.Title; } catch (Exception) { continue; }
                titles.Add(title);
                if (title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) matches.Add(d);
            }

            if (matches.Count == 0)
                throw new ToolException(BridgeErrorCodes.NotFound,
                    "No open document's title starts with '" + prefix + "'. Open documents: " +
                    (titles.Count > 0 ? string.Join(", ", titles.ToArray()) : "none") + ".",
                    J.O("argument", where, "openDocuments", J.AStrings(titles)));

            if (matches.Count > 1)
            {
                var names = new List<string>();
                foreach (Document d in matches) names.Add(d.Title);
                throw ToolException.Invalid(where, "'" + prefix + "' matches " + matches.Count +
                                                   " open documents: " + string.Join(", ", names.ToArray()) + ". Be more specific.");
            }

            Document doc = matches[0];
            if (!allowActive && DevTestTools.IsSame(doc, active))
                throw new ToolException(BridgeErrorCodes.InvalidArgument,
                    "'" + doc.Title + "' is the ACTIVE document in the Revit window. Tests run on background " +
                    "copies, so it was refused. Make a fresh test copy under a new name, or pass " +
                    "\"allowActiveDocument\": true if the human asked for exactly this.",
                    J.O("argument", where, "document", doc.Title));
            return doc;
        }

        private static MethodInfo PickMethod(Type type, string name, List<object> values, List<string> kinds)
        {
            var candidates = new List<MethodInfo>();
            foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (string.Equals(m.Name, name, StringComparison.Ordinal)) candidates.Add(m);

            if (candidates.Count == 0)
            {
                var names = new List<string>();
                foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    if (!names.Contains(m.Name)) names.Add(m.Name);
                throw new ToolException(BridgeErrorCodes.NotFound,
                    "'" + type.FullName + "' has no public static method '" + name + "'.").WithSuggestions(names);
            }

            foreach (MethodInfo m in candidates)
                if (Fits(m, values, kinds)) return m;

            var signatures = new List<string>();
            foreach (MethodInfo m in candidates) signatures.Add(m.Name + "(" + ParameterList(m) + ")");
            throw ToolException.Invalid("arguments", "no overload of " + type.Name + "." + name + " takes (" +
                                                     string.Join(", ", kinds.ToArray()) + "). Available: " +
                                                     string.Join("; ", signatures.ToArray()) + ".");
        }

        private static bool Fits(MethodInfo m, List<object> values, List<string> kinds)
        {
            ParameterInfo[] ps = m.GetParameters();
            if (values.Count > ps.Length) return false;
            for (int i = values.Count; i < ps.Length; i++) if (!ps[i].IsOptional) return false;

            for (int i = 0; i < values.Count; i++)
            {
                Type p = ps[i].ParameterType;
                switch (kinds[i])
                {
                    case "null":
                        if (p.IsValueType && Nullable.GetUnderlyingType(p) == null) return false;
                        break;
                    case "number":
                        if (!IsNumeric(p) || IsIntegral(p)) return false;
                        break;
                    case "integer":
                        if (!IsNumeric(p)) return false;
                        break;
                    default:
                        if (!p.IsAssignableFrom(values[i].GetType())) return false;
                        break;
                }
            }
            return true;
        }

        private static object[] ConvertArguments(MethodInfo m, List<object> values, List<string> kinds)
        {
            ParameterInfo[] ps = m.GetParameters();
            var result = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                if (i >= values.Count) { result[i] = Type.Missing; continue; }
                object v = values[i];
                Type p = Nullable.GetUnderlyingType(ps[i].ParameterType) ?? ps[i].ParameterType;
                if ((kinds[i] == "number" || kinds[i] == "integer") && p != typeof(object))
                    v = Convert.ChangeType(v, p, System.Globalization.CultureInfo.InvariantCulture);
                result[i] = v;
            }
            return result;
        }

        private static bool IsNumeric(Type t)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            return t == typeof(object) || IsIntegral(t) || t == typeof(double) || t == typeof(float) || t == typeof(decimal);
        }

        private static bool IsIntegral(Type t)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            return t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ||
                   t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte);
        }

        private static string ParameterList(MethodInfo m)
        {
            var parts = new List<string>();
            foreach (ParameterInfo p in m.GetParameters()) parts.Add(p.ParameterType.Name + " " + p.Name);
            return string.Join(", ", parts.ToArray());
        }
    }
}
