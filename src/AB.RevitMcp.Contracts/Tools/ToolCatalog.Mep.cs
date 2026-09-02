using AB.RevitMcp.Contracts.Json;

namespace AB.RevitMcp.Contracts.Tools
{
    public static partial class ToolCatalog
    {
        // ============================================================================
        //  MEP TOOLS
        //
        //  Ducts, pipes, cable trays and conduit are MEP *system family* curve elements.
        //  They are not FamilyInstances, so revit_place_family_instance cannot create them -
        //  each has its own static Create() on a dedicated class.
        // ============================================================================
        private static void RegisterMepTools()
        {
            Add("revit_list_mep_systems", ToolCategory.Read, "List MEP systems",
                "Lists the duct, piping, cable tray and conduit SYSTEM TYPES available in the model " +
                "(Supply Air, Return Air, Domestic Cold Water, ...), plus the routing types for each " +
                "discipline. Call this before creating any MEP run to get exact names.",
                Sch.Obj("MEP system listing options.", null,
                    "discipline", Sch.Str("Which discipline to list.",
                        new[] { "all", "duct", "pipe", "cableTray", "conduit" }, "all")));

            Add("revit_create_duct", ToolCategory.Write, "Create duct",
                "Creates a straight duct run between two points. Coordinates and sizes are in " +
                "millimetres. Give width+height for a rectangular duct, or diameter for a round one - " +
                "the duct type decides which shape applies, so use a Rectangular Duct type with " +
                "width/height and a Round Duct type with diameter.",
                Sch.Obj("Duct creation parameters.", new[] { "start", "end", "levelName" },
                    "start", Sch.Point("Start of the duct centreline (mm)."),
                    "end", Sch.Point("End of the duct centreline (mm)."),
                    "levelName", Sch.Str("Reference level name (exact, case-insensitive)."),
                    "ductTypeName", Sch.Str("Duct type name, e.g. \"Mitered Elbows / Tees\". " +
                                            "Omit to use the first available duct type."),
                    "familyName", Sch.Str("Duct family, e.g. \"Rectangular Duct\" or \"Round Duct\" - " +
                                          "use this to disambiguate a type name shared by several families."),
                    "systemTypeName", Sch.Str("Duct system type, e.g. \"Supply Air\". Omit for the first available."),
                    "width", Sch.Num("Rectangular duct width in mm.", 1, 100000),
                    "height", Sch.Num("Rectangular duct height in mm.", 1, 100000),
                    "diameter", Sch.Num("Round duct diameter in mm.", 1, 100000),
                    "offset", Sch.Num("Centreline height above the reference level, in mm.", null, null, 0)));

            Add("revit_create_pipe", ToolCategory.Write, "Create pipe",
                "Creates a straight pipe run between two points. Coordinates, diameter and offset are " +
                "in millimetres.",
                Sch.Obj("Pipe creation parameters.", new[] { "start", "end", "levelName" },
                    "start", Sch.Point("Start of the pipe centreline (mm)."),
                    "end", Sch.Point("End of the pipe centreline (mm)."),
                    "levelName", Sch.Str("Reference level name."),
                    "pipeTypeName", Sch.Str("Pipe type name. Omit to use the first available."),
                    "systemTypeName", Sch.Str("Piping system type, e.g. \"Domestic Cold Water\". " +
                                              "Omit for the first available."),
                    "diameter", Sch.Num("Nominal diameter in mm. Revit snaps this to the nearest size " +
                                        "the pipe type allows.", 1, 100000),
                    "offset", Sch.Num("Centreline height above the reference level, in mm.", null, null, 0)));

            Add("revit_create_cable_tray", ToolCategory.Write, "Create cable tray",
                "Creates a straight cable tray run between two points. Coordinates and sizes in millimetres.",
                Sch.Obj("Cable tray creation parameters.", new[] { "start", "end", "levelName" },
                    "start", Sch.Point("Start of the tray centreline (mm)."),
                    "end", Sch.Point("End of the tray centreline (mm)."),
                    "levelName", Sch.Str("Reference level name."),
                    "trayTypeName", Sch.Str("Cable tray type name. Omit to use the first available."),
                    "width", Sch.Num("Tray width in mm.", 1, 100000),
                    "height", Sch.Num("Tray height in mm.", 1, 100000),
                    "offset", Sch.Num("Centreline height above the reference level, in mm.", null, null, 0)));

            Add("revit_create_conduit", ToolCategory.Write, "Create conduit",
                "Creates a straight conduit run between two points. Coordinates, diameter and offset " +
                "in millimetres.",
                Sch.Obj("Conduit creation parameters.", new[] { "start", "end", "levelName" },
                    "start", Sch.Point("Start of the conduit centreline (mm)."),
                    "end", Sch.Point("End of the conduit centreline (mm)."),
                    "levelName", Sch.Str("Reference level name."),
                    "conduitTypeName", Sch.Str("Conduit type name. Omit to use the first available."),
                    "diameter", Sch.Num("Nominal diameter in mm.", 1, 100000),
                    "offset", Sch.Num("Centreline height above the reference level, in mm.", null, null, 0)));

            Add("revit_get_view_center", ToolCategory.Read, "Get view centre point",
                "Returns the centre of the active view (or a named view) in project coordinates, " +
                "in millimetres - from the crop box if one is active, otherwise from the combined " +
                "extents of everything visible. Use this to answer \"put it in the middle of what " +
                "I am looking at\" instead of guessing a coordinate.",
                Sch.Obj("View centre options.", null,
                    "viewName", Sch.Str("View to measure. Omit for the active view."),
                    "viewId", Sch.Int("View element id. Omit for the active view.", 1)));
        }
    }
}
