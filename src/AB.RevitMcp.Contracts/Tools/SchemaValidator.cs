using System;
using System.Collections.Generic;
using System.Globalization;
using AB.RevitMcp.Contracts.Json;

namespace AB.RevitMcp.Contracts.Tools
{
    /// <summary>
    /// Validates tool arguments against the subset of JSON Schema the catalog actually uses:
    /// type, required, enum, minimum/maximum, minItems/maxItems, nested objects and arrays, and
    /// additionalProperties:false.
    ///
    /// Running this before any Revit API call converts a whole class of "the AI sent nonsense"
    /// failures from mid-transaction exceptions into a clear message the model can act on.
    /// </summary>
    public static class SchemaValidator
    {
        public static bool Validate(JsonValue schema, JsonValue args, out List<string> errors)
        {
            errors = new List<string>();
            if (schema == null || !schema.IsObject) return true;   // nothing to check against
            ValidateNode(schema, args ?? JsonValue.NewObject(), "", errors, 0);
            return errors.Count == 0;
        }

        private static void ValidateNode(JsonValue schema, JsonValue value, string path, List<string> errors, int depth)
        {
            if (depth > 24 || errors.Count > 25) return;

            string type = schema["type"].AsString(null);
            if (type == null) return;

            switch (type)
            {
                case "object": ValidateObject(schema, value, path, errors, depth); break;
                case "array": ValidateArray(schema, value, path, errors, depth); break;
                case "string": ValidateString(schema, value, path, errors); break;
                case "integer": ValidateNumber(schema, value, path, errors, true); break;
                case "number": ValidateNumber(schema, value, path, errors, false); break;
                case "boolean":
                    if (!value.IsNull && !value.IsBool)
                        errors.Add(Name(path) + " must be a boolean (true or false), got " + Describe(value) + ".");
                    break;
            }
        }

        private static void ValidateObject(JsonValue schema, JsonValue value, string path, List<string> errors, int depth)
        {
            if (value.IsNull) return;
            if (!value.IsObject)
            {
                errors.Add(Name(path) + " must be an object, got " + Describe(value) + ".");
                return;
            }

            JsonValue properties = schema["properties"];

            JsonValue required = schema["required"];
            if (required.IsArray)
            {
                foreach (JsonValue r in required.Items)
                {
                    string key = r.AsString(null);
                    if (key == null) continue;
                    if (!value.HasValue(key))
                        errors.Add("Required " + (path.Length == 0 ? "argument" : "property") +
                                   " '" + Join(path, key) + "' is missing.");
                }
            }

            bool allowExtra = !(schema.Has("additionalProperties") && schema["additionalProperties"].IsBool &&
                                !schema["additionalProperties"].AsBool(true));

            foreach (KeyValuePair<string, JsonValue> member in value.Members)
            {
                if (!properties.Has(member.Key))
                {
                    if (!allowExtra)
                        errors.Add("Unknown argument '" + Join(path, member.Key) + "'. " +
                                   "Allowed: " + string.Join(", ", KeyList(properties)) + ".");
                    continue;
                }
                if (member.Value.IsNull) continue;      // explicit null == omitted
                ValidateNode(properties[member.Key], member.Value, Join(path, member.Key), errors, depth + 1);
            }
        }

        private static void ValidateArray(JsonValue schema, JsonValue value, string path, List<string> errors, int depth)
        {
            if (value.IsNull) return;
            if (!value.IsArray)
            {
                errors.Add(Name(path) + " must be an array, got " + Describe(value) + ".");
                return;
            }

            if (schema.Has("minItems") && value.Count < schema["minItems"].AsInt(0))
                errors.Add(Name(path) + " needs at least " + schema["minItems"].AsInt(0) +
                           " item(s), got " + value.Count + ".");
            if (schema.Has("maxItems") && value.Count > schema["maxItems"].AsInt(int.MaxValue))
                errors.Add(Name(path) + " allows at most " + schema["maxItems"].AsInt(0) +
                           " item(s), got " + value.Count + ".");

            JsonValue items = schema["items"];
            if (!items.IsObject) return;

            int index = 0;
            foreach (JsonValue item in value.Items)
            {
                if (index >= 200) break;   // enough to catch a systematic mistake
                ValidateNode(items, item, path + "[" + index + "]", errors, depth + 1);
                index++;
            }
        }

        private static void ValidateString(JsonValue schema, JsonValue value, string path, List<string> errors)
        {
            if (value.IsNull) return;
            if (!value.IsString)
            {
                errors.Add(Name(path) + " must be a string, got " + Describe(value) + ".");
                return;
            }

            JsonValue allowed = schema["enum"];
            if (!allowed.IsArray || allowed.Count == 0) return;

            string s = value.AsString("");
            var names = new List<string>();
            foreach (JsonValue a in allowed.Items)
            {
                string candidate = a.AsString(null);
                if (candidate == null) continue;
                names.Add(candidate);
                if (string.Equals(candidate, s, StringComparison.OrdinalIgnoreCase)) return;
            }
            errors.Add(Name(path) + " must be one of: " + string.Join(", ", names.ToArray()) + ". Got '" + s + "'.");
        }

        private static void ValidateNumber(JsonValue schema, JsonValue value, string path, List<string> errors, bool integer)
        {
            if (value.IsNull) return;
            if (!value.IsNumber)
            {
                errors.Add(Name(path) + " must be a " + (integer ? "integer" : "number") +
                           ", got " + Describe(value) + ".");
                return;
            }

            double d = value.AsDouble(0);
            if (integer && Math.Abs(d - Math.Round(d)) > 1e-9)
            {
                errors.Add(Name(path) + " must be a whole number, got " +
                           d.ToString(CultureInfo.InvariantCulture) + ".");
                return;
            }
            if (schema.Has("minimum") && d < schema["minimum"].AsDouble(double.MinValue))
                errors.Add(Name(path) + " must be >= " +
                           schema["minimum"].AsDouble(0).ToString(CultureInfo.InvariantCulture) + ".");
            if (schema.Has("maximum") && d > schema["maximum"].AsDouble(double.MaxValue))
                errors.Add(Name(path) + " must be <= " +
                           schema["maximum"].AsDouble(0).ToString(CultureInfo.InvariantCulture) + ".");
        }

        // ---------------- helpers ----------------

        private static string Join(string path, string key)
        {
            return path.Length == 0 ? key : path + "." + key;
        }

        private static string Name(string path)
        {
            return path.Length == 0 ? "The arguments object" : "'" + path + "'";
        }

        private static List<string> KeyList(JsonValue properties)
        {
            var keys = new List<string>();
            if (properties.IsObject) foreach (string k in properties.Keys) keys.Add(k);
            if (keys.Count == 0) keys.Add("(none)");
            return keys;
        }

        private static string Describe(JsonValue v)
        {
            switch (v.Kind)
            {
                case JsonKind.Null: return "null";
                case JsonKind.Bool: return "a boolean";
                case JsonKind.Number: return "a number";
                case JsonKind.String: return "a string";
                case JsonKind.Array: return "an array";
                case JsonKind.Object: return "an object";
                default: return "an unknown value";
            }
        }
    }
}
