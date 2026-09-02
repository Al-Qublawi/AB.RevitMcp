using System;
using System.Collections.Generic;
using System.Linq;
using AB.RevitMcp.Addin.Bridge;
using AB.RevitMcp.Addin.Revit;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace AB.RevitMcp.Addin.Tools
{
    /// <summary>
    /// MEP curve elements.
    ///
    /// Ducts, pipes, cable trays and conduit are system-family curve elements, NOT FamilyInstances,
    /// so the generic placement tool cannot create them. Each has its own static Create(), and each
    /// carries its size on different built-in parameters - which is the only genuinely fiddly part.
    /// </summary>
    public static class MepTools
    {
        // ==================================================================
        //  revit_list_mep_systems
        // ==================================================================
        public static JsonValue ListMepSystems(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            string discipline = Args.Str(ctx.Args, "discipline", false, "all",
                new[] { "all", "duct", "pipe", "cableTray", "conduit" });

            bool all = Paging.EqualsCi(discipline, "all");
            JsonValue result = JsonValue.NewObject();

            if (all || Paging.EqualsCi(discipline, "duct"))
            {
                result.Set("ductSystemTypes", NamesOf<MechanicalSystemType>(doc));
                result.Set("ductTypes", TypeNamesOf<DuctType>(doc));
            }
            if (all || Paging.EqualsCi(discipline, "pipe"))
            {
                result.Set("pipingSystemTypes", NamesOf<PipingSystemType>(doc));
                result.Set("pipeTypes", TypeNamesOf<PipeType>(doc));
            }
            if (all || Paging.EqualsCi(discipline, "cableTray"))
            {
                result.Set("cableTrayTypes", TypeNamesOf<CableTrayType>(doc));
            }
            if (all || Paging.EqualsCi(discipline, "conduit"))
            {
                result.Set("conduitTypes", TypeNamesOf<ConduitType>(doc));
            }

            result.Set("note", "Pass these names as systemTypeName / ductTypeName / pipeTypeName etc. " +
                               "If a list is empty the model has no MEP template content loaded for " +
                               "that discipline, and creation will fail until a type is loaded.");
            return result;
        }

        private static JsonValue NamesOf<T>(Document doc) where T : Element
        {
            JsonValue arr = JsonValue.NewArray();
            foreach (T e in new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>())
                arr.Add(J.O("id", Compat.IdValue(e.Id), "name", ElementSerializer.SafeName(e)));
            return arr;
        }

        private static JsonValue TypeNamesOf<T>(Document doc) where T : ElementType
        {
            JsonValue arr = JsonValue.NewArray();
            foreach (T e in new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>())
            {
                string family = null;
                try { family = e.FamilyName; } catch (Exception) { }
                arr.Add(J.O("id", Compat.IdValue(e.Id), "typeName", ElementSerializer.SafeName(e),
                            "familyName", family));
            }
            return arr;
        }

        // ==================================================================
        //  revit_create_duct
        // ==================================================================
        public static JsonValue CreateDuct(ToolContext ctx)
        {
            Document doc = ctx.Doc;

            Level level = Selectors.FindLevel(doc, Args.Str(ctx.Args, "levelName", true));
            double offsetMm = Args.Num(ctx.Args, "offset", false, 0);
            XYZ start = RunEndpoint(ctx, "start", level, offsetMm);
            XYZ end = RunEndpoint(ctx, "end", level, offsetMm);
            RequireLength(start, end, "duct");

            DuctType ductType = PickType<DuctType>(doc,
                Args.Str(ctx.Args, "ductTypeName"), Args.Str(ctx.Args, "familyName"), "duct");
            MechanicalSystemType systemType = PickSystem<MechanicalSystemType>(doc,
                Args.Str(ctx.Args, "systemTypeName"), "duct system");

            double widthMm = Args.Num(ctx.Args, "width", false, double.NaN, 1, 100000);
            double heightMm = Args.Num(ctx.Args, "height", false, double.NaN, 1, 100000);
            double diameterMm = Args.Num(ctx.Args, "diameter", false, double.NaN, 1, 100000);

            Duct duct = ctx.InTransaction("Create duct", delegate
            {
                Duct created = Duct.Create(doc, systemType.Id, ductType.Id, level.Id, start, end);
                if (created == null) return null;

                doc.Regenerate();
                ApplySize(ctx, created, widthMm, heightMm, diameterMm, "duct");
                SetOffset(created, offsetMm);
                return created;
            });

            if (duct == null)
                throw new ToolException(BridgeErrorCodes.RevitApi, "Revit did not create the duct.");

            return DescribeRun(duct, "duct", level, start, end, offsetMm)
                .Set("ductType", ductType.Name)
                .Set("systemType", systemType.Name);
        }

        // ==================================================================
        //  revit_create_pipe
        // ==================================================================
        public static JsonValue CreatePipe(ToolContext ctx)
        {
            Document doc = ctx.Doc;

            Level level = Selectors.FindLevel(doc, Args.Str(ctx.Args, "levelName", true));
            double offsetMm = Args.Num(ctx.Args, "offset", false, 0);
            XYZ start = RunEndpoint(ctx, "start", level, offsetMm);
            XYZ end = RunEndpoint(ctx, "end", level, offsetMm);
            RequireLength(start, end, "pipe");

            PipeType pipeType = PickType<PipeType>(doc, Args.Str(ctx.Args, "pipeTypeName"), null, "pipe");
            PipingSystemType systemType = PickSystem<PipingSystemType>(doc,
                Args.Str(ctx.Args, "systemTypeName"), "piping system");

            double diameterMm = Args.Num(ctx.Args, "diameter", false, double.NaN, 1, 100000);

            Pipe pipe = ctx.InTransaction("Create pipe", delegate
            {
                Pipe created = Pipe.Create(doc, systemType.Id, pipeType.Id, level.Id, start, end);
                if (created == null) return null;

                doc.Regenerate();
                if (!double.IsNaN(diameterMm))
                {
                    if (!SetLengthParam(created, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, diameterMm))
                        ctx.AddWarning("Could not set the pipe diameter; the type's default was kept.");
                }
                SetOffset(created, offsetMm);
                return created;
            });

            if (pipe == null)
                throw new ToolException(BridgeErrorCodes.RevitApi, "Revit did not create the pipe.");

            return DescribeRun(pipe, "pipe", level, start, end, offsetMm)
                .Set("pipeType", pipeType.Name)
                .Set("systemType", systemType.Name);
        }

        // ==================================================================
        //  revit_create_cable_tray
        // ==================================================================
        public static JsonValue CreateCableTray(ToolContext ctx)
        {
            Document doc = ctx.Doc;

            Level level = Selectors.FindLevel(doc, Args.Str(ctx.Args, "levelName", true));
            double offsetMm = Args.Num(ctx.Args, "offset", false, 0);
            XYZ start = RunEndpoint(ctx, "start", level, offsetMm);
            XYZ end = RunEndpoint(ctx, "end", level, offsetMm);
            RequireLength(start, end, "cable tray");

            CableTrayType trayType = PickType<CableTrayType>(doc,
                Args.Str(ctx.Args, "trayTypeName"), null, "cable tray");

            double widthMm = Args.Num(ctx.Args, "width", false, double.NaN, 1, 100000);
            double heightMm = Args.Num(ctx.Args, "height", false, double.NaN, 1, 100000);

            CableTray tray = ctx.InTransaction("Create cable tray", delegate
            {
                CableTray created = CableTray.Create(doc, trayType.Id, start, end, level.Id);
                if (created == null) return null;

                doc.Regenerate();
                if (!double.IsNaN(widthMm) &&
                    !SetLengthParam(created, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, widthMm))
                    ctx.AddWarning("Could not set the cable tray width; the type default was kept.");
                if (!double.IsNaN(heightMm) &&
                    !SetLengthParam(created, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM, heightMm))
                    ctx.AddWarning("Could not set the cable tray height; the type default was kept.");
                SetOffset(created, offsetMm);
                return created;
            });

            if (tray == null)
                throw new ToolException(BridgeErrorCodes.RevitApi, "Revit did not create the cable tray.");

            return DescribeRun(tray, "cableTray", level, start, end, offsetMm)
                .Set("trayType", trayType.Name);
        }

        // ==================================================================
        //  revit_create_conduit
        // ==================================================================
        public static JsonValue CreateConduit(ToolContext ctx)
        {
            Document doc = ctx.Doc;

            Level level = Selectors.FindLevel(doc, Args.Str(ctx.Args, "levelName", true));
            double offsetMm = Args.Num(ctx.Args, "offset", false, 0);
            XYZ start = RunEndpoint(ctx, "start", level, offsetMm);
            XYZ end = RunEndpoint(ctx, "end", level, offsetMm);
            RequireLength(start, end, "conduit");

            ConduitType conduitType = PickType<ConduitType>(doc,
                Args.Str(ctx.Args, "conduitTypeName"), null, "conduit");

            double diameterMm = Args.Num(ctx.Args, "diameter", false, double.NaN, 1, 100000);

            Conduit conduit = ctx.InTransaction("Create conduit", delegate
            {
                Conduit created = Conduit.Create(doc, conduitType.Id, start, end, level.Id);
                if (created == null) return null;

                doc.Regenerate();
                if (!double.IsNaN(diameterMm) &&
                    !SetLengthParam(created, BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM, diameterMm))
                    ctx.AddWarning("Could not set the conduit diameter; the type default was kept.");
                SetOffset(created, offsetMm);
                return created;
            });

            if (conduit == null)
                throw new ToolException(BridgeErrorCodes.RevitApi, "Revit did not create the conduit.");

            return DescribeRun(conduit, "conduit", level, start, end, offsetMm)
                .Set("conduitType", conduitType.Name);
        }

        // ==================================================================
        //  shared helpers
        // ==================================================================

        /// <summary>
        /// Reads a run endpoint. The Z of an MEP run comes from the level plus the offset, so a
        /// caller can think in "level + height" rather than absolute elevation.
        /// </summary>
        private static XYZ RunEndpoint(ToolContext ctx, string name, Level level, double offsetMm)
        {
            XYZ p = Args.PointMm(ctx.Args, name);
            return new XYZ(p.X, p.Y, level.Elevation + Metric.MmToFeet(offsetMm));
        }

        private static void RequireLength(XYZ start, XYZ end, string what)
        {
            double lengthMm = Metric.FeetToMm(start.DistanceTo(end));
            if (lengthMm < 1.0)
                throw ToolException.Invalid("end", "the start and end points are " +
                    Metric.R(lengthMm, 3) + " mm apart. A " + what + " needs a measurable length.");
        }

        private static T PickType<T>(Document doc, string typeName, string familyName, string what)
            where T : ElementType
        {
            List<T> types = new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().ToList();
            if (types.Count == 0)
            {
                throw new ToolException(BridgeErrorCodes.NotFound,
                    "This model has no " + what + " types loaded, so a " + what + " cannot be created. " +
                    "Start from an MEP template, or load the relevant system family first.");
            }

            IEnumerable<T> candidates = types;
            if (!string.IsNullOrEmpty(familyName))
            {
                var byFamily = types.Where(t => SafeFamilyName(t) != null &&
                    string.Equals(SafeFamilyName(t), familyName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byFamily.Count == 0)
                {
                    throw ToolException.NotFound(what + " family", familyName)
                        .WithSuggestions(types.Select(SafeFamilyName).Where(n => n != null).Distinct().Take(15).ToList());
                }
                candidates = byFamily;
            }

            if (string.IsNullOrEmpty(typeName)) return candidates.First();

            T match = candidates.FirstOrDefault(
                t => string.Equals(ElementSerializer.SafeName(t), typeName, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;

            throw ToolException.NotFound(what + " type", typeName)
                .WithSuggestions(candidates.Select(t =>
                    (SafeFamilyName(t) != null ? SafeFamilyName(t) + " : " : string.Empty) +
                    ElementSerializer.SafeName(t)).Take(20).ToList());
        }

        private static string SafeFamilyName(ElementType type)
        {
            try { return type.FamilyName; } catch (Exception) { return null; }
        }

        private static T PickSystem<T>(Document doc, string name, string what) where T : Element
        {
            List<T> systems = new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().ToList();
            if (systems.Count == 0)
            {
                throw new ToolException(BridgeErrorCodes.NotFound,
                    "This model has no " + what + " types, so the run cannot be created. " +
                    "Add one in Revit (Manage > MEP Settings) or start from an MEP template.");
            }

            if (string.IsNullOrEmpty(name)) return systems[0];

            T match = systems.FirstOrDefault(
                s => string.Equals(ElementSerializer.SafeName(s), name, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;

            throw ToolException.NotFound(what, name)
                .WithSuggestions(systems.Select(ElementSerializer.SafeName).Take(20).ToList());
        }

        /// <summary>Applies width/height or diameter, whichever the caller supplied.</summary>
        private static void ApplySize(ToolContext ctx, Element element,
                                      double widthMm, double heightMm, double diameterMm, string what)
        {
            bool wantsRectangular = !double.IsNaN(widthMm) || !double.IsNaN(heightMm);
            bool wantsRound = !double.IsNaN(diameterMm);

            if (wantsRectangular && wantsRound)
            {
                ctx.AddWarning("Both a diameter and a width/height were supplied. The " + what +
                               " type decides its shape, so only the matching pair was applied.");
            }

            if (!double.IsNaN(widthMm) &&
                !SetLengthParam(element, BuiltInParameter.RBS_CURVE_WIDTH_PARAM, widthMm))
                ctx.AddWarning("Could not set the width - this " + what +
                               " type is probably round. Use a rectangular type, or pass 'diameter'.");

            if (!double.IsNaN(heightMm) &&
                !SetLengthParam(element, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, heightMm))
                ctx.AddWarning("Could not set the height - this " + what +
                               " type is probably round. Use a rectangular type, or pass 'diameter'.");

            if (!double.IsNaN(diameterMm) &&
                !SetLengthParam(element, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, diameterMm))
                ctx.AddWarning("Could not set the diameter - this " + what +
                               " type is probably rectangular. Use a round type, or pass width/height.");
        }

        private static void SetOffset(Element element, double offsetMm)
        {
            // The centreline Z was already baked into the creation points; writing the offset
            // parameter as well keeps Revit's own "Offset" readout consistent with it.
            SetLengthParam(element, BuiltInParameter.RBS_OFFSET_PARAM, offsetMm);
        }

        private static bool SetLengthParam(Element element, BuiltInParameter bip, double millimetres)
        {
            try
            {
                Parameter p = element.get_Parameter(bip);
                if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double) return false;
                return p.Set(Metric.MmToFeet(millimetres));
            }
            catch (Exception) { return false; }
        }

        private static JsonValue DescribeRun(Element element, string kind, Level level,
                                             XYZ start, XYZ end, double offsetMm)
        {
            JsonValue result = J.O(
                "created", ElementSerializer.Summarize(element,
                    new SerializeOptions { IncludeLocation = true }),
                "kind", kind,
                "level", level.Name,
                "offsetMm", Metric.R(offsetMm),
                "lengthMm", Metric.R(Metric.FeetToMm(start.DistanceTo(end))),
                "start", Metric.PointToMm(start),
                "end", Metric.PointToMm(end));

            // Report the size Revit actually settled on - it snaps to the nearest catalogued size.
            JsonValue actual = JsonValue.NewObject();
            AddIfPresent(actual, element, BuiltInParameter.RBS_CURVE_WIDTH_PARAM, "widthMm");
            AddIfPresent(actual, element, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, "heightMm");
            AddIfPresent(actual, element, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, "diameterMm");
            AddIfPresent(actual, element, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, "diameterMm");
            AddIfPresent(actual, element, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, "widthMm");
            AddIfPresent(actual, element, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM, "heightMm");
            AddIfPresent(actual, element, BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM, "diameterMm");
            if (actual.Count > 0) result.Set("actualSize", actual);

            return result;
        }

        private static void AddIfPresent(JsonValue target, Element element, BuiltInParameter bip, string name)
        {
            if (target.Has(name)) return;
            try
            {
                Parameter p = element.get_Parameter(bip);
                if (p == null || !p.HasValue || p.StorageType != StorageType.Double) return;
                target.Set(name, Metric.R(Metric.FeetToMm(p.AsDouble())));
            }
            catch (Exception) { }
        }
    }
}
