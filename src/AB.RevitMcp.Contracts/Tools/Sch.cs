using System.Collections.Generic;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;

namespace AB.RevitMcp.Contracts.Tools
{
    /// <summary>
    /// Compact builders for the JSON Schema (draft 2020-12 subset) that MCP clients use to
    /// validate tool arguments. Everything dimensional is documented in metric units.
    /// </summary>
    public static class Sch
    {
        public static JsonValue Str(string description = null, string[] enumValues = null, string defaultValue = null)
        {
            JsonValue s = JsonValue.NewObject();
            s.Set("type", "string");
            if (description != null) s.Set("description", description);
            if (enumValues != null && enumValues.Length > 0) s.Set("enum", J.AStrings(enumValues));
            if (defaultValue != null) s.Set("default", defaultValue);
            return s;
        }

        public static JsonValue Num(string description = null, double? min = null, double? max = null, double? defaultValue = null)
        {
            JsonValue s = JsonValue.NewObject();
            s.Set("type", "number");
            if (description != null) s.Set("description", description);
            if (min.HasValue) s.Set("minimum", min.Value);
            if (max.HasValue) s.Set("maximum", max.Value);
            if (defaultValue.HasValue) s.Set("default", defaultValue.Value);
            return s;
        }

        public static JsonValue Int(string description = null, long? min = null, long? max = null, long? defaultValue = null)
        {
            JsonValue s = JsonValue.NewObject();
            s.Set("type", "integer");
            if (description != null) s.Set("description", description);
            if (min.HasValue) s.Set("minimum", min.Value);
            if (max.HasValue) s.Set("maximum", max.Value);
            if (defaultValue.HasValue) s.Set("default", defaultValue.Value);
            return s;
        }

        public static JsonValue Bool(string description = null, bool? defaultValue = null)
        {
            JsonValue s = JsonValue.NewObject();
            s.Set("type", "boolean");
            if (description != null) s.Set("description", description);
            if (defaultValue.HasValue) s.Set("default", defaultValue.Value);
            return s;
        }

        public static JsonValue Arr(JsonValue items, string description = null, int? minItems = null, int? maxItems = null)
        {
            JsonValue s = JsonValue.NewObject();
            s.Set("type", "array");
            if (description != null) s.Set("description", description);
            s.Set("items", items ?? JsonValue.NewObject());
            if (minItems.HasValue) s.Set("minItems", minItems.Value);
            if (maxItems.HasValue) s.Set("maxItems", maxItems.Value);
            return s;
        }

        /// <summary>
        /// Builds an object schema. <paramref name="propertyPairs"/> alternates property name and
        /// schema: <c>Obj("desc", new[]{"a"}, "a", Sch.Str(), "b", Sch.Int())</c>.
        /// </summary>
        public static JsonValue Obj(string description, string[] required, params object[] propertyPairs)
        {
            JsonValue s = JsonValue.NewObject();
            s.Set("type", "object");
            if (description != null) s.Set("description", description);

            JsonValue props = JsonValue.NewObject();
            for (int i = 0; i + 1 < propertyPairs.Length; i += 2)
            {
                string name = propertyPairs[i] as string;
                JsonValue schema = propertyPairs[i + 1] as JsonValue;
                if (name == null || schema == null) continue;
                props.Set(name, schema);
            }
            s.Set("properties", props);
            if (required != null && required.Length > 0) s.Set("required", J.AStrings(required));
            s.Set("additionalProperties", false);
            return s;
        }

        /// <summary>Schema for a tool that accepts no arguments.</summary>
        public static JsonValue NoArgs()
        {
            JsonValue s = JsonValue.NewObject();
            s.Set("type", "object");
            s.Set("properties", JsonValue.NewObject());
            s.Set("additionalProperties", false);
            return s;
        }

        // ---------- reusable domain fragments ----------

        /// <summary>A 3D point in project coordinates, millimetres.</summary>
        public static JsonValue Point(string description)
        {
            return Obj(description ?? "3D point in project coordinates (millimetres).",
                new[] { "x", "y" },
                "x", Num("X in mm."),
                "y", Num("Y in mm."),
                "z", Num("Z in mm (elevation). Defaults to 0.", null, null, 0));
        }

        /// <summary>A 2D point in sheet space, millimetres from the sheet origin.</summary>
        public static JsonValue SheetPoint(string description)
        {
            return Obj(description ?? "Point on the sheet in millimetres from the sheet origin.",
                new[] { "x", "y" },
                "x", Num("X in mm."),
                "y", Num("Y in mm."));
        }

        public static JsonValue ElementId(string description)
        {
            return Int(description ?? "Revit element id.", 1);
        }

        public static JsonValue ElementIds(string description, int maxItems = 2000)
        {
            return Arr(Int("Revit element id.", 1), description ?? "Revit element ids.", 1, maxItems);
        }

        public static JsonValue Limit()
        {
            return Int("Maximum items to return. Default " + IpcConstants.DefaultPageLimit +
                       ", hard maximum " + IpcConstants.MaxPageLimit + ".",
                       1, IpcConstants.MaxPageLimit, IpcConstants.DefaultPageLimit);
        }

        public static JsonValue Offset()
        {
            return Int("Zero-based index of the first item to return (pagination).", 0, null, 0);
        }

        public static JsonValue Confirm()
        {
            return Bool("Must be exactly true. Safety interlock for a destructive operation - " +
                        "ask the human before setting it.", false);
        }

        public static JsonValue CategoryName()
        {
            return Str("Revit category, either the display name (\"Walls\", \"Structural Columns\") " +
                       "or the BuiltInCategory enum name (\"OST_Walls\"). Case-insensitive.");
        }

        /// <summary>Merges extra properties into an existing object schema (used for shared paging args).</summary>
        public static JsonValue WithPaging(JsonValue objectSchema)
        {
            JsonValue props = objectSchema["properties"];
            if (props.IsObject)
            {
                props.Set("limit", Limit());
                props.Set("offset", Offset());
            }
            return objectSchema;
        }

        public static string[] Req(params string[] names) { return names; }

        public static List<string> ToList(params string[] names)
        {
            return new List<string>(names);
        }
    }
}
