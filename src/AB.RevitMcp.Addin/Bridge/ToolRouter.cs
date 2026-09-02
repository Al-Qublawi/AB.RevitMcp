using System;
using System.Collections.Generic;
using AB.RevitMcp.Addin.Revit;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;
using AB.RevitMcp.Contracts.Tools;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

// NOTE: Autodesk.Revit.Exceptions is deliberately NOT imported. It defines ArgumentException,
// ArgumentNullException and InvalidOperationException with the same simple names as System, and
// importing it turns every one of those into an ambiguous reference. Revit exceptions are fully
// qualified below instead.

namespace AB.RevitMcp.Addin.Bridge
{
    public delegate JsonValue ToolHandler(ToolContext context);

    /// <summary>
    /// Dispatches a validated request to its handler ON THE REVIT UI THREAD, applying the safety
    /// policy that the catalog only documents:
    ///
    ///   1. the tool must exist and be implemented on this Revit release
    ///   2. arguments must validate against the tool's JSON Schema
    ///   3. a destructive tool must carry confirm:true - enforced here, not left to the handler
    ///   4. write and destructive tools run inside a TransactionGroup that is assimilated into a
    ///      single undo step on success and rolled back completely on any failure
    /// </summary>
    public sealed class ToolRouter
    {
        private readonly Dictionary<string, ToolHandler> _handlers =
            new Dictionary<string, ToolHandler>(StringComparer.OrdinalIgnoreCase);
        private readonly RequestLog _log;

        /// <summary>
        /// Tools that must NOT be wrapped in a transaction group:
        ///   revit_set_selection - touches UI state only; a transaction would pointlessly fail on
        ///                         a read-only model
        ///   revit_unload_links  - RevitLinkType.Unload manages its own document state and the
        ///                         Revit API refuses to run it inside transaction control
        /// Both still go through the full validation and confirmation policy.
        /// </summary>
        private static readonly HashSet<string> NoTransactionTools =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "revit_set_selection",
                "revit_unload_links"
            };

        public ToolRouter(RequestLog log)
        {
            _log = log;
            Tools.ToolRegistry.RegisterAll(this);
        }

        public void Register(string toolName, ToolHandler handler)
        {
            if (string.IsNullOrEmpty(toolName)) throw new ArgumentNullException("toolName");
            if (handler == null) throw new ArgumentNullException("handler");
            if (ToolCatalog.Find(toolName) == null)
                throw new InvalidOperationException(
                    "Handler registered for '" + toolName + "', which is not in the shared ToolCatalog. " +
                    "Add the descriptor to the catalog so the MCP server can advertise it.");
            _handlers[toolName] = handler;
        }

        public int HandlerCount { get { return _handlers.Count; } }

        public bool IsImplemented(string toolName)
        {
            return !string.IsNullOrEmpty(toolName) && _handlers.ContainsKey(toolName);
        }

        public IEnumerable<string> UnimplementedTools()
        {
            foreach (ToolDescriptor d in ToolCatalog.All)
                if (!_handlers.ContainsKey(d.Name)) yield return d.Name;
        }

        /// <summary>
        /// Runs one tool. MUST be called on Revit's UI thread (the dispatcher guarantees this).
        /// Never throws: every failure is converted into a structured BridgeResponse.
        /// </summary>
        public BridgeResponse Execute(UIApplication uiApp, BridgeRequest request)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            ToolDescriptor descriptor = ToolCatalog.Find(request.Tool);
            if (descriptor == null)
            {
                return BridgeResponse.Failure(request.Id, BridgeErrorCodes.UnknownTool,
                    "Unknown tool '" + request.Tool + "'. Call tools/list to see what this server offers.",
                    J.O("tool", request.Tool, "toolCount", ToolCatalog.Count), sw.Elapsed.TotalMilliseconds);
            }

            ToolHandler handler;
            if (!_handlers.TryGetValue(descriptor.Name, out handler))
            {
                return BridgeResponse.Failure(request.Id, BridgeErrorCodes.UnknownTool,
                    "Tool '" + descriptor.Name + "' is not implemented for Revit " + Compat.RevitReleaseName + ".",
                    J.O("tool", descriptor.Name, "revitVersion", Compat.RevitReleaseName),
                    sw.Elapsed.TotalMilliseconds);
            }

            // ---- 2. schema validation, before anything touches the Revit API ----
            List<string> schemaErrors;
            if (!SchemaValidator.Validate(descriptor.InputSchema, request.Arguments, out schemaErrors))
            {
                return BridgeResponse.Failure(request.Id, BridgeErrorCodes.InvalidArgument,
                    "Invalid arguments for '" + descriptor.Name + "': " + string.Join(" ", schemaErrors.ToArray()),
                    J.O("tool", descriptor.Name, "errors", J.AStrings(schemaErrors)),
                    sw.Elapsed.TotalMilliseconds);
            }

            var context = new ToolContext(uiApp, descriptor, request.Arguments, request.Id, _log);

            try
            {
                // ---- 3. destructive interlock ----
                if (descriptor.IsDestructive)
                {
                    Args.RequireConfirm(request.Arguments, descriptor.Name,
                        "It would permanently change the model.");
                }

                JsonValue result = (descriptor.Category == ToolCategory.Read || NoTransactionTools.Contains(descriptor.Name))
                    ? handler(context)
                    : RunTransacted(context, descriptor, handler);

                result = context.Decorate(result ?? JsonValue.NewObject());
                return BridgeResponse.Success(request.Id, result, sw.Elapsed.TotalMilliseconds);
            }
            catch (ToolException ex)
            {
                return BridgeResponse.Failure(request.Id, ex.Code, ex.Message, ex.Details, sw.Elapsed.TotalMilliseconds);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                return BridgeResponse.Failure(request.Id, BridgeErrorCodes.InvalidArgument,
                    "Revit rejected an argument: " + ex.Message, null, sw.Elapsed.TotalMilliseconds);
            }
            catch (Autodesk.Revit.Exceptions.InvalidObjectException ex)
            {
                return BridgeResponse.Failure(request.Id, BridgeErrorCodes.NotFound,
                    "An element referenced by this request no longer exists: " + ex.Message,
                    null, sw.Elapsed.TotalMilliseconds);
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                return BridgeResponse.Failure(request.Id, BridgeErrorCodes.RevitApi,
                    "Revit refused the operation: " + ex.Message, null, sw.Elapsed.TotalMilliseconds);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                return BridgeResponse.Failure(request.Id, BridgeErrorCodes.RevitApi,
                    "Revit API error: " + ex.Message,
                    J.O("exception", ex.GetType().Name), sw.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException)
            {
                return BridgeResponse.Failure(request.Id, BridgeErrorCodes.Cancelled,
                    "The operation was cancelled.", null, sw.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                // Genuinely unexpected: log the stack trace, return a safe summary.
                if (_log != null) _log.Error("Unhandled exception in tool '" + descriptor.Name + "'.", ex);
                return BridgeResponse.Failure(request.Id, BridgeErrorCodes.Internal,
                    "Internal error in '" + descriptor.Name + "': " + ex.Message,
                    J.O("exception", ex.GetType().Name), sw.Elapsed.TotalMilliseconds);
            }
        }

        /// <summary>
        /// Write and destructive tools run inside a transaction group. On success the group is
        /// assimilated so the user sees ONE undo entry named after the tool; on any failure - or
        /// when the handler asked for a dry run - the group is rolled back in full.
        /// </summary>
        private static JsonValue RunTransacted(ToolContext context, ToolDescriptor descriptor, ToolHandler handler)
        {
            Document doc = context.Doc;

            if (!Compat.CanModify(doc))
            {
                throw new ToolException(BridgeErrorCodes.ReadOnlyDocument,
                    "The active document is read-only, so '" + descriptor.Name + "' cannot run. " +
                    "This happens with linked models, worksharing-detached previews and files opened as read-only.");
            }

            using (var group = new TransactionGroup(doc, "MCP: " + descriptor.Name))
            {
                if (group.Start() != TransactionStatus.Started)
                    throw new ToolException(BridgeErrorCodes.TransactionFailed,
                        "Revit refused to start a transaction group. Finish the active Revit command and retry.");

                JsonValue result;
                try
                {
                    result = handler(context);
                }
                catch (Exception)
                {
                    try { if (group.HasStarted() && !group.HasEnded()) group.RollBack(); } catch (Exception) { }
                    throw;
                }

                if (context.RollbackRequested)
                {
                    try { group.RollBack(); } catch (Exception) { }
                    if (result != null && result.IsObject)
                    {
                        result.Set("dryRun", true);
                        result.Set("modelChanged", false);
                    }
                    return result;
                }

                TransactionStatus status = group.Assimilate();
                if (status != TransactionStatus.Committed)
                {
                    throw new ToolException(BridgeErrorCodes.TransactionFailed,
                        "Revit could not commit the changes (status: " + status + "). Nothing was applied.");
                }

                if (result != null && result.IsObject && !result.Has("modelChanged"))
                    result.Set("modelChanged", true);
                return result;
            }
        }
    }
}
