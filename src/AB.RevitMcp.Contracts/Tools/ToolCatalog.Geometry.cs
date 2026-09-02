using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;

namespace AB.RevitMcp.Contracts.Tools
{
    public static partial class ToolCatalog
    {
        // ============================================================================
        //  GEOMETRY OPERATIONS, FAMILIES AND EXPORT
        // ============================================================================
        private static void RegisterGeometryTools()
        {
            Add("revit_mirror_elements", ToolCategory.Write, "Mirror elements",
                "Mirrors elements about a vertical plane. Give either an axis ('x' or 'y' through a " +
                "point) or an explicit plane normal. Set copy:true to keep the originals.",
                Sch.Obj("Mirror parameters.", new[] { "elementIds" },
                    "elementIds", Sch.ElementIds("Elements to mirror.", 2000),
                    "axis", Sch.Str("Mirror about a vertical plane running along this axis, through " +
                                    "'point'. Use this or 'normal'.", new[] { "x", "y" }),
                    "point", Sch.Point("A point the mirror plane passes through (mm). " +
                                       "Defaults to the centre of the selection."),
                    "normal", Sch.Obj("Explicit plane normal. Use this instead of 'axis' for an " +
                                      "angled mirror. Z is forced to 0 - the plane is always vertical.",
                        new[] { "x", "y" },
                        "x", Sch.Num("Normal X component."),
                        "y", Sch.Num("Normal Y component.")),
                    "copy", Sch.Bool("Keep the originals and mirror a copy. Default true.", true)));

            Add("revit_array_elements", ToolCategory.Write, "Array elements",
                "Creates a linear array of copies at a fixed spacing. This is a plain repeated copy, " +
                "not a Revit parametric array element, so each copy is independent.",
                Sch.Obj("Array parameters.", new[] { "elementIds", "direction", "count" },
                    "elementIds", Sch.ElementIds("Elements to array.", 1000),
                    "direction", Sch.Obj("Direction and spacing of each step, in mm.", new[] { "dx", "dy" },
                        "dx", Sch.Num("Step along X in mm."),
                        "dy", Sch.Num("Step along Y in mm."),
                        "dz", Sch.Num("Step along Z in mm. Default 0.", null, null, 0)),
                    "count", Sch.Int("Number of COPIES to create, not counting the original.", 1, 500),
                    "includeOriginal", Sch.Bool("Report the original in the result set. Default false.", false)));

            Add("revit_group_elements", ToolCategory.Write, "Group elements",
                "Combines elements into a Revit model group, optionally naming it. Grouping makes the " +
                "set repeatable and editable as one unit.",
                Sch.Obj("Grouping parameters.", new[] { "elementIds" },
                    "elementIds", Sch.ElementIds("Elements to group.", 2000),
                    "name", Sch.Str("Name for the new group type. Must be unique.")));

            Add("revit_join_geometry", ToolCategory.Write, "Join or unjoin geometry",
                "Joins (or unjoins) the geometry of element pairs, which is what makes walls, floors " +
                "and columns clean up against each other and report correct material quantities. " +
                "Every element in 'elementIds' is joined to every element in 'targetIds'.",
                Sch.Obj("Join parameters.", new[] { "elementIds", "targetIds" },
                    "elementIds", Sch.ElementIds("First set of elements.", 500),
                    "targetIds", Sch.ElementIds("Second set of elements.", 500),
                    "action", Sch.Str("Join or unjoin.", new[] { "join", "unjoin" }, "join")));

            Add("revit_create_reference_plane", ToolCategory.Write, "Create reference plane",
                "Draws a named reference plane in a view. Reference planes are the usual way to set " +
                "out work before modelling.",
                Sch.Obj("Reference plane parameters.", new[] { "start", "end" },
                    "start", Sch.Point("Start point (mm)."),
                    "end", Sch.Point("End point (mm)."),
                    "name", Sch.Str("Name for the plane. Must be unique if given."),
                    "viewId", Sch.Int("View to draw in. Omit for the active view.", 1),
                    "viewName", Sch.Str("View name to draw in. Omit for the active view.")));

            Add("revit_load_family", ToolCategory.Write, "Load a family file",
                "Loads a .rfa family file into the project. Give a full path to a file that exists on " +
                "this machine. Existing families with the same name are overwritten with the newer " +
                "definition.",
                Sch.Obj("Family loading parameters.", new[] { "filePath" },
                    "filePath", Sch.Str("Full path to the .rfa file, e.g. C:\\\\Families\\\\Door.rfa."),
                    "activateAllTypes", Sch.Bool("Activate every type in the family so it can be " +
                                                 "placed immediately. Default true.", true)));

            Add("revit_create_ceiling", ToolCategory.Write, "Create ceiling",
                "Creates a ceiling from a closed boundary on a level. Requires Revit 2022 or newer - " +
                "earlier releases have no ceiling creation API and the call will report that clearly.",
                Sch.Obj("Ceiling parameters.", new[] { "boundary", "levelName" },
                    "boundary", Sch.Arr(Sch.Point("Boundary vertex (mm)."),
                        "Ordered boundary points forming a closed loop (minimum 3).", 3, 500),
                    "levelName", Sch.Str("Level the ceiling belongs to."),
                    "ceilingTypeName", Sch.Str("Ceiling type name. Omit for the default."),
                    "heightOffset", Sch.Num("Height above the level in mm. Default 2700.", null, null, 2700)));

            Add("revit_export", ToolCategory.Write, "Export the model",
                "Exports to IFC, DWG, PDF or an image. The output folder must already exist and be " +
                "writable. PDF export requires Revit 2022 or newer. Nothing is uploaded anywhere - " +
                "files are written to the local path you give.",
                Sch.Obj("Export parameters.", new[] { "format", "folder" },
                    "format", Sch.Str("Output format.", new[] { "ifc", "dwg", "pdf", "image" }),
                    "folder", Sch.Str("Existing local folder to write into, e.g. C:\\\\Exports."),
                    "fileName", Sch.Str("Base file name without extension. Defaults to the model name."),
                    "viewIds", Sch.Arr(Sch.Int("View id.", 1),
                        "Views to export. Required for dwg, pdf and image; ignored for ifc.", 1, 200),
                    "viewNames", Sch.Arr(Sch.Str("View name."),
                        "Views to export, by name. Alternative to viewIds.", 1, 200),
                    "imageWidthPixels", Sch.Int("Image width in pixels, for format 'image'. Default 1920.",
                        64, 16000, 1920)),
                // Export drives Revit's own exporters over a whole sheet or view set. On a real
                // project that is minutes, not milliseconds, and unlike a list tool there is no
                // page size the caller can reduce - so it gets its own budget instead of dying at
                // the interactive default.
                IpcConstants.LongRunningTimeoutMs);
        }
    }
}
