using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AB.RevitMcp.Contracts.Json
{
    public enum JsonKind
    {
        Null = 0,
        Bool = 1,
        Number = 2,
        String = 3,
        Array = 4,
        Object = 5
    }

    /// <summary>Thrown when JSON text cannot be parsed.</summary>
    public sealed class JsonException : Exception
    {
        public JsonException(string message) : base(message) { }
        public JsonException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// A minimal, dependency-free JSON document object model shared by the Revit add-in and the
    /// MCP server. Objects preserve key insertion order (stable, diff-able tool schemas) and
    /// integers preserve their textual form so 64-bit element ids survive a round trip exactly.
    /// </summary>
    public sealed class JsonValue
    {
        public static readonly JsonValue Null = new JsonValue();
        public static readonly JsonValue True = new JsonValue(true);
        public static readonly JsonValue False = new JsonValue(false);

        private readonly JsonKind _kind;
        private readonly bool _bool;
        private readonly double _number;
        private readonly string _rawNumber;
        private readonly string _string;
        private readonly List<JsonValue> _array;
        private readonly Dictionary<string, JsonValue> _map;
        private readonly List<string> _order;

        // ---------------- construction ----------------

        private JsonValue() { _kind = JsonKind.Null; }

        private JsonValue(bool value) { _kind = JsonKind.Bool; _bool = value; }

        private JsonValue(double value, string raw)
        {
            _kind = JsonKind.Number;
            _number = value;
            _rawNumber = raw;
        }

        private JsonValue(string value)
        {
            _kind = JsonKind.String;
            _string = value ?? string.Empty;
        }

        private JsonValue(List<JsonValue> items)
        {
            _kind = JsonKind.Array;
            _array = items ?? new List<JsonValue>();
        }

        private JsonValue(Dictionary<string, JsonValue> map, List<string> order)
        {
            _kind = JsonKind.Object;
            _map = map ?? new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            _order = order ?? new List<string>();
        }

        public static JsonValue Bool(bool value) { return value ? True : False; }

        public static JsonValue Num(double value)
        {
            // JSON has no NaN / Infinity literals - degrade to null rather than emit invalid JSON.
            if (double.IsNaN(value) || double.IsInfinity(value)) return Null;
            return new JsonValue(value, null);
        }

        public static JsonValue Num(long value)
        {
            return new JsonValue(value, value.ToString(CultureInfo.InvariantCulture));
        }

        public static JsonValue Num(int value) { return Num((long)value); }

        public static JsonValue Str(string value)
        {
            if (value == null) return Null;
            return new JsonValue(value);
        }

        public static JsonValue NewArray() { return new JsonValue(new List<JsonValue>()); }

        public static JsonValue NewArray(IEnumerable<JsonValue> items)
        {
            var list = new List<JsonValue>();
            if (items != null)
            {
                foreach (var i in items) list.Add(i ?? Null);
            }
            return new JsonValue(list);
        }

        public static JsonValue NewObject()
        {
            return new JsonValue(new Dictionary<string, JsonValue>(StringComparer.Ordinal), new List<string>());
        }

        internal static JsonValue RawNumber(double value, string raw) { return new JsonValue(value, raw); }

        // ---------------- inspection ----------------

        public JsonKind Kind { get { return _kind; } }
        public bool IsNull { get { return _kind == JsonKind.Null; } }
        public bool IsObject { get { return _kind == JsonKind.Object; } }
        public bool IsArray { get { return _kind == JsonKind.Array; } }
        public bool IsString { get { return _kind == JsonKind.String; } }
        public bool IsNumber { get { return _kind == JsonKind.Number; } }
        public bool IsBool { get { return _kind == JsonKind.Bool; } }

        public int Count
        {
            get
            {
                if (_kind == JsonKind.Array) return _array.Count;
                if (_kind == JsonKind.Object) return _order.Count;
                return 0;
            }
        }

        public IEnumerable<string> Keys
        {
            get { return _kind == JsonKind.Object ? (IEnumerable<string>)_order : new string[0]; }
        }

        public IEnumerable<JsonValue> Items
        {
            get { return _kind == JsonKind.Array ? (IEnumerable<JsonValue>)_array : new JsonValue[0]; }
        }

        public IEnumerable<KeyValuePair<string, JsonValue>> Members
        {
            get
            {
                if (_kind != JsonKind.Object) yield break;
                for (int i = 0; i < _order.Count; i++)
                {
                    string k = _order[i];
                    yield return new KeyValuePair<string, JsonValue>(k, _map[k]);
                }
            }
        }

        // ---------------- array access ----------------

        public JsonValue this[int index]
        {
            get
            {
                if (_kind != JsonKind.Array || index < 0 || index >= _array.Count) return Null;
                return _array[index];
            }
        }

        public JsonValue Add(JsonValue value)
        {
            if (_kind != JsonKind.Array) throw new JsonException("Add(value) is only valid on a JSON array.");
            _array.Add(value ?? Null);
            return this;
        }

        public JsonValue Add(string value) { return Add(Str(value)); }
        public JsonValue Add(double value) { return Add(Num(value)); }
        public JsonValue Add(long value) { return Add(Num(value)); }
        public JsonValue Add(bool value) { return Add(Bool(value)); }

        // ---------------- object access ----------------

        /// <summary>Member lookup. Returns <see cref="Null"/> (never a CLR null) for a missing key.</summary>
        public JsonValue this[string name]
        {
            get
            {
                JsonValue v;
                if (_kind == JsonKind.Object && name != null && _map.TryGetValue(name, out v)) return v;
                return Null;
            }
        }

        public bool Has(string name)
        {
            return _kind == JsonKind.Object && name != null && _map.ContainsKey(name);
        }

        /// <summary>True when the member exists AND is not JSON null.</summary>
        public bool HasValue(string name)
        {
            JsonValue v;
            return _kind == JsonKind.Object && name != null && _map.TryGetValue(name, out v) && !v.IsNull;
        }

        public JsonValue Set(string name, JsonValue value)
        {
            if (_kind != JsonKind.Object) throw new JsonException("Set(name, value) is only valid on a JSON object.");
            if (name == null) throw new ArgumentNullException("name");
            if (!_map.ContainsKey(name)) _order.Add(name);
            _map[name] = value ?? Null;
            return this;
        }

        public JsonValue Set(string name, string value) { return Set(name, Str(value)); }
        public JsonValue Set(string name, double value) { return Set(name, Num(value)); }
        public JsonValue Set(string name, long value) { return Set(name, Num(value)); }
        public JsonValue Set(string name, int value) { return Set(name, Num((long)value)); }
        public JsonValue Set(string name, bool value) { return Set(name, Bool(value)); }

        public bool Remove(string name)
        {
            if (_kind != JsonKind.Object || name == null) return false;
            if (!_map.Remove(name)) return false;
            _order.Remove(name);
            return true;
        }

        // ---------------- conversions ----------------

        public string AsString(string fallback = null)
        {
            switch (_kind)
            {
                case JsonKind.String: return _string;
                case JsonKind.Number: return NumberText();
                case JsonKind.Bool: return _bool ? "true" : "false";
                default: return fallback;
            }
        }

        public double AsDouble(double fallback = 0d)
        {
            if (_kind == JsonKind.Number) return _number;
            if (_kind == JsonKind.Bool) return _bool ? 1d : 0d;
            if (_kind == JsonKind.String)
            {
                double d;
                if (double.TryParse(_string, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            }
            return fallback;
        }

        public long AsLong(long fallback = 0L)
        {
            if (_kind == JsonKind.Number)
            {
                if (_rawNumber != null)
                {
                    long l;
                    if (long.TryParse(_rawNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
                }
                if (_number >= -9.2233720368547758E18 && _number <= 9.2233720368547758E18)
                    return (long)Math.Round(_number, MidpointRounding.AwayFromZero);
                return fallback;
            }
            if (_kind == JsonKind.String)
            {
                long l;
                if (long.TryParse(_string, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
                double d;
                if (double.TryParse(_string, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    return (long)Math.Round(d, MidpointRounding.AwayFromZero);
            }
            if (_kind == JsonKind.Bool) return _bool ? 1L : 0L;
            return fallback;
        }

        public int AsInt(int fallback = 0)
        {
            long l = AsLong(fallback);
            if (l > int.MaxValue) return int.MaxValue;
            if (l < int.MinValue) return int.MinValue;
            return (int)l;
        }

        public bool AsBool(bool fallback = false)
        {
            if (_kind == JsonKind.Bool) return _bool;
            if (_kind == JsonKind.Number) return Math.Abs(_number) > double.Epsilon;
            if (_kind == JsonKind.String)
            {
                if (string.Equals(_string, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(_string, "false", StringComparison.OrdinalIgnoreCase)) return false;
                if (string.Equals(_string, "1", StringComparison.Ordinal)) return true;
                if (string.Equals(_string, "0", StringComparison.Ordinal)) return false;
            }
            return fallback;
        }

        internal string NumberText()
        {
            if (_rawNumber != null) return _rawNumber;
            return _number.ToString("R", CultureInfo.InvariantCulture);
        }

        internal bool BoolValue { get { return _bool; } }
        internal string StringValue { get { return _string; } }

        // ---------------- serialization ----------------

        public string ToJson(bool indented = false)
        {
            var sb = new StringBuilder(256);
            JsonWriter.Write(this, sb, indented, 0);
            return sb.ToString();
        }

        public void WriteTo(StringBuilder sb, bool indented = false)
        {
            JsonWriter.Write(this, sb, indented, 0);
        }

        public override string ToString() { return ToJson(false); }

        public static JsonValue Parse(string text) { return JsonParser.Parse(text); }

        public static bool TryParse(string text, out JsonValue value)
        {
            try { value = JsonParser.Parse(text); return true; }
            catch (JsonException) { value = Null; return false; }
            catch (ArgumentException) { value = Null; return false; }
        }

        /// <summary>Deep copy - used when a cached schema is handed to a caller that may mutate it.</summary>
        public JsonValue Clone()
        {
            switch (_kind)
            {
                case JsonKind.Array:
                    {
                        var a = NewArray();
                        for (int i = 0; i < _array.Count; i++) a.Add(_array[i].Clone());
                        return a;
                    }
                case JsonKind.Object:
                    {
                        var o = NewObject();
                        for (int i = 0; i < _order.Count; i++) o.Set(_order[i], _map[_order[i]].Clone());
                        return o;
                    }
                default:
                    return this; // scalars are immutable
            }
        }
    }
}
