using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AB.RevitMcp.Addin.Bridge;
using AB.RevitMcp.Addin.Revit;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace AB.RevitMcp.Addin.Tools
{
    /// <summary>
    /// Revit's Modify panel.
    ///
    /// Split, Trim/Extend and Align are UI gestures with no single API behind them, so each is
    /// built from the underlying geometry. That is why they are strict about what they accept:
    /// silently doing something almost-right to a model is worse than refusing.
    /// </summary>
    public static class ModifyTools
    {
        // ==================================================================
        //  revit_split_element
        // ==================================================================
        public static JsonValue SplitElement(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            long id = Args.Long(ctx.Args, "elementId", true);
            XYZ requested = Args.PointMm(ctx.Args, "point");
            double gapMm = Args.Num(ctx.Args, "gap", false, 0, 0, 100000);

            Element element = doc.GetElement(Compat.ToId(id));
            if (element == null) throw ToolException.NotFound("Element", id.ToString(CultureInfo.InvariantCulture));

            LocationCurve location = element.Location as LocationCurve;
            if (location == null || location.Curve == null)
                throw new ToolException(BridgeErrorCodes.InvalidArgument,
                    "Element " + id + " (" + (Selectors.CategoryName(element) ?? element.GetType().Name) +
                    ") is not curve-driven, so it cannot be split. Splitting applies to walls, beams, " +
                    "lines and MEP runs.");

            Curve curve = location.Curve;
            IntersectionResult projection = curve.Project(requested);
            if (projection == null)
                throw ToolException.Invalid("point", "that point does not project onto the element.");

            XYZ splitPoint = projection.XYZPoint;
            XYZ start = curve.GetEndPoint(0);
            XYZ end = curve.GetEndPoint(1);

            // Refuse a split that would leave a zero-length stub.
            double toStart = splitPoint.DistanceTo(start);
            double toEnd = splitPoint.DistanceTo(end);
            double minimum = Metric.MmToFeet(1.0);
            if (toStart < minimum || toEnd < minimum)
                throw ToolException.Invalid("point",
                    "the split point is at an end of the element (" +
                    Metric.R(Metric.FeetToMm(Math.Min(toStart, toEnd)), 2) + " mm away). " +
                    "Split somewhere along its length.");

            // MEP runs have a proper break routine that preserves system connectivity; using the
            // generic curve surgery below on a duct would orphan it from its system.
            if (element is Duct || element is Pipe)
            {
                long newId = 0;
                ctx.InTransaction("Split " + (element is Duct ? "duct" : "pipe"), delegate
                {
                    ElementId created = element is Duct
                        ? MechanicalUtils.BreakCurve(doc, element.Id, splitPoint)
                        : PlumbingUtils.BreakCurve(doc, element.Id, splitPoint);
                    newId = Compat.IdValue(created);
                });

                if (newId <= 0)
                    throw new ToolException(BridgeErrorCodes.RevitApi, "Revit did not break the run.");

                if (gapMm > 0) ctx.AddWarning("A gap was requested but Revit's MEP break always " +
                                              "produces a clean split; the gap was ignored.");

                return J.O(
                    "split", true,
                    "method", "Revit MEP break (system connectivity preserved)",
                    "originalId", id,
                    "newElementId", newId,
                    "splitPoint", Metric.PointToMm(splitPoint));
            }

            // Everything else: duplicate in place, then trim the original to the first segment and
            // the copy to the second. Revit exposes no generic Split, and crucially the ORIGINAL
            // element survives - it keeps its ElementId, so tags, dimensions, schedules and
            // anything else referencing it stay attached.
            double halfGap = Metric.MmToFeet(gapMm) / 2.0;
            long copyId = 0;
            int insertsCarried = 0;

            var wall = element as Wall;

            ctx.InTransaction("Split element", delegate
            {
                XYZ firstEnd = splitPoint;
                XYZ secondStart = splitPoint;

                if (halfGap > 0)
                {
                    XYZ direction = (end - start).Normalize();
                    firstEnd = splitPoint - direction * halfGap;
                    secondStart = splitPoint + direction * halfGap;
                }

                // Doors and windows are HOSTED by the wall. Copying the wall alone and then
                // shortening it destroys every insert that fell in the discarded half - they lose
                // their host. Copying the wall together with its inserts makes Revit re-host the
                // copies onto the copied wall, so each opening survives on whichever side it
                // belongs to once both walls are trimmed.
                var toCopy = new List<ElementId> { element.Id };
                if (wall != null)
                {
                    try
                    {
                        IList<ElementId> inserts = wall.FindInserts(true, false, false, true);
                        if (inserts != null)
                        {
                            foreach (ElementId insert in inserts) toCopy.Add(insert);
                            insertsCarried = inserts.Count;
                        }
                    }
                    catch (Exception ex)
                    {
                        ctx.AddWarning("Could not enumerate the wall's inserts: " + ex.Message +
                                       " Doors or windows in the second half may be lost.");
                    }
                }

                ICollection<ElementId> copies = ElementTransformUtils.CopyElements(doc, toCopy, XYZ.Zero);
                if (copies == null || copies.Count == 0)
                    throw new ToolException(BridgeErrorCodes.RevitApi,
                        "Revit would not duplicate the element, so it cannot be split this way.");

                // CopyElements returns the whole set in no defined order, so identify the copied
                // HOST by matching type and curve length rather than assuming a position.
                ElementId newElementId = FindCopiedHost(doc, copies, element, curve.Length);
                if (!Compat.IsValid(newElementId))
                    throw new ToolException(BridgeErrorCodes.RevitApi,
                        "Could not identify the duplicated element among the copies.");
                copyId = Compat.IdValue(newElementId);

                // Shorten the ORIGINAL to the first segment - it is kept, never deleted.
                location.Curve = Line.CreateBound(start, firstEnd);

                // ...and give the copy the second.
                Element copy = doc.GetElement(newElementId);
                LocationCurve copyLocation = copy.Location as LocationCurve;
                if (copyLocation == null)
                    throw new ToolException(BridgeErrorCodes.RevitApi, "The duplicate is not curve-driven.");
                copyLocation.Curve = Line.CreateBound(secondStart, end);
            });

            return J.O(
                "split", true,
                "method", "duplicate and retrim",
                "originalKept", true,
                "originalId", id,
                "newElementId", copyId,
                "insertsCarried", insertsCarried,
                "note", "The original element was NOT deleted. It keeps id " +
                        id.ToString(CultureInfo.InvariantCulture) + " and the first segment; " +
                        "the second segment is a new element.",
                "splitPoint", Metric.PointToMm(splitPoint),
                "gapMm", Metric.R(gapMm),
                "firstSegmentMm", Metric.R(Metric.FeetToMm(toStart)),
                "secondSegmentMm", Metric.R(Metric.FeetToMm(toEnd)));
        }

        /// <summary>
        /// Picks the copied HOST out of the set returned by CopyElements.
        ///
        /// The set also holds copies of the wall's doors and windows, and the order is undefined, so
        /// the host is identified by what makes it the host: same type as the original, curve-driven,
        /// and the same length. Length is the tie-breaker for embedded walls of the same type.
        /// </summary>
        private static ElementId FindCopiedHost(Document doc, ICollection<ElementId> copies,
                                                Element original, double expectedLength)
        {
            ElementId typeId = original.GetTypeId();
            ElementId best = ElementId.InvalidElementId;
            double bestError = double.MaxValue;

            foreach (ElementId candidateId in copies)
            {
                Element candidate = doc.GetElement(candidateId);
                if (candidate == null) continue;

                LocationCurve candidateLocation = candidate.Location as LocationCurve;
                if (candidateLocation == null || candidateLocation.Curve == null) continue;
                if (candidate.GetTypeId() != typeId) continue;

                double error = Math.Abs(candidateLocation.Curve.Length - expectedLength);
                if (error < bestError) { bestError = error; best = candidateId; }
            }

            return best;
        }

        // ==================================================================
        //  revit_trim_extend_elements
        // ==================================================================
        public static JsonValue TrimExtendElements(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            long firstId = Args.Long(ctx.Args, "elementId", true);
            long secondId = Args.Long(ctx.Args, "targetId", true);
            string mode = Args.Str(ctx.Args, "mode", false, "corner", new[] { "corner", "extend" });

            if (firstId == secondId)
                throw ToolException.Invalid("targetId", "an element cannot be trimmed to itself.");

            Element first = doc.GetElement(Compat.ToId(firstId));
            Element second = doc.GetElement(Compat.ToId(secondId));
            if (first == null) throw ToolException.NotFound("Element", firstId.ToString(CultureInfo.InvariantCulture));
            if (second == null) throw ToolException.NotFound("Element", secondId.ToString(CultureInfo.InvariantCulture));

            Line firstLine = LineOf(first, "elementId");
            Line secondLine = LineOf(second, "targetId");

            XYZ intersection = HorizontalIntersection(firstLine, secondLine);
            if (intersection == null)
                throw new ToolException(BridgeErrorCodes.InvalidArgument,
                    "Those two elements are parallel in plan, so they have no corner to meet at.");

            bool corner = Paging.EqualsCi(mode, "corner");

            ctx.InTransaction(corner ? "Trim/extend to corner" : "Extend to element", delegate
            {
                MoveNearestEnd(first, firstLine, intersection);
                if (corner) MoveNearestEnd(second, secondLine, intersection);
            });

            return J.O(
                "mode", mode,
                "elementId", firstId,
                "targetId", secondId,
                "movedBoth", corner,
                "intersection", Metric.PointToMm(intersection),
                "note", corner
                    ? "Both elements now meet at the intersection."
                    : "Only the first element moved; the target was left alone.");
        }

        private static Line LineOf(Element element, string argument)
        {
            LocationCurve location = element.Location as LocationCurve;
            if (location == null || location.Curve == null)
                throw ToolException.Invalid(argument,
                    "element " + Compat.IdValue(element.Id) + " is not curve-driven.");

            var line = location.Curve as Line;
            if (line == null)
                throw ToolException.Invalid(argument,
                    "element " + Compat.IdValue(element.Id) + " is an arc or spline. " +
                    "Trim/extend here handles straight lines only.");
            return line;
        }

        /// <summary>
        /// Intersection of two lines seen in plan. Revit's Curve.Intersect only finds intersections
        /// within the bounded extents, but extending is precisely the case where they do NOT yet
        /// overlap - so this solves the infinite lines instead.
        /// </summary>
        private static XYZ HorizontalIntersection(Line a, Line b)
        {
            XYZ p = a.GetEndPoint(0);
            XYZ r = a.GetEndPoint(1) - p;
            XYZ q = b.GetEndPoint(0);
            XYZ s = b.GetEndPoint(1) - q;

            double cross = r.X * s.Y - r.Y * s.X;
            if (Math.Abs(cross) < 1e-9) return null;      // parallel in plan

            double t = ((q.X - p.X) * s.Y - (q.Y - p.Y) * s.X) / cross;

            // Keep the first element's own elevation - these are plan-space operations.
            XYZ flat = p + r * t;
            return new XYZ(flat.X, flat.Y, p.Z);
        }

        private static void MoveNearestEnd(Element element, Line line, XYZ target)
        {
            XYZ start = line.GetEndPoint(0);
            XYZ end = line.GetEndPoint(1);

            // Whichever end is closer to the corner is the one that moves.
            bool moveStart = start.DistanceTo(target) < end.DistanceTo(target);

            XYZ newStart = moveStart ? new XYZ(target.X, target.Y, start.Z) : start;
            XYZ newEnd = moveStart ? end : new XYZ(target.X, target.Y, end.Z);

            if (newStart.DistanceTo(newEnd) < 1e-6)
                throw new ToolException(BridgeErrorCodes.InvalidArgument,
                    "That would collapse element " + Compat.IdValue(element.Id) + " to zero length.");

            ((LocationCurve)element.Location).Curve = Line.CreateBound(newStart, newEnd);
        }

        // ==================================================================
        //  revit_align_elements
        // ==================================================================
        public static JsonValue AlignElements(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            List<long> ids = Args.IdValues(ctx.Args, "elementIds", true, 2000);
            string axis = Args.Str(ctx.Args, "axis", true, null, new[] { "x", "y", "z" });
            string edge = Args.Str(ctx.Args, "edge", false, "center", new[] { "min", "center", "max" });

            long referenceId = Args.Long(ctx.Args, "referenceElementId", false, 0);
            bool hasTarget = ctx.Args.HasValue("targetCoordinate");

            if (!hasTarget && referenceId <= 0)
                throw new ToolException(BridgeErrorCodes.MissingArgument,
                    "Supply either targetCoordinate (mm) or referenceElementId.");

            double targetFeet;
            if (referenceId > 0)
            {
                Element reference = doc.GetElement(Compat.ToId(referenceId));
                if (reference == null)
                    throw ToolException.NotFound("Reference element", referenceId.ToString(CultureInfo.InvariantCulture));

                BoundingBoxXYZ box = reference.get_BoundingBox(null);
                if (box == null)
                    throw new ToolException(BridgeErrorCodes.InvalidArgument,
                        "The reference element has no bounding box to align to.");
                targetFeet = EdgeValue(box, axis, edge);
            }
            else
            {
                targetFeet = Metric.MmToFeet(Args.Num(ctx.Args, "targetCoordinate", true));
            }

            List<long> missing;
            List<Element> elements = Args.Elements(doc, ids, out missing);

            var moved = new List<JsonValue>();
            JsonValue skipped = JsonValue.NewArray();

            ctx.InTransaction("Align elements", delegate
            {
                foreach (Element element in elements)
                {
                    long id = Compat.IdValue(element.Id);
                    try
                    {
                        if (element.Pinned)
                        {
                            skipped.Add(J.O("elementId", id, "reason", "The element is pinned."));
                            continue;
                        }

                        BoundingBoxXYZ box = element.get_BoundingBox(null);
                        if (box == null)
                        {
                            skipped.Add(J.O("elementId", id, "reason", "No bounding box; cannot align."));
                            continue;
                        }

                        double current = EdgeValue(box, axis, edge);
                        double delta = targetFeet - current;
                        if (Math.Abs(delta) < 1e-9) continue;   // already aligned

                        XYZ translation =
                            Paging.EqualsCi(axis, "x") ? new XYZ(delta, 0, 0) :
                            Paging.EqualsCi(axis, "y") ? new XYZ(0, delta, 0) :
                                                          new XYZ(0, 0, delta);

                        ElementTransformUtils.MoveElement(doc, element.Id, translation);
                        moved.Add(J.O("elementId", id, "movedMm", Metric.R(Metric.FeetToMm(delta))));
                    }
                    catch (Exception ex)
                    {
                        skipped.Add(J.O("elementId", id, "reason", ex.Message));
                    }
                }
            });

            JsonValue result = J.O(
                "axis", axis,
                "edge", edge,
                "targetMm", Metric.R(Metric.FeetToMm(targetFeet)),
                "moved", moved.Count,
                "skipped", skipped.Count,
                "movedDetail", J.A(moved),
                "skippedDetail", skipped);
            if (missing.Count > 0) result.Set("notFound", J.ALongs(missing));
            return result;
        }

        private static double EdgeValue(BoundingBoxXYZ box, string axis, string edge)
        {
            double min = Paging.EqualsCi(axis, "x") ? box.Min.X : Paging.EqualsCi(axis, "y") ? box.Min.Y : box.Min.Z;
            double max = Paging.EqualsCi(axis, "x") ? box.Max.X : Paging.EqualsCi(axis, "y") ? box.Max.Y : box.Max.Z;

            if (Paging.EqualsCi(edge, "min")) return min;
            if (Paging.EqualsCi(edge, "max")) return max;
            return (min + max) / 2.0;
        }

        // ==================================================================
        //  revit_offset_elements
        // ==================================================================
        public static JsonValue OffsetElements(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            List<long> ids = Args.IdValues(ctx.Args, "elementIds", true, 1000);
            double distanceMm = Args.Num(ctx.Args, "distance", true, 0, -1000000, 1000000);
            bool copy = Args.Bool(ctx.Args, "copy", false);

            if (Math.Abs(distanceMm) < 1e-6)
                throw ToolException.Invalid("distance", "an offset of zero would do nothing.");

            double distance = Metric.MmToFeet(distanceMm);

            List<long> missing;
            List<Element> elements = Args.Elements(doc, ids, out missing);

            var results = new List<JsonValue>();
            JsonValue skipped = JsonValue.NewArray();

            ctx.InTransaction("Offset elements", delegate
            {
                foreach (Element element in elements)
                {
                    long id = Compat.IdValue(element.Id);
                    try
                    {
                        LocationCurve location = element.Location as LocationCurve;
                        if (location == null || location.Curve == null)
                        {
                            skipped.Add(J.O("elementId", id, "reason", "Not curve-driven."));
                            continue;
                        }

                        var line = location.Curve as Line;
                        if (line == null)
                        {
                            skipped.Add(J.O("elementId", id, "reason", "Arc or spline; offset handles straight lines."));
                            continue;
                        }

                        // Perpendicular in plan: rotate the direction 90 degrees about Z.
                        XYZ direction = (line.GetEndPoint(1) - line.GetEndPoint(0)).Normalize();
                        var perpendicular = new XYZ(-direction.Y, direction.X, 0);
                        if (perpendicular.GetLength() < 1e-9)
                        {
                            skipped.Add(J.O("elementId", id, "reason", "The element is vertical; no plan offset direction."));
                            continue;
                        }
                        XYZ translation = perpendicular.Normalize() * distance;

                        if (copy)
                        {
                            ICollection<ElementId> created =
                                ElementTransformUtils.CopyElement(doc, element.Id, translation);
                            foreach (ElementId newId in created)
                                results.Add(J.O("sourceId", id, "newElementId", Compat.IdValue(newId)));
                        }
                        else
                        {
                            ElementTransformUtils.MoveElement(doc, element.Id, translation);
                            results.Add(J.O("elementId", id, "moved", true));
                        }
                    }
                    catch (Exception ex)
                    {
                        skipped.Add(J.O("elementId", id, "reason", ex.Message));
                    }
                }
            });

            JsonValue result = J.O(
                "distanceMm", Metric.R(distanceMm),
                "copy", copy,
                "affected", results.Count,
                "skipped", skipped.Count,
                "detail", J.A(results),
                "skippedDetail", skipped);
            if (missing.Count > 0) result.Set("notFound", J.ALongs(missing));
            return result;
        }

        // ==================================================================
        //  revit_pin_elements
        // ==================================================================
        public static JsonValue PinElements(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            List<long> ids = Args.IdValues(ctx.Args, "elementIds", true, 5000);
            bool pinned = Args.Bool(ctx.Args, "pinned", true);

            List<long> missing;
            List<Element> elements = Args.Elements(doc, ids, out missing);

            int changed = 0, alreadySet = 0;
            JsonValue skipped = JsonValue.NewArray();

            ctx.InTransaction(pinned ? "Pin elements" : "Unpin elements", delegate
            {
                foreach (Element element in elements)
                {
                    long id = Compat.IdValue(element.Id);
                    try
                    {
                        if (element.Pinned == pinned) { alreadySet++; continue; }
                        element.Pinned = pinned;
                        changed++;
                    }
                    catch (Exception ex)
                    {
                        skipped.Add(J.O("elementId", id, "reason", ex.Message));
                    }
                }
            });

            JsonValue result = J.O(
                "pinned", pinned,
                "changed", changed,
                "alreadyInThatState", alreadySet,
                "skipped", skipped.Count,
                "skippedDetail", skipped);
            if (missing.Count > 0) result.Set("notFound", J.ALongs(missing));
            return result;
        }

        // ==================================================================
        //  revit_cut_geometry
        // ==================================================================
        public static JsonValue CutGeometry(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            List<long> targetIds = Args.IdValues(ctx.Args, "elementIds", true, 500);
            List<long> cuttingIds = Args.IdValues(ctx.Args, "cuttingIds", true, 500);
            string action = Args.Str(ctx.Args, "action", false, "cut", new[] { "cut", "uncut" });
            bool cut = Paging.EqualsCi(action, "cut");

            List<long> missingA, missingB;
            List<Element> targets = Args.Elements(doc, targetIds, out missingA);
            List<Element> cutters = Args.Elements(doc, cuttingIds, out missingB);

            int changed = 0, alreadyCorrect = 0;
            JsonValue skipped = JsonValue.NewArray();

            ctx.InTransaction(cut ? "Cut geometry" : "Uncut geometry", delegate
            {
                foreach (Element target in targets)
                {
                    foreach (Element cutter in cutters)
                    {
                        if (target.Id == cutter.Id) continue;

                        try
                        {
                            bool firstCutsSecond;
                            bool already = SolidSolidCutUtils.CutExistsBetweenElements(
                                target, cutter, out firstCutsSecond);

                            if (cut && already) { alreadyCorrect++; continue; }
                            if (!cut && !already) { alreadyCorrect++; continue; }

                            if (cut)
                            {
                                // Revit reports WHY it refuses, which is far more useful to pass
                                // back than a generic "could not cut".
                                CutFailureReason reason;
                                if (!SolidSolidCutUtils.CanElementCutElement(cutter, target, out reason))
                                {
                                    skipped.Add(J.O(
                                        "elementId", Compat.IdValue(target.Id),
                                        "cuttingId", Compat.IdValue(cutter.Id),
                                        "reason", "Revit refuses this pair: " + reason));
                                    continue;
                                }
                                SolidSolidCutUtils.AddCutBetweenSolids(doc, target, cutter);
                            }
                            else
                            {
                                SolidSolidCutUtils.RemoveCutBetweenSolids(doc, target, cutter);
                            }
                            changed++;
                        }
                        catch (Exception ex)
                        {
                            if (skipped.Count < 50)
                            {
                                skipped.Add(J.O(
                                    "elementId", Compat.IdValue(target.Id),
                                    "cuttingId", Compat.IdValue(cutter.Id),
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
                "skippedDetail", skipped);
            if (missingA.Count > 0 || missingB.Count > 0)
                result.Set("notFound", J.ALongs(missingA.Concat(missingB).ToList()));
            return result;
        }

        // ==================================================================
        //  revit_list_phases  /  revit_set_element_phase
        // ==================================================================
        public static JsonValue ListPhases(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            JsonValue phases = JsonValue.NewArray();
            int sequence = 0;

            foreach (Phase phase in doc.Phases)
            {
                phases.Add(J.O(
                    "id", Compat.IdValue(phase.Id),
                    "name", phase.Name,
                    "sequence", sequence++));
            }

            return J.O("phases", phases, "count", phases.Count,
                       "note", "Phases are listed in project sequence, earliest first.");
        }

        public static JsonValue SetElementPhase(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            List<long> ids = Args.IdValues(ctx.Args, "elementIds", true, 5000);
            string createdName = Args.Str(ctx.Args, "createdPhase");
            string demolishedName = Args.Str(ctx.Args, "demolishedPhase");

            if (string.IsNullOrEmpty(createdName) && string.IsNullOrEmpty(demolishedName))
                throw new ToolException(BridgeErrorCodes.MissingArgument,
                    "Supply createdPhase, demolishedPhase, or both.");

            ElementId createdId = string.IsNullOrEmpty(createdName)
                ? ElementId.InvalidElementId
                : FindPhase(doc, createdName).Id;

            bool unDemolish = !string.IsNullOrEmpty(demolishedName) &&
                              string.Equals(demolishedName, "none", StringComparison.OrdinalIgnoreCase);
            ElementId demolishedId = (string.IsNullOrEmpty(demolishedName) || unDemolish)
                ? ElementId.InvalidElementId
                : FindPhase(doc, demolishedName).Id;

            List<long> missing;
            List<Element> elements = Args.Elements(doc, ids, out missing);

            int updated = 0;
            JsonValue skipped = JsonValue.NewArray();

            ctx.InTransaction("Set element phase", delegate
            {
                foreach (Element element in elements)
                {
                    long id = Compat.IdValue(element.Id);
                    bool touched = false;

                    if (!string.IsNullOrEmpty(createdName))
                        touched |= SetPhaseParam(element, BuiltInParameter.PHASE_CREATED, createdId, id, skipped, "created");

                    if (!string.IsNullOrEmpty(demolishedName))
                        touched |= SetPhaseParam(element, BuiltInParameter.PHASE_DEMOLISHED,
                                                 unDemolish ? ElementId.InvalidElementId : demolishedId,
                                                 id, skipped, "demolished");

                    if (touched) updated++;
                }
            });

            JsonValue result = J.O(
                "updated", updated,
                "createdPhase", createdName,
                "demolishedPhase", unDemolish ? "none (un-demolished)" : demolishedName,
                "skipped", skipped.Count,
                "skippedDetail", skipped,
                "note", "Demolishing is a phase change, not a deletion - the elements are still in " +
                        "the model and appear as demolished in views set to the right phase filter.");
            if (missing.Count > 0) result.Set("notFound", J.ALongs(missing));
            return result;
        }

        private static bool SetPhaseParam(Element element, BuiltInParameter bip, ElementId value,
                                          long id, JsonValue skipped, string label)
        {
            try
            {
                Parameter p = element.get_Parameter(bip);
                if (p == null)
                {
                    skipped.Add(J.O("elementId", id, "reason", "This element has no '" + label + "' phase parameter."));
                    return false;
                }
                if (p.IsReadOnly)
                {
                    skipped.Add(J.O("elementId", id, "reason", "The '" + label + "' phase is read-only here."));
                    return false;
                }
                return p.Set(value);
            }
            catch (Exception ex)
            {
                skipped.Add(J.O("elementId", id, "reason", ex.Message));
                return false;
            }
        }

        private static Phase FindPhase(Document doc, string name)
        {
            var names = new List<string>();
            foreach (Phase phase in doc.Phases)
            {
                if (string.Equals(phase.Name, name, StringComparison.OrdinalIgnoreCase)) return phase;
                names.Add(phase.Name);
            }
            throw ToolException.NotFound("Phase", name).WithSuggestions(names);
        }
    }
}
