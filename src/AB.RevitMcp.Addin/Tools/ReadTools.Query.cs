using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AB.RevitMcp.Addin.Bridge;
using AB.RevitMcp.Addin.Revit;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;
using Autodesk.Revit.DB;

namespace AB.RevitMcp.Addin.Tools
{
    /// <summary>
    /// READ tools, part 3: the element query surface - filtering, text search, parameter reads,
    /// lightweight geometry and the current selection.
    /// </summary>
    public static partial class ReadTools
    {
        // ==================================================================
        //  revit_query_elements
        // ==================================================================
        public static JsonValue QueryElements(ToolContext ctx)
        {
            Document doc = ctx.Doc;

            string categoryName = Args.Str(ctx.Args, "category");
            string familyName = Args.Str(ctx.Args, "familyName");
            string typeName = Args.Str(ctx.Args, "typeName");
            string levelName = Args.Str(ctx.Args, "levelName");
            string worksetName = Args.Str(ctx.Args, "worksetName");
            string elementKind = Args.Str(ctx.Args, "elementType", false, "instances",
                new[] { "instances", "types", "both" });
            bool activeViewOnly = Args.Bool(ctx.Args, "activeViewOnly", false);
            long viewId = Args.Long(ctx.Args, "viewId", false, 0);
            bool includeBoundingBox = Args.Bool(ctx.Args, "includeBoundingBox", false);
            List<string> parameterNames = Args.StrList(ctx.Args, "parameterNames", 40);

            // ---- build the most selective Revit-side filter we can ----
            ElementId scopeViewId = null;
            if (viewId > 0)
            {
                View view = doc.GetElement(Compat.ToId(viewId)) as View;
                if (view == null) throw ToolException.NotFound("View", viewId.ToString(CultureInfo.InvariantCulture));
                scopeViewId = view.Id;
            }
            else if (activeViewOnly)
            {
                scopeViewId = ctx.ActiveView.Id;
            }

            FilteredElementCollector collector = Selectors.Collector(doc, scopeViewId);

            if (!string.IsNullOrEmpty(categoryName))
                collector = collector.OfCategory(Selectors.ResolveCategory(doc, categoryName));

            if (Paging.EqualsCi(elementKind, "instances")) collector = collector.WhereElementIsNotElementType();
            else if (Paging.EqualsCi(elementKind, "types")) collector = collector.WhereElementIsElementType();

            if (!string.IsNullOrEmpty(levelName))
            {
                Level level = Selectors.FindLevel(doc, levelName);
                // ElementLevelFilter only applies to instances; for "types" it would return nothing.
                if (!Paging.EqualsCi(elementKind, "types"))
                    collector = collector.WherePasses(new ElementLevelFilter(level.Id));
            }

            if (!string.IsNullOrEmpty(worksetName))
            {
                Workset workset = Selectors.FindWorkset(doc, worksetName);
                collector = collector.WherePasses(new ElementWorksetFilter(workset.Id, false));
            }

            // ---- in-memory refinement ----
            JsonValue parameterFilter = ctx.Args["parameterFilter"];
            var options = new SerializeOptions
            {
                IncludeBoundingBox = includeBoundingBox,
                IncludeLocation = true,
                ParameterNames = parameterNames.Count > 0 ? parameterNames : null,
                IncludeTypeParameters = parameterNames.Count > 0,
                IncludeEmpty = false
            };

            var items = new List<JsonValue>();
            int scanned = 0;

            foreach (Element element in collector)
            {
                scanned++;
                if (scanned > 400000) { ctx.AddWarning("Scan stopped after 400,000 elements; narrow the filter."); break; }

                if (!string.IsNullOrEmpty(typeName) || !string.IsNullOrEmpty(familyName))
                {
                    string elementTypeName, elementFamilyName;
                    ResolveTypeNames(doc, element, out elementTypeName, out elementFamilyName);
                    if (!string.IsNullOrEmpty(typeName) && !Paging.EqualsCi(elementTypeName, typeName)) continue;
                    if (!string.IsNullOrEmpty(familyName) && !Paging.EqualsCi(elementFamilyName, familyName)) continue;
                }

                if (parameterFilter.IsObject && !PassesParameterFilter(element, parameterFilter, ctx)) continue;

                items.Add(ElementSerializer.Summarize(element, options));
            }

            JsonValue page = Paging.Page(items, Args.Offset(ctx.Args), Args.Limit(ctx.Args), "elements");
            page.Set("scanned", scanned);
            page.Set("filters", DescribeFilters(categoryName, familyName, typeName, levelName, worksetName,
                                                elementKind, scopeViewId != null));
            return page;
        }

        private static void ResolveTypeNames(Document doc, Element element, out string typeName, out string familyName)
        {
            typeName = null;
            familyName = null;

            ElementType asType = element as ElementType;
            if (asType != null)
            {
                typeName = ElementSerializer.SafeName(asType);
                try { familyName = asType.FamilyName; } catch (Exception) { }
                return;
            }

            try
            {
                ElementType type = doc.GetElement(element.GetTypeId()) as ElementType;
                if (type == null) return;
                typeName = ElementSerializer.SafeName(type);
                try { familyName = type.FamilyName; } catch (Exception) { }
            }
            catch (Exception) { }
        }

        /// <summary>Applies the optional single-parameter predicate from the request.</summary>
        private static bool PassesParameterFilter(Element element, JsonValue filter, ToolContext ctx)
        {
            string name = filter["name"].AsString(null);
            if (string.IsNullOrEmpty(name)) return true;

            string op = filter["op"].AsString("equals");
            string wanted = filter["value"].AsString(null);

            Parameter p = Selectors.FindParameter(element, name);
            if (p == null) return Paging.EqualsCi(op, "isEmpty");   // absent counts as empty

            string actual = ParameterAsComparableString(p);
            bool empty = string.IsNullOrEmpty(actual);

            switch (op.ToLowerInvariant())
            {
                case "isempty": return empty;
                case "isnotempty": return !empty;
                case "equals": return Paging.EqualsCi(actual, wanted);
                case "notequals": return !Paging.EqualsCi(actual, wanted);
                case "contains": return Paging.Matches(actual, wanted);
                case "startswith":
                    return !empty && wanted != null &&
                           actual.StartsWith(wanted, StringComparison.OrdinalIgnoreCase);
                case "greaterthan":
                case "lessthan":
                    {
                        double a, b;
                        if (!double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out a) ||
                            !double.TryParse(wanted, NumberStyles.Float, CultureInfo.InvariantCulture, out b))
                            return false;
                        return Paging.EqualsCi(op, "greaterThan") ? a > b : a < b;
                    }
                default:
                    throw ToolException.Invalid("parameterFilter.op", "'" + op + "' is not a supported operator.");
            }
        }

        /// <summary>
        /// Renders a parameter as a comparable string. Doubles are converted to METRIC first, so a
        /// filter like "Width greaterThan 900" means 900 mm, exactly as the caller expects.
        /// </summary>
        private static string ParameterAsComparableString(Parameter p)
        {
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String:
                        return p.AsString();
                    case StorageType.Integer:
                        return p.HasValue ? p.AsInteger().ToString(CultureInfo.InvariantCulture) : null;
                    case StorageType.Double:
                        if (!p.HasValue) return null;
                        return Metric.FromInternal(p.AsDouble(), Metric.UnitOf(p.Definition))
                                    .ToString("R", CultureInfo.InvariantCulture);
                    case StorageType.ElementId:
                        {
                            ElementId id = p.AsElementId();
                            if (!Compat.IsValid(id)) return null;
                            Element referenced = p.Element != null ? p.Element.Document.GetElement(id) : null;
                            return referenced != null
                                ? ElementSerializer.SafeName(referenced)
                                : Compat.IdValue(id).ToString(CultureInfo.InvariantCulture);
                        }
                    default:
                        return null;
                }
            }
            catch (Exception) { return null; }
        }

        private static JsonValue DescribeFilters(string category, string family, string type, string level,
                                                 string workset, string kind, bool viewScoped)
        {
            JsonValue f = JsonValue.NewObject();
            if (!string.IsNullOrEmpty(category)) f.Set("category", category);
            if (!string.IsNullOrEmpty(family)) f.Set("familyName", family);
            if (!string.IsNullOrEmpty(type)) f.Set("typeName", type);
            if (!string.IsNullOrEmpty(level)) f.Set("levelName", level);
            if (!string.IsNullOrEmpty(workset)) f.Set("worksetName", workset);
            f.Set("elementType", kind);
            if (viewScoped) f.Set("viewScoped", true);
            return f;
        }

        // ==================================================================
        //  revit_search_elements
        // ==================================================================
        public static JsonValue SearchElements(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            string query = Args.Str(ctx.Args, "query", true);
            string categoryName = Args.Str(ctx.Args, "category");
            List<string> fields = Args.StrList(ctx.Args, "fields", 5);
            if (fields.Count == 0) fields.AddRange(new[] { "name", "typeName", "familyName", "mark", "comments" });

            bool searchName = fields.Any(f => Paging.EqualsCi(f, "name"));
            bool searchType = fields.Any(f => Paging.EqualsCi(f, "typeName"));
            bool searchFamily = fields.Any(f => Paging.EqualsCi(f, "familyName"));
            bool searchMark = fields.Any(f => Paging.EqualsCi(f, "mark"));
            bool searchComments = fields.Any(f => Paging.EqualsCi(f, "comments"));

            var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            if (!string.IsNullOrEmpty(categoryName))
                collector = collector.OfCategory(Selectors.ResolveCategory(doc, categoryName));

            var options = new SerializeOptions { IncludeLocation = true };
            var items = new List<JsonValue>();

            foreach (Element element in collector)
            {
                var hits = new List<string>();

                if (searchName && Paging.Matches(ElementSerializer.SafeName(element), query)) hits.Add("name");

                if (searchType || searchFamily)
                {
                    string typeName, familyName;
                    ResolveTypeNames(doc, element, out typeName, out familyName);
                    if (searchType && Paging.Matches(typeName, query)) hits.Add("typeName");
                    if (searchFamily && Paging.Matches(familyName, query)) hits.Add("familyName");
                }

                if (searchMark && Paging.Matches(
                        ElementSerializer.QuickString(element, BuiltInParameter.ALL_MODEL_MARK), query))
                    hits.Add("mark");

                if (searchComments && Paging.Matches(
                        ElementSerializer.QuickString(element, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS), query))
                    hits.Add("comments");

                if (hits.Count == 0) continue;

                JsonValue item = ElementSerializer.Summarize(element, options);
                item.Set("matchedFields", J.AStrings(hits));
                items.Add(item);
            }

            JsonValue page = Paging.Page(items, Args.Offset(ctx.Args), Args.Limit(ctx.Args), "elements");
            page.Set("query", query);
            page.Set("searchedFields", J.AStrings(fields));
            return page;
        }

        // ==================================================================
        //  revit_get_element_parameters
        // ==================================================================
        public static JsonValue GetElementParameters(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            List<long> ids = Args.IdValues(ctx.Args, "elementIds", true, 200);

            var options = new SerializeOptions
            {
                IncludeAllParameters = true,
                IncludeTypeParameters = Args.Bool(ctx.Args, "includeTypeParameters", true),
                IncludeReadOnly = Args.Bool(ctx.Args, "includeReadOnly", true),
                IncludeEmpty = Args.Bool(ctx.Args, "includeEmpty", false),
                IncludeLocation = false,
                ParameterNames = Args.StrList(ctx.Args, "parameterNames", 80)
            };
            if (options.ParameterNames.Count == 0) options.ParameterNames = null;

            List<long> missing;
            List<Element> elements = Args.Elements(doc, ids, out missing);

            JsonValue results = JsonValue.NewArray();
            for (int i = 0; i < elements.Count; i++)
                results.Add(ElementSerializer.Summarize(elements[i], options));

            JsonValue result = J.O(
                "elements", results,
                "count", results.Count,
                "units", Metric.Convention());

            if (missing.Count > 0)
            {
                result.Set("notFound", J.ALongs(missing));
                ctx.AddWarning(missing.Count + " element id(s) do not exist in this model.");
            }
            return result;
        }

        // ==================================================================
        //  revit_get_element_geometry
        // ==================================================================
        public static JsonValue GetElementGeometry(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            List<long> ids = Args.IdValues(ctx.Args, "elementIds", true, 500);
            bool includeBoundingBox = Args.Bool(ctx.Args, "includeBoundingBox", true);
            bool includeLocation = Args.Bool(ctx.Args, "includeLocation", true);

            List<long> missing;
            List<Element> elements = Args.Elements(doc, ids, out missing);

            JsonValue items = JsonValue.NewArray();
            for (int i = 0; i < elements.Count; i++)
            {
                Element element = elements[i];
                JsonValue g = J.O(
                    "id", Compat.IdValue(element.Id),
                    "name", ElementSerializer.SafeName(element),
                    "category", Selectors.CategoryName(element));

                if (includeBoundingBox)
                {
                    JsonValue bb = ElementSerializer.BoundingBoxToJson(element);
                    g.Set("boundingBox", bb);
                    if (bb.IsNull) g.Set("boundingBoxNote", "This element has no model-level bounding box.");
                }

                if (includeLocation) g.Set("location", ElementSerializer.LocationToJson(element));

                // Orientation is genuinely useful and cheap; solids are not, and are never returned.
                FamilyInstance instance = element as FamilyInstance;
                if (instance != null)
                {
                    // Unit direction vectors - dimensionless, so no unit conversion applies.
                    try { g.Set("facingOrientation", UnitVector(instance.FacingOrientation)); } catch (Exception) { }
                    try { g.Set("handOrientation", UnitVector(instance.HandOrientation)); } catch (Exception) { }
                    try { if (instance.Host != null) g.Set("hostId", Compat.IdValue(instance.Host.Id)); }
                    catch (Exception) { }
                }

                items.Add(g);
            }

            JsonValue result = J.O(
                "elements", items,
                "count", items.Count,
                "unit", "mm",
                "note", "Solid, mesh and face geometry is intentionally not returned. " +
                        "Use bounding boxes and location curves for spatial reasoning.");

            if (missing.Count > 0) result.Set("notFound", J.ALongs(missing));
            return result;
        }

        /// <summary>Serializes a dimensionless direction vector (no unit conversion).</summary>
        private static JsonValue UnitVector(XYZ v)
        {
            if (v == null) return JsonValue.Null;
            return J.O("x", Metric.R(v.X, 4), "y", Metric.R(v.Y, 4), "z", Metric.R(v.Z, 4));
        }

        // ==================================================================
        //  revit_get_selection
        // ==================================================================
        public static JsonValue GetSelection(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            bool includeParameters = Args.Bool(ctx.Args, "includeParameters", false);
            int limit = Args.Limit(ctx.Args);

            ICollection<ElementId> selected = ctx.UiDoc.Selection.GetElementIds();

            var options = new SerializeOptions
            {
                IncludeLocation = true,
                IncludeAllParameters = includeParameters,
                IncludeTypeParameters = false,
                IncludeEmpty = false
            };

            JsonValue items = JsonValue.NewArray();
            int index = 0;
            foreach (ElementId id in selected)
            {
                if (index++ >= limit) break;
                Element element = doc.GetElement(id);
                if (element == null) continue;
                items.Add(ElementSerializer.Summarize(element, options));
            }

            return J.O(
                "elements", items,
                "returned", items.Count,
                "total", selected.Count,
                "hasMore", selected.Count > items.Count,
                "note", selected.Count == 0
                    ? "Nothing is selected in Revit right now."
                    : null);
        }

        // ==================================================================
        //  revit_bridge_status
        // ==================================================================
        public static JsonValue BridgeStatus(ToolContext ctx)
        {
            BridgeService service = BridgeService.Current;
            if (service == null)
            {
                return J.O("running", false,
                           "note", "The bridge service object is not available in this Revit session.");
            }

            JsonValue status = service.StatusJson();

            Document doc = ctx.TryGetDoc();
            status.Set("activeDocument", doc != null ? doc.Title : null);
            status.Set("documentIsReadOnly", J.Wrap(doc != null ? (object)doc.IsReadOnly : null));

            JsonValue unimplemented = J.AStrings(service.Router.UnimplementedTools());
            if (unimplemented.Count > 0) status.Set("unimplementedTools", unimplemented);

            status.Set("errorCodes", J.A(
                BridgeErrorCodes.InvalidArgument, BridgeErrorCodes.MissingArgument,
                BridgeErrorCodes.UnknownTool, BridgeErrorCodes.NotFound,
                BridgeErrorCodes.NoActiveDocument, BridgeErrorCodes.ReadOnlyDocument,
                BridgeErrorCodes.ConfirmationRequired, BridgeErrorCodes.RevitApi,
                BridgeErrorCodes.TransactionFailed, BridgeErrorCodes.Timeout,
                BridgeErrorCodes.Cancelled, BridgeErrorCodes.Internal));

            return status;
        }
    }
}
