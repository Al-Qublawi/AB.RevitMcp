using AB.RevitMcp.Contracts.Json;

namespace AB.RevitMcp.Contracts.Tools
{
    public static partial class ToolCatalog
    {
        // ============================================================================
        //  WRITE TOOLS - each runs inside a TransactionGroup that is rolled back on failure
        // ============================================================================
        private static void RegisterWriteTools()
        {
            Add("revit_create_wall", ToolCategory.Write, "Create wall",
                "Creates a straight wall from a start point to an end point on a level. Coordinates and height are in " +
                "millimetres. If wallTypeName is omitted the document's default wall type is used.",
                Sch.Obj("Wall creation parameters.", new[] { "start", "end", "levelName" },
                    "start", Sch.Point("Wall start point (mm). Z is ignored - the level sets the base."),
                    "end", Sch.Point("Wall end point (mm). Z is ignored."),
                    "levelName", Sch.Str("Base level name (exact, case-insensitive). Use revit_list_levels to find it."),
                    "height", Sch.Num("Unconnected height in mm. Default 3000.", 1, 1000000, 3000),
                    "wallTypeName", Sch.Str("Wall type name, e.g. \"Generic - 200mm\". Omit for the default type."),
                    "baseOffset", Sch.Num("Base offset from the level in mm. Default 0.", null, null, 0),
                    "structural", Sch.Bool("Create as a structural wall. Default false.", false),
                    "flipped", Sch.Bool("Flip the wall's exterior side. Default false.", false)));

            Add("revit_create_column", ToolCategory.Write, "Create column",
                "Places a column (architectural or structural) at a point on a level. Provide a top level name or an " +
                "explicit height in millimetres.",
                Sch.Obj("Column creation parameters.", new[] { "point", "levelName" },
                    "point", Sch.Point("Insertion point (mm). Z is ignored - the base level sets the elevation."),
                    "levelName", Sch.Str("Base level name (exact, case-insensitive)."),
                    "topLevelName", Sch.Str("Top level name. If omitted, height is used."),
                    "height", Sch.Num("Height in mm when topLevelName is omitted. Default 3000.", 1, 1000000, 3000),
                    "typeName", Sch.Str("Column type name. Omit to use the first loaded column type."),
                    "familyName", Sch.Str("Column family name, used to disambiguate typeName."),
                    "structural", Sch.Bool("Place as a structural column (OST_StructuralColumns) rather than an " +
                                           "architectural column. Default true.", true),
                    "rotationDegrees", Sch.Num("Rotation about the vertical axis, degrees. Default 0.", -360, 360, 0)));

            Add("revit_create_beam", ToolCategory.Write, "Create beam",
                "Creates a structural framing member (beam) between two points on a level. Coordinates in millimetres.",
                Sch.Obj("Beam creation parameters.", new[] { "start", "end", "levelName" },
                    "start", Sch.Point("Beam start point (mm)."),
                    "end", Sch.Point("Beam end point (mm)."),
                    "levelName", Sch.Str("Reference level name (exact, case-insensitive)."),
                    "typeName", Sch.Str("Structural framing type name. Omit to use the first loaded type."),
                    "familyName", Sch.Str("Structural framing family name, used to disambiguate typeName.")));

            Add("revit_create_floor", ToolCategory.Write, "Create floor",
                "Creates a floor from a closed boundary of at least three points on a level. The boundary is " +
                "automatically closed; points are in millimetres and must form a planar, non self-intersecting loop.",
                Sch.Obj("Floor creation parameters.", new[] { "boundary", "levelName" },
                    "boundary", Sch.Arr(Sch.Point("Boundary vertex (mm)."),
                        "Ordered boundary points forming a closed loop (minimum 3).", 3, 500),
                    "levelName", Sch.Str("Level name the floor is hosted on."),
                    "floorTypeName", Sch.Str("Floor type name. Omit for the default floor type."),
                    "heightOffset", Sch.Num("Height offset from the level in mm. Default 0.", null, null, 0),
                    "structural", Sch.Bool("Mark the floor as structural. Default false.", false)));

            Add("revit_place_family_instance", ToolCategory.Write, "Place family instance",
                "Places a loadable family instance at a point. Works for furniture, equipment, generic models, " +
                "casework, plumbing fixtures and anything else point-based. Use revit_list_family_types first to " +
                "find an exact familyName / typeName pair.",
                Sch.Obj("Family instance placement parameters.", new[] { "point", "typeName" },
                    "point", Sch.Point("Insertion point (mm)."),
                    "typeName", Sch.Str("Family type name (exact, case-insensitive)."),
                    "familyName", Sch.Str("Family name, used to disambiguate a type name shared by several families."),
                    "category", Sch.CategoryName(),
                    "levelName", Sch.Str("Level to associate the instance with. Defaults to the nearest level below the point."),
                    "hostElementId", Sch.Int("Host element id for hosted families (a wall, floor or ceiling).", 1),
                    "rotationDegrees", Sch.Num("Rotation about the vertical axis at the insertion point, degrees.", -360, 360, 0)));

            Add("revit_create_door", ToolCategory.Write, "Create door in wall",
                "Inserts a door into an existing wall. Give the host wall id and a point on the wall; the point is " +
                "projected onto the wall centreline.",
                Sch.Obj("Door creation parameters.", new[] { "hostWallId", "point" },
                    "hostWallId", Sch.Int("Id of the wall to host the door.", 1),
                    "point", Sch.Point("Approximate door location (mm). Projected onto the wall."),
                    "typeName", Sch.Str("Door type name. Omit to use the first loaded door type."),
                    "familyName", Sch.Str("Door family name, used to disambiguate typeName."),
                    "levelName", Sch.Str("Level for the door. Defaults to the host wall's base level.")));

            Add("revit_create_window", ToolCategory.Write, "Create window in wall",
                "Inserts a window into an existing wall and optionally sets its sill height. Give the host wall id " +
                "and a point on the wall.",
                Sch.Obj("Window creation parameters.", new[] { "hostWallId", "point" },
                    "hostWallId", Sch.Int("Id of the wall to host the window.", 1),
                    "point", Sch.Point("Approximate window location (mm). Projected onto the wall."),
                    "typeName", Sch.Str("Window type name. Omit to use the first loaded window type."),
                    "familyName", Sch.Str("Window family name, used to disambiguate typeName."),
                    "sillHeight", Sch.Num("Sill height above the level in mm. Omit to keep the type default.", 0, 100000),
                    "levelName", Sch.Str("Level for the window. Defaults to the host wall's base level.")));

            Add("revit_create_level", ToolCategory.Write, "Create level",
                "Creates a level at an elevation in millimetres and names it. Optionally creates a matching floor plan view.",
                Sch.Obj("Level creation parameters.", new[] { "elevation", "name" },
                    "elevation", Sch.Num("Elevation above project base point, in mm."),
                    "name", Sch.Str("Level name. Must be unique in the document."),
                    "createFloorPlan", Sch.Bool("Also create a floor plan view for the new level. Default true.", true)));

            Add("revit_create_grid", ToolCategory.Write, "Create grid",
                "Creates a straight grid line between two points and names it. Coordinates in millimetres.",
                Sch.Obj("Grid creation parameters.", new[] { "start", "end", "name" },
                    "start", Sch.Point("Grid start point (mm)."),
                    "end", Sch.Point("Grid end point (mm)."),
                    "name", Sch.Str("Grid name, e.g. \"A\" or \"1\". Must be unique.")));

            Add("revit_create_room", ToolCategory.Write, "Create room",
                "Places a room at a point on a level and optionally sets its name and number. The point must sit " +
                "inside a closed, bounded area or the room will be created unbounded.",
                Sch.Obj("Room creation parameters.", new[] { "point", "levelName" },
                    "point", Sch.Point("Point inside the enclosed area (mm). Z is ignored."),
                    "levelName", Sch.Str("Level name for the room."),
                    "name", Sch.Str("Room name."),
                    "number", Sch.Str("Room number."),
                    "department", Sch.Str("Department value.")));

            Add("revit_set_parameters", ToolCategory.Write, "Set parameter values",
                "Writes one or more parameter values onto one or more elements. Values are metric: lengths in mm, " +
                "areas in m2, volumes in m3, angles in degrees - the bridge converts to Revit's internal units. " +
                "Read-only and calculated parameters are reported as skipped rather than failing the whole call.",
                Sch.Obj("Parameter write parameters.", new[] { "elementIds", "parameters" },
                    "elementIds", Sch.ElementIds("Element ids to update.", 5000),
                    "parameters", Sch.Arr(
                        Sch.Obj("One parameter assignment.", new[] { "name" },
                            "name", Sch.Str("Parameter name exactly as it appears in Revit."),
                            "value", Sch.Str("Value to write. Numbers and booleans may be sent as strings; " +
                                             "an ElementId parameter accepts a numeric id or an element name."),
                            "clear", Sch.Bool("Set true to clear the parameter instead of writing a value.", false)),
                        "Parameter assignments applied to every listed element.", 1, 100),
                    "applyToType", Sch.Bool("Write to each element's TYPE instead of the instance. Affects every " +
                                            "instance of that type - default false.", false)));

            Add("revit_move_elements", ToolCategory.Write, "Move elements",
                "Translates elements by a vector in millimetres. Pinned elements and elements that cannot move are " +
                "reported as skipped.",
                Sch.Obj("Move parameters.", new[] { "elementIds", "translation" },
                    "elementIds", Sch.ElementIds("Element ids to move.", 5000),
                    "translation", Sch.Obj("Translation vector in mm.", new[] { "dx", "dy" },
                        "dx", Sch.Num("Movement along X in mm."),
                        "dy", Sch.Num("Movement along Y in mm."),
                        "dz", Sch.Num("Movement along Z in mm. Default 0.", null, null, 0))));

            Add("revit_rotate_elements", ToolCategory.Write, "Rotate elements",
                "Rotates elements about an axis by an angle in degrees. By default the axis is vertical (Z) through " +
                "the centre of the selection's bounding box.",
                Sch.Obj("Rotate parameters.", new[] { "elementIds", "angleDegrees" },
                    "elementIds", Sch.ElementIds("Element ids to rotate.", 5000),
                    "angleDegrees", Sch.Num("Rotation angle in degrees, counter-clockwise seen from +Z.", -3600, 3600),
                    "axisPoint", Sch.Point("A point on the rotation axis (mm). Defaults to the selection centre."),
                    "axisDirection", Sch.Obj("Axis direction vector. Defaults to vertical (0,0,1).", null,
                        "x", Sch.Num("X component.", null, null, 0),
                        "y", Sch.Num("Y component.", null, null, 0),
                        "z", Sch.Num("Z component.", null, null, 1))));

            Add("revit_copy_elements", ToolCategory.Write, "Copy elements",
                "Copies elements by a translation vector in millimetres, optionally repeating the offset to create " +
                "an array of copies.",
                Sch.Obj("Copy parameters.", new[] { "elementIds", "translation" },
                    "elementIds", Sch.ElementIds("Element ids to copy.", 2000),
                    "translation", Sch.Obj("Offset applied to each successive copy, in mm.", new[] { "dx", "dy" },
                        "dx", Sch.Num("Offset along X in mm."),
                        "dy", Sch.Num("Offset along Y in mm."),
                        "dz", Sch.Num("Offset along Z in mm. Default 0.", null, null, 0)),
                    "count", Sch.Int("Number of copies to create. Default 1.", 1, 200, 1)));

            Add("revit_create_view", ToolCategory.Write, "Create view",
                "Creates a new view: a floor plan or ceiling plan on a level, a default 3D view, or a drafting view. " +
                "Optionally applies a view template and sets the scale.",
                Sch.Obj("View creation parameters.", new[] { "viewType" },
                    "viewType", Sch.Str("Kind of view to create.",
                        new[] { "FloorPlan", "CeilingPlan", "ThreeD", "Drafting", "StructuralPlan", "AreaPlan" }),
                    "levelName", Sch.Str("Level name - required for FloorPlan, CeilingPlan, StructuralPlan and AreaPlan."),
                    "name", Sch.Str("Name for the new view. Must be unique. Omit to let Revit name it."),
                    "viewTemplateName", Sch.Str("View template to apply after creation."),
                    "scale", Sch.Int("View scale denominator, e.g. 50 for 1:50.", 1, 24000),
                    "detailLevel", Sch.Str("Detail level.", new[] { "Coarse", "Medium", "Fine" })));

            Add("revit_create_sheet", ToolCategory.Write, "Create sheet",
                "Creates a drawing sheet with a number and name, using a title block type. Use this to build a " +
                "drawing register - call it once per sheet.",
                Sch.Obj("Sheet creation parameters.", new[] { "sheetNumber", "name" },
                    "sheetNumber", Sch.Str("Sheet number, e.g. \"A-101\". Must be unique."),
                    "name", Sch.Str("Sheet name, e.g. \"Ground Floor Plan\"."),
                    "titleBlockTypeName", Sch.Str("Title block type name. Omit to use the first available title block."),
                    "parameters", Sch.Arr(
                        Sch.Obj("Additional sheet parameter to set.", new[] { "name", "value" },
                            "name", Sch.Str("Parameter name."),
                            "value", Sch.Str("Value to write.")),
                        "Extra sheet parameters to populate (drawn by, checked by, revision, ...).", null, 40)));

            Add("revit_place_view_on_sheet", ToolCategory.Write, "Place view on sheet",
                "Places a view onto a sheet as a viewport. Position is in millimetres from the sheet origin; omit it " +
                "to centre the view on the sheet. Fails cleanly if the view is already placed or cannot be placed.",
                Sch.Obj("Viewport placement parameters.", null,
                    "sheetId", Sch.Int("Target sheet element id. Provide this or sheetNumber.", 1),
                    "sheetNumber", Sch.Str("Target sheet number. Provide this or sheetId."),
                    "viewId", Sch.Int("View element id to place. Provide this or viewName.", 1),
                    "viewName", Sch.Str("View name to place. Provide this or viewId."),
                    "position", Sch.SheetPoint("Viewport centre on the sheet, mm from the sheet origin. " +
                                               "Omit to centre automatically.")));

            Add("revit_set_element_material", ToolCategory.Write, "Assign material",
                "Assigns a project material to elements by writing it into a material-valued parameter " +
                "(Structural Material by default). The material must already exist in the project.",
                Sch.Obj("Material assignment parameters.", new[] { "elementIds", "materialName" },
                    "elementIds", Sch.ElementIds("Element ids to update.", 5000),
                    "materialName", Sch.Str("Existing project material name (exact, case-insensitive)."),
                    "parameterName", Sch.Str("Material parameter to write. Omit to use Structural Material, " +
                                             "falling back to the element's first material-valued parameter."),
                    "applyToType", Sch.Bool("Write the material onto the element TYPE instead of the instance. " +
                                            "Default false.", false)));

            Add("revit_set_element_workset", ToolCategory.Write, "Assign workset",
                "Moves elements onto a named user workset. Only valid in a workshared model; elements owned by " +
                "another user are reported as skipped.",
                Sch.Obj("Workset assignment parameters.", new[] { "elementIds", "worksetName" },
                    "elementIds", Sch.ElementIds("Element ids to reassign.", 5000),
                    "worksetName", Sch.Str("Target user workset name (exact, case-insensitive).")));

            Add("revit_set_selection", ToolCategory.Write, "Select elements in Revit",
                "Sets the user's selection in the Revit UI and optionally zooms the active view to fit those " +
                "elements. Useful for showing a human exactly which elements an answer refers to.",
                Sch.Obj("Selection parameters.", new[] { "elementIds" },
                    "elementIds", Sch.Arr(Sch.Int("Element id.", 1),
                        "Element ids to select. Pass an empty array to clear the selection.", 0, 10000),
                    "zoomTo", Sch.Bool("Zoom the active view to the selection. Default true.", true)));
        }
    }
}
