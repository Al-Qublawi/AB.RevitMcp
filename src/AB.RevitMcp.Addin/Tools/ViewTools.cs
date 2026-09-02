using System;
using System.Collections.Generic;
using System.Linq;
using AB.RevitMcp.Addin.Bridge;
using AB.RevitMcp.Addin.Revit;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;
using Autodesk.Revit.DB;

namespace AB.RevitMcp.Addin.Tools
{
    /// <summary>View management: duplication, templates, visibility, graphic overrides, section boxes.</summary>
    public static class ViewTools
    {
        // ==================================================================
        //  revit_duplicate_view
        // ==================================================================
        public static JsonValue DuplicateView(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            View source = AnnotationTools.ResolveView(ctx);
            string newName = Args.Str(ctx.Args, "newName");
            string mode = Args.Str(ctx.Args, "mode", false, "Duplicate",
                new[] { "Duplicate", "WithDetailing", "AsDependent" });

            ViewDuplicateOption option =
                Paging.EqualsCi(mode, "WithDetailing") ? ViewDuplicateOption.WithDetailing :
                Paging.EqualsCi(mode, "AsDependent") ? ViewDuplicateOption.AsDependent :
                ViewDuplicateOption.Duplicate;

            if (!source.CanViewBeDuplicated(option))
                throw new ToolException(BridgeErrorCodes.RevitApi,
                    "Revit will not duplicate '" + source.Name + "' with mode '" + mode + "'. " +
                    "Schedules, legends and some system views cannot be duplicated this way.");

            View copy = ctx.InTransaction("Duplicate view", delegate
            {
                ElementId id = source.Duplicate(option);
                View created = doc.GetElement(id) as View;
                if (created != null && !string.IsNullOrEmpty(newName))
                {
                    try { created.Name = newName; }
                    catch (Exception ex)
                    {
                        ctx.AddWarning("The view was duplicated but could not be renamed to '" +
                                       newName + "': " + ex.Message);
                    }
                }
                return created;
            });

            if (copy == null)
                throw new ToolException(BridgeErrorCodes.RevitApi, "Revit did not return the duplicated view.");

            return J.O(
                "created", J.O("id", Compat.IdValue(copy.Id), "name", copy.Name,
                               "viewType", copy.ViewType.ToString()),
                "duplicatedFrom", J.O("id", Compat.IdValue(source.Id), "name", source.Name),
                "mode", mode);
        }

        // ==================================================================
        //  revit_apply_view_template
        // ==================================================================
        public static JsonValue ApplyViewTemplate(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            View template = Selectors.FindViewTemplate(doc, Args.Str(ctx.Args, "templateName", true), true);
            List<long> viewIds = Args.IdValues(ctx.Args, "viewIds", true, 500);

            JsonValue applied = JsonValue.NewArray();
            JsonValue skipped = JsonValue.NewArray();

            ctx.InTransaction("Apply view template", delegate
            {
                for (int i = 0; i < viewIds.Count; i++)
                {
                    View view = doc.GetElement(Compat.ToId(viewIds[i])) as View;
                    if (view == null)
                    {
                        skipped.Add(J.O("viewId", viewIds[i], "reason", "No view with this id exists."));
                        continue;
                    }
                    if (view.IsTemplate)
                    {
                        skipped.Add(J.O("viewId", viewIds[i], "name", view.Name,
                                        "reason", "This is itself a view template."));
                        continue;
                    }
                    try
                    {
                        view.ViewTemplateId = template.Id;
                        applied.Add(J.O("viewId", viewIds[i], "name", view.Name));
                    }
                    catch (Exception ex)
                    {
                        skipped.Add(J.O("viewId", viewIds[i], "name", view.Name, "reason", ex.Message));
                    }
                }
            });

            return J.O(
                "template", template.Name,
                "applied", applied.Count,
                "skipped", skipped.Count,
                "appliedTo", applied,
                "skippedDetail", skipped);
        }

        // ==================================================================
        //  revit_set_element_visibility
        // ==================================================================
        public static JsonValue SetElementVisibility(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            View view = AnnotationTools.ResolveView(ctx);
            List<long> ids = Args.IdValues(ctx.Args, "elementIds", true, 5000);
            string action = Args.Str(ctx.Args, "action", true, null, new[] { "hide", "unhide", "isolate" });

            if (view.IsTemplate)
                throw new ToolException(BridgeErrorCodes.InvalidArgument,
                    "Visibility cannot be set on a view template.");

            var target = new HashSet<long>(ids);
            var toHide = new List<ElementId>();
            var toUnhide = new List<ElementId>();
            JsonValue skipped = JsonValue.NewArray();

            if (Paging.EqualsCi(action, "isolate"))
            {
                // Hide everything visible in the view that is NOT in the keep set.
                foreach (Element element in new FilteredElementCollector(doc, view.Id)
                             .WhereElementIsNotElementType())
                {
                    if (target.Contains(Compat.IdValue(element.Id))) continue;
                    if (!CanHide(element, view)) continue;
                    toHide.Add(element.Id);
                }
            }
            else
            {
                for (int i = 0; i < ids.Count; i++)
                {
                    Element element = null;
                    try { element = doc.GetElement(Compat.ToId(ids[i])); } catch (Exception) { }
                    if (element == null)
                    {
                        skipped.Add(J.O("elementId", ids[i], "reason", "No element with this id exists."));
                        continue;
                    }
                    if (Paging.EqualsCi(action, "hide"))
                    {
                        if (!CanHide(element, view))
                        {
                            skipped.Add(J.O("elementId", ids[i],
                                "reason", "Revit does not allow this element to be hidden in this view."));
                            continue;
                        }
                        toHide.Add(element.Id);
                    }
                    else
                    {
                        toUnhide.Add(element.Id);
                    }
                }
            }

            ctx.InTransaction(Paging.EqualsCi(action, "unhide") ? "Unhide elements" : "Hide elements", delegate
            {
                if (toHide.Count > 0) view.HideElements(toHide);
                if (toUnhide.Count > 0) view.UnhideElements(toUnhide);
            });

            return J.O(
                "action", action,
                "view", view.Name,
                "hidden", toHide.Count,
                "unhidden", toUnhide.Count,
                "skipped", skipped.Count,
                "skippedDetail", skipped,
                "note", Paging.EqualsCi(action, "isolate")
                    ? "Everything else in this view was hidden. This is permanent view state, not " +
                      "Revit's temporary Isolate mode - use action 'unhide' to reverse it."
                    : null);
        }

        private static bool CanHide(Element element, View view)
        {
            try { return element.CanBeHidden(view); }
            catch (Exception) { return false; }
        }

        // ==================================================================
        //  revit_override_element_graphics
        // ==================================================================
        public static JsonValue OverrideElementGraphics(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            View view = AnnotationTools.ResolveView(ctx);
            List<long> ids = Args.IdValues(ctx.Args, "elementIds", true, 5000);
            bool reset = Args.Bool(ctx.Args, "reset", false);

            if (view.IsTemplate)
                throw new ToolException(BridgeErrorCodes.InvalidArgument,
                    "Graphic overrides cannot be set on a view template.");

            Color colour = null;
            JsonValue colourArg = ctx.Args["color"];
            if (colourArg.IsObject)
            {
                colour = new Color(
                    (byte)Math.Max(0, Math.Min(255, colourArg["r"].AsInt(0))),
                    (byte)Math.Max(0, Math.Min(255, colourArg["g"].AsInt(0))),
                    (byte)Math.Max(0, Math.Min(255, colourArg["b"].AsInt(0))));
            }

            int transparency = Args.Int(ctx.Args, "surfaceTransparency", false, -1, 0, 100);
            int lineWeight = Args.Int(ctx.Args, "lineWeight", false, -1, 1, 16);
            bool hasHalftone = ctx.Args.HasValue("halftone");
            bool halftone = Args.Bool(ctx.Args, "halftone", false);

            if (!reset && colour == null && transparency < 0 && lineWeight < 0 && !hasHalftone)
                throw new ToolException(BridgeErrorCodes.MissingArgument,
                    "Nothing to do: supply at least one of color, surfaceTransparency, lineWeight, " +
                    "halftone - or reset:true.");

            OverrideGraphicSettings settings = new OverrideGraphicSettings();

            if (!reset)
            {
                if (colour != null)
                {
                    settings.SetProjectionLineColor(colour);
                    settings.SetCutLineColor(colour);

                    // A colour is invisible unless a pattern carries it, so a solid fill is needed.
                    ElementId solidFill = SolidFillPatternId(doc);
                    if (Compat.IsValid(solidFill))
                    {
                        settings.SetSurfaceForegroundPatternVisible(true);
                        settings.SetSurfaceForegroundPatternId(solidFill);
                        settings.SetSurfaceForegroundPatternColor(colour);
                        settings.SetCutForegroundPatternVisible(true);
                        settings.SetCutForegroundPatternId(solidFill);
                        settings.SetCutForegroundPatternColor(colour);
                    }
                    else
                    {
                        ctx.AddWarning("No solid fill pattern exists in this model, so only the line " +
                                       "colour was applied - surfaces will not be filled.");
                    }
                }
                if (transparency >= 0) settings.SetSurfaceTransparency(transparency);
                if (lineWeight >= 1) settings.SetProjectionLineWeight(lineWeight);
                if (hasHalftone) settings.SetHalftone(halftone);
            }

            int applied = 0;
            JsonValue skipped = JsonValue.NewArray();

            ctx.InTransaction(reset ? "Clear graphic overrides" : "Override element graphics", delegate
            {
                for (int i = 0; i < ids.Count; i++)
                {
                    ElementId id = Compat.ToId(ids[i]);
                    if (doc.GetElement(id) == null)
                    {
                        skipped.Add(J.O("elementId", ids[i], "reason", "No element with this id exists."));
                        continue;
                    }
                    try
                    {
                        view.SetElementOverrides(id, settings);
                        applied++;
                    }
                    catch (Exception ex)
                    {
                        skipped.Add(J.O("elementId", ids[i], "reason", ex.Message));
                    }
                }
            });

            return J.O(
                "view", view.Name,
                "applied", applied,
                "skipped", skipped.Count,
                "skippedDetail", skipped,
                "reset", reset,
                "color", colour != null
                    ? J.O("r", (int)colour.Red, "g", (int)colour.Green, "b", (int)colour.Blue)
                    : JsonValue.Null);
        }

        private static ElementId SolidFillPatternId(Document doc)
        {
            try
            {
                FillPatternElement solid = new FilteredElementCollector(doc)
                    .OfClass(typeof(FillPatternElement))
                    .Cast<FillPatternElement>()
                    .FirstOrDefault(f =>
                    {
                        try { FillPattern p = f.GetFillPattern(); return p != null && p.IsSolidFill; }
                        catch (Exception) { return false; }
                    });
                return solid != null ? solid.Id : ElementId.InvalidElementId;
            }
            catch (Exception) { return ElementId.InvalidElementId; }
        }

        // ==================================================================
        //  revit_set_view_section_box
        // ==================================================================
        public static JsonValue SetViewSectionBox(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            View view = AnnotationTools.ResolveView(ctx);

            View3D view3d = view as View3D;
            if (view3d == null)
                throw new ToolException(BridgeErrorCodes.InvalidArgument,
                    "'" + view.Name + "' is a " + view.ViewType + ", not a 3D view. " +
                    "Section boxes only exist on 3D views.");

            bool clear = Args.Bool(ctx.Args, "clear", false);

            if (clear)
            {
                ctx.InTransaction("Clear section box", delegate { view3d.IsSectionBoxActive = false; });
                return J.O("view", view3d.Name, "sectionBoxActive", false,
                           "note", "The section box was switched off.");
            }

            XYZ min = null, max = null;
            List<long> elementIds = Args.IdValues(ctx.Args, "elementIds", false, 5000);
            double paddingMm = Args.Num(ctx.Args, "paddingMm", false, 500, 0, 100000);

            if (elementIds.Count > 0)
            {
                double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
                int counted = 0;

                for (int i = 0; i < elementIds.Count; i++)
                {
                    Element element = null;
                    try { element = doc.GetElement(Compat.ToId(elementIds[i])); } catch (Exception) { }
                    if (element == null) continue;
                    BoundingBoxXYZ bb = null;
                    try { bb = element.get_BoundingBox(null); } catch (Exception) { }
                    if (bb == null) continue;
                    counted++;
                    minX = Math.Min(minX, bb.Min.X); minY = Math.Min(minY, bb.Min.Y); minZ = Math.Min(minZ, bb.Min.Z);
                    maxX = Math.Max(maxX, bb.Max.X); maxY = Math.Max(maxY, bb.Max.Y); maxZ = Math.Max(maxZ, bb.Max.Z);
                }

                if (counted == 0)
                    throw new ToolException(BridgeErrorCodes.NotFound,
                        "None of those elements has a bounding box, so a section box cannot be fitted.");

                double pad = Metric.MmToFeet(paddingMm);
                min = new XYZ(minX - pad, minY - pad, minZ - pad);
                max = new XYZ(maxX + pad, maxY + pad, maxZ + pad);
            }
            else
            {
                min = Args.PointMm(ctx.Args, "min", false, null);
                max = Args.PointMm(ctx.Args, "max", false, null);
                if (min == null || max == null)
                    throw new ToolException(BridgeErrorCodes.MissingArgument,
                        "Supply either elementIds to fit around, or both min and max corners, or clear:true.");
            }

            XYZ lo = new XYZ(Math.Min(min.X, max.X), Math.Min(min.Y, max.Y), Math.Min(min.Z, max.Z));
            XYZ hi = new XYZ(Math.Max(min.X, max.X), Math.Max(min.Y, max.Y), Math.Max(min.Z, max.Z));

            if (hi.DistanceTo(lo) < 1e-6)
                throw ToolException.Invalid("max", "the section box has zero size.");

            ctx.InTransaction("Set section box", delegate
            {
                var box = new BoundingBoxXYZ();
                box.Min = lo;
                box.Max = hi;
                view3d.SetSectionBox(box);
                view3d.IsSectionBoxActive = true;
            });

            return J.O(
                "view", view3d.Name,
                "sectionBoxActive", true,
                "min", Metric.PointToMm(lo),
                "max", Metric.PointToMm(hi),
                "sizeMm", J.O(
                    "x", Metric.R(Metric.FeetToMm(hi.X - lo.X)),
                    "y", Metric.R(Metric.FeetToMm(hi.Y - lo.Y)),
                    "z", Metric.R(Metric.FeetToMm(hi.Z - lo.Z))),
                "fittedToElements", elementIds.Count > 0 ? (object)elementIds.Count : null);
        }
    }
}
