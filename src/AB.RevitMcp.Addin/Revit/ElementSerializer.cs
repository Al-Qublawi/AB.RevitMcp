using System;
using System.Collections.Generic;
using System.Linq;
using AB.RevitMcp.Contracts.Json;
using Autodesk.Revit.DB;

namespace AB.RevitMcp.Addin.Revit
{
    /// <summary>What to include when serializing an element. Defaults are deliberately lean.</summary>
    public sealed class SerializeOptions
    {
        public bool IncludeBoundingBox;
        public bool IncludeLocation;
        public bool IncludeAllParameters;
        public bool IncludeTypeParameters;
        public bool IncludeReadOnly = true;
        public bool IncludeEmpty;
        public List<string> ParameterNames;

        public static SerializeOptions Summary { get { return new SerializeOptions(); } }
    }

    /// <summary>
    /// Element -> JSON. Solids, faces, meshes and edges are NEVER serialized: an AI cannot use
    /// them and they would blow the frame budget. Geometry is reduced to a bounding box and a
    /// location point or curve, in millimetres.
    /// </summary>
    public static class ElementSerializer
    {
        public static JsonValue Summarize(Element element, SerializeOptions options)
        {
            if (element == null) return JsonValue.Null;
            options = options ?? SerializeOptions.Summary;

            Document doc = element.Document;
            JsonValue o = JsonValue.NewObject();

            o.Set("id", Compat.IdValue(element.Id));
            o.Set("name", SafeName(element));

            Category category = null;
            try { category = element.Category; } catch (Exception) { }
            o.Set("category", category != null ? category.Name : null);

            // Type / family identity
            ElementId typeId = null;
            try { typeId = element.GetTypeId(); } catch (Exception) { }
            if (Compat.IsValid(typeId))
            {
                ElementType type = doc.GetElement(typeId) as ElementType;
                if (type != null)
                {
                    o.Set("typeId", Compat.IdValue(typeId));
                    o.Set("typeName", type.Name);
                    string family = null;
                    try { family = type.FamilyName; } catch (Exception) { }
                    if (!string.IsNullOrEmpty(family)) o.Set("familyName", family);
                }
            }

            // Level
            string levelName = LevelNameOf(element, doc);
            if (levelName != null) o.Set("level", levelName);

            // Workset (only meaningful in a workshared model)
            if (doc.IsWorkshared)
            {
                try
                {
                    WorksetId wsId = element.WorksetId;
                    Workset ws = doc.GetWorksetTable().GetWorkset(wsId);
                    if (ws != null) o.Set("workset", ws.Name);
                }
                catch (Exception) { }
            }

            // Two identifiers every BIM manager reaches for first
            string mark = QuickString(element, BuiltInParameter.ALL_MODEL_MARK);
            if (!string.IsNullOrEmpty(mark)) o.Set("mark", mark);

            if (element is ElementType) o.Set("isType", true);
            try { if (element.Pinned) o.Set("pinned", true); } catch (Exception) { }

            if (options.IncludeLocation)
            {
                JsonValue loc = LocationToJson(element);
                if (!loc.IsNull) o.Set("location", loc);
            }

            if (options.IncludeBoundingBox)
            {
                JsonValue bb = BoundingBoxToJson(element);
                if (!bb.IsNull) o.Set("boundingBox", bb);
            }

            if (options.IncludeAllParameters || (options.ParameterNames != null && options.ParameterNames.Count > 0))
            {
                o.Set("parameters", ParametersToJson(element, options));
            }

            return o;
        }

        // ---------------- identity helpers ----------------

        public static string SafeName(Element element)
        {
            try { return element.Name; }
            catch (Exception) { return null; }   // some elements throw on Name
        }

        public static string LevelNameOf(Element element, Document doc)
        {
            try
            {
                ElementId levelId = element.LevelId;
                if (Compat.IsValid(levelId))
                {
                    Element level = doc.GetElement(levelId);
                    if (level != null) return level.Name;
                }
            }
            catch (Exception) { }

            // Many families report their level only through a parameter.
            BuiltInParameter[] levelParams =
            {
                BuiltInParameter.FAMILY_LEVEL_PARAM,
                BuiltInParameter.SCHEDULE_LEVEL_PARAM,
                BuiltInParameter.WALL_BASE_CONSTRAINT,
                BuiltInParameter.LEVEL_PARAM,
                BuiltInParameter.ROOM_LEVEL_ID
            };
            for (int i = 0; i < levelParams.Length; i++)
            {
                try
                {
                    Parameter p = element.get_Parameter(levelParams[i]);
                    if (p != null && p.StorageType == StorageType.ElementId && Compat.IsValid(p.AsElementId()))
                    {
                        Element level = doc.GetElement(p.AsElementId());
                        if (level is Level) return level.Name;
                    }
                }
                catch (Exception) { }
            }
            return null;
        }

        public static string QuickString(Element element, BuiltInParameter bip)
        {
            try
            {
                Parameter p = element.get_Parameter(bip);
                return p != null && p.HasValue ? p.AsString() : null;
            }
            catch (Exception) { return null; }
        }

        // ---------------- geometry (lightweight only) ----------------

        public static JsonValue BoundingBoxToJson(Element element)
        {
            try
            {
                BoundingBoxXYZ bb = element.get_BoundingBox(null);   // model extents, view-independent
                return Metric.BoundingBoxToMm(bb);
            }
            catch (Exception) { return JsonValue.Null; }
        }

        public static JsonValue LocationToJson(Element element)
        {
            try
            {
                Location loc = element.Location;
                if (loc == null) return JsonValue.Null;

                LocationPoint lp = loc as LocationPoint;
                if (lp != null)
                {
                    JsonValue o = J.O("kind", "point", "point", Metric.PointToMm(lp.Point));
                    try { o.Set("rotationDegrees", Metric.R(Metric.RadToDeg(lp.Rotation), 3)); }
                    catch (Exception) { }
                    return o;
                }

                LocationCurve lc = loc as LocationCurve;
                if (lc != null && lc.Curve != null)
                {
                    Curve c = lc.Curve;
                    JsonValue o = J.O("kind", "curve", "curveType", c.GetType().Name);
                    if (c.IsBound)
                    {
                        o.Set("start", Metric.PointToMm(c.GetEndPoint(0)));
                        o.Set("end", Metric.PointToMm(c.GetEndPoint(1)));
                    }
                    try { o.Set("lengthMm", Metric.R(Metric.FeetToMm(c.Length))); } catch (Exception) { }
                    return o;
                }

                return JsonValue.Null;
            }
            catch (Exception) { return JsonValue.Null; }
        }

        // ---------------- parameters ----------------

        public static JsonValue ParametersToJson(Element element, SerializeOptions options)
        {
            JsonValue arr = JsonValue.NewArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AppendParameters(element, arr, seen, options, false);

            if (options.IncludeTypeParameters)
            {
                try
                {
                    Element type = element.Document.GetElement(element.GetTypeId());
                    if (type != null) AppendParameters(type, arr, seen, options, true);
                }
                catch (Exception) { }
            }

            return arr;
        }

        private static void AppendParameters(Element element, JsonValue arr, HashSet<string> seen,
                                             SerializeOptions options, bool isTypeParameter)
        {
            IList<Parameter> parameters;
            try { parameters = new List<Parameter>(element.Parameters.Cast<Parameter>()); }
            catch (Exception) { return; }

            for (int i = 0; i < parameters.Count; i++)
            {
                Parameter p = parameters[i];
                if (p == null || p.Definition == null) continue;

                string name = p.Definition.Name;
                if (string.IsNullOrEmpty(name)) continue;

                if (options.ParameterNames != null && options.ParameterNames.Count > 0)
                {
                    bool wanted = false;
                    for (int k = 0; k < options.ParameterNames.Count; k++)
                    {
                        if (string.Equals(options.ParameterNames[k], name, StringComparison.OrdinalIgnoreCase))
                        { wanted = true; break; }
                    }
                    if (!wanted) continue;
                }

                if (!options.IncludeReadOnly && p.IsReadOnly) continue;
                if (seen.Contains(name)) continue;     // instance parameter wins over the type's

                JsonValue jp = ParameterToJson(p, element.Document, isTypeParameter);
                if (jp.IsNull) continue;

                bool hasValue = jp.HasValue("value");
                if (!options.IncludeEmpty && !hasValue) continue;

                seen.Add(name);
                arr.Add(jp);
            }
        }

        /// <summary>
        /// One parameter, unit-converted. Reports both the normalised metric <c>value</c> and the
        /// <c>displayValue</c> exactly as Revit shows it in the properties palette.
        /// </summary>
        public static JsonValue ParameterToJson(Parameter p, Document doc, bool isTypeParameter = false)
        {
            if (p == null || p.Definition == null) return JsonValue.Null;

            JsonValue o = JsonValue.NewObject();
            o.Set("name", p.Definition.Name);
            o.Set("storageType", p.StorageType.ToString());
            if (p.IsReadOnly) o.Set("readOnly", true);
            if (isTypeParameter) o.Set("scope", "type");
            try { if (p.IsShared) o.Set("shared", true); } catch (Exception) { }

            InternalDefinition internalDef = p.Definition as InternalDefinition;
            if (internalDef != null)
            {
                try
                {
                    BuiltInParameter bip = internalDef.BuiltInParameter;
                    if (bip != BuiltInParameter.INVALID) o.Set("builtIn", bip.ToString());
                }
                catch (Exception) { }
            }

            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String:
                        {
                            string s = p.AsString();
                            if (!string.IsNullOrEmpty(s)) o.Set("value", s);
                            break;
                        }

                    case StorageType.Integer:
                        {
                            if (!p.HasValue) break;
                            int i = p.AsInteger();
                            if (Metric.IsYesNo(p.Definition)) o.Set("value", i != 0);
                            else o.Set("value", (long)i);
                            break;
                        }

                    case StorageType.Double:
                        {
                            if (!p.HasValue) break;
                            double raw = p.AsDouble();
                            MetricUnit unit = Metric.UnitOf(p.Definition);
                            o.Set("value", Metric.R(Metric.FromInternal(raw, unit), 4));
                            string label = Metric.Label(unit);
                            if (label != null) o.Set("unit", label);
                            break;
                        }

                    case StorageType.ElementId:
                        {
                            ElementId id = p.AsElementId();
                            if (!Compat.IsValid(id)) break;
                            o.Set("value", Compat.IdValue(id));
                            Element referenced = doc != null ? doc.GetElement(id) : null;
                            if (referenced != null) o.Set("valueName", SafeName(referenced));
                            break;
                        }
                }

                string display = null;
                try { display = p.AsValueString(); } catch (Exception) { }
                if (!string.IsNullOrEmpty(display)) o.Set("displayValue", display);
            }
            catch (Exception ex)
            {
                o.Set("error", ex.Message);
            }

            return o;
        }
    }
}
