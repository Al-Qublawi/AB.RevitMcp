using System;
using System.Collections.Generic;
using System.Linq;
using AB.RevitMcp.Addin.Bridge;
using AB.RevitMcp.Addin.Revit;
using AB.RevitMcp.Contracts.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;

namespace AB.RevitMcp.Addin.Tools
{
    /// <summary>
    /// READ tools, part 2: views, sheets, schedules, warnings, links, materials, family types and
    /// rooms - the documentation and coordination surface of the model.
    /// </summary>
    public static partial class ReadTools
    {
        // ==================================================================
        //  revit_list_views
        // ==================================================================
        public static JsonValue ListViews(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            string viewType = Args.Str(ctx.Args, "viewType");
            string nameContains = Args.Str(ctx.Args, "nameContains");
            bool includeTemplates = Args.Bool(ctx.Args, "includeTemplates", false);
            bool onlyOnSheets = Args.Bool(ctx.Args, "onlyOnSheets", false);

            Dictionary<long, string> sheetOfView = BuildViewToSheetMap(doc);

            var items = new List<JsonValue>();
            foreach (View view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
            {
                if (view.IsTemplate && !includeTemplates) continue;
                if (!Paging.Matches(view.Name, nameContains)) continue;
                if (!string.IsNullOrEmpty(viewType) &&
                    !Paging.EqualsCi(view.ViewType.ToString(), viewType)) continue;

                long id = Compat.IdValue(view.Id);
                string sheet;
                sheetOfView.TryGetValue(id, out sheet);
                if (onlyOnSheets && sheet == null) continue;

                JsonValue v = J.O(
                    "id", id,
                    "name", view.Name,
                    "viewType", view.ViewType.ToString(),
                    "isTemplate", view.IsTemplate,
                    "onSheet", sheet);

                if (!view.IsTemplate)
                {
                    try { v.Set("scale", view.Scale); } catch (Exception) { }
                    try { v.Set("detailLevel", view.DetailLevel.ToString()); } catch (Exception) { }
                    try { v.Set("discipline", view.Discipline.ToString()); } catch (Exception) { }
                    try { v.Set("cropActive", view.CropBoxActive); } catch (Exception) { }
                    try
                    {
                        if (Compat.IsValid(view.ViewTemplateId))
                        {
                            Element template = doc.GetElement(view.ViewTemplateId);
                            if (template != null) v.Set("viewTemplate", template.Name);
                        }
                    }
                    catch (Exception) { }

                    ViewPlan plan = view as ViewPlan;
                    if (plan != null && plan.GenLevel != null) v.Set("level", plan.GenLevel.Name);
                }

                items.Add(v);
            }

            items.Sort(delegate (JsonValue a, JsonValue b)
            {
                int byType = string.Compare(a["viewType"].AsString(""), b["viewType"].AsString(""), StringComparison.OrdinalIgnoreCase);
                if (byType != 0) return byType;
                return string.Compare(a["name"].AsString(""), b["name"].AsString(""), StringComparison.OrdinalIgnoreCase);
            });

            return Paging.Page(items, Args.Offset(ctx.Args), Args.Limit(ctx.Args), "views");
        }

        // ==================================================================
        //  revit_get_active_view
        // ==================================================================
        public static JsonValue GetActiveView(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            View view = ctx.ActiveView;

            JsonValue result = J.O(
                "id", Compat.IdValue(view.Id),
                "name", view.Name,
                "viewType", view.ViewType.ToString(),
                "isTemplate", view.IsTemplate);

            try { result.Set("scale", view.Scale); } catch (Exception) { }
            try { result.Set("detailLevel", view.DetailLevel.ToString()); } catch (Exception) { }
            try { result.Set("displayStyle", view.DisplayStyle.ToString()); } catch (Exception) { }
            try { result.Set("discipline", view.Discipline.ToString()); } catch (Exception) { }
            try { result.Set("cropActive", view.CropBoxActive); } catch (Exception) { }
            try { result.Set("cropVisible", view.CropBoxVisible); } catch (Exception) { }

            try
            {
                if (Compat.IsValid(view.ViewTemplateId))
                {
                    Element template = doc.GetElement(view.ViewTemplateId);
                    if (template != null) result.Set("viewTemplate", template.Name);
                }
            }
            catch (Exception) { }

            ViewPlan plan = view as ViewPlan;
            if (plan != null && plan.GenLevel != null)
            {
                result.Set("level", plan.GenLevel.Name);
                result.Set("levelElevationMm", Metric.R(Metric.FeetToMm(plan.GenLevel.Elevation)));
            }

            try
            {
                Parameter phase = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
                if (phase != null && Compat.IsValid(phase.AsElementId()))
                {
                    Element ph = doc.GetElement(phase.AsElementId());
                    if (ph != null) result.Set("phase", ph.Name);
                }
            }
            catch (Exception) { }

            // What the user can actually see right now.
            try
            {
                int visible = new FilteredElementCollector(doc, view.Id)
                    .WhereElementIsNotElementType().GetElementCount();
                result.Set("visibleElementCount", visible);
            }
            catch (Exception) { result.Set("visibleElementCount", JsonValue.Null); }

            try
            {
                Dictionary<long, string> sheetOfView = BuildViewToSheetMap(doc);
                string sheet;
                if (sheetOfView.TryGetValue(Compat.IdValue(view.Id), out sheet)) result.Set("onSheet", sheet);
            }
            catch (Exception) { }

            try
            {
                ICollection<ElementId> selected = ctx.UiDoc.Selection.GetElementIds();
                result.Set("selectedElementCount", selected.Count);
            }
            catch (Exception) { }

            return result;
        }

        // ==================================================================
        //  revit_list_sheets
        // ==================================================================
        public static JsonValue ListSheets(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            string numberContains = Args.Str(ctx.Args, "numberContains");
            bool includeViewports = Args.Bool(ctx.Args, "includeViewports", true);
            bool includePlaceholders = Args.Bool(ctx.Args, "includePlaceholders", false);

            var items = new List<JsonValue>();
            foreach (ViewSheet sheet in new FilteredElementCollector(doc)
                         .OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
            {
                if (sheet.IsPlaceholder && !includePlaceholders) continue;
                if (!Paging.Matches(sheet.SheetNumber, numberContains)) continue;

                JsonValue s = J.O(
                    "id", Compat.IdValue(sheet.Id),
                    "sheetNumber", sheet.SheetNumber,
                    "name", sheet.Name,
                    "isPlaceholder", sheet.IsPlaceholder);

                try { s.Set("revision", SafeParam(sheet, BuiltInParameter.SHEET_CURRENT_REVISION)); } catch (Exception) { }
                try { s.Set("drawnBy", SafeParam(sheet, BuiltInParameter.SHEET_DRAWN_BY)); } catch (Exception) { }
                try { s.Set("checkedBy", SafeParam(sheet, BuiltInParameter.SHEET_CHECKED_BY)); } catch (Exception) { }
                try { s.Set("issueDate", SafeParam(sheet, BuiltInParameter.SHEET_ISSUE_DATE)); } catch (Exception) { }

                if (includeViewports && !sheet.IsPlaceholder)
                {
                    JsonValue placed = JsonValue.NewArray();
                    try
                    {
                        foreach (ElementId vpId in sheet.GetAllViewports())
                        {
                            Viewport vp = doc.GetElement(vpId) as Viewport;
                            if (vp == null) continue;
                            View v = doc.GetElement(vp.ViewId) as View;
                            if (v == null) continue;
                            placed.Add(J.O(
                                "viewId", Compat.IdValue(v.Id),
                                "viewName", v.Name,
                                "viewType", v.ViewType.ToString(),
                                "viewportId", Compat.IdValue(vpId),
                                "centre", Metric.PointToMm(vp.GetBoxCenter())));
                        }
                    }
                    catch (Exception) { }
                    s.Set("views", placed);
                    s.Set("viewCount", placed.Count);
                }

                items.Add(s);
            }

            items.Sort(delegate (JsonValue a, JsonValue b)
            {
                return string.Compare(a["sheetNumber"].AsString(""), b["sheetNumber"].AsString(""),
                    StringComparison.OrdinalIgnoreCase);
            });

            return Paging.Page(items, Args.Offset(ctx.Args), Args.Limit(ctx.Args), "sheets");
        }

        // ==================================================================
        //  revit_list_schedules
        // ==================================================================
        public static JsonValue ListSchedules(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            bool includeFields = Args.Bool(ctx.Args, "includeFields", true);

            var items = new List<JsonValue>();
            foreach (ViewSchedule schedule in new FilteredElementCollector(doc)
                         .OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>())
            {
                if (schedule.IsTemplate) continue;

                JsonValue s = J.O(
                    "id", Compat.IdValue(schedule.Id),
                    "name", schedule.Name,
                    "isTitleblockRevisionSchedule", schedule.IsTitleblockRevisionSchedule);

                try
                {
                    ScheduleDefinition definition = schedule.Definition;
                    if (definition != null)
                    {
                        s.Set("isMaterialTakeoff", definition.IsMaterialTakeoff);
                        s.Set("isKeySchedule", definition.IsKeySchedule);

                        ElementId categoryId = definition.CategoryId;
                        if (Compat.IsValid(categoryId))
                        {
                            Category category = Category.GetCategory(doc, categoryId);
                            if (category != null) s.Set("category", category.Name);
                        }

                        if (includeFields)
                        {
                            JsonValue fields = JsonValue.NewArray();
                            int fieldCount = definition.GetFieldCount();
                            for (int i = 0; i < fieldCount; i++)
                            {
                                try
                                {
                                    ScheduleField field = definition.GetField(i);
                                    fields.Add(field.GetName());
                                }
                                catch (Exception) { }
                            }
                            s.Set("fields", fields);
                            s.Set("fieldCount", fieldCount);
                        }
                    }
                }
                catch (Exception) { }

                try
                {
                    TableData table = schedule.GetTableData();
                    TableSectionData body = table.GetSectionData(SectionType.Body);
                    if (body != null) s.Set("rowCount", body.NumberOfRows);
                }
                catch (Exception) { }

                items.Add(s);
            }

            items.Sort(delegate (JsonValue a, JsonValue b)
            {
                return string.Compare(a["name"].AsString(""), b["name"].AsString(""), StringComparison.OrdinalIgnoreCase);
            });

            return Paging.Page(items, Args.Offset(ctx.Args), Args.Limit(ctx.Args), "schedules");
        }

        // ==================================================================
        //  revit_list_warnings
        // ==================================================================
        public static JsonValue ListWarnings(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            bool group = Args.Bool(ctx.Args, "groupByDescription", true);
            bool includeIds = Args.Bool(ctx.Args, "includeElementIds", true);

            IList<FailureMessage> warnings = doc.GetWarnings();
            var items = new List<JsonValue>();

            if (group)
            {
                var grouped = new Dictionary<string, List<FailureMessage>>(StringComparer.Ordinal);
                for (int i = 0; i < warnings.Count; i++)
                {
                    string text;
                    try { text = warnings[i].GetDescriptionText(); }
                    catch (Exception) { text = "(unreadable warning)"; }
                    if (!grouped.ContainsKey(text)) grouped[text] = new List<FailureMessage>();
                    grouped[text].Add(warnings[i]);
                }

                foreach (KeyValuePair<string, List<FailureMessage>> pair in grouped)
                {
                    JsonValue w = J.O(
                        "description", pair.Key,
                        "count", pair.Value.Count,
                        "severity", SafeSeverity(pair.Value[0]));

                    if (includeIds)
                    {
                        JsonValue ids = JsonValue.NewArray();
                        for (int i = 0; i < pair.Value.Count && ids.Count < 200; i++)
                        {
                            try
                            {
                                foreach (ElementId id in pair.Value[i].GetFailingElements())
                                {
                                    ids.Add(Compat.IdValue(id));
                                    if (ids.Count >= 200) break;
                                }
                            }
                            catch (Exception) { }
                        }
                        w.Set("elementIds", ids);
                        if (pair.Value.Count > 200)
                            w.Set("elementIdsTruncated", true);
                    }
                    items.Add(w);
                }

                items.Sort(delegate (JsonValue a, JsonValue b)
                {
                    return b["count"].AsInt(0).CompareTo(a["count"].AsInt(0));
                });
            }
            else
            {
                for (int i = 0; i < warnings.Count; i++)
                {
                    JsonValue w = J.O("severity", SafeSeverity(warnings[i]));
                    try { w.Set("description", warnings[i].GetDescriptionText()); } catch (Exception) { }
                    if (includeIds)
                    {
                        JsonValue ids = JsonValue.NewArray();
                        try { foreach (ElementId id in warnings[i].GetFailingElements()) ids.Add(Compat.IdValue(id)); }
                        catch (Exception) { }
                        w.Set("elementIds", ids);
                    }
                    items.Add(w);
                }
            }

            JsonValue page = Paging.Page(items, Args.Offset(ctx.Args), Args.Limit(ctx.Args), "warnings");
            page.Set("totalWarningInstances", warnings.Count);
            page.Set("grouped", group);
            return page;
        }

        private static string SafeSeverity(FailureMessage message)
        {
            try { return message.GetSeverity().ToString(); }
            catch (Exception) { return "Unknown"; }
        }

        // ==================================================================
        //  revit_list_linked_models
        // ==================================================================
        public static JsonValue ListLinkedModels(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            bool includeCad = Args.Bool(ctx.Args, "includeCad", true);

            // instances per link type
            var instancesByType = new Dictionary<long, List<RevitLinkInstance>>();
            foreach (RevitLinkInstance instance in new FilteredElementCollector(doc)
                         .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                long typeId = Compat.IdValue(instance.GetTypeId());
                if (!instancesByType.ContainsKey(typeId)) instancesByType[typeId] = new List<RevitLinkInstance>();
                instancesByType[typeId].Add(instance);
            }

            JsonValue rvtLinks = JsonValue.NewArray();
            int loaded = 0, unloaded = 0, missing = 0;

            foreach (RevitLinkType linkType in new FilteredElementCollector(doc)
                         .OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>())
            {
                long typeId = Compat.IdValue(linkType.Id);
                JsonValue link = J.O("linkTypeId", typeId, "name", ElementSerializer.SafeName(linkType));

                string status = "Unknown";
                try
                {
                    LinkedFileStatus fileStatus = linkType.GetLinkedFileStatus();
                    status = fileStatus.ToString();
                    if (fileStatus == LinkedFileStatus.Loaded) loaded++;
                    else if (fileStatus == LinkedFileStatus.Unloaded) unloaded++;
                    else if (fileStatus == LinkedFileStatus.NotFound) missing++;
                }
                catch (Exception) { }
                link.Set("status", status);

                try
                {
                    ExternalFileReference reference = linkType.GetExternalFileReference();
                    if (reference != null)
                    {
                        link.Set("pathType", reference.PathType.ToString());
                        link.Set("path", ModelPathUtils.ConvertModelPathToUserVisiblePath(reference.GetAbsolutePath()));
                    }
                }
                catch (Exception) { }

                try { link.Set("isNestedLink", linkType.IsNestedLink); } catch (Exception) { }
                try { link.Set("attachmentType", linkType.AttachmentType.ToString()); } catch (Exception) { }

                List<RevitLinkInstance> instances;
                instancesByType.TryGetValue(typeId, out instances);
                JsonValue instanceJson = JsonValue.NewArray();
                if (instances != null)
                {
                    for (int i = 0; i < instances.Count; i++)
                    {
                        JsonValue inst = J.O("instanceId", Compat.IdValue(instances[i].Id));
                        try
                        {
                            Transform transform = instances[i].GetTotalTransform();
                            inst.Set("origin", Metric.PointToMm(transform.Origin));
                            inst.Set("isIdentity", transform.IsIdentity);
                            if (!transform.IsIdentity)
                                inst.Set("rotationDegrees",
                                    Metric.R(Metric.RadToDeg(Math.Atan2(transform.BasisX.Y, transform.BasisX.X)), 3));
                        }
                        catch (Exception) { }
                        instanceJson.Add(inst);
                    }
                }
                link.Set("instances", instanceJson);
                link.Set("instanceCount", instanceJson.Count);
                if (instanceJson.Count == 0)
                    link.Set("note", "This link type has no placed instance in the model.");

                rvtLinks.Add(link);
            }

            JsonValue result = J.O(
                "revitLinks", rvtLinks,
                "revitLinkCount", rvtLinks.Count,
                "summary", J.O("loaded", loaded, "unloaded", unloaded, "notFound", missing));

            if (includeCad)
            {
                JsonValue cad = JsonValue.NewArray();
                foreach (ImportInstance import in new FilteredElementCollector(doc)
                             .OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
                {
                    JsonValue c = J.O(
                        "id", Compat.IdValue(import.Id),
                        "name", ElementSerializer.SafeName(import),
                        "isLinked", import.IsLinked,
                        "pinned", import.Pinned);
                    try
                    {
                        Element type = doc.GetElement(import.GetTypeId());
                        CADLinkType cadType = type as CADLinkType;
                        if (cadType != null)
                        {
                            ExternalFileReference reference = cadType.GetExternalFileReference();
                            if (reference != null)
                                c.Set("path", ModelPathUtils.ConvertModelPathToUserVisiblePath(reference.GetAbsolutePath()));
                        }
                    }
                    catch (Exception) { }
                    cad.Add(c);
                }
                result.Set("cadLinks", cad);
                result.Set("cadLinkCount", cad.Count);
                result.Set("cadImportWarning",
                    "Entries with isLinked=false are IMPORTED (exploded into the model) rather than linked - " +
                    "a common source of model bloat.");
            }

            return result;
        }

        // ==================================================================
        //  revit_list_materials
        // ==================================================================
        public static JsonValue ListMaterials(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            string nameContains = Args.Str(ctx.Args, "nameContains");

            var items = new List<JsonValue>();
            foreach (Material material in new FilteredElementCollector(doc)
                         .OfClass(typeof(Material)).Cast<Material>())
            {
                if (!Paging.Matches(material.Name, nameContains)) continue;

                JsonValue m = J.O(
                    "id", Compat.IdValue(material.Id),
                    "name", material.Name);

                try { m.Set("materialClass", material.MaterialClass); } catch (Exception) { }
                try { m.Set("materialCategory", material.MaterialCategory); } catch (Exception) { }
                try
                {
                    Color colour = material.Color;
                    if (colour != null && colour.IsValid)
                        m.Set("color", J.O("r", colour.Red, "g", colour.Green, "b", colour.Blue));
                }
                catch (Exception) { }
                try { m.Set("hasAppearanceAsset", Compat.IsValid(material.AppearanceAssetId)); } catch (Exception) { }
                try { m.Set("hasStructuralAsset", Compat.IsValid(material.StructuralAssetId)); } catch (Exception) { }
                try { m.Set("hasThermalAsset", Compat.IsValid(material.ThermalAssetId)); } catch (Exception) { }

                items.Add(m);
            }

            items.Sort(delegate (JsonValue a, JsonValue b)
            {
                return string.Compare(a["name"].AsString(""), b["name"].AsString(""), StringComparison.OrdinalIgnoreCase);
            });

            return Paging.Page(items, Args.Offset(ctx.Args), Args.Limit(ctx.Args), "materials");
        }

        // ==================================================================
        //  revit_list_family_types
        // ==================================================================
        public static JsonValue ListFamilyTypes(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            string categoryName = Args.Str(ctx.Args, "category");
            string familyContains = Args.Str(ctx.Args, "familyNameContains");
            string typeContains = Args.Str(ctx.Args, "typeNameContains");

            var collector = new FilteredElementCollector(doc).WhereElementIsElementType();
            if (!string.IsNullOrEmpty(categoryName))
            {
                BuiltInCategory bic = Selectors.ResolveCategory(doc, categoryName);
                collector = collector.OfCategory(bic);
            }

            var items = new List<JsonValue>();
            foreach (Element element in collector)
            {
                ElementType type = element as ElementType;
                if (type == null) continue;

                string typeName = ElementSerializer.SafeName(type);
                string familyName = null;
                try { familyName = type.FamilyName; } catch (Exception) { }

                if (!Paging.Matches(typeName, typeContains)) continue;
                if (!Paging.Matches(familyName, familyContains)) continue;

                FamilySymbol symbol = type as FamilySymbol;
                JsonValue t = J.O(
                    "typeId", Compat.IdValue(type.Id),
                    "typeName", typeName,
                    "familyName", familyName,
                    "category", Selectors.CategoryName(type),
                    "kind", symbol != null ? "loadable" : "system");

                if (symbol != null)
                {
                    try { t.Set("isActive", symbol.IsActive); } catch (Exception) { }
                    try { if (symbol.Family != null) t.Set("familyId", Compat.IdValue(symbol.Family.Id)); }
                    catch (Exception) { }
                }

                items.Add(t);
            }

            items.Sort(delegate (JsonValue a, JsonValue b)
            {
                int byCategory = string.Compare(a["category"].AsString(""), b["category"].AsString(""), StringComparison.OrdinalIgnoreCase);
                if (byCategory != 0) return byCategory;
                int byFamily = string.Compare(a["familyName"].AsString(""), b["familyName"].AsString(""), StringComparison.OrdinalIgnoreCase);
                if (byFamily != 0) return byFamily;
                return string.Compare(a["typeName"].AsString(""), b["typeName"].AsString(""), StringComparison.OrdinalIgnoreCase);
            });

            return Paging.Page(items, Args.Offset(ctx.Args), Args.Limit(ctx.Args), "types");
        }

        // ==================================================================
        //  revit_list_rooms
        // ==================================================================
        public static JsonValue ListRooms(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            string kind = Args.Str(ctx.Args, "kind", false, "rooms", new[] { "rooms", "spaces", "areas" });
            string levelName = Args.Str(ctx.Args, "levelName");
            string nameContains = Args.Str(ctx.Args, "nameContains");
            bool includeUnplaced = Args.Bool(ctx.Args, "includeUnplaced", true);

            BuiltInCategory category =
                Paging.EqualsCi(kind, "spaces") ? BuiltInCategory.OST_MEPSpaces :
                Paging.EqualsCi(kind, "areas") ? BuiltInCategory.OST_Areas :
                BuiltInCategory.OST_Rooms;

            var items = new List<JsonValue>();
            double totalArea = 0;
            int unplaced = 0, unbounded = 0;

            foreach (Element element in new FilteredElementCollector(doc)
                         .OfCategory(category).WhereElementIsNotElementType())
            {
                var spatial = element as SpatialElement;
                if (spatial == null) continue;

                string name = null, number = null;
                try { name = spatial.Name; } catch (Exception) { }
                try { number = spatial.Number; } catch (Exception) { }
                if (!Paging.Matches(name, nameContains)) continue;

                string level = null;
                try { level = spatial.Level != null ? spatial.Level.Name : null; } catch (Exception) { }
                if (!string.IsNullOrEmpty(levelName) && !Paging.EqualsCi(level, levelName)) continue;

                bool isPlaced = spatial.Location != null;
                double areaSqFt = 0;
                try { areaSqFt = spatial.Area; } catch (Exception) { }

                if (!isPlaced) unplaced++;
                else if (areaSqFt <= 1e-6) unbounded++;
                if (!isPlaced && !includeUnplaced) continue;

                double areaSqM = Metric.SqFeetToSqM(areaSqFt);
                totalArea += areaSqM;

                JsonValue r = J.O(
                    "id", Compat.IdValue(spatial.Id),
                    "number", number,
                    "name", name,
                    "level", level,
                    "areaM2", Metric.R(areaSqM, 3),
                    "placed", isPlaced);

                if (!isPlaced) r.Set("status", "unplaced");
                else if (areaSqM <= 1e-9) r.Set("status", "unbounded (not enclosed by bounding elements)");
                else r.Set("status", "ok");

                try { r.Set("perimeterMm", Metric.R(Metric.FeetToMm(spatial.Perimeter))); } catch (Exception) { }

                var room = spatial as Room;
                if (room != null)
                {
                    try { r.Set("volumeM3", Metric.R(Metric.CuFeetToCuM(room.Volume), 3)); } catch (Exception) { }
                    try { r.Set("unboundedHeightMm", Metric.R(Metric.FeetToMm(room.UnboundedHeight))); } catch (Exception) { }
                    r.Set("department", ElementSerializer.QuickString(room, BuiltInParameter.ROOM_DEPARTMENT));
                    r.Set("occupancy", ElementSerializer.QuickString(room, BuiltInParameter.ROOM_OCCUPANCY));
                }

                var space = spatial as Space;
                if (space != null)
                {
                    try { r.Set("volumeM3", Metric.R(Metric.CuFeetToCuM(space.Volume), 3)); } catch (Exception) { }
                }

                if (isPlaced)
                {
                    LocationPoint lp = spatial.Location as LocationPoint;
                    if (lp != null) r.Set("point", Metric.PointToMm(lp.Point));
                }

                items.Add(r);
            }

            items.Sort(delegate (JsonValue a, JsonValue b)
            {
                int byLevel = string.Compare(a["level"].AsString(""), b["level"].AsString(""), StringComparison.OrdinalIgnoreCase);
                if (byLevel != 0) return byLevel;
                return string.Compare(a["number"].AsString(""), b["number"].AsString(""), StringComparison.OrdinalIgnoreCase);
            });

            JsonValue page = Paging.Page(items, Args.Offset(ctx.Args), Args.Limit(ctx.Args), "rooms");
            page.Set("kind", kind);
            page.Set("totalAreaM2", Metric.R(totalArea, 2));
            page.Set("unplacedCount", unplaced);
            page.Set("unboundedCount", unbounded);
            page.Set("areaNote", "Areas are measured to the room-bounding rule set in the model " +
                                 "(Area and Volume Computations), not necessarily to a specific GIA standard.");
            return page;
        }

        // ==================================================================
        //  shared helper
        // ==================================================================
        internal static Dictionary<long, string> BuildViewToSheetMap(Document doc)
        {
            var map = new Dictionary<long, string>();
            foreach (Viewport vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
            {
                try
                {
                    ViewSheet sheet = doc.GetElement(vp.SheetId) as ViewSheet;
                    if (sheet == null) continue;
                    map[Compat.IdValue(vp.ViewId)] = sheet.SheetNumber + " - " + sheet.Name;
                }
                catch (Exception) { }
            }

            // Schedules are placed with ScheduleSheetInstance, not Viewport.
            foreach (ScheduleSheetInstance instance in new FilteredElementCollector(doc)
                         .OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
            {
                try
                {
                    ViewSheet sheet = doc.GetElement(instance.OwnerViewId) as ViewSheet;
                    if (sheet == null) continue;
                    map[Compat.IdValue(instance.ScheduleId)] = sheet.SheetNumber + " - " + sheet.Name;
                }
                catch (Exception) { }
            }

            return map;
        }
    }
}
