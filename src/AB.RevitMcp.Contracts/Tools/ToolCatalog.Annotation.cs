using AB.RevitMcp.Contracts.Json;

namespace AB.RevitMcp.Contracts.Tools
{
    public static partial class ToolCatalog
    {
        // ============================================================================
        //  ANNOTATION & DOCUMENTATION
        // ============================================================================
        private static void RegisterAnnotationTools()
        {
            Add("revit_create_text_note", ToolCategory.Write, "Create text note",
                "Places a text note in a view. Position is in millimetres, in the view's own " +
                "coordinates (for a sheet that is millimetres from the sheet origin).",
                Sch.Obj("Text note parameters.", new[] { "text", "point" },
                    "text", Sch.Str("The text to place. Use \\n for a line break."),
                    "point", Sch.Point("Where to place the note (mm)."),
                    "viewId", Sch.Int("Target view id. Omit for the active view.", 1),
                    "viewName", Sch.Str("Target view name. Omit for the active view."),
                    "typeName", Sch.Str("Text note type name. Omit for the document default."),
                    "width", Sch.Num("Wrap width in mm. Omit for an unwrapped note.", 1, 100000),
                    "horizontalAlign", Sch.Str("Horizontal alignment.",
                        new[] { "Left", "Center", "Right" }, "Left")));

            Add("revit_tag_elements", ToolCategory.Write, "Tag elements",
                "Places a tag on each of the given elements in a view. Tags are annotation, so they " +
                "only exist in the view you place them in. Elements that have no loaded tag family " +
                "for their category are reported as skipped.",
                Sch.Obj("Tagging parameters.", new[] { "elementIds" },
                    "elementIds", Sch.ElementIds("Elements to tag.", 500),
                    "viewId", Sch.Int("View to place the tags in. Omit for the active view.", 1),
                    "viewName", Sch.Str("View name to place the tags in. Omit for the active view."),
                    "tagTypeName", Sch.Str("Tag type name. Omit to use the first tag type for each category."),
                    "addLeader", Sch.Bool("Draw a leader line. Default false.", false),
                    "orientation", Sch.Str("Tag orientation.", new[] { "Horizontal", "Vertical" }, "Horizontal"),
                    "offset", Sch.Obj("Offset of the tag from the element, in mm.", null,
                        "dx", Sch.Num("Offset along X in mm.", null, null, 0),
                        "dy", Sch.Num("Offset along Y in mm.", null, null, 0))));

            Add("revit_create_detail_line", ToolCategory.Write, "Create detail line",
                "Draws a detail line in a view. Detail lines are view-specific annotation - they do " +
                "not appear in any other view. Coordinates in millimetres.",
                Sch.Obj("Detail line parameters.", new[] { "start", "end" },
                    "start", Sch.Point("Line start (mm)."),
                    "end", Sch.Point("Line end (mm)."),
                    "viewId", Sch.Int("View to draw in. Omit for the active view.", 1),
                    "viewName", Sch.Str("View name to draw in. Omit for the active view."),
                    "lineStyleName", Sch.Str("Line style name, e.g. \"Thin Lines\". Omit for the default.")));

            Add("revit_create_schedule", ToolCategory.Write, "Create schedule",
                "Creates a schedule for a category and adds the requested fields, in order. " +
                "Use revit_list_categories to find the category name, and check the response for " +
                "which requested fields were actually available.",
                Sch.Obj("Schedule parameters.", new[] { "category" },
                    "category", Sch.CategoryName(),
                    "name", Sch.Str("Schedule name. Omit to let Revit name it."),
                    "fields", Sch.Arr(Sch.Str("Field (parameter) name."),
                        "Columns to add, in order. Names must match the schedulable field names " +
                        "for that category.", null, 60),
                    "sortByField", Sch.Str("Field name to sort by."),
                    "isItemized", Sch.Bool("Show every instance rather than grouped totals. Default true.", true)));
            // NOTE: there is deliberately no separate "list schedulable fields" tool. Revit can only
            // enumerate schedulable fields from an EXISTING ViewSchedule, so such a tool would have
            // to create one and roll it back - a write dressed up as a read. Instead,
            // revit_create_schedule returns the full list of available field names whenever a
            // requested field does not match, which solves the same problem in one call.
        }

        // ============================================================================
        //  VIEW MANAGEMENT & GRAPHICS
        // ============================================================================
        private static void RegisterViewTools()
        {
            Add("revit_duplicate_view", ToolCategory.Write, "Duplicate view",
                "Duplicates a view, optionally with detailing or as a dependent view.",
                Sch.Obj("Duplication parameters.", null,
                    "viewId", Sch.Int("View to duplicate. Omit for the active view.", 1),
                    "viewName", Sch.Str("View name to duplicate. Omit for the active view."),
                    "newName", Sch.Str("Name for the copy. Must be unique."),
                    "mode", Sch.Str("Duplication mode.",
                        new[] { "Duplicate", "WithDetailing", "AsDependent" }, "Duplicate")));

            Add("revit_apply_view_template", ToolCategory.Write, "Apply view template",
                "Applies a view template to one or more views. A template can lock scale, detail " +
                "level and visibility settings, so later changes to those may be refused.",
                Sch.Obj("View template parameters.", new[] { "templateName", "viewIds" },
                    "templateName", Sch.Str("View template name (exact, case-insensitive)."),
                    "viewIds", Sch.Arr(Sch.Int("View id.", 1), "Views to apply it to.", 1, 500)));

            Add("revit_set_element_visibility", ToolCategory.Write, "Hide or isolate elements in a view",
                "Hides, unhides or isolates elements in a single view. This changes only that view - " +
                "the elements stay in the model. Isolation here is permanent view state, not Revit's " +
                "temporary Isolate mode.",
                Sch.Obj("Visibility parameters.", new[] { "elementIds", "action" },
                    "elementIds", Sch.ElementIds("Elements to act on.", 5000),
                    "action", Sch.Str("What to do.", new[] { "hide", "unhide", "isolate" }),
                    "viewId", Sch.Int("View to change. Omit for the active view.", 1),
                    "viewName", Sch.Str("View name to change. Omit for the active view.")));

            Add("revit_override_element_graphics", ToolCategory.Write, "Colour or override element graphics",
                "Overrides the graphics of elements in one view - colour, transparency, line weight, " +
                "halftone. Use this to colour-code a model by any criterion you have already queried. " +
                "Pass reset:true to clear overrides instead.",
                Sch.Obj("Graphic override parameters.", new[] { "elementIds" },
                    "elementIds", Sch.ElementIds("Elements to override.", 5000),
                    "viewId", Sch.Int("View to change. Omit for the active view.", 1),
                    "viewName", Sch.Str("View name to change. Omit for the active view."),
                    "color", Sch.Obj("RGB colour, 0-255 per channel.", new[] { "r", "g", "b" },
                        "r", Sch.Int("Red.", 0, 255),
                        "g", Sch.Int("Green.", 0, 255),
                        "b", Sch.Int("Blue.", 0, 255)),
                    "surfaceTransparency", Sch.Int("Surface transparency, 0 (opaque) to 100 (invisible).", 0, 100),
                    "halftone", Sch.Bool("Draw the elements halftone."),
                    "lineWeight", Sch.Int("Projection line weight, 1-16.", 1, 16),
                    "reset", Sch.Bool("Clear all overrides for these elements instead. Default false.", false)));

            Add("revit_set_view_section_box", ToolCategory.Write, "Set 3D view section box",
                "Sets or clears the section box of a 3D view, in millimetres. Use this to crop a 3D " +
                "view to an area of interest before exporting an image or handing it to a coordinator.",
                Sch.Obj("Section box parameters.", null,
                    "viewId", Sch.Int("3D view id. Omit for the active view.", 1),
                    "viewName", Sch.Str("3D view name. Omit for the active view."),
                    "min", Sch.Point("Lower corner of the box (mm)."),
                    "max", Sch.Point("Upper corner of the box (mm)."),
                    "elementIds", Sch.Arr(Sch.Int("Element id.", 1),
                        "Fit the box around these elements instead of giving min/max.", 1, 5000),
                    "paddingMm", Sch.Num("Extra margin around the fitted box, in mm. Default 500.", 0, 100000, 500),
                    "clear", Sch.Bool("Turn the section box off instead. Default false.", false)));
        }
    }
}
