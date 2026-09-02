using AB.RevitMcp.Contracts.Json;

namespace AB.RevitMcp.Contracts.Tools
{
    public static partial class ToolCatalog
    {
        // ============================================================================
        //  THE MODIFY TAB
        //
        //  Revit's Modify panel operations that have no single API call behind them. Split,
        //  Trim/Extend and Align are UI gestures, not API methods, so each is implemented from
        //  the underlying geometry - which is why they are documented tightly about what they
        //  support and what they refuse.
        // ============================================================================
        private static void RegisterModifyTools()
        {
            Add("revit_split_element", ToolCategory.Write, "Split element",
                "Splits a straight, curve-driven element at a point - walls, beams, lines, ducts, " +
                "pipes, cable trays and conduit. The ORIGINAL ELEMENT IS NEVER DELETED: it keeps its " +
                "ElementId and takes the first segment, so tags, dimensions and schedule rows stay " +
                "attached to it, and a new element takes the second segment. Doors and windows hosted " +
                "in a split wall are carried onto whichever side they fall on. Ducts and pipes use " +
                "Revit's own break routine, so their system connectivity is preserved.",
                Sch.Obj("Split parameters.", new[] { "elementId", "point" },
                    "elementId", Sch.ElementId("The curve-driven element to split."),
                    "point", Sch.Point("Where to split (mm). Projected onto the element's centreline, " +
                                       "so it does not have to be exact."),
                    "gap", Sch.Num("Gap to leave between the two pieces, in mm. Default 0 (a clean split).",
                                   0, 100000, 0)));

            Add("revit_trim_extend_elements", ToolCategory.Write, "Trim or extend elements",
                "Trims or extends straight curve-driven elements to meet. 'corner' brings BOTH " +
                "elements to their intersection (Revit's Trim/Extend to Corner). 'extend' moves only " +
                "the first element's nearest end onto the second element's line, leaving the second " +
                "untouched. Straight lines only - arcs are reported as skipped.",
                Sch.Obj("Trim/extend parameters.", new[] { "elementId", "targetId" },
                    "elementId", Sch.ElementId("Element to modify."),
                    "targetId", Sch.ElementId("Element to meet."),
                    "mode", Sch.Str("Which ends move.", new[] { "corner", "extend" }, "corner")));

            Add("revit_align_elements", ToolCategory.Write, "Align elements",
                "Moves elements so that a chosen edge or centre lines up on one axis - the scripted " +
                "equivalent of Revit's Align. Give a target coordinate, or a reference element to " +
                "align to. Nothing is rotated; each element only translates along that one axis.",
                Sch.Obj("Alignment parameters.", new[] { "elementIds", "axis" },
                    "elementIds", Sch.ElementIds("Elements to move.", 2000),
                    "axis", Sch.Str("Axis to align along.", new[] { "x", "y", "z" }),
                    "edge", Sch.Str("Which part of each element lines up.",
                        new[] { "min", "center", "max" }, "center"),
                    "targetCoordinate", Sch.Num("Coordinate to align to, in mm. Use this or referenceElementId."),
                    "referenceElementId", Sch.Int("Align to this element's own edge/centre instead of " +
                                                  "a coordinate.", 1)));

            Add("revit_offset_elements", ToolCategory.Write, "Offset elements",
                "Offsets straight curve-driven elements perpendicular to their own direction, in the " +
                "horizontal plane - Revit's Offset tool. Set copy:true to leave the originals in place.",
                Sch.Obj("Offset parameters.", new[] { "elementIds", "distance" },
                    "elementIds", Sch.ElementIds("Curve-driven elements to offset.", 1000),
                    "distance", Sch.Num("Offset distance in mm. Negative offsets to the other side.",
                                        -1000000, 1000000),
                    "copy", Sch.Bool("Offset a copy and keep the original. Default false.", false)));

            Add("revit_pin_elements", ToolCategory.Write, "Pin or unpin elements",
                "Pins or unpins elements. Pinned elements cannot be moved or deleted by accident - " +
                "worth doing to grids, levels and links before letting anything loose on a model.",
                Sch.Obj("Pin parameters.", new[] { "elementIds", "pinned" },
                    "elementIds", Sch.ElementIds("Elements to pin or unpin.", 5000),
                    "pinned", Sch.Bool("true to pin, false to unpin.")));

            Add("revit_cut_geometry", ToolCategory.Write, "Cut or uncut geometry",
                "Makes one element cut a void out of another (Revit's Cut Geometry), or removes an " +
                "existing cut. Both elements must actually overlap and be of kinds Revit permits to " +
                "cut - the response says which pairs it refused and why.",
                Sch.Obj("Cut parameters.", new[] { "elementIds", "cuttingIds" },
                    "elementIds", Sch.ElementIds("Elements to BE cut.", 500),
                    "cuttingIds", Sch.ElementIds("Elements doing the cutting.", 500),
                    "action", Sch.Str("Cut or uncut.", new[] { "cut", "uncut" }, "cut")));

            Add("revit_set_element_phase", ToolCategory.Write, "Set phase / demolish",
                "Sets the phase an element is created in, or the phase it is demolished in. " +
                "Demolishing is how existing fabric is shown as removed in a refurbishment model - " +
                "it is a phase change, not a deletion, so the element stays in the model.",
                Sch.Obj("Phase parameters.", new[] { "elementIds" },
                    "elementIds", Sch.ElementIds("Elements to update.", 5000),
                    "createdPhase", Sch.Str("Phase name the element is created in."),
                    "demolishedPhase", Sch.Str("Phase name the element is demolished in. " +
                                               "Pass \"none\" to un-demolish.")));

            Add("revit_list_phases", ToolCategory.Read, "List phases",
                "Lists the project's phases in sequence, with the id and name of each. " +
                "Call this before revit_set_element_phase to get exact names.",
                Sch.NoArgs());
        }
    }
}
