using AB.RevitMcp.Contracts.Json;

namespace AB.RevitMcp.Contracts.Tools
{
    public static partial class ToolCatalog
    {
        /// <summary>The one tool that is not schema-bounded. Named here so gates can find it.</summary>
        public const string ExecuteCodeToolName = "revit_execute_code";

        /// <summary>Runs a static method of a compiled assembly (development test harnesses). Same gates.</summary>
        public const string RunAssemblyMethodToolName = "revit_run_assembly_method";

        /// <summary>Collects the result of a call that outlived its wait.</summary>
        public const string GetJobResultToolName = "revit_get_job_result";

        public const string ListOpenDocumentsToolName = "revit_list_open_documents";

        // ============================================================================
        //  THE ESCAPE HATCH, AND THE DEVELOPMENT-TEST TOOLS
        //
        //  Everything else in this catalogue is a bounded, validated operation. revit_execute_code
        //  is not: it compiles and runs whatever C# it is given against the live model, and
        //  revit_run_assembly_method runs whatever a DLL on disk does. Both exist because the
        //  Revit API is far larger than any tool list, and because the AB add-ins are tested by a
        //  harness running inside Revit. Both are gated THREE times (see CodeGates):
        //
        //    1. the MCP server must be started with --allow-code-execution
        //    2. "Code exec" must be switched on in this Revit session (off again when Revit closes)
        //    3. every call must carry confirm: true
        //
        //  Any one of those missing and the call is refused, naming the closed gate.
        // ============================================================================
        private static void RegisterCodeTools()
        {
            Add(ListOpenDocumentsToolName, ToolCategory.Read, "List open documents",
                "Lists every document open in this Revit session - the visible ones AND those opened in " +
                "the background by a script - with title, path, which one is active, whether it has a " +
                "window open, and its linked / family / workshared / modified / read-only state. Use it " +
                "to find background test documents, and to make sure a test never touches the model the " +
                "human is working in.",
                Sch.NoArgs());

            Add(GetJobResultToolName, ToolCategory.Read, "Get the result of a long-running call",
                "Any call that is still running when its wait runs out answers with state \"running\" and " +
                "a jobId instead of a timeout - the work carries on inside Revit. Call this with that " +
                "jobId to see whether it has finished and to collect its result. Answered without " +
                "waiting for Revit, so it works while the job is busy. Do NOT repeat the original call " +
                "while it is running: that would run it twice.",
                Sch.Obj("Job lookup.", new[] { "jobId" },
                    "jobId", Sch.Str("The jobId a previous call returned."),
                    "waitSeconds", Sch.Int("Wait up to this long for the job to finish before answering. " +
                                           "Default 0 (answer at once).", 0, 600, 0)));

            Add(ExecuteCodeToolName, ToolCategory.Destructive, "Execute Revit API code",
                "Compiles and runs C# against the open Revit session, for anything the other tools do not " +
                "cover. Your code is the BODY of a method with `UIApplication uiApp` and `Document doc` (the " +
                "active document) in scope; return any value and it comes back as JSON, a long string in " +
                "full to a file. With transaction \"active\" (the default) a transaction is already open on " +
                "the active document, so do not start one there. With transaction \"none\" nothing is " +
                "opened, `doc` may be null, and the code starts its own transactions - use that to open, " +
                "change, save and close BACKGROUND documents without touching the active one. A run longer " +
                "than waitSeconds answers with a jobId (see revit_get_job_result). " +
                "Revit 2020-2024 compile inside Revit (C# 5); Revit 2025 and newer are compiled by this MCP " +
                "server (C# 13). Disabled by default behind three gates - if it is refused, prefer an " +
                "existing tool rather than asking the human to unlock it.",
                Sch.Obj("Code execution parameters.", new[] { "code", "confirm" },
                    "code", Sch.Str("C# statements. Example: " +
                                    "return new FilteredElementCollector(doc)" +
                                    ".OfClass(typeof(Wall)).GetElementCount();"),
                    "confirm", Sch.Confirm(),
                    "description", Sch.Str("One line saying what this code does, recorded in the log " +
                                           "so a human can audit what ran."),
                    "usings", Sch.Arr(Sch.Str("Namespace."),
                        "Extra namespaces to import beyond the Revit and System defaults.", null, 30),
                    "transaction", Sch.Str("\"active\": run inside one undoable transaction on the active " +
                                           "document (default). \"none\": open nothing; the code manages " +
                                           "its own transactions and documents.",
                                           new[] { "active", "none" }, "active"),
                    "waitSeconds", WaitSeconds(),
                    "maxInlineChars", MaxInlineChars()));

            Add(RunAssemblyMethodToolName, ToolCategory.Destructive, "Run a method of a compiled assembly",
                "Development testing: loads a compiled .NET assembly from its bytes (the file is not " +
                "locked) and calls one public static method, passing Revit objects and plain values - " +
                "e.g. an add-in's DevHarness.RunAll(Document source, Document destination). Other DLLs " +
                "in the same folder are loaded on demand as its dependencies. Revit never unloads an " +
                "assembly: the same bytes are reused, but a REBUILT file that kept its assembly version " +
                "is refused - give each dev build a new version. Documents are picked by title prefix " +
                "and the ACTIVE document is refused unless allowActiveDocument is true. No transaction " +
                "is opened; the method manages its own. A string result comes back in full (long text " +
                "to a file); a run longer than waitSeconds answers with a jobId. Same three gates as " +
                "revit_execute_code. The method can do anything the Revit API allows.",
                Sch.Obj("Assembly method call.", new[] { "assemblyPath", "typeName", "methodName", "confirm" },
                    "assemblyPath", Sch.Str("Full path of the .dll; environment variables are expanded, e.g. " +
                                            "%LOCALAPPDATA%\\\\Temp\\\\ABTransferDev\\\\v3\\\\AB.TransferAny.Addin.dll."),
                    "typeName", Sch.Str("Full name of the type, e.g. AB.TransferAny.Addin.Diagnostics.DevHarness."),
                    "methodName", Sch.Str("Name of the public static method, e.g. RunAll."),
                    "arguments", Sch.Arr(
                        Sch.Obj("One argument. Give exactly one of these properties.", null,
                            "document", Sch.Str("An open document whose title starts with this text " +
                                                "(case-insensitive). Exactly one may match."),
                            "uiApplication", Sch.Bool("Pass the UIApplication."),
                            "application", Sch.Bool("Pass the Autodesk.Revit.ApplicationServices.Application."),
                            "string", Sch.Str("A string value, passed exactly as given."),
                            "path", Sch.Str("A file or folder path, passed as a string after expanding " +
                                            "environment variables (%LOCALAPPDATA%\\\\Temp\\\\...)."),
                            "number", Sch.Num("A number (double)."),
                            "integer", Sch.Int("An integer (int or long)."),
                            "boolean", Sch.Bool("A boolean value."),
                            "null", Sch.Bool("Pass null.")),
                        "The method's arguments, in order. Omit for a method without parameters.", null, 20),
                    "allowActiveDocument", Sch.Bool("Allow a document argument to be the document active in " +
                                                    "the Revit window. Default false - tests run on " +
                                                    "background copies.", false),
                    "confirm", Sch.Confirm(),
                    "description", Sch.Str("One line saying what this run is, recorded in the log."),
                    "waitSeconds", WaitSeconds(),
                    "maxInlineChars", MaxInlineChars()));
        }

        private static JsonValue WaitSeconds()
        {
            return Sch.Int("How long to wait for the result before answering with a jobId instead. " +
                           "Default: the server's request budget (30 s unless --timeout raised it). " +
                           "The work is never cut short by this.", 1, 600);
        }

        private static JsonValue MaxInlineChars()
        {
            return Sch.Int("A returned string longer than this is written in full to a file under " +
                           "%LOCALAPPDATA%\\ABRevitMcp\\results and shortened inline (its start and end). " +
                           "Default 20000.", 1000, 1000000, 20000);
        }
    }
}
