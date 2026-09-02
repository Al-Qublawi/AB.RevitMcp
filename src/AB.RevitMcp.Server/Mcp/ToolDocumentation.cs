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

            return sb.ToString();
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
