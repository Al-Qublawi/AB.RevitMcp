using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AB.RevitMcp.Contracts.Json
{
    /// <summary>
    /// Strict, allocation-conscious recursive-descent JSON parser (RFC 8259 subset: no comments,
    /// no trailing commas, no NaN/Infinity literals). Depth-limited so hostile input from an MCP
    /// client cannot blow the Revit UI thread's stack.
    /// </summary>
    internal static class JsonParser
    {
        private const int MaxDepth = 96;

        public static JsonValue Parse(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
            int p = 0;
            SkipWs(text, ref p);
            JsonValue value = ParseValue(text, ref p, 0);
            SkipWs(text, ref p);
            if (p != text.Length)
                throw new JsonException("Unexpected trailing content at offset " + p + ".");
            return value;
        }

        private static void SkipWs(string s, ref int p)
        {
            while (p < s.Length)
            {
                char c = s[p];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') p++;
                else break;
            }
        }

        private static JsonValue ParseValue(string s, ref int p, int depth)
        {
            if (depth > MaxDepth) throw new JsonException("Maximum JSON nesting depth exceeded.");
            if (p >= s.Length) throw new JsonException("Unexpected end of JSON input.");

            char c = s[p];
            switch (c)
            {
                case '{': return ParseObject(s, ref p, depth);
                case '[': return ParseArray(s, ref p, depth);
                case '"': return JsonValue.Str(ParseString(s, ref p));
                case 't': Literal(s, ref p, "true"); return JsonValue.True;
                case 'f': Literal(s, ref p, "false"); return JsonValue.False;
                case 'n': Literal(s, ref p, "null"); return JsonValue.Null;
                default: return ParseNumber(s, ref p);
            }
        }

        private static void Literal(string s, ref int p, string literal)
        {
            if (p + literal.Length > s.Length || string.CompareOrdinal(s, p, literal, 0, literal.Length) != 0)
                throw new JsonException("Invalid JSON literal at offset " + p + "; expected '" + literal + "'.");
            p += literal.Length;
        }

        private static JsonValue ParseObject(string s, ref int p, int depth)
        {
            p++; // '{'
            JsonValue obj = JsonValue.NewObject();
            SkipWs(s, ref p);
            if (p < s.Length && s[p] == '}') { p++; return obj; }

            while (true)
            {
                SkipWs(s, ref p);
                if (p >= s.Length || s[p] != '"')
                    throw new JsonException("Expected a property name string at offset " + p + ".");
                string name = ParseString(s, ref p);
                SkipWs(s, ref p);
                if (p >= s.Length || s[p] != ':')
                    throw new JsonException("Expected ':' after property name at offset " + p + ".");
                p++;
                SkipWs(s, ref p);
                obj.Set(name, ParseValue(s, ref p, depth + 1));
                SkipWs(s, ref p);
                if (p >= s.Length) throw new JsonException("Unterminated JSON object.");
                if (s[p] == ',') { p++; continue; }
                if (s[p] == '}') { p++; return obj; }
                throw new JsonException("Expected ',' or '}' at offset " + p + ".");
            }
        }

        private static JsonValue ParseArray(string s, ref int p, int depth)
        {
            p++; // '['
            JsonValue arr = JsonValue.NewArray();
            SkipWs(s, ref p);
            if (p < s.Length && s[p] == ']') { p++; return arr; }

            while (true)
            {
                SkipWs(s, ref p);
                arr.Add(ParseValue(s, ref p, depth + 1));
                SkipWs(s, ref p);
                if (p >= s.Length) throw new JsonException("Unterminated JSON array.");
                if (s[p] == ',') { p++; continue; }
                if (s[p] == ']') { p++; return arr; }
                throw new JsonException("Expected ',' or ']' at offset " + p + ".");
            }
        }

        private static string ParseString(string s, ref int p)
        {
            p++; // opening quote
            int start = p;

            // Fast path: no escapes.
            while (p < s.Length)
            {
                char c = s[p];
                if (c == '"') { string fast = s.Substring(start, p - start); p++; return fast; }
                if (c == '\\') break;
                if (c < 0x20) throw new JsonException("Unescaped control character in string at offset " + p + ".");
                p++;
            }
            if (p >= s.Length) throw new JsonException("Unterminated JSON string.");

            var sb = new StringBuilder(s.Substring(start, p - start));
            while (p < s.Length)
            {
                char c = s[p];
                if (c == '"') { p++; return sb.ToString(); }
                if (c < 0x20) throw new JsonException("Unescaped control character in string at offset " + p + ".");
                if (c != '\\') { sb.Append(c); p++; continue; }

                p++; // backslash
                if (p >= s.Length) throw new JsonException("Unterminated escape sequence.");
                char e = s[p++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        {
                            if (p + 4 > s.Length) throw new JsonException("Truncated \\u escape sequence.");
                            int code = 0;
                            for (int i = 0; i < 4; i++)
                            {
                                int d = HexDigit(s[p + i]);
                                if (d < 0) throw new JsonException("Invalid \\u escape sequence at offset " + p + ".");
                                code = (code << 4) | d;
                            }
                            p += 4;
                            // UTF-16 code units are appended verbatim: a valid surrogate pair in the
                            // source becomes a valid surrogate pair in the resulting .NET string.
                            sb.Append((char)code);
                            break;
                        }
                    default:
                        throw new JsonException("Unsupported escape character '\\" + e + "'.");
                }
            }
            throw new JsonException("Unterminated JSON string.");
        }

        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        private static JsonValue ParseNumber(string s, ref int p)
        {
            int start = p;
            bool isInteger = true;

            if (p < s.Length && s[p] == '-') p++;
            if (p >= s.Length || s[p] < '0' || s[p] > '9')
                throw new JsonException("Invalid JSON value at offset " + start + ".");
            while (p < s.Length && s[p] >= '0' && s[p] <= '9') p++;

            if (p < s.Length && s[p] == '.')
            {
                isInteger = false;
                p++;
                if (p >= s.Length || s[p] < '0' || s[p] > '9')
                    throw new JsonException("Invalid fractional part at offset " + p + ".");
                while (p < s.Length && s[p] >= '0' && s[p] <= '9') p++;
            }

            if (p < s.Length && (s[p] == 'e' || s[p] == 'E'))
            {
                isInteger = false;
                p++;
                if (p < s.Length && (s[p] == '+' || s[p] == '-')) p++;
                if (p >= s.Length || s[p] < '0' || s[p] > '9')
                    throw new JsonException("Invalid exponent at offset " + p + ".");
                while (p < s.Length && s[p] >= '0' && s[p] <= '9') p++;
            }

            string raw = s.Substring(start, p - start);
            double d;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new JsonException("Number out of range at offset " + start + ": " + raw);

            if (isInteger)
            {
                long l;
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out l))
                    return JsonValue.Num(l); // keeps exact 64-bit element ids
            }
            return JsonValue.RawNumber(d, null);
        }
    }
}
