using System;
using System.Collections.Generic;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;
using Autodesk.Revit.DB;

namespace AB.RevitMcp.Addin.Revit
{
    /// <summary>
    /// Argument validation. Every value an MCP client sends is checked here BEFORE any Revit API
    /// call happens, so a malformed request fails with a clear message instead of throwing deep
    /// inside the Revit API (or worse, half-applying a transaction).
    /// </summary>
    public static class Args
    {
        // ---------------- strings ----------------

        public static string Str(JsonValue args, string name, bool required = false,
                                 string defaultValue = null, string[] allowed = null)
        {
            JsonValue v = args[name];
            if (v.IsNull || (v.IsString && v.AsString("").Length == 0))
            {
                if (required) throw ToolException.Missing(name);
                return defaultValue;
            }
            if (!v.IsString && !v.IsNumber && !v.IsBool)
                throw ToolException.Invalid(name, "expected a string.");

            string s = v.AsString("");
            if (allowed != null && allowed.Length > 0)
            {
                for (int i = 0; i < allowed.Length; i++)
                    if (string.Equals(s, allowed[i], StringComparison.OrdinalIgnoreCase)) return allowed[i];
                throw ToolException.Invalid(name, "must be one of: " + string.Join(", ", allowed) + ". Got '" + s + "'.");
            }
            return s;
        }

        public static List<string> StrList(JsonValue args, string name, int maxItems = 200)
        {
            JsonValue v = args[name];
            var result = new List<string>();
            if (v.IsNull) return result;
            if (v.IsString) { result.Add(v.AsString("")); return result; }
            if (!v.IsArray) throw ToolException.Invalid(name, "expected an array of strings.");
            foreach (JsonValue item in v.Items)
            {
                if (result.Count >= maxItems)
                    throw ToolException.Invalid(name, "at most " + maxItems + " items are allowed.");
                string s = item.AsString(null);
                if (!string.IsNullOrEmpty(s)) result.Add(s);
            }
            return result;
        }

        // ---------------- numbers ----------------

        public static double Num(JsonValue args, string name, bool required = false, double defaultValue = 0,
                                 double? min = null, double? max = null)
        {
            JsonValue v = args[name];
            if (v.IsNull)
            {
                if (required) throw ToolException.Missing(name);
                return defaultValue;
            }
            if (!v.IsNumber && !v.IsString) throw ToolException.Invalid(name, "expected a number.");

            double d = v.AsDouble(double.NaN);
            if (double.IsNaN(d) || double.IsInfinity(d))
                throw ToolException.Invalid(name, "expected a finite number, got '" + v.AsString("") + "'.");
            if (min.HasValue && d < min.Value)
                throw ToolException.Invalid(name, "must be >= " + min.Value + ", got " + d + ".");
            if (max.HasValue && d > max.Value)
                throw ToolException.Invalid(name, "must be <= " + max.Value + ", got " + d + ".");
            return d;
        }

        public static int Int(JsonValue args, string name, bool required = false, int defaultValue = 0,
                              int? min = null, int? max = null)
        {
            double d = Num(args, name, required, defaultValue,
                           min.HasValue ? (double?)min.Value : null,
                           max.HasValue ? (double?)max.Value : null);
            return (int)Math.Round(d, MidpointRounding.AwayFromZero);
        }

        public static long Long(JsonValue args, string name, bool required = false, long defaultValue = 0)
        {
            JsonValue v = args[name];
            if (v.IsNull)
            {
                if (required) throw ToolException.Missing(name);
                return defaultValue;
            }
            return v.AsLong(defaultValue);
        }

        public static bool Bool(JsonValue args, string name, bool defaultValue = false)
        {
            JsonValue v = args[name];
            if (v.IsNull) return defaultValue;
            return v.AsBool(defaultValue);
        }

        // ---------------- element ids ----------------

        public static List<long> IdValues(JsonValue args, string name, bool required = true, int maxItems = 5000)
        {
            JsonValue v = args[name];
            var result = new List<long>();

            if (v.IsNull)
            {
                if (required) throw ToolException.Missing(name);
                return result;
            }

            if (v.IsNumber) { result.Add(v.AsLong(0)); }
            else if (v.IsArray)
            {
                foreach (JsonValue item in v.Items)
                {
                    if (result.Count >= maxItems)
                        throw ToolException.Invalid(name, "at most " + maxItems + " ids are allowed per call. " +
                                                          "Split the work into pages.");
                    long id = item.AsLong(0);
                    if (id <= 0)
                        throw ToolException.Invalid(name, "'" + item.AsString("") + "' is not a valid element id.");
                    result.Add(id);
                }
            }
            else
            {
                throw ToolException.Invalid(name, "expected an element id or an array of element ids.");
            }

            if (required && result.Count == 0)
                throw ToolException.Invalid(name, "at least one element id is required.");
            return result;
        }

        /// <summary>Resolves ids to elements, reporting the ones that do not exist rather than throwing.</summary>
        public static List<Element> Elements(Document doc, List<long> ids, out List<long> missing)
        {
            missing = new List<long>();
            var elements = new List<Element>();
            for (int i = 0; i < ids.Count; i++)
            {
                Element e = null;
                try { e = doc.GetElement(Compat.ToId(ids[i])); }
                catch (Exception) { e = null; }
                if (e == null) missing.Add(ids[i]); else elements.Add(e);
            }
            return elements;
        }

        // ---------------- geometry ----------------

        /// <summary>Reads a {x,y,z} millimetre point and converts it to Revit internal feet.</summary>
        public static XYZ PointMm(JsonValue args, string name, bool required = true, XYZ defaultValue = null)
        {
            JsonValue v = args[name];
            if (v.IsNull)
            {
                if (required) throw ToolException.Missing(name);
                return defaultValue;
            }
            if (!v.IsObject) throw ToolException.Invalid(name, "expected an object like {\"x\":0,\"y\":0,\"z\":0} in mm.");
            if (v["x"].IsNull || v["y"].IsNull) throw ToolException.Invalid(name, "both 'x' and 'y' are required (mm).");

            double x = v["x"].AsDouble(double.NaN);
            double y = v["y"].AsDouble(double.NaN);
            double z = v["z"].AsDouble(0);
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z) ||
                double.IsInfinity(x) || double.IsInfinity(y) || double.IsInfinity(z))
                throw ToolException.Invalid(name, "coordinates must be finite numbers in millimetres.");

            return Metric.PointFromMm(x, y, z);
        }

        /// <summary>Reads a {dx,dy,dz} millimetre translation and converts it to internal feet.</summary>
        public static XYZ VectorMm(JsonValue args, string name, bool required = true)
        {
            JsonValue v = args[name];
            if (v.IsNull)
            {
                if (required) throw ToolException.Missing(name);
                return XYZ.Zero;
            }
            if (!v.IsObject) throw ToolException.Invalid(name, "expected an object like {\"dx\":0,\"dy\":0,\"dz\":0} in mm.");

            double dx = v["dx"].AsDouble(double.NaN);
            double dy = v["dy"].AsDouble(double.NaN);
            double dz = v["dz"].AsDouble(0);
            if (double.IsNaN(dx) || double.IsNaN(dy) || double.IsNaN(dz))
                throw ToolException.Invalid(name, "'dx' and 'dy' are required and must be finite numbers in mm.");

            return Metric.PointFromMm(dx, dy, dz);
        }

        /// <summary>Reads an ordered boundary of millimetre points as internal-unit XYZ.</summary>
        public static List<XYZ> PointListMm(JsonValue args, string name, int minPoints, int maxPoints)
        {
            JsonValue v = args[name];
            if (v.IsNull) throw ToolException.Missing(name);
            if (!v.IsArray) throw ToolException.Invalid(name, "expected an array of {x,y,z} points in mm.");

            var points = new List<XYZ>();
            int index = 0;
            foreach (JsonValue item in v.Items)
            {
                if (!item.IsObject)
                    throw ToolException.Invalid(name, "item " + index + " is not a point object.");
                double x = item["x"].AsDouble(double.NaN);
                double y = item["y"].AsDouble(double.NaN);
                double z = item["z"].AsDouble(0);
                if (double.IsNaN(x) || double.IsNaN(y))
                    throw ToolException.Invalid(name, "item " + index + " is missing 'x' or 'y'.");
                points.Add(Metric.PointFromMm(x, y, z));
                index++;
            }

            if (points.Count < minPoints)
                throw ToolException.Invalid(name, "at least " + minPoints + " points are required, got " + points.Count + ".");
            if (points.Count > maxPoints)
                throw ToolException.Invalid(name, "at most " + maxPoints + " points are allowed, got " + points.Count + ".");
            return points;
        }

        // ---------------- paging ----------------

        public static int Limit(JsonValue args)
        {
            int limit = Int(args, "limit", false, IpcConstants.DefaultPageLimit, 1, IpcConstants.MaxPageLimit);
            return limit;
        }

        public static int Offset(JsonValue args)
        {
            return Int(args, "offset", false, 0, 0, int.MaxValue);
        }

        // ---------------- safety interlock ----------------

        /// <summary>
        /// The destructive gate. Enforced server-side: a tool marked destructive cannot run unless
        /// the client explicitly sends confirm:true, and "true" as a string does not count.
        /// </summary>
        public static void RequireConfirm(JsonValue args, string toolName, string consequence)
        {
            JsonValue v = args["confirm"];
            if (v.IsBool && v.AsBool(false)) return;
            throw ToolException.NeedsConfirmation(toolName, consequence);
        }
    }
}
