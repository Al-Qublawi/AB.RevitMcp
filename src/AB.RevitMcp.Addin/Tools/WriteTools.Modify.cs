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
    /// WRITE tools, part 2: editing existing elements, and creating views, sheets and viewports.
    ///
    /// The batch tools follow one rule consistently: an element that cannot be changed is REPORTED
    /// as skipped, with the reason, rather than aborting the whole call. A partial success with an
    /// honest report is far more useful to an AI than an all-or-nothing failure.
    /// </summary>
    public static partial class WriteTools
    {
        // ==================================================================
        //  revit_set_parameters
        // ==================================================================
        public static JsonValue SetParameters(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            List<long> ids = Args.IdValues(ctx.Args, "elementIds", true, 5000);
            bool applyToType = Args.Bool(ctx.Args, "applyToType", false);

            JsonValue assignments = ctx.Args["parameters"];
            if (!assignments.IsArray || assignments.Count == 0)
                throw ToolException.Invalid("parameters", "at least one parameter assignment is required.");

            List<long> missing;
            List<Element> elements = Args.Elements(doc, ids, out missing);

            var targets = new List<Element>();
            var seenTypeIds = new HashSet<long>();
            for (int i = 0; i < elements.Count; i++)
            {
                if (!applyToType) { targets.Add(elements[i]); continue; }

                ElementId typeId = elements[i].GetTypeId();
                if (!Compat.IsValid(typeId)) continue;
                if (!seenTypeIds.Add(Compat.IdValue(typeId))) continue;   // edit each type once
                Element type = doc.GetElement(typeId);
                if (type != null) targets.Add(type);
            }

            JsonValue changes = JsonValue.NewArray();
            int updated = 0, skipped = 0;

            ctx.InTransaction("Set parameters", delegate
            {
                for (int t = 0; t < targets.Count; t++)
                {
                    Element target = targets[t];

                    foreach (JsonValue assignment in assignments.Items)
                    {
                        string name = assignment["name"].AsString(null);
                        if (string.IsNullOrEmpty(name)) continue;

                        bool clear = assignment["clear"].AsBool(false);
                        JsonValue value = assignment["value"];

                        JsonValue outcome = J.O(
                            "elementId", Compat.IdValue(target.Id),
                            "parameter", name);

                        Parameter p = Selectors.FindParameter(target, name, false);
                        if (p == null)
                        {
                            outcome.Set("status", "skipped");
                            outcome.Set("reason", "This element has no parameter called '" + name + "'.");
                            skipped++;
                            changes.Add(outcome);
                            continue;
                        }

                        if (p.IsReadOnly)
                        {
                            outcome.Set("status", "skipped");
                            outcome.Set("reason", "The parameter is read-only or calculated by Revit.");
                            skipped++;
                            changes.Add(outcome);
                            continue;
                        }

                        try
                        {
                            string before = SafeDisplay(p);
                            bool ok = clear ? ClearParameter(p) : ApplyValue(doc, p, value);

                            if (ok)
                            {
                                updated++;
                                outcome.Set("status", "updated");
                                outcome.Set("before", before);
                                outcome.Set("after", SafeDisplay(p));
                            }
                            else
                            {
                                skipped++;
                                outcome.Set("status", "skipped");
                                outcome.Set("reason", "Revit rejected the value for this parameter type (" +
                                                     p.StorageType + ").");
                            }
                        }
                        catch (ToolException ex)
                        {
                            skipped++;
                            outcome.Set("status", "skipped");
                            outcome.Set("reason", ex.Message);
                        }
                        catch (Exception ex)
                        {
                            skipped++;
                            outcome.Set("status", "skipped");
                            outcome.Set("reason", ex.Message);
                        }

                        changes.Add(outcome);
                    }
                }
            });

            JsonValue result = J.O(
                "updated", updated,
                "skipped", skipped,
                "elementsTargeted", targets.Count,
                "appliedToType", applyToType,
                "changes", changes,
                "units", Metric.Convention());

            if (missing.Count > 0) result.Set("notFound", J.ALongs(missing));
            return result;
        }

        /// <summary>Writes a JSON value into a Revit parameter, converting metric -> internal units.</summary>
        private static bool ApplyValue(Document doc, Parameter p, JsonValue value)
        {
            if (value.IsNull) return ClearParameter(p);

            switch (p.StorageType)
            {
                case StorageType.String:
                    return p.Set(value.AsString(string.Empty));

                case StorageType.Integer:
                    {
                        if (Metric.IsYesNo(p.Definition))
                            return p.Set(value.AsBool(false) ? 1 : 0);

                        double d = value.AsDouble(double.NaN);
                        if (double.IsNaN(d))
                            throw new ToolException(BridgeErrorCodes.InvalidArgument,
                                "'" + value.AsString("") + "' is not a whole number.");
                        return p.Set((int)Math.Round(d, MidpointRounding.AwayFromZero));
                    }

                case StorageType.Double:
                    {
                        double metric = value.AsDouble(double.NaN);
                        if (double.IsNaN(metric))
                            throw new ToolException(BridgeErrorCodes.InvalidArgument,
                                "'" + value.AsString("") + "' is not a number.");
                        MetricUnit unit = Metric.UnitOf(p.Definition);
                        return p.Set(Metric.ToInternal(metric, unit));
                    }

                case StorageType.ElementId:
                    {
                        // A numeric id, or the NAME of an element (material, level, type ...).
                        long id = value.AsLong(0);
                        if (id > 0 && value.IsNumber) return p.Set(Compat.ToId(id));

                        string name = value.AsString(null);
                        if (string.IsNullOrEmpty(name)) return false;

                        ElementId resolved = ResolveIdByName(doc, p, name);
                        if (!Compat.IsValid(resolved))
                            throw new ToolException(BridgeErrorCodes.NotFound,
                                "Could not resolve '" + name + "' to an element for parameter '" +
                                p.Definition.Name + "'. Pass a numeric element id instead.");
                        return p.Set(resolved);
                    }

                default:
                    return false;
            }
        }

        private static ElementId ResolveIdByName(Document doc, Parameter p, string name)
        {
            // Materials are by far the most common ElementId parameter written by name.
            Material material = Selectors.FindMaterial(doc, name, false);
            if (material != null) return material.Id;

            Level level = Selectors.FindLevel(doc, name, false);
            if (level != null) return level.Id;

            ElementType type = new FilteredElementCollector(doc)
                .WhereElementIsElementType().Cast<ElementType>()
                .FirstOrDefault(t => string.Equals(ElementSerializer.SafeName(t), name, StringComparison.OrdinalIgnoreCase));
            if (type != null) return type.Id;

            return ElementId.InvalidElementId;
        }

        private static bool ClearParameter(Parameter p)
        {
            switch (p.StorageType)
            {
                case StorageType.String: return p.Set(string.Empty);
                case StorageType.Integer: return p.Set(0);
                case StorageType.Double: return p.Set(0.0);
                case StorageType.ElementId: return p.Set(ElementId.InvalidElementId);
                default: return false;
            }
        }

        private static string SafeDisplay(Parameter p)
        {
            try
            {
                if (!p.HasValue) return null;
                switch (p.StorageType)
                {
                    case StorageType.String: return p.AsString();
                    case StorageType.Integer:
                        return Metric.IsYesNo(p.Definition)
                            ? (p.AsInteger() != 0 ? "true" : "false")
                            : p.AsInteger().ToString(CultureInfo.InvariantCulture);
                    case StorageType.Double:
                        {
                            MetricUnit unit = Metric.UnitOf(p.Definition);
                            string label = Metric.Label(unit);
                            double v = Metric.R(Metric.FromInternal(p.AsDouble(), unit), 4);
                            return v.ToString(CultureInfo.InvariantCulture) + (label != null ? " " + label : string.Empty);
                        }
                    case StorageType.ElementId:
                        return Compat.IdValue(p.AsElementId()).ToString(CultureInfo.InvariantCulture);
                    default: return null;
                }
            }
            catch (Exception) { return null; }
        }

        // ==================================================================
        //  revit_move_elements / revit_copy_elements / revit_rotate_elements
        // ==================================================================
        public static JsonValue MoveElements(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            List<long> ids = Args.IdValues(ctx.Args, "elementIds", true, 5000);
            XYZ translation = Args.VectorMm(ctx.Args, "translation");

            List<long> missing;
            List<Element> elements = Args.Elements(doc, ids, out missing);

            var moved = new List<long>();
            var skipped = JsonValue.NewArray();

            ctx.InTransaction("Move elements", delegate
            {
                for (int i = 0; i < elements.Count; i++)
                {
                    Element element = elements[i];
                    long id = Compat.IdValue(element.Id);
                    try
                    {
                        if (element.Pinned)
                        {
                            skipped.Add(J.O("elementId", id, "reason", "The element is pinned."));
                            continue;
                        }
                        ElementTransformUtils.MoveElement(doc, element.Id, translation);
                        moved.Add(id);
                    }
                    catch (Exception ex)
                    {
                        skipped.Add(J.O("elementId", id, "reason", ex.Message));
                    }
                }
            });

            JsonValue result = J.O(
                "moved", moved.Count,
                "skipped", skipped.Count,
                "movedIds", J.ALongs(moved),
                "skippedDetail", skipped,
                "translationMm", J.O(
                    "dx", Metric.R(Metric.FeetToMm(translation.X)),
                    "dy", Metric.R(Metric.FeetToMm(translation.Y)),
                    "dz", Metric.R(Metric.FeetToMm(translation.Z))));
            if (missing.Count > 0) result.Set("notFound", J.ALongs(missing));
            return result;
        }

        public static JsonValue CopyElements(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            List<long> ids = Args.IdValues(ctx.Args, "elementIds", true, 2000);
            XYZ step = Args.VectorMm(ctx.Args, "translation");
            int count = Args.Int(ctx.Args, "count", false, 1, 1, 200);

            List<long> missing;
            List<Element> elements = Args.Elements(doc, ids, out missing);
            if (elements.Count == 0)
                throw new ToolException(BridgeErrorCodes.NotFound, "None of the supplied element ids exist.");

            var sourceIds = new List<ElementId>();
            for (int i = 0; i < elements.Count; i++) sourceIds.Add(elements[i].Id);

            JsonValue copies = JsonValue.NewArray();
            int totalCreated = 0;

            ctx.InTransaction("Copy elements", delegate
            {
                for (int c = 1; c <= count; c++)
                {
                    XYZ offset = new XYZ(step.X * c, step.Y * c, step.Z * c);
                    ICollection<ElementId> created = ElementTransformUtils.CopyElements(doc, sourceIds, offset);
                    var newIds = new List<long>();
                    foreach (ElementId id in created) newIds.Add(Compat.IdValue(id));
                    totalCreated += newIds.Count;
                    copies.Add(J.O(
                        "copyIndex", c,
                        "createdIds", J.ALongs(newIds),
                        "offsetMm", J.O(
                            "dx", Metric.R(Metric.FeetToMm(offset.X)),
                            "dy", Metric.R(Metric.FeetToMm(offset.Y)),
                            "dz", Metric.R(Metric.FeetToMm(offset.Z)))));
                }
            });

            JsonValue result = J.O(
                "sourceElements", sourceIds.Count,
                "copies", count,
                "elementsCreated", totalCreated,
                "detail", copies);
            if (missing.Count > 0) result.Set("notFound", J.ALongs(missing));
            return result;
        }

        public static JsonValue RotateElements(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            List<long> ids = Args.IdValues(ctx.Args, "elementIds", true, 5000);
            double angleDegrees = Args.Num(ctx.Args, "angleDegrees", true, 0, -3600, 3600);

            List<long> missing;
            List<Element> elements = Args.Elements(doc, ids, out missing);
            if (elements.Count == 0)
                throw new ToolException(BridgeErrorCodes.NotFound, "None of the supplied element ids exist.");

            // Axis point: explicit, or the centre of the selection's combined bounding box.
            XYZ axisPoint = Args.PointMm(ctx.Args, "axisPoint", false, null);
            if (axisPoint == null) axisPoint = CombinedCentre(elements);

            XYZ direction = XYZ.BasisZ;
            JsonValue axisJson = ctx.Args["axisDirection"];
            if (axisJson.IsObject)
            {
                XYZ candidate = new XYZ(
                    axisJson["x"].AsDouble(0),
                    axisJson["y"].AsDouble(0),
                    axisJson["z"].AsDouble(1));
                if (candidate.GetLength() < 1e-9)
                    throw ToolException.Invalid("axisDirection", "the direction vector cannot be zero-length.");
                direction = candidate.Normalize();
            }

            var rotated = new List<long>();
            var skipped = JsonValue.NewArray();
            double angleRadians = Metric.DegToRad(angleDegrees);

            ctx.InTransaction("Rotate elements", delegate
            {
                Line axis = Line.CreateBound(axisPoint, axisPoint + direction);
                for (int i = 0; i < elements.Count; i++)
                {
                    Element element = elements[i];
                    long id = Compat.IdValue(element.Id);
                    try
                    {
                        if (element.Pinned)
                        {
                            skipped.Add(J.O("elementId", id, "reason", "The element is pinned."));
                            continue;
                        }
                        ElementTransformUtils.RotateElement(doc, element.Id, axis, angleRadians);
                        rotated.Add(id);
                    }
                    catch (Exception ex)
                    {
                        skipped.Add(J.O("elementId", id, "reason", ex.Message));
                    }
                }
            });

            JsonValue result = J.O(
                "rotated", rotated.Count,
                "skipped", skipped.Count,
                "rotatedIds", J.ALongs(rotated),
                "skippedDetail", skipped,
                "angleDegrees", Metric.R(angleDegrees, 4),
                "axisPointMm", Metric.PointToMm(axisPoint),
                "axisDirection", J.O(
                    "x", Metric.R(direction.X, 4),
                    "y", Metric.R(direction.Y, 4),
                    "z", Metric.R(direction.Z, 4)));
            if (missing.Count > 0) result.Set("notFound", J.ALongs(missing));
            return result;
        }

        private static XYZ CombinedCentre(List<Element> elements)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            bool any = false;

            for (int i = 0; i < elements.Count; i++)
            {
                BoundingBoxXYZ bb = null;
                try { bb = elements[i].get_BoundingBox(null); } catch (Exception) { }
                if (bb == null) continue;
                any = true;
                minX = Math.Min(minX, bb.Min.X); minY = Math.Min(minY, bb.Min.Y); minZ = Math.Min(minZ, bb.Min.Z);
                maxX = Math.Max(maxX, bb.Max.X); maxY = Math.Max(maxY, bb.Max.Y); maxZ = Math.Max(maxZ, bb.Max.Z);
            }

            if (!any) return XYZ.Zero;
            return new XYZ((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
        }

        // ==================================================================
        //  revit_create_view
        // ==================================================================
        public static JsonValue CreateView(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            string viewType = Args.Str(ctx.Args, "viewType", true, null,
                new[] { "FloorPlan", "CeilingPlan", "ThreeD", "Drafting", "StructuralPlan", "AreaPlan" });
            string levelName = Args.Str(ctx.Args, "levelName");
            string name = Args.Str(ctx.Args, "name");
            string templateName = Args.Str(ctx.Args, "viewTemplateName");
            int scale = Args.Int(ctx.Args, "scale", false, 0, 1, 24000);
            string detailLevel = Args.Str(ctx.Args, "detailLevel", false, null, new[] { "Coarse", "Medium", "Fine" });

            bool needsLevel = !Paging.EqualsCi(viewType, "ThreeD") && !Paging.EqualsCi(viewType, "Drafting");
            Level level = needsLevel ? Selectors.FindLevel(doc, levelName, true) : null;
            View template = Selectors.FindViewTemplate(doc, templateName, !string.IsNullOrEmpty(templateName));

            View view = ctx.InTransaction("Create view", delegate
            {
                View created = CreateViewOfType(doc, viewType, level);
                if (created == null) return null;

                if (!string.IsNullOrEmpty(name))
                {
                    try { created.Name = name; }
                    catch (Exception ex)
                    {
                        ctx.AddWarning("The view was created but could not be named '" + name +
                                       "' (a view with that name may already exist): " + ex.Message);
                    }
                }

                if (scale > 0)
                {
                    try { created.Scale = scale; }
                    catch (Exception ex) { ctx.AddWarning("Could not set the view scale: " + ex.Message); }
                }

                if (!string.IsNullOrEmpty(detailLevel))
                {
                    try
                    {
                        created.DetailLevel = (ViewDetailLevel)Enum.Parse(typeof(ViewDetailLevel), detailLevel, true);
                    }
                    catch (Exception ex) { ctx.AddWarning("Could not set the detail level: " + ex.Message); }
                }

                // Applied last: a template can lock scale and detail level.
                if (template != null)
                {
                    try { created.ViewTemplateId = template.Id; }
                    catch (Exception ex) { ctx.AddWarning("Could not apply the view template: " + ex.Message); }
                }

                return created;
            });

            if (view == null)
                throw new ToolException(BridgeErrorCodes.RevitApi,
                    "Revit could not create a " + viewType + " view. Check that a matching view family type exists.");

            return J.O(
                "created", J.O(
                    "id", Compat.IdValue(view.Id),
                    "name", view.Name,
                    "viewType", view.ViewType.ToString()),
                "level", level != null ? level.Name : null,
                "viewTemplate", template != null ? template.Name : null,
                "scale", SafeScale(view));
        }

        private static View CreateViewOfType(Document doc, string viewType, Level level)
        {
            if (Paging.EqualsCi(viewType, "ThreeD"))
            {
                ViewFamilyType type = Selectors.FindViewFamilyType(doc, ViewFamily.ThreeDimensional);
                if (type == null) return null;
                View3D view = View3D.CreateIsometric(doc, type.Id);
                return view;
            }

            if (Paging.EqualsCi(viewType, "Drafting"))
            {
                ViewFamilyType type = Selectors.FindViewFamilyType(doc, ViewFamily.Drafting);
                if (type == null) return null;
                return ViewDrafting.Create(doc, type.Id);
            }

            if (Paging.EqualsCi(viewType, "AreaPlan"))
            {
                AreaScheme scheme = new FilteredElementCollector(doc)
                    .OfClass(typeof(AreaScheme)).Cast<AreaScheme>().FirstOrDefault();
                if (scheme == null)
                    throw new ToolException(BridgeErrorCodes.NotFound,
                        "This model has no area scheme, so an area plan cannot be created.");
                return ViewPlan.CreateAreaPlan(doc, scheme.Id, level.Id);
            }

            ViewFamily family =
                Paging.EqualsCi(viewType, "CeilingPlan") ? ViewFamily.CeilingPlan :
                Paging.EqualsCi(viewType, "StructuralPlan") ? ViewFamily.StructuralPlan :
                ViewFamily.FloorPlan;

            ViewFamilyType planType = Selectors.FindViewFamilyType(doc, family);
            if (planType == null) return null;
            return ViewPlan.Create(doc, planType.Id, level.Id);
        }

        private static object SafeScale(View view)
        {
            try { return view.Scale; } catch (Exception) { return null; }
        }

        // ==================================================================
        //  revit_create_sheet
        // ==================================================================
        public static JsonValue CreateSheet(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            string sheetNumber = Args.Str(ctx.Args, "sheetNumber", true);
            string name = Args.Str(ctx.Args, "name", true);
            string titleBlockName = Args.Str(ctx.Args, "titleBlockTypeName");

            if (Selectors.FindSheet(doc, sheetNumber, false) != null)
                throw ToolException.Invalid("sheetNumber", "sheet '" + sheetNumber + "' already exists.");

            List<FamilySymbol> titleBlocks = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType()
                .Cast<FamilySymbol>()
                .ToList();

            if (titleBlocks.Count == 0)
                throw new ToolException(BridgeErrorCodes.NotFound,
                    "No title block family is loaded, so a sheet cannot be created. " +
                    "Load a title block into the project first.");

            FamilySymbol titleBlock = string.IsNullOrEmpty(titleBlockName)
                ? titleBlocks[0]
                : titleBlocks.FirstOrDefault(t => string.Equals(t.Name, titleBlockName, StringComparison.OrdinalIgnoreCase));

            if (titleBlock == null)
                throw ToolException.NotFound("Title block type", titleBlockName)
                    .WithSuggestions(titleBlocks.Select(t => t.Name).Take(20).ToList());

            JsonValue extraParameters = ctx.Args["parameters"];

            ViewSheet sheet = ctx.InTransaction("Create sheet", delegate
            {
                Activate(doc, titleBlock);
                ViewSheet created = ViewSheet.Create(doc, titleBlock.Id);
                if (created == null) return null;

                try { created.SheetNumber = sheetNumber; }
                catch (Exception ex) { ctx.AddWarning("Could not set the sheet number: " + ex.Message); }
                try { created.Name = name; }
                catch (Exception ex) { ctx.AddWarning("Could not set the sheet name: " + ex.Message); }

                if (extraParameters.IsArray)
                {
                    foreach (JsonValue assignment in extraParameters.Items)
                    {
                        string parameterName = assignment["name"].AsString(null);
                        string parameterValue = assignment["value"].AsString(null);
                        if (string.IsNullOrEmpty(parameterName)) continue;

                        Parameter p = Selectors.FindParameter(created, parameterName, false);
                        if (p == null || p.IsReadOnly)
                        {
                            ctx.AddWarning("Sheet parameter '" + parameterName + "' was not writable and was skipped.");
                            continue;
                        }
                        try { p.Set(parameterValue ?? string.Empty); }
                        catch (Exception ex)
                        {
                            ctx.AddWarning("Could not set sheet parameter '" + parameterName + "': " + ex.Message);
                        }
                    }
                }
                return created;
            });

            if (sheet == null)
                throw new ToolException(BridgeErrorCodes.RevitApi, "Revit did not create the sheet.");

            return J.O(
                "created", J.O(
                    "id", Compat.IdValue(sheet.Id),
                    "sheetNumber", sheet.SheetNumber,
                    "name", sheet.Name),
                "titleBlock", titleBlock.Name);
        }

        // ==================================================================
        //  revit_place_view_on_sheet
        // ==================================================================
        public static JsonValue PlaceViewOnSheet(ToolContext ctx)
        {
            Document doc = ctx.Doc;

            long sheetId = Args.Long(ctx.Args, "sheetId", false, 0);
            string sheetNumber = Args.Str(ctx.Args, "sheetNumber");
            long viewId = Args.Long(ctx.Args, "viewId", false, 0);
            string viewName = Args.Str(ctx.Args, "viewName");

            ViewSheet sheet = sheetId > 0
                ? doc.GetElement(Compat.ToId(sheetId)) as ViewSheet
                : Selectors.FindSheet(doc, sheetNumber, true);
            if (sheet == null)
                throw new ToolException(BridgeErrorCodes.MissingArgument,
                    "Provide either sheetId or sheetNumber identifying an existing sheet.");

            View view = viewId > 0
                ? doc.GetElement(Compat.ToId(viewId)) as View
                : Selectors.FindView(doc, viewName, true);
            if (view == null)
                throw new ToolException(BridgeErrorCodes.MissingArgument,
                    "Provide either viewId or viewName identifying an existing view.");

            if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
            {
                throw new ToolException(BridgeErrorCodes.RevitApi,
                    "Revit will not place view '" + view.Name + "' on sheet '" + sheet.SheetNumber + "'. " +
                    "The usual reasons are: the view is already on another sheet, it is a view template, " +
                    "or it is a schedule (schedules are placed differently).");
            }

            // Centre of the title block if no position was given.
            XYZ position;
            JsonValue positionArg = ctx.Args["position"];
            if (positionArg.IsObject)
            {
                position = new XYZ(
                    Metric.MmToFeet(positionArg["x"].AsDouble(0)),
                    Metric.MmToFeet(positionArg["y"].AsDouble(0)),
                    0);
            }
            else
            {
                BoundingBoxUV outline = sheet.Outline;
                position = outline != null
                    ? new XYZ((outline.Min.U + outline.Max.U) / 2, (outline.Min.V + outline.Max.V) / 2, 0)
                    : XYZ.Zero;
            }

            Viewport viewport = ctx.InTransaction("Place view on sheet", delegate
            {
                return Viewport.Create(doc, sheet.Id, view.Id, position);
            });

            if (viewport == null)
                throw new ToolException(BridgeErrorCodes.RevitApi, "Revit did not create the viewport.");

            return J.O(
                "viewportId", Compat.IdValue(viewport.Id),
                "sheet", J.O("id", Compat.IdValue(sheet.Id), "sheetNumber", sheet.SheetNumber, "name", sheet.Name),
                "view", J.O("id", Compat.IdValue(view.Id), "name", view.Name, "viewType", view.ViewType.ToString()),
                "positionMm", Metric.PointToMm(position));
        }

        // ==================================================================
        //  revit_set_element_material
        // ==================================================================
        public static JsonValue SetElementMaterial(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            List<long> ids = Args.IdValues(ctx.Args, "elementIds", true, 5000);
            string materialName = Args.Str(ctx.Args, "materialName", true);
            string parameterName = Args.Str(ctx.Args, "parameterName");
            bool applyToType = Args.Bool(ctx.Args, "applyToType", false);

            Material material = Selectors.FindMaterial(doc, materialName);

            List<long> missing;
            List<Element> elements = Args.Elements(doc, ids, out missing);

            int updated = 0;
            JsonValue skipped = JsonValue.NewArray();

            ctx.InTransaction("Assign material", delegate
            {
                var editedTypes = new HashSet<long>();

                for (int i = 0; i < elements.Count; i++)
                {
                    Element target = elements[i];
                    long reportedId = Compat.IdValue(target.Id);

                    if (applyToType)
                    {
                        ElementId typeId = target.GetTypeId();
                        if (!Compat.IsValid(typeId))
                        {
                            skipped.Add(J.O("elementId", reportedId, "reason", "The element has no type."));
                            continue;
                        }
                        if (!editedTypes.Add(Compat.IdValue(typeId))) continue;
                        target = doc.GetElement(typeId);
                        if (target == null) continue;
                    }

                    Parameter p = FindMaterialParameter(target, parameterName);
                    if (p == null)
                    {
                        skipped.Add(J.O("elementId", reportedId, "reason",
                            string.IsNullOrEmpty(parameterName)
                                ? "No writable material parameter was found on this element. " +
                                  "Many categories carry materials on the type's compound structure instead."
                                : "No writable parameter called '" + parameterName + "' was found."));
                        continue;
                    }

                    try
                    {
                        if (p.Set(material.Id)) updated++;
                        else skipped.Add(J.O("elementId", reportedId, "reason", "Revit rejected the material value."));
                    }
                    catch (Exception ex)
                    {
                        skipped.Add(J.O("elementId", reportedId, "reason", ex.Message));
                    }
                }
            });

            JsonValue result = J.O(
                "material", material.Name,
                "materialId", Compat.IdValue(material.Id),
                "updated", updated,
                "skipped", skipped.Count,
                "skippedDetail", skipped,
                "appliedToType", applyToType);
            if (missing.Count > 0) result.Set("notFound", J.ALongs(missing));
            return result;
        }

        private static Parameter FindMaterialParameter(Element element, string parameterName)
        {
            if (!string.IsNullOrEmpty(parameterName))
            {
                Parameter named = Selectors.FindParameter(element, parameterName, false);
                return named != null && !named.IsReadOnly && named.StorageType == StorageType.ElementId ? named : null;
            }

            BuiltInParameter[] candidates =
            {
                BuiltInParameter.STRUCTURAL_MATERIAL_PARAM,
                BuiltInParameter.MATERIAL_ID_PARAM
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                try
                {
                    Parameter p = element.get_Parameter(candidates[i]);
                    if (p != null && !p.IsReadOnly && p.StorageType == StorageType.ElementId) return p;
                }
                catch (Exception) { }
            }

            // Last resort: any writable ElementId parameter whose name mentions "material".
            try
            {
                foreach (Parameter p in element.Parameters)
                {
                    if (p == null || p.Definition == null || p.IsReadOnly) continue;
                    if (p.StorageType != StorageType.ElementId) continue;
                    if (p.Definition.Name.IndexOf("material", StringComparison.OrdinalIgnoreCase) >= 0) return p;
                }
            }
            catch (Exception) { }

            return null;
        }

        // ==================================================================
        //  revit_set_element_workset
        // ==================================================================
        public static JsonValue SetElementWorkset(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            if (!doc.IsWorkshared)
                throw new ToolException(BridgeErrorCodes.InvalidArgument,
                    "This model is not workshared, so elements cannot be assigned to a workset.");

            List<long> ids = Args.IdValues(ctx.Args, "elementIds", true, 5000);
            Workset workset = Selectors.FindWorkset(doc, Args.Str(ctx.Args, "worksetName", true));

            List<long> missing;
            List<Element> elements = Args.Elements(doc, ids, out missing);

            int updated = 0;
            JsonValue skipped = JsonValue.NewArray();

            ctx.InTransaction("Assign workset", delegate
            {
                for (int i = 0; i < elements.Count; i++)
                {
                    Element element = elements[i];
                    long id = Compat.IdValue(element.Id);
                    try
                    {
                        Parameter p = element.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                        if (p == null)
                        {
                            skipped.Add(J.O("elementId", id, "reason", "This element does not belong to a workset."));
                            continue;
                        }
                        if (p.IsReadOnly)
                        {
                            skipped.Add(J.O("elementId", id, "reason",
                                "The workset parameter is read-only - the element may be owned by another user " +
                                "or borrowed. Synchronise and retry."));
                            continue;
                        }
                        if (p.Set(workset.Id.IntegerValue)) updated++;
                        else skipped.Add(J.O("elementId", id, "reason", "Revit rejected the workset change."));
                    }
                    catch (Exception ex)
                    {
                        skipped.Add(J.O("elementId", id, "reason", ex.Message));
                    }
                }
            });

            JsonValue result = J.O(
                "workset", workset.Name,
                "worksetId", workset.Id.IntegerValue,
                "updated", updated,
                "skipped", skipped.Count,
                "skippedDetail", skipped);
            if (missing.Count > 0) result.Set("notFound", J.ALongs(missing));
            return result;
        }

        // ==================================================================
        //  revit_set_selection  (UI state only - runs without a transaction)
        // ==================================================================
        public static JsonValue SetSelection(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            List<long> ids = Args.IdValues(ctx.Args, "elementIds", false, 10000);
            bool zoomTo = Args.Bool(ctx.Args, "zoomTo", true);

            var valid = new List<ElementId>();
            var missing = new List<long>();
            for (int i = 0; i < ids.Count; i++)
            {
                Element element = null;
                try { element = doc.GetElement(Compat.ToId(ids[i])); } catch (Exception) { }
                if (element != null) valid.Add(element.Id); else missing.Add(ids[i]);
            }

            ctx.UiDoc.Selection.SetElementIds(valid);

            bool zoomed = false;
            if (zoomTo && valid.Count > 0)
            {
                try { ctx.UiDoc.ShowElements(valid); zoomed = true; }
                catch (Exception ex) { ctx.AddWarning("Could not zoom to the selection: " + ex.Message); }
            }

            try { ctx.UiDoc.RefreshActiveView(); } catch (Exception) { }

            JsonValue result = J.O(
                "selected", valid.Count,
                "zoomed", zoomed,
                "modelChanged", false,
                "note", valid.Count == 0
                    ? "The Revit selection was cleared."
                    : "The user now has " + valid.Count + " element(s) selected in Revit.");
            if (missing.Count > 0) result.Set("notFound", J.ALongs(missing));
            return result;
        }
    }
}
