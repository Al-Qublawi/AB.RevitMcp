using System;
using System.Collections.Generic;
using AB.RevitMcp.Contracts.Json;

namespace AB.RevitMcp.Contracts.Tools
{
    /// <summary>
    /// The single source of truth for every MCP tool the bridge exposes.
    ///
    /// Living in the shared contracts assembly means:
    ///   * the MCP server can answer <c>tools/list</c> even while Revit is closed, and
    ///   * the add-in binds its handlers to these exact descriptors, so a schema and its
    ///     implementation can never drift apart.
    ///
    /// UNIT CONVENTION (applies to every tool, in and out):
    ///   length = millimetres, area = square metres, volume = cubic metres, angle = degrees.
    /// Revit's internal decimal feet never cross this boundary.
    /// </summary>
    public static partial class ToolCatalog
    {
        private static readonly List<ToolDescriptor> _all = new List<ToolDescriptor>();
        private static readonly Dictionary<string, ToolDescriptor> _byName =
            new Dictionary<string, ToolDescriptor>(StringComparer.OrdinalIgnoreCase);

        static ToolCatalog()
        {
            RegisterReadTools();
            RegisterWriteTools();
            RegisterMepTools();
            RegisterAnnotationTools();
            RegisterViewTools();
            RegisterGeometryTools();
            RegisterModifyTools();
            RegisterDestructiveTools();
            RegisterCodeTools();
        }

        private static void Add(string name, ToolCategory category, string title, string description,
                                JsonValue schema, int timeoutMs = 0)
        {
            var d = new ToolDescriptor(name, category, title, description, schema, timeoutMs);
            if (_byName.ContainsKey(name)) throw new InvalidOperationException("Duplicate tool name: " + name);
            _all.Add(d);
            _byName[name] = d;
        }

        public static IReadOnlyList<ToolDescriptor> All { get { return _all; } }

        public static int Count { get { return _all.Count; } }

        public static ToolDescriptor Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            ToolDescriptor d;
            return _byName.TryGetValue(name, out d) ? d : null;
        }

        public static IEnumerable<ToolDescriptor> ByCategory(ToolCategory category)
        {
            for (int i = 0; i < _all.Count; i++)
                if (_all[i].Category == category) yield return _all[i];
        }

        /// <summary>The array returned by MCP <c>tools/list</c>.</summary>
        public static JsonValue ToMcpToolsArray()
        {
            JsonValue arr = JsonValue.NewArray();
            for (int i = 0; i < _all.Count; i++) arr.Add(_all[i].ToMcpTool());
            return arr;
        }

        public static JsonValue ToDescriptorArray()
        {
            JsonValue arr = JsonValue.NewArray();
            for (int i = 0; i < _all.Count; i++) arr.Add(_all[i].ToDescriptorJson());
            return arr;
        }

        // ============================================================================
        //  READ TOOLS - never open a transaction, safe on any model at any time
        // ============================================================================
        private static void RegisterReadTools()
        {
            Add("revit_get_model_info", ToolCategory.Read, "Get model info",
                "Returns identity and context for the active Revit document: title, file path, Revit version and build, " +
                "worksharing state, project information (name, number, address, client), level count, element count, " +
                "active view, and the unit convention used by every tool in this server.",
                Sch.NoArgs());

            Add("revit_get_model_health", ToolCategory.Read, "Model health check",
                "Runs a model-health audit: file size, total and per-category element counts, warning count grouped by " +
                "type, in-place family count, unused/unplaced view count, DWG import count, workset count, linked model " +
                "count and unplaced/unbounded room count. Use this before a submission or coordination review.",
                Sch.Obj("Model health options.", null,
                    "topCategories", Sch.Int("How many of the largest categories to report. Default 15.", 1, 100, 15)));

            Add("revit_list_categories", ToolCategory.Read, "List categories",
                "Lists the model categories present in the document with a live element count for each. " +
                "Use this first to discover the exact category name to pass to other tools.",
                Sch.Obj("Category listing options.", null,
                    "onlyWithElements", Sch.Bool("Only return categories that currently contain elements. Default true.", true),
                    "nameContains", Sch.Str("Case-insensitive substring filter on the category name."),
                    "limit", Sch.Limit(),
                    "offset", Sch.Offset()));

            Add("revit_list_levels", ToolCategory.Read, "List levels",
                "Lists every level with its id, name and elevation in millimetres, sorted by elevation. " +
                "Also reports which levels have an associated floor plan view.",
                Sch.NoArgs());

            Add("revit_list_worksets", ToolCategory.Read, "List worksets",
                "Lists the worksets in a workshared model with id, name, kind, owner, open/closed state and " +
                "visibility default. Returns an empty list with a note if the model is not workshared.",
                Sch.Obj("Workset listing options.", null,
                    "kind", Sch.Str("Workset kind filter.", new[] { "user", "standard", "view", "family", "all" }, "user")));

            Add("revit_list_views", ToolCategory.Read, "List views",
                "Lists views with id, name, view type, scale, detail level, template flag, and whether the view is " +
                "placed on a sheet. Excludes view templates unless asked for.",
                Sch.Obj("View listing options.", null,
                    "viewType", Sch.Str("Filter by view type, e.g. FloorPlan, CeilingPlan, ThreeD, Section, Elevation, " +
                                        "Drafting, Schedule, Legend, DraftingView. Case-insensitive."),
                    "nameContains", Sch.Str("Case-insensitive substring filter on the view name."),
                    "includeTemplates", Sch.Bool("Include view templates. Default false.", false),
                    "onlyOnSheets", Sch.Bool("Only return views that are placed on a sheet. Default false.", false),
                    "limit", Sch.Limit(),
                    "offset", Sch.Offset()));

            Add("revit_get_active_view", ToolCategory.Read, "Get active view",
                "Returns full details of the currently active view: id, name, type, scale, detail level, discipline, " +
                "phase, associated level, view template, crop state, and the element count visible in it.",
                Sch.NoArgs());

            Add("revit_list_sheets", ToolCategory.Read, "List sheets",
                "Lists drawing sheets with number, name, id, revision, and the views placed on each one. " +
                "This is the drawing register.",
                Sch.Obj("Sheet listing options.", null,
                    "numberContains", Sch.Str("Case-insensitive substring filter on the sheet number."),
                    "includeViewports", Sch.Bool("Include the list of views placed on each sheet. Default true.", true),
                    "includePlaceholders", Sch.Bool("Include placeholder sheets. Default false.", false),
                    "limit", Sch.Limit(),
                    "offset", Sch.Offset()));

            Add("revit_list_schedules", ToolCategory.Read, "List schedules",
                "Lists schedules and material takeoffs with id, name, the category they schedule, field names, " +
                "and row count.",
                Sch.Obj("Schedule listing options.", null,
                    "includeFields", Sch.Bool("Include each schedule's field names. Default true.", true),
                    "limit", Sch.Limit(),
                    "offset", Sch.Offset()));

            Add("revit_list_warnings", ToolCategory.Read, "List model warnings",
                "Lists the model's outstanding warnings grouped by warning text, with severity and the ids of the " +
                "elements involved. This is the primary model-quality signal.",
                Sch.Obj("Warning listing options.", null,
                    "groupByDescription", Sch.Bool("Group identical warnings and report a count. Default true.", true),
                    "includeElementIds", Sch.Bool("Include the failing element ids. Default true.", true),
                    "limit", Sch.Limit(),
                    "offset", Sch.Offset()));

            Add("revit_list_linked_models", ToolCategory.Read, "List linked models",
                "Lists RVT links and CAD links with load state, path, path type, shared-coordinates state, " +
                "instance count and the transform of each instance. Run this before federation or IFC export.",
                Sch.Obj("Link listing options.", null,
                    "includeCad", Sch.Bool("Include imported/linked CAD (DWG, DXF, DGN). Default true.", true)));

            Add("revit_list_materials", ToolCategory.Read, "List materials",
                "Lists project materials with id, name, class, colour and appearance/structural asset presence.",
                Sch.Obj("Material listing options.", null,
                    "nameContains", Sch.Str("Case-insensitive substring filter on the material name."),
                    "limit", Sch.Limit(),
                    "offset", Sch.Offset()));

            Add("revit_list_family_types", ToolCategory.Read, "List family types",
                "Lists loadable-family symbols and system-family types available for placement, with id, family name, " +
                "type name and category. Call this to find the exact typeName / familyName to pass to a create tool.",
                Sch.Obj("Family type listing options.", null,
                    "category", Sch.CategoryName(),
                    "familyNameContains", Sch.Str("Case-insensitive substring filter on the family name."),
                    "typeNameContains", Sch.Str("Case-insensitive substring filter on the type name."),
                    "limit", Sch.Limit(),
                    "offset", Sch.Offset()));

            Add("revit_list_rooms", ToolCategory.Read, "List rooms and spaces",
                "Lists rooms (or MEP spaces) with number, name, level, department, area in m2, volume in m3, " +
                "perimeter in mm and placement state. Unplaced and unbounded rooms are flagged.",
                Sch.Obj("Room listing options.", null,
                    "kind", Sch.Str("What to list.", new[] { "rooms", "spaces", "areas" }, "rooms"),
                    "levelName", Sch.Str("Only rooms on this level (exact, case-insensitive)."),
                    "nameContains", Sch.Str("Case-insensitive substring filter on the room name."),
                    "includeUnplaced", Sch.Bool("Include unplaced rooms (area = 0, no location). Default true.", true),
                    "limit", Sch.Limit(),
                    "offset", Sch.Offset()));

            Add("revit_list_grids", ToolCategory.Read, "List grids",
                "Lists grid lines with id, name, and their start/end points in millimetres.",
                Sch.NoArgs());

            Add("revit_query_elements", ToolCategory.Read, "Query elements",
                "The main element query. Filters instances by any combination of category, family name, type name, " +
                "level, workset and owning view, then returns a paginated, geometry-free summary of each element. " +
                "Ask for specific parameters with parameterNames rather than fetching everything.",
                Sch.Obj("Element query filters. All filters are ANDed; omit a filter to ignore it.", null,
                    "category", Sch.CategoryName(),
                    "familyName", Sch.Str("Exact family name (case-insensitive)."),
                    "typeName", Sch.Str("Exact type name (case-insensitive)."),
                    "levelName", Sch.Str("Exact level name (case-insensitive)."),
                    "worksetName", Sch.Str("Exact workset name (case-insensitive)."),
                    "viewId", Sch.Int("Restrict to elements visible in this view id.", 1),
                    "activeViewOnly", Sch.Bool("Restrict to elements visible in the active view. Default false.", false),
                    "elementType", Sch.Str("Which kind of element to return.",
                        new[] { "instances", "types", "both" }, "instances"),
                    "parameterFilter", Sch.Obj("Optional single-parameter value filter.", new[] { "name" },
                        "name", Sch.Str("Parameter name to test."),
                        "op", Sch.Str("Comparison operator.",
                            new[] { "equals", "notEquals", "contains", "startsWith", "greaterThan", "lessThan", "isEmpty", "isNotEmpty" },
                            "equals"),
                        "value", Sch.Str("Value to compare against (string form; numbers are parsed).")),
                    "parameterNames", Sch.Arr(Sch.Str("Parameter name."),
                        "Parameters to include for each returned element. Keep this list short.", null, 40),
                    "includeBoundingBox", Sch.Bool("Include an axis-aligned bounding box in mm. Default false.", false),
                    "limit", Sch.Limit(),
                    "offset", Sch.Offset()));

            Add("revit_search_elements", ToolCategory.Read, "Search elements by text",
                "Free-text search across element name, type name, family name, Mark and Comments. " +
                "Use this when the user names something loosely (\"the north entrance door\", \"pump P-01\").",
                Sch.Obj("Text search options.", new[] { "query" },
                    "query", Sch.Str("Text to search for. Case-insensitive substring match."),
                    "fields", Sch.Arr(Sch.Str("Field name.", new[] { "name", "typeName", "familyName", "mark", "comments" }),
                        "Which fields to search. Defaults to all of them.", null, 5),
                    "category", Sch.CategoryName(),
                    "limit", Sch.Limit(),
                    "offset", Sch.Offset()));

            Add("revit_get_element_parameters", ToolCategory.Read, "Get element parameters",
                "Returns the full parameter set for one or more elements: name, value (metric, formatted), raw value, " +
                "storage type, read-only flag, shared/built-in origin, and the type parameters inherited from the " +
                "element's type.",
                Sch.Obj("Parameter read options.", new[] { "elementIds" },
                    "elementIds", Sch.ElementIds("Element ids to read.", 200),
                    "parameterNames", Sch.Arr(Sch.Str("Parameter name."),
                        "Only return these parameters. Omit to return all.", null, 80),
                    "includeTypeParameters", Sch.Bool("Also return the element type's parameters. Default true.", true),
                    "includeReadOnly", Sch.Bool("Include read-only parameters. Default true.", true),
                    "includeEmpty", Sch.Bool("Include parameters with no value. Default false.", false)));

            Add("revit_get_element_geometry", ToolCategory.Read, "Get element location and bounds",
                "Returns lightweight geometry for elements: axis-aligned bounding box, location point or location " +
                "curve endpoints, and orientation - all in millimetres. Solids and meshes are never returned.",
                Sch.Obj("Geometry summary options.", new[] { "elementIds" },
                    "elementIds", Sch.ElementIds("Element ids to describe.", 500),
                    "includeBoundingBox", Sch.Bool("Include the bounding box. Default true.", true),
                    "includeLocation", Sch.Bool("Include the location point/curve. Default true.", true)));

            Add("revit_get_selection", ToolCategory.Read, "Get current selection",
                "Returns the elements the user currently has selected in the Revit UI. " +
                "Use this when the user says \"these\", \"the selected ones\" or \"what I have picked\".",
                Sch.Obj("Selection read options.", null,
                    "includeParameters", Sch.Bool("Include a short parameter summary per element. Default false.", false),
                    "limit", Sch.Limit()));

            Add("revit_bridge_status", ToolCategory.Read, "Bridge status",
                "Diagnostics for the bridge itself: pipe name, Revit process id and version, uptime, requests served, " +
                "average execution time, last error, and the number of registered tools. Use this to troubleshoot " +
                "connectivity before blaming a tool.",
                Sch.NoArgs());
        }
    }
}
