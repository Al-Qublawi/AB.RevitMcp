using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AB.RevitMcp.Addin.Bridge;
using AB.RevitMcp.Addin.Revit;
using AB.RevitMcp.Contracts.Json;
using Autodesk.Revit.DB;

namespace AB.RevitMcp.Addin.Tools
{
    /// <summary>
    /// READ tools, part 1: model identity, health, and the listing tools that let an AI discover
    /// the exact names it must use in later calls. None of these open a transaction.
    /// </summary>
    public static partial class ReadTools
    {
        // ==================================================================
        //  revit_get_model_info
        // ==================================================================
        public static JsonValue GetModelInfo(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            ProjectInfo info = doc.ProjectInformation;

            JsonValue project = JsonValue.NewObject();
            if (info != null)
            {
                project.Set("name", SafeParam(info, BuiltInParameter.PROJECT_NAME));
                project.Set("number", SafeParam(info, BuiltInParameter.PROJECT_NUMBER));
                project.Set("status", SafeParam(info, BuiltInParameter.PROJECT_STATUS));
                project.Set("clientName", SafeParam(info, BuiltInParameter.CLIENT_NAME));
                project.Set("address", SafeParam(info, BuiltInParameter.PROJECT_ADDRESS));
                project.Set("buildingName", SafeParam(info, BuiltInParameter.PROJECT_BUILDING_NAME));
                project.Set("organizationName", SafeParam(info, BuiltInParameter.PROJECT_ORGANIZATION_NAME));
            }

            int totalElements = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType().GetElementCount();
            int totalTypes = new FilteredElementCollector(doc)
                .WhereElementIsElementType().GetElementCount();

            string path = null;
            long fileSize = -1;
            try
            {
                path = doc.PathName;
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) fileSize = new FileInfo(path).Length;
            }
            catch (Exception) { }

            JsonValue result = J.O(
                "title", doc.Title,
                "pathName", string.IsNullOrEmpty(path) ? null : path,
                "fileSizeMb", fileSize > 0 ? (object)Metric.R(fileSize / 1048576.0, 2) : null,
                "revitVersion", doc.Application.VersionNumber,
                "revitBuild", doc.Application.VersionBuild,
                "revitName", doc.Application.VersionName,
                "addinCompiledFor", Compat.RevitReleaseName,
                "isWorkshared", doc.IsWorkshared,
                "isReadOnly", doc.IsReadOnly,
                "isModified", doc.IsModified,
                "isFamilyDocument", doc.IsFamilyDocument,
                "isDetached", doc.IsDetached,
                "project", project,
                "counts", J.O(
                    "elements", totalElements,
                    "types", totalTypes,
                    "levels", new FilteredElementCollector(doc).OfClass(typeof(Level)).GetElementCount(),
                    "grids", new FilteredElementCollector(doc).OfClass(typeof(Grid)).GetElementCount(),
                    "views", new FilteredElementCollector(doc).OfClass(typeof(View))
                                 .Cast<View>().Count(v => !v.IsTemplate),
                    "sheets", new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).GetElementCount(),
                    "rooms", new FilteredElementCollector(doc)
                                 .OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().GetElementCount(),
                    "warnings", doc.GetWarnings().Count,
                    "revitLinks", new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).GetElementCount()),
                "units", Metric.Convention());

            if (doc.IsWorkshared)
            {
                try
                {
                    result.Set("worksets", new FilteredWorksetCollector(doc)
                        .OfKind(WorksetKind.UserWorkset).ToWorksets().Count);
                    result.Set("centralPath", SafeCentralPath(doc));
                }
                catch (Exception) { }
            }

            try
            {
                View active = doc.ActiveView;
                if (active != null)
                {
                    result.Set("activeView", J.O(
                        "id", Compat.IdValue(active.Id),
                        "name", active.Name,
                        "type", active.ViewType.ToString()));
                }
            }
            catch (Exception) { }

            return result;
        }

        // ==================================================================
        //  revit_get_model_health
        // ==================================================================
        public static JsonValue GetModelHealth(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            int topCategories = Args.Int(ctx.Args, "topCategories", false, 15, 1, 100);

            // ---- element counts per category ----
            var perCategory = new Dictionary<string, int>(StringComparer.Ordinal);
            int inPlaceFamilies = 0;
            int totalElements = 0;

            foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                totalElements++;
                Category c = null;
                try { c = e.Category; } catch (Exception) { }
                string name = c != null && !string.IsNullOrEmpty(c.Name) ? c.Name : "(no category)";
                int current;
                perCategory[name] = perCategory.TryGetValue(name, out current) ? current + 1 : 1;
            }

            foreach (Family f in new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>())
            {
                try { if (f.IsInPlace) inPlaceFamilies++; }
                catch (Exception) { }
            }

            var sortedCategories = perCategory.ToList();
            sortedCategories.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            {
                return b.Value.CompareTo(a.Value);
            });

            JsonValue categories = JsonValue.NewArray();
            for (int i = 0; i < sortedCategories.Count && i < topCategories; i++)
                categories.Add(J.O("category", sortedCategories[i].Key, "count", sortedCategories[i].Value));

            // ---- warnings grouped by text ----
            IList<FailureMessage> warnings = doc.GetWarnings();
            var warningGroups = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < warnings.Count; i++)
            {
                string text;
                try { text = warnings[i].GetDescriptionText(); }
                catch (Exception) { text = "(unreadable warning)"; }
                int current;
                warningGroups[text] = warningGroups.TryGetValue(text, out current) ? current + 1 : 1;
            }
            var topWarnings = warningGroups.ToList();
            topWarnings.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            {
                return b.Value.CompareTo(a.Value);
            });
            JsonValue warningJson = JsonValue.NewArray();
            for (int i = 0; i < topWarnings.Count && i < 15; i++)
                warningJson.Add(J.O("description", topWarnings[i].Key, "count", topWarnings[i].Value));

            // ---- views, sheets, links, imports ----
            List<View> views = new FilteredElementCollector(doc).OfClass(typeof(View))
                .Cast<View>().Where(v => !v.IsTemplate).ToList();
            var placedViewIds = new HashSet<long>();
            foreach (Viewport vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
                placedViewIds.Add(Compat.IdValue(vp.ViewId));

            int viewsNotOnSheets = views.Count(v => !placedViewIds.Contains(Compat.IdValue(v.Id))
                                                    && v.ViewType != ViewType.ProjectBrowser
                                                    && v.ViewType != ViewType.SystemBrowser
                                                    && v.ViewType != ViewType.DrawingSheet
                                                    && v.ViewType != ViewType.Schedule
                                                    && v.ViewType != ViewType.Legend);

            int dwgImports = 0, dwgLinks = 0;
            foreach (ImportInstance import in new FilteredElementCollector(doc)
                         .OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
            {
                if (import.IsLinked) dwgLinks++; else dwgImports++;
            }

            // ---- rooms ----
            List<Element> rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().ToElements().ToList();
            int unplacedRooms = 0, unboundedRooms = 0;
            for (int i = 0; i < rooms.Count; i++)
            {
                var room = rooms[i] as Autodesk.Revit.DB.Architecture.Room;
                if (room == null) continue;
                if (room.Location == null) unplacedRooms++;
                else if (room.Area <= 1e-6) unboundedRooms++;
            }

            long fileSize = -1;
            try
            {
                if (!string.IsNullOrEmpty(doc.PathName) && File.Exists(doc.PathName))
                    fileSize = new FileInfo(doc.PathName).Length;
            }
            catch (Exception) { }

            // ---- a plain-language verdict, so the AI does not have to invent thresholds ----
            var flags = new List<string>();
            if (warnings.Count > 1000) flags.Add("Warning count is very high (" + warnings.Count + "). Review and resolve before issuing.");
            else if (warnings.Count > 250) flags.Add("Warning count is elevated (" + warnings.Count + ").");
            if (inPlaceFamilies > 20) flags.Add(inPlaceFamilies + " in-place families - these bloat the model and do not schedule well.");
            if (dwgImports > 0) flags.Add(dwgImports + " EXPLODED/imported CAD instance(s) - a common cause of model bloat and stray line styles.");
            if (fileSize > 400L * 1024 * 1024) flags.Add("File is over 400 MB - expect performance problems.");
            if (viewsNotOnSheets > 200) flags.Add(viewsNotOnSheets + " views are not placed on any sheet - consider purging working views.");
            if (unplacedRooms > 0) flags.Add(unplacedRooms + " unplaced room(s).");
            if (unboundedRooms > 0) flags.Add(unboundedRooms + " unbounded room(s) with zero area.");
            if (flags.Count == 0) flags.Add("No headline issues found against the standard thresholds.");

            return J.O(
                "title", doc.Title,
                "fileSizeMb", fileSize > 0 ? (object)Metric.R(fileSize / 1048576.0, 2) : null,
                "totalElements", totalElements,
                "elementTypes", new FilteredElementCollector(doc).WhereElementIsElementType().GetElementCount(),
                "topCategories", categories,
                "warnings", J.O(
                    "total", warnings.Count,
                    "distinctDescriptions", warningGroups.Count,
                    "top", warningJson),
                "families", J.O(
                    "total", new FilteredElementCollector(doc).OfClass(typeof(Family)).GetElementCount(),
                    "inPlace", inPlaceFamilies),
                "views", J.O(
                    "total", views.Count,
                    "templates", new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Count(v => v.IsTemplate),
                    "sheets", new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).GetElementCount(),
                    "notOnSheets", viewsNotOnSheets),
                "cad", J.O("importedInstances", dwgImports, "linkedInstances", dwgLinks),
                "links", J.O(
                    "revitLinkTypes", new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).GetElementCount(),
                    "revitLinkInstances", new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).GetElementCount()),
                "rooms", J.O("total", rooms.Count, "unplaced", unplacedRooms, "unbounded", unboundedRooms),
                "worksets", doc.IsWorkshared
                    ? (object)new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().Count
                    : null,
                "isWorkshared", doc.IsWorkshared,
                "findings", J.AStrings(flags));
        }

        // ==================================================================
        //  revit_list_categories
        // ==================================================================
        public static JsonValue ListCategories(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            bool onlyWithElements = Args.Bool(ctx.Args, "onlyWithElements", true);
            string nameContains = Args.Str(ctx.Args, "nameContains");

            // One pass over the model beats one collector per category.
            var counts = new Dictionary<long, int>();
            foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                Category c = null;
                try { c = e.Category; } catch (Exception) { }
                if (c == null) continue;
                long key = Compat.IdValue(c.Id);
                int current;
                counts[key] = counts.TryGetValue(key, out current) ? current + 1 : 1;
            }

            var items = new List<JsonValue>();
            foreach (Category c in doc.Settings.Categories)
            {
                if (c == null || string.IsNullOrEmpty(c.Name)) continue;
                if (!Paging.Matches(c.Name, nameContains)) continue;

                long id = Compat.IdValue(c.Id);
                int count;
                counts.TryGetValue(id, out count);
                if (onlyWithElements && count == 0) continue;

                BuiltInCategory bic = Compat.GetBuiltInCategory(c);
                items.Add(J.O(
                    "name", c.Name,
                    "builtInCategory", bic == BuiltInCategory.INVALID ? null : bic.ToString(),
                    "categoryId", id,
                    "elementCount", count,
                    "categoryType", c.CategoryType.ToString(),
                    "canHaveMaterial", SafeBool(delegate { return c.AllowsBoundParameters; })));
            }

            items.Sort(delegate (JsonValue a, JsonValue b)
            {
                int byCount = b["elementCount"].AsInt(0).CompareTo(a["elementCount"].AsInt(0));
                if (byCount != 0) return byCount;
                return string.Compare(a["name"].AsString(""), b["name"].AsString(""), StringComparison.OrdinalIgnoreCase);
            });

            return Paging.Page(items, Args.Offset(ctx.Args), Args.Limit(ctx.Args), "categories");
        }

        // ==================================================================
        //  revit_list_levels
        // ==================================================================
        public static JsonValue ListLevels(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            List<Level> levels = Selectors.AllLevels(doc);

            // Which levels already have a plan view?
            var levelsWithPlans = new Dictionary<long, List<string>>();
            foreach (ViewPlan plan in new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>())
            {
                if (plan.IsTemplate || plan.GenLevel == null) continue;
                long key = Compat.IdValue(plan.GenLevel.Id);
                if (!levelsWithPlans.ContainsKey(key)) levelsWithPlans[key] = new List<string>();
                levelsWithPlans[key].Add(plan.Name);
            }

            JsonValue items = JsonValue.NewArray();
            for (int i = 0; i < levels.Count; i++)
            {
                Level level = levels[i];
                long key = Compat.IdValue(level.Id);
                List<string> plans;
                levelsWithPlans.TryGetValue(key, out plans);

                double? toNext = null;
                if (i + 1 < levels.Count)
                    toNext = Metric.R(Metric.FeetToMm(levels[i + 1].Elevation - level.Elevation));

                items.Add(J.O(
                    "id", key,
                    "name", level.Name,
                    "elevationMm", Metric.R(Metric.FeetToMm(level.Elevation)),
                    "heightToNextLevelMm", toNext.HasValue ? (object)toNext.Value : null,
                    "planViews", plans != null ? J.AStrings(plans) : JsonValue.NewArray(),
                    "hasPlanView", plans != null && plans.Count > 0));
            }

            return J.O("levels", items, "count", levels.Count, "unit", "mm");
        }

        // ==================================================================
        //  revit_list_worksets
        // ==================================================================
        public static JsonValue ListWorksets(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            if (!doc.IsWorkshared)
            {
                return J.O(
                    "isWorkshared", false,
                    "worksets", JsonValue.NewArray(),
                    "count", 0,
                    "note", "This model is not workshared, so it has no worksets. " +
                            "Enable worksharing in Revit (Collaborate > Worksets) first.");
            }

            string kind = Args.Str(ctx.Args, "kind", false, "user",
                new[] { "user", "standard", "view", "family", "all" });

            var collector = new FilteredWorksetCollector(doc);
            if (!Paging.EqualsCi(kind, "all")) collector = collector.OfKind(ParseWorksetKind(kind));

            WorksetTable table = doc.GetWorksetTable();
            WorksetId activeId = table.GetActiveWorksetId();

            JsonValue items = JsonValue.NewArray();
            int count = 0;
            foreach (Workset ws in collector.ToWorksets())
            {
                count++;
                items.Add(J.O(
                    "id", ws.Id.IntegerValue,
                    "name", ws.Name,
                    "kind", ws.Kind.ToString(),
                    "isOpen", ws.IsOpen,
                    "isEditable", ws.IsEditable,
                    "isDefaultVisible", ws.IsVisibleByDefault,
                    "owner", string.IsNullOrEmpty(ws.Owner) ? null : ws.Owner,
                    "isActive", ws.Id == activeId));
            }

            return J.O("isWorkshared", true, "worksets", items, "count", count,
                       "activeWorksetId", activeId.IntegerValue);
        }

        private static WorksetKind ParseWorksetKind(string kind)
        {
            switch (kind.ToLowerInvariant())
            {
                case "standard": return WorksetKind.OtherWorkset;
                case "view": return WorksetKind.ViewWorkset;
                case "family": return WorksetKind.FamilyWorkset;
                default: return WorksetKind.UserWorkset;
            }
        }

        // ==================================================================
        //  revit_list_grids
        // ==================================================================
        public static JsonValue ListGrids(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            JsonValue items = JsonValue.NewArray();
            int count = 0;

            foreach (Grid grid in new FilteredElementCollector(doc)
                         .OfClass(typeof(Grid)).WhereElementIsNotElementType().Cast<Grid>())
            {
                count++;
                JsonValue g = J.O("id", Compat.IdValue(grid.Id), "name", grid.Name);
                try
                {
                    Curve c = grid.Curve;
                    if (c != null && c.IsBound)
                    {
                        g.Set("start", Metric.PointToMm(c.GetEndPoint(0)));
                        g.Set("end", Metric.PointToMm(c.GetEndPoint(1)));
                        g.Set("lengthMm", Metric.R(Metric.FeetToMm(c.Length)));
                        g.Set("isCurved", !(c is Line));
                    }
                }
                catch (Exception) { }
                items.Add(g);
            }

            return J.O("grids", items, "count", count, "unit", "mm");
        }

        // ==================================================================
        //  helpers
        // ==================================================================

        internal static string SafeParam(Element element, BuiltInParameter bip)
        {
            try
            {
                Parameter p = element.get_Parameter(bip);
                if (p == null || !p.HasValue) return null;
                string s = p.AsString();
                return string.IsNullOrEmpty(s) ? p.AsValueString() : s;
            }
            catch (Exception) { return null; }
        }

        internal static object SafeBool(Func<bool> getter)
        {
            try { return getter(); }
            catch (Exception) { return null; }
        }

        private static string SafeCentralPath(Document doc)
        {
            try
            {
                ModelPath central = doc.GetWorksharingCentralModelPath();
                if (central == null) return null;
                return ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
            }
            catch (Exception) { return null; }
        }
    }
}
