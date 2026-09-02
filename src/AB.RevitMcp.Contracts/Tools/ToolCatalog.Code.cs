using AB.RevitMcp.Contracts.Json;

namespace AB.RevitMcp.Contracts.Tools
{
    public static partial class ToolCatalog
    {
        /// <summary>The one tool that is not schema-bounded. Named here so gates can find it.</summary>
        public const string ExecuteCodeToolName = "revit_execute_code";

        // ============================================================================
        //  THE ESCAPE HATCH
        //
        //  Everything else in this catalogue is a bounded, validated operation. This one is not:
        //  it compiles and runs whatever C# it is given against the live model. It exists because
        //  the Revit API is far larger than any tool list, and it is gated THREE times because
        //  that power is exactly as dangerous as it sounds:
        //
        //    1. the MCP server must be started with --allow-code-execution
        //    2. the Revit add-in must have code execution enabled on the ribbon (off by default)
        //    3. every call must carry confirm: true
        //
        //  Any one of those missing and the call is refused.
        // ============================================================================
        private static void RegisterCodeTools()
        {
            Add(ExecuteCodeToolName, ToolCategory.Destructive, "Execute Revit API code",
                "Compiles and runs C# against the open model, for anything the other tools do not " +
                "cover. Your code is the BODY of a method with `UIApplication uiApp` and " +
                "`Document doc` in scope; return any value and it comes back as JSON. A transaction " +
                "is already open, so do not start one. " +
                "This is disabled by default and must be enabled in three separate places - if it " +
                "is refused, prefer an existing tool rather than asking the human to unlock it. " +
                "Requires Revit 2020-2024 (.NET Framework); Revit 2025+ has no in-box C# compiler.",
                Sch.Obj("Code execution parameters.", new[] { "code", "confirm" },
                    "code", Sch.Str("C# statements. Example: " +
                                    "return new FilteredElementCollector(doc)" +
                                    ".OfClass(typeof(Wall)).GetElementCount();"),
                    "confirm", Sch.Confirm(),
                    "description", Sch.Str("One line saying what this code does, recorded in the log " +
                                           "so a human can audit what ran."),
                    "usings", Sch.Arr(Sch.Str("Namespace."),
                        "Extra namespaces to import beyond the Revit and System defaults.", null, 30)));
        }
    }
}
