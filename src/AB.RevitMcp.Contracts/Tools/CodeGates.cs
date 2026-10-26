using System;
using System.Text;

namespace AB.RevitMcp.Contracts.Tools
{
    /// <summary>
    /// The three gates in front of the code tools (revit_execute_code, revit_run_assembly_method),
    /// and the one refusal text both processes use, so a refusal always says WHICH gate is closed,
    /// what state the other two are in, and how to open the closed one.
    ///
    ///   1. the MCP server was started with --allow-code-execution (or AB_REVITMCP_ALLOW_CODE=1)
    ///   2. "Code exec" is switched on in this Revit session (AB Adv Tools > MCP Bridge)
    ///   3. the call carries "confirm": true
    /// </summary>
    public static class CodeGates
    {
        public enum State { Open, Closed, Unknown }

        public static bool IsCodeTool(string toolName)
        {
            return string.Equals(toolName, ToolCatalog.ExecuteCodeToolName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(toolName, ToolCatalog.RunAssemblyMethodToolName, StringComparison.OrdinalIgnoreCase);
        }

        public const string Gate1Name = "MCP server flag --allow-code-execution";
        public const string Gate2Name = "Revit ribbon: AB Adv Tools > MCP Bridge > Code exec";
        public const string Gate3Name = "\"confirm\": true on this call";

        public const string Gate1HowTo =
            "add --allow-code-execution to the args of the 'revit' MCP server in your AI client's " +
            "config (or set AB_REVITMCP_ALLOW_CODE=1 in its env block), then restart or reconnect " +
            "that server. Take it out again after the test session.";

        public const string Gate2HowTo =
            "in Revit, AB Adv Tools tab > MCP Bridge panel, press 'Code exec: off' and answer Yes. " +
            "It stays on only until that Revit closes.";

        public const string Gate3HowTo =
            "ask the human to approve this specific run, then call again with \"confirm\": true.";

        /// <summary>
        /// The refusal text. <paramref name="refusedBy"/> is 1, 2 or 3 - the gate that stopped the
        /// call. Gates in state Unknown are reported as such rather than guessed.
        /// </summary>
        public static string Refusal(string toolName, int refusedBy, State gate1, State gate2, State gate3)
        {
            var sb = new StringBuilder();
            sb.Append("'").Append(toolName).Append("' was refused: gate ").Append(refusedBy)
              .Append(" of 3 is closed.\n\n");

            Line(sb, 1, Gate1Name, gate1, refusedBy);
            Line(sb, 2, Gate2Name, gate2, refusedBy);
            Line(sb, 3, Gate3Name, gate3, refusedBy);

            sb.Append("\nTo open gate ").Append(refusedBy).Append(": ")
              .Append(refusedBy == 1 ? Gate1HowTo : refusedBy == 2 ? Gate2HowTo : Gate3HowTo);

            if (refusedBy != 3)
            {
                sb.Append("\n\nGates 1 and 2 are for the human to open - do not ask to have them opened " +
                          "unless no bounded tool can do the job.");
            }
            return sb.ToString();
        }

        private static void Line(StringBuilder sb, int number, string name, State state, int refusedBy)
        {
            sb.Append("  ").Append(number).Append(". ").Append(name.PadRight(56)).Append(' ')
              .Append(state == State.Open ? "open" : state == State.Closed ? "CLOSED" : "unknown");
            if (number == refusedBy) sb.Append("   <- refused here");
            sb.Append('\n');
        }

        public static State From(bool open) { return open ? State.Open : State.Closed; }
    }
}
