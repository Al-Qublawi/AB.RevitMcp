using System;
using System.Collections.Generic;
using System.Text;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Tools;

namespace AB.RevitMcp.Server
{
    /// <summary>
    /// Renders the shared tool catalogue as Markdown (<c>--print-tools</c>), so the documentation
    /// is generated from the same descriptors the server advertises and cannot go stale.
    /// </summary>
    public static class ToolDocumentation
    {
        public static string ToMarkdown()
        {
            var sb = new StringBuilder();

            sb.AppendLine("# Revit MCP tool reference");
            sb.AppendLine();
            sb.AppendLine("Generated from the shared tool catalogue by `AB.RevitMcp.Server.exe --print-tools`.");
            sb.AppendLine();
            sb.AppendLine("**Units** - every value in and out is metric: lengths in millimetres (mm), areas in ");
            sb.AppendLine("square metres (m2), volumes in cubic metres (m3), angles in degrees. Revit's internal ");
            sb.AppendLine("decimal feet never cross this boundary.");
            sb.AppendLine();

            AppendSummary(sb);

            AppendCategory(sb, ToolCategory.Read, "Read tools",
                "Query only. These never open a Revit transaction and are safe to call at any time.");
            AppendCategory(sb, ToolCategory.Write, "Write tools",
                "Each call runs inside one transaction group and appears as a single entry in Revit's " +
                "undo stack, so a human can reverse it with Ctrl+Z.");
            AppendCategory(sb, ToolCategory.Destructive, "Destructive tools",
                "These refuse to run unless the caller passes `\"confirm\": true`. The interlock is " +
                "enforced by the server AND independently by the Revit add-in. Most also accept " +
                "`\"dryRun\": true`, which performs the operation, reports exactly what it would " +
                "affect, and then rolls the model back.");

            AppendCodeExecution(sb);

            return sb.ToString();
        }

        /// <summary>The gates, how to switch them for a test session, and the dev-test workflow.</summary>
        private static void AppendCodeExecution(StringBuilder sb)
        {
            string[] lines =
            {
                "## Code execution and development testing",
                "",
                "`revit_execute_code` (C# snippets) and `revit_run_assembly_method` (a static method of a DLL, " +
                "e.g. an add-in's `DevHarness`) can do anything the Revit API allows. Both sit behind the " +
                "same three gates, and a refusal always names the closed gate and shows the state of all three.",
                "",
                "| Gate | Where | Default | Open | Close |",
                "| --- | --- | --- | --- | --- |",
                "| 1 | MCP server | off | add `--allow-code-execution` to the server's `args` (or `AB_REVITMCP_ALLOW_CODE=1` in its `env`), then restart or reconnect the server | take it out again, restart or reconnect |",
                "| 2 | Revit | off at every Revit start | AB Adv Tools > MCP Bridge > **Code exec: off** > Yes | press it again, or close Revit |",
                "| 3 | each call | - | `\"confirm\": true`, after the human approved that run | - |",
                "",
                "### A test session, on and off",
                "",
                "1. In the AI client's config, give the `revit` server `\"args\": [\"--allow-code-execution\"]` " +
                "and reconnect it (Claude Code: `/mcp`, or a new session).",
                "2. In Revit: **Start Bridge**, then **Code exec**.",
                "3. Run the tests. `revit_bridge_status` shows all gates: `mcpServer.gate1AllowCodeExecution` " +
                "and `codeExecution.gate2RevitSwitch`.",
                "4. Afterwards remove the flag again and reconnect. Gate 2 switches itself off when Revit closes.",
                "",
                "### What runs where",
                "",
                "- **Compiling.** Revit 2020-2024 compile snippets inside Revit with the in-box compiler (C# 5). " +
                "Revit 2025 and newer have no compiler; the MCP server compiles the snippet with Roslyn " +
                "(C# 13) against the running Revit's own framework and API assemblies and sends the finished " +
                "assembly. No compiler is ever loaded into Revit.",
                "- **Transactions.** `revit_execute_code` with `\"transaction\": \"active\"` (default) runs " +
                "inside one transaction on the active document: one undo step, rolled back on any error. " +
                "With `\"none\"` nothing is opened and `doc` may be null - the code opens, changes, saves " +
                "and closes BACKGROUND documents with transactions of its own. `revit_run_assembly_method` " +
                "never opens a transaction.",
                "- **Long runs.** A call still running when its wait (`waitSeconds`, default 30 s) runs out " +
                "answers `\"state\": \"running\"` with a `jobId`; the work carries on. Collect it with " +
                "`revit_get_job_result` (answered even while Revit is busy; `waitSeconds` lets it wait). " +
                "Never repeat the original call. This applies to every tool, not only these two.",
                "- **Long text.** A returned string over `maxInlineChars` (20000) is written in full to " +
                "`%LOCALAPPDATA%\\ABRevitMcp\\results` and shortened inline to its start and end.",
                "- **Dev builds.** `revit_run_assembly_method` loads the DLL from its bytes (with its .pdb), " +
                "reuses identical bytes, and refuses a rebuilt DLL that kept its version - Revit never " +
                "unloads an assembly, so each dev build needs a new version. DLLs beside it load on demand. " +
                "Documents are picked by title prefix; the active document is refused unless " +
                "`allowActiveDocument` is true. `revit_list_open_documents` shows which documents are " +
                "background copies (`openWindows: 0`).",
                "",
                "### Example: an add-in's test harness on background copies",
                "",
                "```json",
                "{ \"name\": \"revit_run_assembly_method\", \"arguments\": {",
                "    \"assemblyPath\": \"%LOCALAPPDATA%\\\\Temp\\\\ABTransferDev\\\\v5\\\\AB.TransferAny.Addin.dll\",",
                "    \"typeName\": \"AB.TransferAny.Addin.Diagnostics.DevHarness\",",
                "    \"methodName\": \"RunAll\",",
                "    \"arguments\": [ { \"document\": \"ABTA_Source\" }, { \"document\": \"ABTA_Dest\" } ],",
                "    \"waitSeconds\": 30, \"confirm\": true } }",
                "```",
                "",
                "The background copies themselves are made with `revit_execute_code` and " +
                "`\"transaction\": \"none\"` (open a copy of a sample with `OpenDocumentFile`, create one with " +
                "`NewProjectDocument`, `SaveAs`).",
                ""
            };
            foreach (string line in lines) sb.AppendLine(line);
        }

        private static void AppendSummary(StringBuilder sb)
        {
            int read = 0, write = 0, destructive = 0;
            foreach (ToolDescriptor descriptor in ToolCatalog.All)
            {
                if (descriptor.Category == ToolCategory.Read) read++;
                else if (descriptor.Category == ToolCategory.Write) write++;
                else destructive++;
            }

            sb.AppendLine("| Category | Count | Transaction | Confirmation |");
            sb.AppendLine("| --- | ---: | --- | --- |");
            sb.AppendLine("| Read | " + read + " | none | not required |");
            sb.AppendLine("| Write | " + write + " | one transaction group per call | not required |");
            sb.AppendLine("| Destructive | " + destructive + " | one transaction group per call | `confirm: true` required |");
            sb.AppendLine("| **Total** | **" + ToolCatalog.Count + "** | | |");
            sb.AppendLine();
        }

        private static void AppendCategory(StringBuilder sb, ToolCategory category, string title, string blurb)
        {
            sb.AppendLine("## " + title);
            sb.AppendLine();
            sb.AppendLine(blurb);
            sb.AppendLine();

            foreach (ToolDescriptor descriptor in ToolCatalog.ByCategory(category))
            {
                sb.AppendLine("### `" + descriptor.Name + "`");
                sb.AppendLine();
                sb.AppendLine(descriptor.Description);
                sb.AppendLine();

                JsonValue schema = descriptor.InputSchema;
                JsonValue properties = schema["properties"];

                if (!properties.IsObject || properties.Count == 0)
                {
                    sb.AppendLine("Takes no arguments.");
                    sb.AppendLine();
                    continue;
                }

                var required = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonValue r in schema["required"].Items)
                {
                    string name = r.AsString(null);
                    if (name != null) required.Add(name);
                }

                sb.AppendLine("| Argument | Type | Required | Description |");
                sb.AppendLine("| --- | --- | --- | --- |");

                foreach (KeyValuePair<string, JsonValue> member in properties.Members)
                {
                    JsonValue property = member.Value;
                    string type = property["type"].AsString("any");

                    if (type == "array")
                    {
                        string itemType = property["items"]["type"].AsString("any");
                        type = itemType + "[]";
                    }

                    JsonValue enumValues = property["enum"];
                    if (enumValues.IsArray && enumValues.Count > 0)
                    {
                        var names = new List<string>();
                        foreach (JsonValue e in enumValues.Items) names.Add("`" + e.AsString("") + "`");
                        type = string.Join(" \\| ", names.ToArray());
                    }

                    string description = property["description"].AsString(string.Empty).Replace("|", "\\|");
                    if (property.HasValue("default"))
                        description += " (default `" + property["default"].AsString("") + "`)";

                    sb.AppendLine("| `" + member.Key + "` | " + type + " | " +
                                  (required.Contains(member.Key) ? "yes" : "no") + " | " +
                                  description + " |");
                }

                sb.AppendLine();
            }
        }
    }
}
