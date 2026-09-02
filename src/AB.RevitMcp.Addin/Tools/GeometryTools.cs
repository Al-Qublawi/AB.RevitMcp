using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AB.RevitMcp.Addin.Bridge;
using AB.RevitMcp.Addin.Revit;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;
using Autodesk.Revit.DB;

namespace AB.RevitMcp.Addin.Tools
{
    /// <summary>Geometry operations, family loading and export.</summary>
    public static class GeometryTools
    {
        // ==================================================================
        //  revit_mirror_elements
        // ==================================================================
        public static JsonValue MirrorElements(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            List<long> ids = Args.IdValues(ctx.Args, "elementIds", true, 2000);
            bool copy = Args.Bool(ctx.Args, "copy", true);

            List<long> missing;
            List<Element> elements = Args.Elements(doc, ids, out missing);
            if (elements.Count == 0)
                throw new ToolException(BridgeErrorCodes.NotFound, "None of the supplied element ids exist.");

            XYZ origin = Args.PointMm(ctx.Args, "point", false, null) ?? CombinedCentre(elements);

            // The mirror plane is always vertical: a horizontal mirror is almost never what a
            // person means, and Revit refuses most of them anyway.
            XYZ normal;
            string axis = Args.Str(ctx.Args, "axis", false, null, new[] { "x", "y" });
            JsonValue normalArg = ctx.Args["normal"];

            if (normalArg.IsObject)
            {
                var candidate = new XYZ(normalArg["x"].AsDouble(0), normalArg["y"].AsDouble(0), 0);
                if (candidate.GetLength() < 1e-9)
                    throw ToolException.Invalid("normal", "the normal vector cannot be zero-length.");
                normal = candidate.Normalize();
            }
            else if (!string.IsNullOrEmpty(axis))
            {
                // "Mirror about the X axis" means the plane RUNS along X, so its normal is Y.
                normal = Paging.EqualsCi(axis, "x") ? XYZ.BasisY : XYZ.BasisX;
            }
            else
            {
                throw new ToolException(BridgeErrorCodes.MissingArgument,
                    "Supply either 'axis' (x or y) or an explicit 'normal' vector.");
            }

            var sourceIds = elements.Select(e => e.Id).ToList();
            var created = new List<long>();

            ctx.InTransaction("Mirror elements", delegate
            {
                Plane plane = MakePlane(normal, origin);

                if (copy)
                {
                    ICollection<ElementId> result = ElementTransformUtils.MirrorElements(doc, sourceIds, plane, true);
                    if (result != null) foreach (ElementId id in result) created.Add(Compat.IdValue(id));
                }
                else
                {
                    ElementTransformUtils.MirrorElements(doc, sourceIds, plane, false);
                }
            });

            return J.O(
                "mirrored", sourceIds.Count,
                "copy", copy,
                "createdIds", J.ALongs(created),
                "planeOriginMm", Metric.PointToMm(origin),
                "planeNormal", J.O("x", Metric.R(normal.X, 4), "y", Metric.R(normal.Y, 4), "z", 0),
                "notFound", missing.Count > 0 ? J.ALongs(missing) : JsonValue.Null);
        }

        private static Plane MakePlane(XYZ normal, XYZ origin)
        {
#if REVIT2021_OR_GREATER
            return Plane.CreateByNormalAndOrigin(normal, origin);
#else
            return Plane.CreateByNormalAndOrigin(normal, origin);
#endif
        }

        internal static XYZ CombinedCentre(List<Element> elements)
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

            return any ? new XYZ((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2) : XYZ.Zero;
        }

        // ==================================================================
        //  revit_array_elements
        // ==================================================================
        public static JsonValue ArrayElements(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            List<long> ids = Args.IdValues(ctx.Args, "elementIds", true, 1000);
            XYZ step = Args.VectorMm(ctx.Args, "direction");
            int count = Args.Int(ctx.Args, "count", true, 1, 1, 500);
            bool includeOriginal = Args.Bool(ctx.Args, "includeOriginal", false);

            if (step.GetLength() < 1e-9)
                throw ToolException.Invalid("direction", "the step vector cannot be zero - the copies " +
                                                         "would all land on top of each other.");

            List<long> missing;
            List<Element> elements = Args.Elements(doc, ids, out missing);
            if (elements.Count == 0)
                throw new ToolException(BridgeErrorCodes.NotFound, "None of the supplied element ids exist.");

            var sourceIds = elements.Select(e => e.Id).ToList();
            JsonValue copies = JsonValue.NewArray();
            int total = 0;

            ctx.InTransaction("Array elements", delegate
            {
                for (int i = 1; i <= count; i++)
                {
                    var offset = new XYZ(step.X * i, step.Y * i, step.Z * i);
                    ICollection<ElementId> result = ElementTransformUtils.CopyElements(doc, sourceIds, offset);

                    var newIds = new List<long>();
                    if (result != null) foreach (ElementId id in result) newIds.Add(Compat.IdValue(id));
                    total += newIds.Count;

                    copies.Add(J.O("index", i, "createdIds", J.ALongs(newIds),
                                   "offsetMm", J.O(
                                       "dx", Metric.R(Metric.FeetToMm(offset.X)),
                                       "dy", Metric.R(Metric.FeetToMm(offset.Y)),
                                       "dz", Metric.R(Metric.FeetToMm(offset.Z)))));
                }
            });

            JsonValue result2 = J.O(
                "sourceElements", sourceIds.Count,
                "copies", count,
                "elementsCreated", total,
                "detail", copies);
            if (includeOriginal) result2.Set("originalIds", J.ALongs(ids));
            if (missing.Count > 0) result2.Set("notFound", J.ALongs(missing));
            return result2;
        }

        // ==================================================================
        //  revit_group_elements
        // ==================================================================
        public static JsonValue GroupElements(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            List<long> ids = Args.IdValues(ctx.Args, "elementIds", true, 2000);
            string name = Args.Str(ctx.Args, "name");

            List<long> missing;
            List<Element> elements = Args.Elements(doc, ids, out missing);
            if (elements.Count < 1)
                throw new ToolException(BridgeErrorCodes.NotFound, "None of the supplied element ids exist.");

            var elementIds = elements.Select(e => e.Id).ToList();

            Group group = ctx.InTransaction("Group elements", delegate
            {
                Group created = doc.Create.NewGroup(elementIds);
                if (created != null && !string.IsNullOrEmpty(name))
                {
                    try { created.GroupType.Name = name; }
                    catch (Exception ex)
                    {
                        ctx.AddWarning("The group was created but could not be named '" + name +
                                       "' (the name may already be in use): " + ex.Message);
                    }
                }
                return created;
            });

            if (group == null)
                throw new ToolException(BridgeErrorCodes.RevitApi,
                    "Revit did not create a group. Some element kinds cannot be grouped together.");

            JsonValue result = J.O(
                "created", J.O(
                    "id", Compat.IdValue(group.Id),
                    "name", ElementSerializer.SafeName(group),
                    "typeName", group.GroupType != null ? group.GroupType.Name : null),
                "memberCount", elementIds.Count);
            if (missing.Count > 0) result.Set("notFound", J.ALongs(missing));
            return result;
        }

        // ==================================================================
        //  revit_join_geometry
        // ==================================================================
        public static JsonValue JoinGeometry(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            List<long> firstIds = Args.IdValues(ctx.Args, "elementIds", true, 500);
            List<long> secondIds = Args.IdValues(ctx.Args, "targetIds", true, 500);
            string action = Args.Str(ctx.Args, "action", false, "join", new[] { "join", "unjoin" });
            bool join = Paging.EqualsCi(action, "join");

            List<long> missingA, missingB;
            List<Element> first = Args.Elements(doc, firstIds, out missingA);
            List<Element> second = Args.Elements(doc, secondIds, out missingB);

            int changed = 0, alreadyCorrect = 0;
            JsonValue skipped = JsonValue.NewArray();

            ctx.InTransaction(join ? "Join geometry" : "Unjoin geometry", delegate
            {
                foreach (Element a in first)
                {
                    foreach (Element b in second)
                    {
                        if (a.Id == b.Id) continue;

                        try
                        {
                            bool joined = JoinGeometryUtils.AreElementsJoined(doc, a, b);

                            if (join && joined) { alreadyCorrect++; continue; }
                            if (!join && !joined) { alreadyCorrect++; continue; }

                            if (join) JoinGeometryUtils.JoinGeometry(doc, a, b);
                            else JoinGeometryUtils.UnjoinGeometry(doc, a, b);
                            changed++;
                        }
                        catch (Exception ex)
                        {
                            if (skipped.Count < 50)
                            {
                                skipped.Add(J.O(
                                    "elementId", Compat.IdValue(a.Id),
                                    "targetId", Compat.IdValue(b.Id),
                                    "reason", ex.Message));
                            }
                        }
                    }
                }
            });

            JsonValue result = J.O(
                "action", action,
                "changed", changed,
                "alreadyInThatState", alreadyCorrect,
                "skipped", skipped.Count,
                "skippedDetail", skipped,
                "note", "Only elements that physically intersect can be joined; the rest are " +
                        "reported as skipped.");
            if (missingA.Count > 0 || missingB.Count > 0)
                result.Set("notFound", J.ALongs(missingA.Concat(missingB).ToList()));
            return result;
        }

        // ==================================================================
        //  revit_create_reference_plane
        // ==================================================================
        public static JsonValue CreateReferencePlane(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            View view = AnnotationTools.ResolveView(ctx);
            XYZ start = Args.PointMm(ctx.Args, "start");
            XYZ end = Args.PointMm(ctx.Args, "end");
            string name = Args.Str(ctx.Args, "name");

            if (start.DistanceTo(end) < 1e-6)
                throw ToolException.Invalid("end", "the start and end points are identical.");

            ReferencePlane plane = ctx.InTransaction("Create reference plane", delegate
            {
                // The "cut vector" defines the plane's orientation; vertical is the sane default.
                ReferencePlane created = doc.Create.NewReferencePlane(start, end, XYZ.BasisZ, view);
                if (created != null && !string.IsNullOrEmpty(name))
                {
                    try { created.Name = name; }
                    catch (Exception ex)
                    {
                        ctx.AddWarning("The plane was created but could not be named '" + name + "': " + ex.Message);
                    }
                }
                return created;
            });

            if (plane == null)
                throw new ToolException(BridgeErrorCodes.RevitApi, "Revit did not create the reference plane.");

            return J.O(
                "created", J.O("id", Compat.IdValue(plane.Id), "name", ElementSerializer.SafeName(plane)),
                "view", view.Name,
                "start", Metric.PointToMm(start),
                "end", Metric.PointToMm(end));
        }

        // ==================================================================
        //  revit_load_family
        // ==================================================================
        public static JsonValue LoadFamily(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            string path = Args.Str(ctx.Args, "filePath", true);
            bool activateAll = Args.Bool(ctx.Args, "activateAllTypes", true);

            if (!path.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
                throw ToolException.Invalid("filePath", "a Revit family file must end in .rfa.");
            if (!File.Exists(path))
                throw new ToolException(BridgeErrorCodes.NotFound,
                    "No file exists at '" + path + "'. Give a full path to a .rfa on this machine.");

            Family family = null;
            var activated = new List<string>();

            ctx.InTransaction("Load family", delegate
            {
                Family loaded;
                // The overload with an IFamilyLoadOptions decides what happens when the family is
                // already present; without it Revit refuses rather than overwriting.
                if (!doc.LoadFamily(path, new OverwriteFamilyOptions(), out loaded))
                {
                    return;
                }
                family = loaded;

                if (family != null && activateAll)
                {
                    foreach (ElementId symbolId in family.GetFamilySymbolIds())
                    {
                        var symbol = doc.GetElement(symbolId) as FamilySymbol;
                        if (symbol == null) continue;
                        try
                        {
                            if (!symbol.IsActive) symbol.Activate();
                            activated.Add(symbol.Name);
                        }
                        catch (Exception) { }
                    }
                    doc.Regenerate();
                }
            });

            if (family == null)
                throw new ToolException(BridgeErrorCodes.RevitApi,
                    "Revit did not load '" + Path.GetFileName(path) + "'. It may already be loaded and " +
                    "identical, or the file may be for a newer Revit release than " +
                    Compat.RevitReleaseName + ".");

            return J.O(
                "loaded", J.O(
                    "id", Compat.IdValue(family.Id),
                    "name", ElementSerializer.SafeName(family),
                    "category", Selectors.CategoryName(family)),
                "file", path,
                "typesActivated", J.AStrings(activated),
                "typeCount", activated.Count);
        }

        /// <summary>Overwrite an already-loaded family, keeping the incoming parameter values.</summary>
        private sealed class OverwriteFamilyOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = true;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse,
                                            out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = true;
                return true;
            }
        }

        // ==================================================================
        //  revit_create_ceiling
        // ==================================================================
        public static JsonValue CreateCeiling(ToolContext ctx)
        {
#if REVIT2022_OR_GREATER
            Document doc = ctx.Doc;
            List<XYZ> points = Args.PointListMm(ctx.Args, "boundary", 3, 500);
            Level level = Selectors.FindLevel(doc, Args.Str(ctx.Args, "levelName", true));
            double offsetMm = Args.Num(ctx.Args, "heightOffset", false, 2700);

            CeilingType ceilingType = Selectors.FindElementType<CeilingType>(
                doc, Args.Str(ctx.Args, "ceilingTypeName"), true);
            if (ceilingType == null)
            {
                ceilingType = new FilteredElementCollector(doc)
                    .OfClass(typeof(CeilingType)).Cast<CeilingType>().FirstOrDefault();
            }
            if (ceilingType == null)
                throw new ToolException(BridgeErrorCodes.NotFound, "This model contains no ceiling types.");

            double z = level.Elevation;
            var flat = new List<XYZ>();
            for (int i = 0; i < points.Count; i++) flat.Add(new XYZ(points[i].X, points[i].Y, z));
            if (flat[0].DistanceTo(flat[flat.Count - 1]) > 1e-6) flat.Add(flat[0]);

            var loop = new CurveLoop();
            for (int i = 0; i < flat.Count - 1; i++)
            {
                if (flat[i].DistanceTo(flat[i + 1]) < 1e-6)
                    throw ToolException.Invalid("boundary",
                        "points " + i + " and " + (i + 1) + " are coincident.");
                loop.Append(Line.CreateBound(flat[i], flat[i + 1]));
            }

            Ceiling ceiling = ctx.InTransaction("Create ceiling", delegate
            {
                Ceiling created = Ceiling.Create(doc, new List<CurveLoop> { loop }, ceilingType.Id, level.Id);
                if (created != null && Math.Abs(offsetMm) > 1e-9)
                {
                    WriteTools.SetDoubleParam(created,
                        BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM, Metric.MmToFeet(offsetMm));
                }
                return created;
            });

            if (ceiling == null)
                throw new ToolException(BridgeErrorCodes.RevitApi, "Revit did not create the ceiling.");

            return J.O(
                "created", WriteTools.Describe(ceiling),
                "ceilingType", ceilingType.Name,
                "level", level.Name,
                "heightOffsetMm", Metric.R(offsetMm),
                "boundaryPoints", flat.Count - 1);
#else
            throw new ToolException(BridgeErrorCodes.RevitApi,
                "Ceiling creation requires Revit 2022 or newer - the Ceiling.Create API does not " +
                "exist in Revit " + Compat.RevitReleaseName + ". Draw the ceiling by hand, then use " +
                "revit_set_parameters to configure it.");
#endif
        }

        // ==================================================================
        //  revit_export
        // ==================================================================
        public static JsonValue Export(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            string format = Args.Str(ctx.Args, "format", true, null, new[] { "ifc", "dwg", "pdf", "image" });
            string folder = Args.Str(ctx.Args, "folder", true);
            string fileName = Args.Str(ctx.Args, "fileName");

            if (!Directory.Exists(folder))
                throw new ToolException(BridgeErrorCodes.NotFound,
                    "The folder '" + folder + "' does not exist. Create it first - this tool will not " +
                    "make directories on your behalf.");

            // Fail early on a read-only folder rather than deep inside Revit's exporter.
            try
            {
                string probe = Path.Combine(folder, ".abmcp-write-test");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                throw new ToolException(BridgeErrorCodes.InvalidArgument,
                    "The folder '" + folder + "' is not writable: " + ex.Message);
            }

            if (string.IsNullOrEmpty(fileName)) fileName = SanitiseFileName(doc.Title);

            List<View> views = ResolveViews(ctx, doc);

            switch (format.ToLowerInvariant())
            {
                case "ifc": return ExportIfc(ctx, doc, folder, fileName);
                case "dwg": return ExportDwg(ctx, doc, folder, fileName, views);
                case "image": return ExportImage(ctx, doc, folder, fileName, views);
                case "pdf": return ExportPdf(ctx, doc, folder, fileName, views);
                default:
                    throw ToolException.Invalid("format", "'" + format + "' is not supported.");
            }
        }

        private static List<View> ResolveViews(ToolContext ctx, Document doc)
        {
            var views = new List<View>();

            List<long> viewIds = Args.IdValues(ctx.Args, "viewIds", false, 200);
            foreach (long id in viewIds)
            {
                var view = doc.GetElement(Compat.ToId(id)) as View;
                if (view != null) views.Add(view);
                else ctx.AddWarning("View id " + id + " was not found.");
            }

            foreach (string name in Args.StrList(ctx.Args, "viewNames", 200))
            {
                View view = Selectors.FindView(doc, name, false);
                if (view != null) views.Add(view);
                else ctx.AddWarning("View '" + name + "' was not found.");
            }

            return views;
        }

        private static JsonValue ExportIfc(ToolContext ctx, Document doc, string folder, string fileName)
        {
            ctx.InTransaction("Export IFC", delegate
            {
                var options = new IFCExportOptions();
                doc.Export(folder, fileName, options);
            });

            string path = Path.Combine(folder, fileName + ".ifc");
            return J.O(
                "format", "ifc",
                "file", path,
                "exists", File.Exists(path),
                "sizeMb", File.Exists(path) ? (object)Metric.R(new FileInfo(path).Length / 1048576.0, 2) : null,
                "note", "Exported with Revit's current IFC settings. Configure mapping and the IFC " +
                        "schema version in Revit (File > Export > IFC > Modify setup) if needed.");
        }

        private static JsonValue ExportDwg(ToolContext ctx, Document doc, string folder, string fileName,
                                           List<View> views)
        {
            if (views.Count == 0)
                throw new ToolException(BridgeErrorCodes.MissingArgument,
                    "DWG export needs at least one view. Pass viewIds or viewNames.");

            var ids = views.Select(v => v.Id).ToList();
            bool ok = false;

            ctx.InTransaction("Export DWG", delegate
            {
                var options = new DWGExportOptions();
                ok = doc.Export(folder, fileName, ids, options);
            });

            JsonValue files = JsonValue.NewArray();
            foreach (string f in Directory.GetFiles(folder, fileName + "*.dwg")) files.Add(f);

            return J.O(
                "format", "dwg",
                "succeeded", ok,
                "views", views.Count,
                "files", files,
                "folder", folder);
        }

        private static JsonValue ExportImage(ToolContext ctx, Document doc, string folder, string fileName,
                                             List<View> views)
        {
            int widthPixels = Args.Int(ctx.Args, "imageWidthPixels", false, 1920, 64, 16000);
            string path = Path.Combine(folder, fileName);

            ctx.InTransaction("Export image", delegate
            {
                var options = new ImageExportOptions
                {
                    FilePath = path,
                    FitDirection = FitDirectionType.Horizontal,
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG,
                    ImageResolution = ImageResolution.DPI_150,
                    ZoomType = ZoomFitType.FitToPage,
                    PixelSize = widthPixels
                };

                if (views.Count > 0)
                {
                    options.ExportRange = ExportRange.SetOfViews;
                    options.SetViewsAndSheets(views.Select(v => v.Id).ToList());
                }
                else
                {
                    options.ExportRange = ExportRange.CurrentView;
                }

                doc.ExportImage(options);
            });

            JsonValue files = JsonValue.NewArray();
            foreach (string f in Directory.GetFiles(folder, fileName + "*.png")) files.Add(f);

            return J.O(
                "format", "image",
                "views", views.Count > 0 ? (object)views.Count : "active view",
                "widthPixels", widthPixels,
                "files", files,
                "folder", folder);
        }

        private static JsonValue ExportPdf(ToolContext ctx, Document doc, string folder, string fileName,
                                           List<View> views)
        {
#if REVIT2022_OR_GREATER
            if (views.Count == 0)
                throw new ToolException(BridgeErrorCodes.MissingArgument,
                    "PDF export needs at least one view or sheet. Pass viewIds or viewNames.");

            var ids = views.Select(v => v.Id).ToList();
            bool ok = false;

            ctx.InTransaction("Export PDF", delegate
            {
                var options = new PDFExportOptions
                {
                    FileName = fileName,
                    Combine = true
                };
                ok = doc.Export(folder, ids, options);
            });

            JsonValue files = JsonValue.NewArray();
            foreach (string f in Directory.GetFiles(folder, fileName + "*.pdf")) files.Add(f);

            return J.O(
                "format", "pdf",
                "succeeded", ok,
                "views", views.Count,
                "files", files,
                "folder", folder);
#else
            throw new ToolException(BridgeErrorCodes.RevitApi,
                "PDF export requires Revit 2022 or newer - the PDFExportOptions API does not exist " +
                "in Revit " + Compat.RevitReleaseName + ". Export DWG or images instead, or print to " +
                "PDF from the Revit UI.");
#endif
        }

        private static string SanitiseFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "export";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}
