using System;
using System.Collections.Generic;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;
using AB.RevitMcp.Contracts.Tools;
using AB.RevitMcp.Addin.Revit;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AB.RevitMcp.Addin.Bridge
{
    /// <summary>
    /// Everything a tool handler needs, and nothing it does not. Constructed on the Revit UI
    /// thread immediately before the handler runs, so the Document reference is always valid.
    /// </summary>
    public sealed class ToolContext
    {
        private readonly List<string> _warnings = new List<string>();

        public ToolContext(UIApplication uiApp, ToolDescriptor tool, JsonValue args, string requestId, RequestLog log)
        {
            UiApp = uiApp;
            Tool = tool;
            Args = args ?? JsonValue.NewObject();
            RequestId = requestId;
            Log = log;
        }

        public UIApplication UiApp { get; private set; }
        public ToolDescriptor Tool { get; private set; }
        public JsonValue Args { get; private set; }
        public string RequestId { get; private set; }
        public RequestLog Log { get; private set; }

        /// <summary>Set by a handler running in dryRun mode so the router rolls back instead of committing.</summary>
        public bool RollbackRequested { get; private set; }

        public void RequestRollback() { RollbackRequested = true; }

        public UIDocument UiDoc
        {
            get
            {
                UIDocument uiDoc = UiApp != null ? UiApp.ActiveUIDocument : null;
                if (uiDoc == null) throw ToolException.NoDocument();
                return uiDoc;
            }
        }

        /// <summary>The active project document. Throws NO_ACTIVE_DOCUMENT rather than returning null.</summary>
        public Document Doc
        {
            get
            {
                UIDocument uiDoc = UiApp != null ? UiApp.ActiveUIDocument : null;
                Document doc = uiDoc != null ? uiDoc.Document : null;
                if (doc == null) throw ToolException.NoDocument();
                if (doc.IsFamilyDocument)
                    throw new ToolException(BridgeErrorCodes.NoActiveDocument,
                        "The active document is a FAMILY document. These tools operate on project (.rvt) models. " +
                        "Switch to a project document in Revit and try again.");
                return doc;
            }
        }

        /// <summary>Non-throwing accessor for status/diagnostic tools.</summary>
        public Document TryGetDoc()
        {
            try
            {
                UIDocument uiDoc = UiApp != null ? UiApp.ActiveUIDocument : null;
                return uiDoc != null ? uiDoc.Document : null;
            }
            catch (Exception) { return null; }
        }

        public View ActiveView
        {
            get
            {
                View v = Doc.ActiveView;
                if (v == null)
                    throw new ToolException(BridgeErrorCodes.NotFound, "There is no active view in Revit.");
                return v;
            }
        }

        /// <summary>Non-fatal remarks collected during execution and returned alongside the result.</summary>
        public void AddWarning(string message)
        {
            if (!string.IsNullOrEmpty(message) && _warnings.Count < 50) _warnings.Add(message);
        }

        public IReadOnlyList<string> Warnings { get { return _warnings; } }

        /// <summary>Attaches accumulated warnings to a result object just before it is returned.</summary>
        public JsonValue Decorate(JsonValue result)
        {
            if (result != null && result.IsObject && _warnings.Count > 0)
                result.Set("warnings", J.AStrings(_warnings));
            return result;
        }

        // ------------------------------------------------------------------
        //  Transactions
        // ------------------------------------------------------------------

        /// <summary>
        /// Runs <paramref name="body"/> inside a Revit transaction with silent failure handling.
        /// Any exception rolls the transaction back before it propagates, so the document can
        /// never be left half-modified.
        /// </summary>
        public T InTransaction<T>(string name, Func<T> body)
        {
            Document doc = Doc;
            if (!Compat.CanModify(doc))
                throw new ToolException(BridgeErrorCodes.ReadOnlyDocument,
                    "The active document is read-only and cannot be modified.");

            using (var t = new Transaction(doc, Truncate(name, 250)))
            {
                if (t.Start() != TransactionStatus.Started)
                    throw new ToolException(BridgeErrorCodes.TransactionFailed,
                        "Revit refused to start a transaction. Another command may still be running.");

                var handler = new SilentFailureHandler();
                FailureHandlingOptions options = t.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(handler);
                options.SetClearAfterRollback(true);
                options.SetForcedModalHandling(false);   // never block Revit with a modal dialog
                t.SetFailureHandlingOptions(options);

                T result;
                try
                {
                    result = body();
                }
                catch (Exception)
                {
                    try { t.RollBack(); } catch (Exception) { }
                    throw;
                }

                TransactionStatus status = t.Commit();
                for (int i = 0; i < handler.Captured.Count; i++) AddWarning("Revit: " + handler.Captured[i]);

                if (status != TransactionStatus.Committed)
                {
                    throw new ToolException(BridgeErrorCodes.TransactionFailed,
                        "Revit rolled the transaction back (status: " + status + "). " +
                        (handler.Captured.Count > 0
                            ? "Reported: " + string.Join(" | ", handler.Captured.ToArray())
                            : "No changes were applied."));
                }
                return result;
            }
        }

        public void InTransaction(string name, Action body)
        {
            InTransaction<object>(name, delegate { body(); return null; });
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "MCP";
            return s.Length <= max ? s : s.Substring(0, max);
        }
    }

    /// <summary>
    /// Swallows warnings and rolls back on errors so the Revit API never raises a modal dialog on
    /// a machine that is being driven by an AI. Captured text is surfaced in the tool response.
    /// </summary>
    public sealed class SilentFailureHandler : IFailuresPreprocessor
    {
        public List<string> Captured = new List<string>();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            IList<FailureMessageAccessor> failures = accessor.GetFailureMessages();
            bool hasError = false;

            for (int i = 0; i < failures.Count; i++)
            {
                FailureMessageAccessor f = failures[i];
                string text;
                try { text = f.GetDescriptionText(); }
                catch (Exception) { text = "(unreadable failure message)"; }

                if (Captured.Count < 50) Captured.Add(text);

                if (f.GetSeverity() == FailureSeverity.Warning)
                {
                    accessor.DeleteWarning(f);       // acknowledge and continue
                }
                else
                {
                    hasError = true;
                }
            }

            return hasError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}
