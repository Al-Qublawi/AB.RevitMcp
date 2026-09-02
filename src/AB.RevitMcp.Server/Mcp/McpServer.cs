using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;
using AB.RevitMcp.Contracts.Tools;
using AB.RevitMcp.Server.Bridge;

namespace AB.RevitMcp.Server.Mcp
{
    /// <summary>
    /// Model Context Protocol implementation - transport-agnostic.
    ///
    /// Deliberately hand-rolled over JSON-RPC 2.0 rather than built on a vendor SDK: the server
    /// must stay decoupled from any particular AI provider, and the only dependency it needs is
    /// the shared contracts assembly.
    /// </summary>
    public sealed class McpServer
    {
        public const string ServerName = "ab-revit-mcp";
        public const string ServerVersion = "1.0.0";

        /// <summary>Protocol revisions understood, newest first.</summary>
        private static readonly string[] SupportedProtocolVersions =
        {
            "2025-06-18",
            "2025-03-26",
            "2024-11-05"
        };

        // JSON-RPC 2.0 error codes
        private const int ParseError = -32700;
        private const int InvalidRequest = -32600;
        private const int MethodNotFound = -32601;
        private const int InvalidParams = -32602;
        private const int InternalError = -32603;

        private readonly BridgeConnection _bridge;
        private readonly ServerOptions _options;
        private readonly Action<string> _log;

        private string _negotiatedProtocol = SupportedProtocolVersions[0];
        private string _clientName;
        private bool _initialized;

        public McpServer(BridgeConnection bridge, ServerOptions options, Action<string> log)
        {
            _bridge = bridge;
            _options = options;
            _log = log ?? delegate { };
        }

        /// <summary>
        /// Handles one incoming JSON-RPC message (or batch). Returns the text to send back, or
        /// null when the message was a notification that needs no reply.
        /// </summary>
        public async Task<string> HandleMessageAsync(string payload, CancellationToken ct)
        {
            JsonValue message;
            if (!JsonValue.TryParse(payload, out message))
                return Error(JsonValue.Null, ParseError, "Invalid JSON.").ToJson();

            if (message.IsArray)
            {
                // JSON-RPC batch (used by MCP 2025-03-26 and earlier).
                JsonValue responses = JsonValue.NewArray();
                foreach (JsonValue item in message.Items)
                {
                    JsonValue response = await HandleSingleAsync(item, ct).ConfigureAwait(false);
                    if (response != null) responses.Add(response);
                }
                return responses.Count == 0 ? null : responses.ToJson();
            }

            JsonValue single = await HandleSingleAsync(message, ct).ConfigureAwait(false);
            return single == null ? null : single.ToJson();
        }

        private async Task<JsonValue> HandleSingleAsync(JsonValue message, CancellationToken ct)
        {
            if (!message.IsObject)
                return Error(JsonValue.Null, InvalidRequest, "A JSON-RPC message must be an object.");

            JsonValue id = message["id"];
            string method = message["method"].AsString(null);
            bool isNotification = !message.Has("id") || id.IsNull;

            if (string.IsNullOrEmpty(method))
            {
                // A response to something we sent - this server issues no requests, so ignore it.
                return null;
            }

            if (_options.Verbose) _log("<- " + method + (isNotification ? " (notification)" : string.Empty));

            try
            {
                switch (method)
                {
                    case "initialize":
                        return Result(id, Initialize(message["params"]));

                    case "notifications/initialized":
                    case "initialized":
                        _initialized = true;
                        return null;

                    case "notifications/cancelled":
                        return null;

                    case "ping":
                        return Result(id, JsonValue.NewObject());

                    case "tools/list":
                        return Result(id, ListTools());

                    case "tools/call":
                        return Result(id, await CallToolAsync(message["params"], ct).ConfigureAwait(false));

                    // Advertised as absent, but answered rather than erroring: several clients
                    // probe these on connect and treat -32601 as a hard failure.
                    case "resources/list":
                        return Result(id, J.O("resources", JsonValue.NewArray()));

                    case "resources/templates/list":
                        return Result(id, J.O("resourceTemplates", JsonValue.NewArray()));

                    case "prompts/list":
                        return Result(id, J.O("prompts", JsonValue.NewArray()));

                    case "logging/setLevel":
                        return Result(id, JsonValue.NewObject());

                    case "completion/complete":
                        return Result(id, J.O("completion",
                            J.O("values", JsonValue.NewArray(), "total", 0, "hasMore", false)));

                    case "shutdown":
                        return Result(id, JsonValue.NewObject());

                    default:
                        if (isNotification) return null;
                        return Error(id, MethodNotFound, "Method '" + method + "' is not supported by this server.");
                }
            }
            catch (OperationCanceledException)
            {
                return isNotification ? null : Error(id, InternalError, "The request was cancelled.");
            }
            catch (Exception ex)
            {
                _log("Unhandled error in '" + method + "': " + ex);
                return isNotification ? null : Error(id, InternalError, ex.Message);
            }
        }

        // ==================================================================
        //  initialize
        // ==================================================================
        private JsonValue Initialize(JsonValue parameters)
        {
            string requested = parameters["protocolVersion"].AsString(null);
            _negotiatedProtocol = NegotiateProtocol(requested);

            JsonValue clientInfo = parameters["clientInfo"];
            _clientName = clientInfo["name"].AsString("unknown-client");
            _log("Client '" + _clientName + " " + clientInfo["version"].AsString("") +
                 "' connected; protocol " + _negotiatedProtocol +
                 (requested != null && requested != _negotiatedProtocol
                     ? " (requested " + requested + ")"
                     : string.Empty));

            JsonValue capabilities = J.O(
                "tools", J.O("listChanged", false),
                "logging", JsonValue.NewObject());

            return J.O(
                "protocolVersion", _negotiatedProtocol,
                "capabilities", capabilities,
                "serverInfo", J.O(
                    "name", ServerName,
                    "title", "Autodesk Revit",
                    "version", ServerVersion),
                "instructions", Instructions());
        }

        private static string NegotiateProtocol(string requested)
        {
            if (string.IsNullOrEmpty(requested)) return SupportedProtocolVersions[0];
            for (int i = 0; i < SupportedProtocolVersions.Length; i++)
                if (SupportedProtocolVersions[i] == requested) return SupportedProtocolVersions[i];
            // Unknown version: answer with our newest and let the client decide.
            return SupportedProtocolVersions[0];
        }

        private string Instructions()
        {
            return
                "These tools read and modify the Autodesk Revit model that is currently open on this " +
                "machine.\n\n" +
                "UNITS - every value in and out is metric: lengths in millimetres, areas in square " +
                "metres, volumes in cubic metres, angles in degrees. Revit's internal decimal feet " +
                "never appear.\n\n" +
                "WORKFLOW - start with revit_get_model_info to learn what is open. Use the list tools " +
                "(revit_list_levels, revit_list_categories, revit_list_family_types) to discover the " +
                "EXACT names to pass to other tools; guessing a level or type name will fail, though " +
                "the error will suggest close matches.\n\n" +
                "PAGING - list tools return at most " + IpcConstants.DefaultPageLimit + " items by " +
                "default. Follow 'nextOffset' rather than raising 'limit' to a huge number.\n\n" +
                "SAFETY - tools are annotated read-only, write or destructive. Destructive tools " +
                "(delete, purge, unload links) do nothing unless you pass \"confirm\": true, and you " +
                "should ask the human before you do. Most of them also accept \"dryRun\": true, which " +
                "reports the exact blast radius and then rolls the model back - prefer that first.\n\n" +
                "UNDO - each write tool is a single Revit undo step, so a human can reverse anything " +
                "you do with Ctrl+Z." +
                (_options.AllowCodeExecution
                    ? "\n\nCODE EXECUTION is enabled on this server. Reach for revit_execute_code only " +
                      "when no bounded tool fits - it has no schema validation and can do anything " +
                      "the Revit API allows."
                    : string.Empty) +
                (_options.AllowDestructive
                    ? string.Empty
                    : "\n\nTHIS SERVER IS IN READ-ONLY MODE. Write and destructive tools will refuse to run.");
        }

        // ==================================================================
        //  tools/list
        // ==================================================================
        private JsonValue ListTools()
        {
            JsonValue tools = ToolCatalog.ToMcpToolsArray();

            // Gate 1 of 3: an unadvertised tool is one the model will not try to reach for.
            if (!_options.AllowCodeExecution)
            {
                JsonValue filtered = JsonValue.NewArray();
                foreach (JsonValue tool in tools.Items)
                {
                    if (tool["name"].AsString(null) == ToolCatalog.ExecuteCodeToolName) continue;
                    filtered.Add(tool);
                }
                tools = filtered;
            }

            if (!_options.AllowDestructive)
            {
                foreach (JsonValue tool in tools.Items)
                {
                    ToolDescriptor descriptor = ToolCatalog.Find(tool["name"].AsString(null));
                    if (descriptor == null || descriptor.IsReadOnly) continue;
                    tool.Set("description",
                        "[DISABLED - this server runs in read-only mode] " + tool["description"].AsString(""));
                }
            }

            return J.O("tools", tools);
        }

        // ==================================================================
        //  tools/call
        // ==================================================================
        private async Task<JsonValue> CallToolAsync(JsonValue parameters, CancellationToken ct)
        {
            string name = parameters["name"].AsString(null);
            JsonValue arguments = parameters["arguments"];
            if (!arguments.IsObject) arguments = JsonValue.NewObject();

            if (string.IsNullOrEmpty(name))
                return ToolError("The 'name' parameter is required for tools/call.");

            ToolDescriptor descriptor = ToolCatalog.Find(name);
            if (descriptor == null)
            {
                return ToolError("Unknown tool '" + name + "'. Call tools/list to see the " +
                                 ToolCatalog.Count + " available tools.");
            }

            // ---- policy gates, applied before Revit is even contacted ----
            if (name == ToolCatalog.ExecuteCodeToolName && !_options.AllowCodeExecution)
            {
                return ToolError(
                    "'" + name + "' is disabled. This MCP server was not started with " +
                    "--allow-code-execution.\n\n" +
                    "That is deliberate: it compiles and runs arbitrary C# against the live model, " +
                    "with none of the schema validation the other tools have. Use one of the " +
                    (ToolCatalog.Count - 1) + " bounded tools instead.");
            }

            if (!_options.AllowDestructive && !descriptor.IsReadOnly)
            {
                return ToolError("'" + name + "' is a " + descriptor.Category.ToString().ToLowerInvariant() +
                                 " tool and this MCP server was started in read-only mode. " +
                                 "Only read tools are permitted.");
            }

            List<string> schemaErrors;
            if (!SchemaValidator.Validate(descriptor.InputSchema, arguments, out schemaErrors))
            {
                return ToolError("Invalid arguments for '" + name + "':\n  " +
                                 string.Join("\n  ", schemaErrors.ToArray()));
            }

            if (descriptor.IsDestructive && !arguments["confirm"].AsBool(false))
            {
                return ToolError(
                    "'" + name + "' is DESTRUCTIVE and was not executed because \"confirm\": true was " +
                    "not supplied.\n\n" +
                    "Ask the human to approve this specific change first. " +
                    "If the tool supports \"dryRun\": true, run that with confirm:true to preview the " +
                    "exact effect without modifying the model.");
            }

            // ---- dispatch to Revit ----
            var request = new BridgeRequest
            {
                Id = Guid.NewGuid().ToString("N"),
                Tool = name,
                Arguments = arguments,
                // A tool that declares its own budget (export, for one) gets it; everything else
                // uses the server default. Declared budgets act as a floor, so --timeout can still
                // raise them but never silently cut an export short.
                TimeoutMs = descriptor.EffectiveTimeoutMs(_options.RequestTimeoutMs),
                ClientName = _clientName
            };

            BridgeResponse response;
            try
            {
                response = await _bridge.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (BridgeUnavailableException ex)
            {
                return ToolError(ex.Message);
            }

            if (!response.Ok)
            {
                JsonValue details = response.ErrorDetails;
                string text = "[" + response.ErrorCode + "] " + response.ErrorMessage;
                if (details != null && !details.IsNull) text += "\n\nDetails: " + details.ToJson(true);
                return ToolError(text, response.ErrorCode);
            }

            JsonValue result = response.Result ?? JsonValue.NewObject();

            JsonValue content = JsonValue.NewArray();
            content.Add(J.O("type", "text", "text", result.ToJson(true)));

            JsonValue payload = J.O("content", content, "isError", false);

            // structuredContent is part of MCP 2025-06-18; harmless extra data for older clients.
            if (result.IsObject) payload.Set("structuredContent", result);

            return payload;
        }

        private static JsonValue ToolError(string message, string code = null)
        {
            JsonValue content = JsonValue.NewArray();
            content.Add(J.O("type", "text", "text", message));

            JsonValue payload = J.O("content", content, "isError", true);
            if (!string.IsNullOrEmpty(code))
                payload.Set("structuredContent", J.O("errorCode", code, "message", message));
            return payload;
        }

        // ==================================================================
        //  JSON-RPC envelopes
        // ==================================================================
        private static JsonValue Result(JsonValue id, JsonValue result)
        {
            JsonValue response = JsonValue.NewObject();
            response.Set("jsonrpc", "2.0");
            response.Set("id", id ?? JsonValue.Null);
            response.Set("result", result ?? JsonValue.NewObject());
            return response;
        }

        private static JsonValue Error(JsonValue id, int code, string message, JsonValue data = null)
        {
            JsonValue error = JsonValue.NewObject();
            error.Set("code", code);
            error.Set("message", message ?? "Error");
            if (data != null && !data.IsNull) error.Set("data", data);

            JsonValue response = JsonValue.NewObject();
            response.Set("jsonrpc", "2.0");
            response.Set("id", id ?? JsonValue.Null);
            response.Set("error", error);
            return response;
        }

        public bool IsInitialized { get { return _initialized; } }
    }
}
