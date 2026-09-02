using System.Collections.Generic;

namespace AB.RevitMcp.Contracts.Json
{
    /// <summary>
    /// Terse factory helpers. Tool handlers build a lot of JSON, so <c>J.O()</c> / <c>J.A()</c>
    /// keeps them readable: <c>J.O("id", 1234, "name", "Basic Wall")</c>.
    /// </summary>
    public static class J
    {
        public static JsonValue S(string v) { return JsonValue.Str(v); }
        public static JsonValue N(double v) { return JsonValue.Num(v); }
        public static JsonValue N(long v) { return JsonValue.Num(v); }
        public static JsonValue N(int v) { return JsonValue.Num((long)v); }
        public static JsonValue B(bool v) { return JsonValue.Bool(v); }
        public static JsonValue Null { get { return JsonValue.Null; } }

        /// <summary>Builds an object from alternating name / value arguments.</summary>
        public static JsonValue O(params object[] nameValuePairs)
        {
            JsonValue o = JsonValue.NewObject();
            if (nameValuePairs == null) return o;
            for (int i = 0; i + 1 < nameValuePairs.Length; i += 2)
            {
                string name = nameValuePairs[i] as string;
                if (name == null) continue;
                o.Set(name, Wrap(nameValuePairs[i + 1]));
            }
            return o;
        }

        public static JsonValue A(params object[] values)
        {
            JsonValue a = JsonValue.NewArray();
            if (values == null) return a;
            for (int i = 0; i < values.Length; i++) a.Add(Wrap(values[i]));
            return a;
        }

        public static JsonValue A(IEnumerable<JsonValue> values) { return JsonValue.NewArray(values); }

        public static JsonValue AStrings(IEnumerable<string> values)
        {
            JsonValue a = JsonValue.NewArray();
            if (values != null) foreach (string s in values) a.Add(JsonValue.Str(s));
            return a;
        }

        public static JsonValue ALongs(IEnumerable<long> values)
        {
            JsonValue a = JsonValue.NewArray();
            if (values != null) foreach (long l in values) a.Add(JsonValue.Num(l));
            return a;
        }

        /// <summary>Boxes a CLR value into a <see cref="JsonValue"/>. Unknown types fall back to ToString().</summary>
        public static JsonValue Wrap(object value)
        {
            if (value == null) return JsonValue.Null;
            JsonValue jv = value as JsonValue;
            if (jv != null) return jv;

            if (value is string) return JsonValue.Str((string)value);
            if (value is bool) return JsonValue.Bool((bool)value);
            if (value is int) return JsonValue.Num((long)(int)value);
            if (value is long) return JsonValue.Num((long)value);
            if (value is short) return JsonValue.Num((long)(short)value);
            if (value is byte) return JsonValue.Num((long)(byte)value);
            if (value is double) return JsonValue.Num((double)value);
            if (value is float) return JsonValue.Num((double)(float)value);
            if (value is decimal) return JsonValue.Num((double)(decimal)value);
            return JsonValue.Str(value.ToString());
        }
    }
}
