using System;
using AB.RevitMcp.Contracts.Json;

namespace AB.RevitMcp.Contracts.Protocol
{
    /// <summary>Stable machine-readable error codes surfaced to the AI client.</summary>
    public static class BridgeErrorCodes
    {
        public const string InvalidArgument = "INVALID_ARGUMENT";
        public const string MissingArgument = "MISSING_ARGUMENT";
        public const string UnknownTool = "UNKNOWN_TOOL";
        public const string NotFound = "NOT_FOUND";
        public const string NoActiveDocument = "NO_ACTIVE_DOCUMENT";
        public const string ReadOnlyDocument = "READ_ONLY_DOCUMENT";
        public const string ConfirmationRequired = "CONFIRMATION_REQUIRED";
        public const string RevitApi = "REVIT_API_ERROR";
        public const string TransactionFailed = "TRANSACTION_FAILED";
        public const string Timeout = "TIMEOUT";
        public const string Cancelled = "CANCELLED";
        public const string Busy = "BUSY";
        public const string NotConnected = "NOT_CONNECTED";
        public const string ProtocolError = "PROTOCOL_ERROR";
        public const string Internal = "INTERNAL_ERROR";
    }

    /// <summary>A single tool invocation travelling from the MCP server to the Revit add-in.</summary>
    public sealed class BridgeRequest
    {
        public string Id;
        public string Tool;
        public JsonValue Arguments = JsonValue.NewObject();
        public int TimeoutMs = IpcConstants.DefaultRequestTimeoutMs;
        public string ClientName;

        public JsonValue ToJson()
        {
            JsonValue o = JsonValue.NewObject();
            o.Set("v", IpcConstants.ProtocolVersion);
            o.Set("id", Id ?? Guid.NewGuid().ToString("N"));
            o.Set("tool", Tool ?? string.Empty);
            o.Set("args", Arguments ?? JsonValue.NewObject());
            o.Set("timeoutMs", TimeoutMs);
            if (!string.IsNullOrEmpty(ClientName)) o.Set("client", ClientName);
            return o;
        }

        public static BridgeRequest FromJson(JsonValue v)
        {
            if (v == null || !v.IsObject) throw new JsonException("Bridge request must be a JSON object.");
            BridgeRequest r = new BridgeRequest();
            r.Id = v["id"].AsString(Guid.NewGuid().ToString("N"));
            r.Tool = v["tool"].AsString(null);
            r.Arguments = v["args"].IsObject ? v["args"] : JsonValue.NewObject();
            int t = v["timeoutMs"].AsInt(IpcConstants.DefaultRequestTimeoutMs);
            if (t <= 0) t = IpcConstants.DefaultRequestTimeoutMs;
            if (t > IpcConstants.MaxRequestTimeoutMs) t = IpcConstants.MaxRequestTimeoutMs;
            r.TimeoutMs = t;
            r.ClientName = v["client"].AsString(null);
            return r;
        }
    }

    /// <summary>The Revit add-in's reply for one <see cref="BridgeRequest"/>.</summary>
    public sealed class BridgeResponse
    {
        public string Id;
        public bool Ok;
        public JsonValue Result = JsonValue.Null;
        public string ErrorCode;
        public string ErrorMessage;
        public JsonValue ErrorDetails = JsonValue.Null;
        public double DurationMs;

        public static BridgeResponse Success(string id, JsonValue result, double durationMs)
        {
            return new BridgeResponse
            {
                Id = id,
                Ok = true,
                Result = result ?? JsonValue.Null,
                DurationMs = durationMs
            };
        }

        public static BridgeResponse Failure(string id, string code, string message, JsonValue details = null, double durationMs = 0)
        {
            return new BridgeResponse
            {
                Id = id,
                Ok = false,
                ErrorCode = code ?? BridgeErrorCodes.Internal,
                ErrorMessage = message ?? "Unspecified error.",
                ErrorDetails = details ?? JsonValue.Null,
                DurationMs = durationMs
            };
        }

        public JsonValue ToJson()
        {
            JsonValue o = JsonValue.NewObject();
            o.Set("v", IpcConstants.ProtocolVersion);
            o.Set("id", Id ?? string.Empty);
            o.Set("ok", Ok);
            o.Set("durationMs", Math.Round(DurationMs, 2));
            if (Ok)
            {
                o.Set("result", Result ?? JsonValue.Null);
            }
            else
            {
                JsonValue err = JsonValue.NewObject();
                err.Set("code", ErrorCode ?? BridgeErrorCodes.Internal);
                err.Set("message", ErrorMessage ?? string.Empty);
                if (ErrorDetails != null && !ErrorDetails.IsNull) err.Set("details", ErrorDetails);
                o.Set("error", err);
            }
            return o;
        }

        public static BridgeResponse FromJson(JsonValue v)
        {
            if (v == null || !v.IsObject) throw new JsonException("Bridge response must be a JSON object.");
            BridgeResponse r = new BridgeResponse();
            r.Id = v["id"].AsString(string.Empty);
            r.Ok = v["ok"].AsBool(false);
            r.DurationMs = v["durationMs"].AsDouble(0);
            if (r.Ok)
            {
                r.Result = v["result"];
            }
            else
            {
                JsonValue err = v["error"];
                r.ErrorCode = err["code"].AsString(BridgeErrorCodes.Internal);
                r.ErrorMessage = err["message"].AsString("Unspecified error.");
                r.ErrorDetails = err["details"];
            }
            return r;
        }
    }

    /// <summary>Descriptor written to disk so the MCP server can discover live Revit sessions.</summary>
    public sealed class BridgeEndpoint
    {
        public string PipeName;
        public int ProcessId;
        public string RevitVersion;
        public string RevitBuild;
        public string DocumentTitle;
        public string UserName;
        public DateTime StartedUtc;
        public int ProtocolVersion = IpcConstants.ProtocolVersion;

        public JsonValue ToJson()
        {
            JsonValue o = JsonValue.NewObject();
            o.Set("pipeName", PipeName);
            o.Set("processId", ProcessId);
            o.Set("revitVersion", RevitVersion);
            o.Set("revitBuild", RevitBuild);
            o.Set("documentTitle", DocumentTitle);
            o.Set("userName", UserName);
            o.Set("startedUtc", StartedUtc.ToString("o"));
            o.Set("protocolVersion", ProtocolVersion);
            return o;
        }

        public static BridgeEndpoint FromJson(JsonValue v)
        {
            if (v == null || !v.IsObject) return null;
            BridgeEndpoint e = new BridgeEndpoint();
            e.PipeName = v["pipeName"].AsString(null);
            e.ProcessId = v["processId"].AsInt(0);
            e.RevitVersion = v["revitVersion"].AsString(null);
            e.RevitBuild = v["revitBuild"].AsString(null);
            e.DocumentTitle = v["documentTitle"].AsString(null);
            e.UserName = v["userName"].AsString(null);
            e.ProtocolVersion = v["protocolVersion"].AsInt(0);
            DateTime dt;
            if (DateTime.TryParse(v["startedUtc"].AsString(null), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out dt))
            {
                e.StartedUtc = dt;
            }
            return string.IsNullOrEmpty(e.PipeName) ? null : e;
        }
    }
}
