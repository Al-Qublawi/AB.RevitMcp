using System;
using System.Globalization;
using System.Linq;
using AB.RevitMcp.Addin.Bridge;
using AB.RevitMcp.Addin.Revit;
using AB.RevitMcp.Contracts.Json;
using Autodesk.Revit.DB;

namespace AB.RevitMcp.Addin.Tools
{
    public static partial class ReadTools
    {
        // ==================================================================
        //  revit_get_view_center
        //
        //  "Put it in the middle of the current view" is one of the most natural things a person
        //  asks for, and without this an AI has to invent a coordinate. The crop box is the honest
        //  answer when one is active; otherwise the extents of what is actually visible.
        // ==================================================================
        public static JsonValue GetViewCenter(ToolContext ctx)
        {
            Document doc = ctx.Doc;

            View view;
            long viewId = Args.Long(ctx.Args, "viewId", false, 0);
            string viewName = Args.Str(ctx.Args, "viewName");

            if (viewId > 0)
            {
                view = doc.GetElement(Compat.ToId(viewId)) as View;
                if (view == null)
                    throw ToolException.NotFound("View", viewId.ToString(CultureInfo.InvariantCulture));
            }
            else if (!string.IsNullOrEmpty(viewName))
            {
                view = Selectors.FindView(doc, viewName);
            }
            else
            {
                view = ctx.ActiveView;
            }

            JsonValue result = J.O(
                "view", J.O(
                    "id", Compat.IdValue(view.Id),
                    "name", view.Name,
                    "viewType", view.ViewType.ToString()));

            // 1. An active crop box is exactly "what I am looking at".
            bool cropActive = false;
            try { cropActive = view.CropBoxActive; } catch (Exception) { }

            if (cropActive)
            {
                try
                {
                    BoundingBoxXYZ crop = view.CropBox;
                    if (crop != null)
                    {
                        // The crop box is expressed in the view's own coordinate system.
                        XYZ localCentre = (crop.Min + crop.Max) * 0.5;
                        XYZ centre = crop.Transform != null ? crop.Transform.OfPoint(localCentre) : localCentre;

                        result.Set("source", "cropBox");
                        result.Set("center", Metric.PointToMm(centre));
                        result.Set("bounds", Metric.BoundingBoxToMm(crop));
                        AddLevel(result, view, doc);
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    ctx.AddWarning("Could not read the crop box: " + ex.Message);
                }
            }

            // 2. Otherwise, the combined extents of what the view actually shows.
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            int counted = 0;

            foreach (Element element in new FilteredElementCollector(doc, view.Id)
                         .WhereElementIsNotElementType())
            {
                BoundingBoxXYZ bb = null;
                try { bb = element.get_BoundingBox(view); } catch (Exception) { }
                if (bb == null)
                {
                    try { bb = element.get_BoundingBox(null); } catch (Exception) { }
                }
                if (bb == null) continue;

                counted++;
                minX = Math.Min(minX, bb.Min.X); minY = Math.Min(minY, bb.Min.Y); minZ = Math.Min(minZ, bb.Min.Z);
                maxX = Math.Max(maxX, bb.Max.X); maxY = Math.Max(maxY, bb.Max.Y); maxZ = Math.Max(maxZ, bb.Max.Z);
            }

            if (counted == 0)
            {
                result.Set("source", "empty");
                result.Set("center", JsonValue.Null);
                result.Set("note", "This view contains nothing with measurable extents, so it has no " +
                                   "meaningful centre. Pass explicit coordinates instead.");
                return result;
            }

            XYZ computed = new XYZ((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);

            result.Set("source", "visibleElements");
            result.Set("elementsMeasured", counted);
            result.Set("center", Metric.PointToMm(computed));
            result.Set("bounds", J.O(
                "min", Metric.PointToMm(new XYZ(minX, minY, minZ)),
                "max", Metric.PointToMm(new XYZ(maxX, maxY, maxZ)),
                "sizeMm", J.O(
                    "x", Metric.R(Metric.FeetToMm(maxX - minX)),
                    "y", Metric.R(Metric.FeetToMm(maxY - minY)),
                    "z", Metric.R(Metric.FeetToMm(maxZ - minZ)))));
            AddLevel(result, view, doc);
            return result;
        }

        private static void AddLevel(JsonValue result, View view, Document doc)
        {
            try
            {
                ViewPlan plan = view as ViewPlan;
                if (plan != null && plan.GenLevel != null)
                {
                    result.Set("level", plan.GenLevel.Name);
                    result.Set("levelElevationMm", Metric.R(Metric.FeetToMm(plan.GenLevel.Elevation)));
                }
            }
            catch (Exception) { }
        }
    }
}
