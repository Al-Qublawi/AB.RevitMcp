using System;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;

namespace AB.RevitMcp.Addin.Revit
{
    /// <summary>
    /// A controlled, expected failure inside a tool: bad arguments, a missing element, a refused
    /// destructive call. Carries a stable machine code so the AI client can react rather than
    /// re-read an English sentence. Anything that escapes as a plain Exception is treated as an
    /// internal bug and logged with its stack trace.
    /// </summary>
    public sealed class ToolException : Exception
    {
        public string Code { get; private set; }
        public JsonValue Details { get; private set; }

        public ToolException(string code, string message, JsonValue details = null)
            : base(message)
        {
            Code = code ?? BridgeErrorCodes.Internal;
            Details = details ?? JsonValue.Null;
        }

        /// <summary>
        /// Attaches near-miss candidates to a NOT_FOUND error. This matters more than it looks:
        /// an AI that gets back "did you mean: Level 01, Level 02" self-corrects on the next call
        /// instead of guessing again.
        /// </summary>
        public ToolException WithSuggestions(System.Collections.Generic.IEnumerable<string> suggestions)
        {
            if (suggestions == null) return this;

            JsonValue list = JsonValue.NewArray();
            foreach (string s in suggestions)
            {
                if (string.IsNullOrEmpty(s)) continue;
                list.Add(s);
                if (list.Count >= 20) break;
            }
            if (list.Count == 0) return this;

            JsonValue details = Details != null && Details.IsObject ? Details : JsonValue.NewObject();
            details.Set("didYouMean", list);
            Details = details;
            return this;
        }

        public static ToolException Missing(string argumentName)
        {
            return new ToolException(BridgeErrorCodes.MissingArgument,
                "Required argument '" + argumentName + "' was not supplied.",
                J.O("argument", argumentName));
        }

        public static ToolException Invalid(string argumentName, string why)
        {
            return new ToolException(BridgeErrorCodes.InvalidArgument,
                "Argument '" + argumentName + "' is invalid: " + why,
                J.O("argument", argumentName, "reason", why));
        }

        public static ToolException NotFound(string what, string identifier)
        {
            return new ToolException(BridgeErrorCodes.NotFound,
                what + " '" + identifier + "' was not found in this model.",
                J.O("kind", what, "identifier", identifier));
        }

        public static ToolException NoDocument()
        {
            return new ToolException(BridgeErrorCodes.NoActiveDocument,
                "No Revit project document is open. Open a model in Revit and try again.");
        }

        public static ToolException NeedsConfirmation(string toolName, string consequence)
        {
            return new ToolException(BridgeErrorCodes.ConfirmationRequired,
                "'" + toolName + "' is destructive and was not executed. " + consequence +
                " Ask the human to approve, then call again with \"confirm\": true.",
                J.O("tool", toolName, "requiredArgument", "confirm", "requiredValue", true));
        }
    }
}
