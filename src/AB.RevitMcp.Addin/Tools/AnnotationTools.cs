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
    /// <summary>Text, tags, detail lines and schedules - the documentation surface.</summary>
    public static class AnnotationTools
    {
        // ==================================================================
        //  revit_create_text_note
        // ==================================================================
        public static JsonValue CreateTextNote(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            View view = ResolveView(ctx);
            string text = Args.Str(ctx.Args, "text", true);
            XYZ point = Args.PointMm(ctx.Args, "point");
            double widthMm = Args.Num(ctx.Args, "width", false, double.NaN, 1, 100000);
            string align = Args.Str(ctx.Args, "horizontalAlign", false, "Left",
                new[] { "Left", "Center", "Right" });

            TextNoteType noteType = Selectors.FindElementType<TextNoteType>(
                doc, Args.Str(ctx.Args, "typeName"), true);
            if (noteType == null)
            {
                noteType = Selectors.DefaultType<TextNoteType>(doc, ElementTypeGroup.TextNoteType);
                if (noteType == null)
                    throw new ToolException(BridgeErrorCodes.NotFound, "This model has no text note types.");
            }

            TextNote note = ctx.InTransaction("Create text note", delegate
            {
                var options = new TextNoteOptions(noteType.Id);
                options.HorizontalAlignment =
                    (HorizontalTextAlignment)Enum.Parse(typeof(HorizontalTextAlignment), align, true);

                return double.IsNaN(widthMm)
                    ? TextNote.Create(doc, view.Id, point, text, options)
                    : TextNote.Create(doc, view.Id, point, Metric.MmToFeet(widthMm), text, options);
            });

            if (note == null)
                throw new ToolException(BridgeErrorCodes.RevitApi, "Revit did not create the text note.");

            return J.O(
                "created", J.O("id", Compat.IdValue(note.Id), "text", text),
                "view", view.Name,
                "type", noteType.Name,
                "point", Metric.PointToMm(point));
        }

        // ==================================================================
        //  revit_tag_elements
        // ==================================================================
        public static JsonValue TagElements(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            View view = ResolveView(ctx);
            List<long> ids = Args.IdValues(ctx.Args, "elementIds", true, 500);
            bool addLeader = Args.Bool(ctx.Args, "addLeader", false);
            string orientation = Args.Str(ctx.Args, "orientation", false, "Horizontal",
                new[] { "Horizontal", "Vertical" });
            string tagTypeName = Args.Str(ctx.Args, "tagTypeName");

            XYZ offset = XYZ.Zero;
            JsonValue offsetArg = ctx.Args["offset"];
            if (offsetArg.IsObject)
            {
                offset = Metric.PointFromMm(offsetArg["dx"].AsDouble(0), offsetArg["dy"].AsDouble(0), 0);
            }

            List<long> missing;
            List<Element> elements = Args.Elements(doc, ids, out missing);

            TagOrientation tagOrientation = Paging.EqualsCi(orientation, "Vertical")
                ? TagOrientation.Vertical
                : TagOrientation.Horizontal;

            var tagTypeByCategory = new Dictionary<long, ElementId>();
            JsonValue tagged = JsonValue.NewArray();
            JsonValue skipped = JsonValue.NewArray();

            ctx.InTransaction("Tag elements", delegate
            {
                for (int i = 0; i < elements.Count; i++)
                {
                    Element element = elements[i];
                    long id = Compat.IdValue(element.Id);

                    Category category = null;
                    try { category = element.Category; } catch (Exception) { }
                    if (category == null)
                    {
                        skipped.Add(J.O("elementId", id, "reason", "The element has no category."));
                        continue;
                    }

                    ElementId tagTypeId;
                    long categoryKey = Compat.IdValue(category.Id);
                    if (!tagTypeByCategory.TryGetValue(categoryKey, out tagTypeId))
                    {
                        tagTypeId = FindTagType(doc, category, tagTypeName);
                        tagTypeByCategory[categoryKey] = tagTypeId;
                    }

                    if (!Compat.IsValid(tagTypeId))
                    {
                        skipped.Add(J.O("elementId", id, "category", category.Name,
                            "reason", "No tag family is loaded for '" + category.Name +
                                      "'. Load a tag family for that category first."));
                        continue;
                    }

                    try
                    {
                        XYZ point = TagPoint(element, offset);
                        IndependentTag tag = IndependentTag.Create(
                            doc, tagTypeId, view.Id, new Reference(element), addLeader, tagOrientation, point);
                        tagged.Add(J.O("elementId", id, "tagId", Compat.IdValue(tag.Id),
                                       "category", category.Name));
                    }
                    catch (Exception ex)
                    {
                        skipped.Add(J.O("elementId", id, "category", category.Name, "reason", ex.Message));
                    }
                }
            });

            JsonValue result = J.O(
                "tagged", tagged.Count,
                "skipped", skipped.Count,
                "view", view.Name,
                "tags", tagged,
                "skippedDetail", skipped);
            if (missing.Count > 0) result.Set("notFound", J.ALongs(missing));
            return result;
        }

        /// <summary>
        /// Finds a tag FamilySymbol valid for an element category.
        ///
        /// Revit does not expose a category -> tag-category relationship, so this walks the
        /// naming convention (OST_Walls -> OST_WallTags) with an explicit table for the cases
        /// where the convention does not hold, notably MEP (OST_DuctCurves -> OST_DuctTags).
        /// </summary>
        private static ElementId FindTagType(Document doc, Category category, string preferredTypeName)
        {
            BuiltInCategory tagCategory = TagCategoryFor(category);
            if (tagCategory == BuiltInCategory.INVALID) return ElementId.InvalidElementId;

            List<FamilySymbol> symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(tagCategory)
                .Cast<FamilySymbol>()
                .ToList();

            if (symbols.Count == 0) return ElementId.InvalidElementId;

            FamilySymbol chosen = null;
            if (!string.IsNullOrEmpty(preferredTypeName))
            {
                chosen = symbols.FirstOrDefault(
                    s => string.Equals(s.Name, preferredTypeName, StringComparison.OrdinalIgnoreCase));
            }
            if (chosen == null) chosen = symbols[0];

            if (!chosen.IsActive)
            {
                chosen.Activate();
                doc.Regenerate();
            }
            return chosen.Id;
        }

        private static readonly Dictionary<BuiltInCategory, BuiltInCategory> TagExceptions =
            new Dictionary<BuiltInCategory, BuiltInCategory>
            {
                { BuiltInCategory.OST_DuctCurves,      BuiltInCategory.OST_DuctTags },
                { BuiltInCategory.OST_PipeCurves,      BuiltInCategory.OST_PipeTags },
                { BuiltInCategory.OST_FlexDuctCurves,  BuiltInCategory.OST_FlexDuctTags },
                { BuiltInCategory.OST_FlexPipeCurves,  BuiltInCategory.OST_FlexPipeTags },
                { BuiltInCategory.OST_CableTray,       BuiltInCategory.OST_CableTrayTags },
                { BuiltInCategory.OST_Conduit,         BuiltInCategory.OST_ConduitTags },
                { BuiltInCategory.OST_DuctFitting,     BuiltInCategory.OST_DuctFittingTags },
                { BuiltInCategory.OST_PipeFitting,     BuiltInCategory.OST_PipeFittingTags },
                { BuiltInCategory.OST_MEPSpaces,       BuiltInCategory.OST_MEPSpaceTags },
                { BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralFramingTags },
                { BuiltInCategory.OST_Areas,           BuiltInCategory.OST_AreaTags },
            };

        private static BuiltInCategory TagCategoryFor(Category category)
        {
            BuiltInCategory bic = Compat.GetBuiltInCategory(category);
            if (bic == BuiltInCategory.INVALID) return BuiltInCategory.INVALID;

            BuiltInCategory mapped;
            if (TagExceptions.TryGetValue(bic, out mapped)) return mapped;

            string name = bic.ToString();                    // e.g. "OST_Walls"
            var candidates = new List<string>();
            if (name.EndsWith("s", StringComparison.Ordinal))
                candidates.Add(name.Substring(0, name.Length - 1) + "Tags");   // OST_WallTags
            candidates.Add(name + "Tags");                                     // OST_FurnitureTags

            for (int i = 0; i < candidates.Count; i++)
            {
                try
                {
                    if (Enum.IsDefined(typeof(BuiltInCategory), candidates[i]))
                        return (BuiltInCategory)Enum.Parse(typeof(BuiltInCategory), candidates[i]);
                }
                catch (Exception) { }
            }
            return BuiltInCategory.INVALID;
        }

        private static XYZ TagPoint(Element element, XYZ offset)
        {
            XYZ basePoint = null;
            try
            {
                LocationPoint lp = element.Location as LocationPoint;
                if (lp != null) basePoint = lp.Point;

                LocationCurve lc = element.Location as LocationCurve;
                if (basePoint == null && lc != null && lc.Curve != null)
                    basePoint = lc.Curve.Evaluate(0.5, true);
            }
            catch (Exception) { }

            if (basePoint == null)
            {
                try
                {
                    BoundingBoxXYZ bb = element.get_BoundingBox(null);
                    if (bb != null) basePoint = (bb.Min + bb.Max) * 0.5;
                }
                catch (Exception) { }
            }

            if (basePoint == null) basePoint = XYZ.Zero;
            return basePoint + offset;
        }

        // ==================================================================
        //  revit_create_detail_line
        // ==================================================================
        public static JsonValue CreateDetailLine(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            View view = ResolveView(ctx);
            XYZ start = Args.PointMm(ctx.Args, "start");
            XYZ end = Args.PointMm(ctx.Args, "end");
            string lineStyleName = Args.Str(ctx.Args, "lineStyleName");

            if (start.DistanceTo(end) < 1e-9)
                throw ToolException.Invalid("end", "the start and end points are identical.");

            if (view.ViewType == ViewType.ThreeD)
                throw new ToolException(BridgeErrorCodes.InvalidArgument,
                    "Detail lines cannot be drawn in a 3D view. Use a plan, section or drafting view.");

            DetailCurve curve = ctx.InTransaction("Create detail line", delegate
            {
                DetailCurve created = doc.Create.NewDetailCurve(view, Line.CreateBound(start, end));
                if (created != null && !string.IsNullOrEmpty(lineStyleName))
                {
                    GraphicsStyle style = FindLineStyle(doc, lineStyleName);
                    if (style != null) created.LineStyle = style;
                    else ctx.AddWarning("Line style '" + lineStyleName + "' was not found; the default was used.");
                }
                return created;
            });

            if (curve == null)
                throw new ToolException(BridgeErrorCodes.RevitApi, "Revit did not create the detail line.");

            return J.O(
                "created", J.O("id", Compat.IdValue(curve.Id)),
                "view", view.Name,
                "start", Metric.PointToMm(start),
                "end", Metric.PointToMm(end),
                "lengthMm", Metric.R(Metric.FeetToMm(start.DistanceTo(end))));
        }

        private static GraphicsStyle FindLineStyle(Document doc, string name)
        {
            try
            {
                Category lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                if (lines == null) return null;
                foreach (Category sub in lines.SubCategories)
                {
                    if (string.Equals(sub.Name, name, StringComparison.OrdinalIgnoreCase))
                        return sub.GetGraphicsStyle(GraphicsStyleType.Projection);
                }
            }
            catch (Exception) { }
            return null;
        }

        // ==================================================================
        //  revit_create_schedule
        // ==================================================================
        public static JsonValue CreateSchedule(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            BuiltInCategory category = Selectors.ResolveCategory(doc, Args.Str(ctx.Args, "category", true));
            string name = Args.Str(ctx.Args, "name");
            List<string> wanted = Args.StrList(ctx.Args, "fields", 60);
            string sortBy = Args.Str(ctx.Args, "sortByField");
            bool itemized = Args.Bool(ctx.Args, "isItemized", true);

            JsonValue added = JsonValue.NewArray();
            JsonValue notFound = JsonValue.NewArray();
            var available = new List<string>();

            ViewSchedule schedule = ctx.InTransaction("Create schedule", delegate
            {
                ViewSchedule created = ViewSchedule.CreateSchedule(doc, new ElementId(category));
                if (created == null) return null;

                if (!string.IsNullOrEmpty(name))
                {
                    try { created.Name = name; }
                    catch (Exception ex) { ctx.AddWarning("Could not name the schedule: " + ex.Message); }
                }

                ScheduleDefinition definition = created.Definition;
                definition.IsItemized = itemized;

                // Index the schedulable fields once - the list can be long.
                var byName = new Dictionary<string, SchedulableField>(StringComparer.OrdinalIgnoreCase);
                foreach (SchedulableField field in definition.GetSchedulableFields())
                {
                    string fieldName;
                    try { fieldName = field.GetName(doc); }
                    catch (Exception) { continue; }
                    if (string.IsNullOrEmpty(fieldName)) continue;
                    available.Add(fieldName);
                    if (!byName.ContainsKey(fieldName)) byName[fieldName] = field;
                }

                foreach (string want in wanted)
                {
                    SchedulableField field;
                    if (!byName.TryGetValue(want, out field)) { notFound.Add(want); continue; }
                    try
                    {
                        ScheduleField addedField = definition.AddField(field);
                        added.Add(J.O("name", want, "fieldId", addedField.FieldId.IntegerValue));
                    }
                    catch (Exception ex)
                    {
                        notFound.Add(want);
                        ctx.AddWarning("Field '" + want + "' could not be added: " + ex.Message);
                    }
                }

                if (!string.IsNullOrEmpty(sortBy))
                {
                    ScheduleField sortField = null;
                    for (int i = 0; i < definition.GetFieldCount(); i++)
                    {
                        ScheduleField f = definition.GetField(i);
                        if (string.Equals(f.GetName(), sortBy, StringComparison.OrdinalIgnoreCase)) { sortField = f; break; }
                    }
                    if (sortField != null)
                    {
                        definition.AddSortGroupField(new ScheduleSortGroupField(sortField.FieldId));
                    }
                    else
                    {
                        ctx.AddWarning("Sort field '" + sortBy + "' is not one of the schedule's columns.");
                    }
                }

                return created;
            });

            if (schedule == null)
                throw new ToolException(BridgeErrorCodes.RevitApi, "Revit did not create the schedule.");

            JsonValue result = J.O(
                "created", J.O(
                    "id", Compat.IdValue(schedule.Id),
                    "name", schedule.Name),
                "category", Args.Str(ctx.Args, "category", true),
                "fieldsAdded", added,
                "isItemized", itemized);

            if (notFound.Count > 0)
            {
                // Returning the whole valid set here is what removes the need for a separate
                // "list schedulable fields" tool - one call tells you exactly what you may use.
                available.Sort(StringComparer.OrdinalIgnoreCase);
                result.Set("fieldsNotFound", notFound);
                result.Set("availableFields", J.AStrings(available));
                result.Set("hint", "The names in 'availableFields' are the only valid columns for " +
                                   "this category. Re-run with those.");
            }

            return result;
        }

        // ==================================================================
        //  shared
        // ==================================================================

        /// <summary>Resolves the target view from viewId / viewName, defaulting to the active view.</summary>
        internal static View ResolveView(ToolContext ctx)
        {
            Document doc = ctx.Doc;

            long viewId = Args.Long(ctx.Args, "viewId", false, 0);
            if (viewId > 0)
            {
                View byId = doc.GetElement(Compat.ToId(viewId)) as View;
                if (byId == null)
                    throw ToolException.NotFound("View", viewId.ToString(CultureInfo.InvariantCulture));
                return byId;
            }

            string viewName = Args.Str(ctx.Args, "viewName");
            if (!string.IsNullOrEmpty(viewName)) return Selectors.FindView(doc, viewName);

            return ctx.ActiveView;
        }
    }
}
