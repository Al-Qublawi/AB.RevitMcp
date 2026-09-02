using System;
using System.Collections.Generic;
using System.Text;

namespace AB.RevitMcp.Contracts.Json
{
    /// <summary>Serializes a <see cref="JsonValue"/> tree to compact or indented JSON text.</summary>
    internal static class JsonWriter
    {
        public static void Write(JsonValue value, StringBuilder sb, bool indented, int depth)
        {
            if (value == null) { sb.Append("null"); return; }

            switch (value.Kind)
            {
                case JsonKind.Null:
                    sb.Append("null");
                    break;

                case JsonKind.Bool:
                    sb.Append(value.BoolValue ? "true" : "false");
                    break;

                case JsonKind.Number:
                    sb.Append(value.NumberText());
                    break;

                case JsonKind.String:
                    WriteString(value.StringValue, sb);
                    break;

                case JsonKind.Array:
                    {
                        if (value.Count == 0) { sb.Append("[]"); break; }
                        sb.Append('[');
                        bool first = true;
                        foreach (JsonValue item in value.Items)
                        {
                            if (!first) sb.Append(',');
                            first = false;
                            if (indented) { sb.Append('\n'); Indent(sb, depth + 1); }
                            Write(item, sb, indented, depth + 1);
                        }
                        if (indented) { sb.Append('\n'); Indent(sb, depth); }
                        sb.Append(']');
                        break;
                    }

                case JsonKind.Object:
                    {
                        if (value.Count == 0) { sb.Append("{}"); break; }
                        sb.Append('{');
                        bool first = true;
                        foreach (KeyValuePair<string, JsonValue> m in value.Members)
                        {
                            if (!first) sb.Append(',');
                            first = false;
                            if (indented) { sb.Append('\n'); Indent(sb, depth + 1); }
                            WriteString(m.Key, sb);
                            sb.Append(':');
                            if (indented) sb.Append(' ');
                            Write(m.Value, sb, indented, depth + 1);
                        }
                        if (indented) { sb.Append('\n'); Indent(sb, depth); }
                        sb.Append('}');
                        break;
                    }

                default:
                    sb.Append("null");
                    break;
            }
        }

        private static void Indent(StringBuilder sb, int depth)
        {
            for (int i = 0; i < depth; i++) sb.Append("  ");
        }

        private static void WriteString(string s, StringBuilder sb)
        {
            sb.Append('"');
            if (s != null)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20 || c == 0x2028 || c == 0x2029)
                            {
                                sb.Append("\\u");
                                sb.Append(((int)c).ToString("x4"));
                            }
                            else
                            {
                                sb.Append(c);
                            }
                            break;
                    }
                }
            }
            sb.Append('"');
        }
    }
}
