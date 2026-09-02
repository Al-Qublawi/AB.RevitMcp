# Revit MCP tool reference

Generated from the shared tool catalogue by `AB.RevitMcp.Server.exe --print-tools`.

**Units** - every value in and out is metric: lengths in millimetres (mm), areas in 
square metres (m2), volumes in cubic metres (m3), angles in degrees. Revit's internal 
decimal feet never cross this boundary.

| Category | Count | Transaction | Confirmation |
| --- | ---: | --- | --- |
| Read | 24 | none | not required |
| Write | 48 | one transaction group per call | not required |
| Destructive | 6 | one transaction group per call | `confirm: true` required |
| **Total** | **78** | | |

## Read tools

Query only. These never open a Revit transaction and are safe to call at any time.

### `revit_get_model_info`

Returns identity and context for the active Revit document: title, file path, Revit version and build, worksharing state, project information (name, number, address, client), level count, element count, active view, and the unit convention used by every tool in this server.

Takes no arguments.

### `revit_get_model_health`

Runs a model-health audit: file size, total and per-category element counts, warning count grouped by type, in-place family count, unused/unplaced view count, DWG import count, workset count, linked model count and unplaced/unbounded room count. Use this before a submission or coordination review.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `topCategories` | integer | no | How many of the largest categories to report. Default 15. (default `15`) |

### `revit_list_categories`

Lists the model categories present in the document with a live element count for each. Use this first to discover the exact category name to pass to other tools.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `onlyWithElements` | boolean | no | Only return categories that currently contain elements. Default true. (default `true`) |
| `nameContains` | string | no | Case-insensitive substring filter on the category name. |
| `limit` | integer | no | Maximum items to return. Default 50, hard maximum 500. (default `50`) |
| `offset` | integer | no | Zero-based index of the first item to return (pagination). (default `0`) |

### `revit_list_levels`

Lists every level with its id, name and elevation in millimetres, sorted by elevation. Also reports which levels have an associated floor plan view.

Takes no arguments.

### `revit_list_worksets`

Lists the worksets in a workshared model with id, name, kind, owner, open/closed state and visibility default. Returns an empty list with a note if the model is not workshared.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `kind` | `user` \| `standard` \| `view` \| `family` \| `all` | no | Workset kind filter. (default `user`) |

### `revit_list_views`

Lists views with id, name, view type, scale, detail level, template flag, and whether the view is placed on a sheet. Excludes view templates unless asked for.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `viewType` | string | no | Filter by view type, e.g. FloorPlan, CeilingPlan, ThreeD, Section, Elevation, Drafting, Schedule, Legend, DraftingView. Case-insensitive. |
| `nameContains` | string | no | Case-insensitive substring filter on the view name. |
| `includeTemplates` | boolean | no | Include view templates. Default false. (default `false`) |
| `onlyOnSheets` | boolean | no | Only return views that are placed on a sheet. Default false. (default `false`) |
| `limit` | integer | no | Maximum items to return. Default 50, hard maximum 500. (default `50`) |
| `offset` | integer | no | Zero-based index of the first item to return (pagination). (default `0`) |

### `revit_get_active_view`

Returns full details of the currently active view: id, name, type, scale, detail level, discipline, phase, associated level, view template, crop state, and the element count visible in it.

Takes no arguments.

### `revit_list_sheets`

Lists drawing sheets with number, name, id, revision, and the views placed on each one. This is the drawing register.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `numberContains` | string | no | Case-insensitive substring filter on the sheet number. |
| `includeViewports` | boolean | no | Include the list of views placed on each sheet. Default true. (default `true`) |
| `includePlaceholders` | boolean | no | Include placeholder sheets. Default false. (default `false`) |
| `limit` | integer | no | Maximum items to return. Default 50, hard maximum 500. (default `50`) |
| `offset` | integer | no | Zero-based index of the first item to return (pagination). (default `0`) |

### `revit_list_schedules`

Lists schedules and material takeoffs with id, name, the category they schedule, field names, and row count.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `includeFields` | boolean | no | Include each schedule's field names. Default true. (default `true`) |
| `limit` | integer | no | Maximum items to return. Default 50, hard maximum 500. (default `50`) |
| `offset` | integer | no | Zero-based index of the first item to return (pagination). (default `0`) |

### `revit_list_warnings`

Lists the model's outstanding warnings grouped by warning text, with severity and the ids of the elements involved. This is the primary model-quality signal.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `groupByDescription` | boolean | no | Group identical warnings and report a count. Default true. (default `true`) |
| `includeElementIds` | boolean | no | Include the failing element ids. Default true. (default `true`) |
| `limit` | integer | no | Maximum items to return. Default 50, hard maximum 500. (default `50`) |
| `offset` | integer | no | Zero-based index of the first item to return (pagination). (default `0`) |

### `revit_list_linked_models`

Lists RVT links and CAD links with load state, path, path type, shared-coordinates state, instance count and the transform of each instance. Run this before federation or IFC export.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `includeCad` | boolean | no | Include imported/linked CAD (DWG, DXF, DGN). Default true. (default `true`) |

### `revit_list_materials`

Lists project materials with id, name, class, colour and appearance/structural asset presence.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `nameContains` | string | no | Case-insensitive substring filter on the material name. |
| `limit` | integer | no | Maximum items to return. Default 50, hard maximum 500. (default `50`) |
| `offset` | integer | no | Zero-based index of the first item to return (pagination). (default `0`) |

### `revit_list_family_types`

Lists loadable-family symbols and system-family types available for placement, with id, family name, type name and category. Call this to find the exact typeName / familyName to pass to a create tool.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `category` | string | no | Revit category, either the display name ("Walls", "Structural Columns") or the BuiltInCategory enum name ("OST_Walls"). Case-insensitive. |
| `familyNameContains` | string | no | Case-insensitive substring filter on the family name. |
| `typeNameContains` | string | no | Case-insensitive substring filter on the type name. |
| `limit` | integer | no | Maximum items to return. Default 50, hard maximum 500. (default `50`) |
| `offset` | integer | no | Zero-based index of the first item to return (pagination). (default `0`) |

### `revit_list_rooms`

Lists rooms (or MEP spaces) with number, name, level, department, area in m2, volume in m3, perimeter in mm and placement state. Unplaced and unbounded rooms are flagged.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `kind` | `rooms` \| `spaces` \| `areas` | no | What to list. (default `rooms`) |
| `levelName` | string | no | Only rooms on this level (exact, case-insensitive). |
| `nameContains` | string | no | Case-insensitive substring filter on the room name. |
| `includeUnplaced` | boolean | no | Include unplaced rooms (area = 0, no location). Default true. (default `true`) |
| `limit` | integer | no | Maximum items to return. Default 50, hard maximum 500. (default `50`) |
| `offset` | integer | no | Zero-based index of the first item to return (pagination). (default `0`) |

### `revit_list_grids`

Lists grid lines with id, name, and their start/end points in millimetres.

Takes no arguments.

### `revit_query_elements`

The main element query. Filters instances by any combination of category, family name, type name, level, workset and owning view, then returns a paginated, geometry-free summary of each element. Ask for specific parameters with parameterNames rather than fetching everything.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `category` | string | no | Revit category, either the display name ("Walls", "Structural Columns") or the BuiltInCategory enum name ("OST_Walls"). Case-insensitive. |
| `familyName` | string | no | Exact family name (case-insensitive). |
| `typeName` | string | no | Exact type name (case-insensitive). |
| `levelName` | string | no | Exact level name (case-insensitive). |
| `worksetName` | string | no | Exact workset name (case-insensitive). |
| `viewId` | integer | no | Restrict to elements visible in this view id. |
| `activeViewOnly` | boolean | no | Restrict to elements visible in the active view. Default false. (default `false`) |
| `elementType` | `instances` \| `types` \| `both` | no | Which kind of element to return. (default `instances`) |
| `parameterFilter` | object | no | Optional single-parameter value filter. |
| `parameterNames` | string[] | no | Parameters to include for each returned element. Keep this list short. |
| `includeBoundingBox` | boolean | no | Include an axis-aligned bounding box in mm. Default false. (default `false`) |
| `limit` | integer | no | Maximum items to return. Default 50, hard maximum 500. (default `50`) |
| `offset` | integer | no | Zero-based index of the first item to return (pagination). (default `0`) |

### `revit_search_elements`

Free-text search across element name, type name, family name, Mark and Comments. Use this when the user names something loosely ("the north entrance door", "pump P-01").

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `query` | string | yes | Text to search for. Case-insensitive substring match. |
| `fields` | string[] | no | Which fields to search. Defaults to all of them. |
| `category` | string | no | Revit category, either the display name ("Walls", "Structural Columns") or the BuiltInCategory enum name ("OST_Walls"). Case-insensitive. |
| `limit` | integer | no | Maximum items to return. Default 50, hard maximum 500. (default `50`) |
| `offset` | integer | no | Zero-based index of the first item to return (pagination). (default `0`) |

### `revit_get_element_parameters`

Returns the full parameter set for one or more elements: name, value (metric, formatted), raw value, storage type, read-only flag, shared/built-in origin, and the type parameters inherited from the element's type.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `elementIds` | integer[] | yes | Element ids to read. |
| `parameterNames` | string[] | no | Only return these parameters. Omit to return all. |
| `includeTypeParameters` | boolean | no | Also return the element type's parameters. Default true. (default `true`) |
| `includeReadOnly` | boolean | no | Include read-only parameters. Default true. (default `true`) |
| `includeEmpty` | boolean | no | Include parameters with no value. Default false. (default `false`) |

### `revit_get_element_geometry`

Returns lightweight geometry for elements: axis-aligned bounding box, location point or location curve endpoints, and orientation - all in millimetres. Solids and meshes are never returned.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `elementIds` | integer[] | yes | Element ids to describe. |
| `includeBoundingBox` | boolean | no | Include the bounding box. Default true. (default `true`) |
| `includeLocation` | boolean | no | Include the location point/curve. Default true. (default `true`) |

### `revit_get_selection`

Returns the elements the user currently has selected in the Revit UI. Use this when the user says "these", "the selected ones" or "what I have picked".

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `includeParameters` | boolean | no | Include a short parameter summary per element. Default false. (default `false`) |
| `limit` | integer | no | Maximum items to return. Default 50, hard maximum 500. (default `50`) |

### `revit_bridge_status`

Diagnostics for the bridge itself: pipe name, Revit process id and version, uptime, requests served, average execution time, last error, and the number of registered tools. Use this to troubleshoot connectivity before blaming a tool.

Takes no arguments.

### `revit_list_mep_systems`

Lists the duct, piping, cable tray and conduit SYSTEM TYPES available in the model (Supply Air, Return Air, Domestic Cold Water, ...), plus the routing types for each discipline. Call this before creating any MEP run to get exact names.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `discipline` | `all` \| `duct` \| `pipe` \| `cableTray` \| `conduit` | no | Which discipline to list. (default `all`) |

### `revit_get_view_center`

Returns the centre of the active view (or a named view) in project coordinates, in millimetres - from the crop box if one is active, otherwise from the combined extents of everything visible. Use this to answer "put it in the middle of what I am looking at" instead of guessing a coordinate.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `viewName` | string | no | View to measure. Omit for the active view. |
| `viewId` | integer | no | View element id. Omit for the active view. |

### `revit_list_phases`

Lists the project's phases in sequence, with the id and name of each. Call this before revit_set_element_phase to get exact names.

Takes no arguments.

## Write tools

Each call runs inside one transaction group and appears as a single entry in Revit's undo stack, so a human can reverse it with Ctrl+Z.

### `revit_create_wall`

Creates a straight wall from a start point to an end point on a level. Coordinates and height are in millimetres. If wallTypeName is omitted the document's default wall type is used.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `start` | object | yes | Wall start point (mm). Z is ignored - the level sets the base. |
| `end` | object | yes | Wall end point (mm). Z is ignored. |
| `levelName` | string | yes | Base level name (exact, case-insensitive). Use revit_list_levels to find it. |
| `height` | number | no | Unconnected height in mm. Default 3000. (default `3000`) |
| `wallTypeName` | string | no | Wall type name, e.g. "Generic - 200mm". Omit for the default type. |
| `baseOffset` | number | no | Base offset from the level in mm. Default 0. (default `0`) |
| `structural` | boolean | no | Create as a structural wall. Default false. (default `false`) |
| `flipped` | boolean | no | Flip the wall's exterior side. Default false. (default `false`) |

### `revit_create_column`

Places a column (architectural or structural) at a point on a level. Provide a top level name or an explicit height in millimetres.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `point` | object | yes | Insertion point (mm). Z is ignored - the base level sets the elevation. |
| `levelName` | string | yes | Base level name (exact, case-insensitive). |
| `topLevelName` | string | no | Top level name. If omitted, height is used. |
| `height` | number | no | Height in mm when topLevelName is omitted. Default 3000. (default `3000`) |
| `typeName` | string | no | Column type name. Omit to use the first loaded column type. |
| `familyName` | string | no | Column family name, used to disambiguate typeName. |
| `structural` | boolean | no | Place as a structural column (OST_StructuralColumns) rather than an architectural column. Default true. (default `true`) |
| `rotationDegrees` | number | no | Rotation about the vertical axis, degrees. Default 0. (default `0`) |

### `revit_create_beam`

Creates a structural framing member (beam) between two points on a level. Coordinates in millimetres.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `start` | object | yes | Beam start point (mm). |
| `end` | object | yes | Beam end point (mm). |
| `levelName` | string | yes | Reference level name (exact, case-insensitive). |
| `typeName` | string | no | Structural framing type name. Omit to use the first loaded type. |
| `familyName` | string | no | Structural framing family name, used to disambiguate typeName. |

### `revit_create_floor`

Creates a floor from a closed boundary of at least three points on a level. The boundary is automatically closed; points are in millimetres and must form a planar, non self-intersecting loop.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `boundary` | object[] | yes | Ordered boundary points forming a closed loop (minimum 3). |
| `levelName` | string | yes | Level name the floor is hosted on. |
| `floorTypeName` | string | no | Floor type name. Omit for the default floor type. |
| `heightOffset` | number | no | Height offset from the level in mm. Default 0. (default `0`) |
| `structural` | boolean | no | Mark the floor as structural. Default false. (default `false`) |

### `revit_place_family_instance`

Places a loadable family instance at a point. Works for furniture, equipment, generic models, casework, plumbing fixtures and anything else point-based. Use revit_list_family_types first to find an exact familyName / typeName pair.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `point` | object | yes | Insertion point (mm). |
| `typeName` | string | yes | Family type name (exact, case-insensitive). |
| `familyName` | string | no | Family name, used to disambiguate a type name shared by several families. |
| `category` | string | no | Revit category, either the display name ("Walls", "Structural Columns") or the BuiltInCategory enum name ("OST_Walls"). Case-insensitive. |
| `levelName` | string | no | Level to associate the instance with. Defaults to the nearest level below the point. |
| `hostElementId` | integer | no | Host element id for hosted families (a wall, floor or ceiling). |
| `rotationDegrees` | number | no | Rotation about the vertical axis at the insertion point, degrees. (default `0`) |

### `revit_create_door`

Inserts a door into an existing wall. Give the host wall id and a point on the wall; the point is projected onto the wall centreline.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `hostWallId` | integer | yes | Id of the wall to host the door. |
| `point` | object | yes | Approximate door location (mm). Projected onto the wall. |
| `typeName` | string | no | Door type name. Omit to use the first loaded door type. |
| `familyName` | string | no | Door family name, used to disambiguate typeName. |
| `levelName` | string | no | Level for the door. Defaults to the host wall's base level. |

### `revit_create_window`

Inserts a window into an existing wall and optionally sets its sill height. Give the host wall id and a point on the wall.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `hostWallId` | integer | yes | Id of the wall to host the window. |
| `point` | object | yes | Approximate window location (mm). Projected onto the wall. |
| `typeName` | string | no | Window type name. Omit to use the first loaded window type. |
| `familyName` | string | no | Window family name, used to disambiguate typeName. |
| `sillHeight` | number | no | Sill height above the level in mm. Omit to keep the type default. |
| `levelName` | string | no | Level for the window. Defaults to the host wall's base level. |

### `revit_create_level`

Creates a level at an elevation in millimetres and names it. Optionally creates a matching floor plan view.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `elevation` | number | yes | Elevation above project base point, in mm. |
| `name` | string | yes | Level name. Must be unique in the document. |
| `createFloorPlan` | boolean | no | Also create a floor plan view for the new level. Default true. (default `true`) |

### `revit_create_grid`

Creates a straight grid line between two points and names it. Coordinates in millimetres.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `start` | object | yes | Grid start point (mm). |
| `end` | object | yes | Grid end point (mm). |
| `name` | string | yes | Grid name, e.g. "A" or "1". Must be unique. |

### `revit_create_room`

Places a room at a point on a level and optionally sets its name and number. The point must sit inside a closed, bounded area or the room will be created unbounded.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `point` | object | yes | Point inside the enclosed area (mm). Z is ignored. |
| `levelName` | string | yes | Level name for the room. |
| `name` | string | no | Room name. |
| `number` | string | no | Room number. |
| `department` | string | no | Department value. |

### `revit_set_parameters`

Writes one or more parameter values onto one or more elements. Values are metric: lengths in mm, areas in m2, volumes in m3, angles in degrees - the bridge converts to Revit's internal units. Read-only and calculated parameters are reported as skipped rather than failing the whole call.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `elementIds` | integer[] | yes | Element ids to update. |
| `parameters` | object[] | yes | Parameter assignments applied to every listed element. |
| `applyToType` | boolean | no | Write to each element's TYPE instead of the instance. Affects every instance of that type - default false. (default `false`) |

### `revit_move_elements`

Translates elements by a vector in millimetres. Pinned elements and elements that cannot move are reported as skipped.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `elementIds` | integer[] | yes | Element ids to move. |
| `translation` | object | yes | Translation vector in mm. |

### `revit_rotate_elements`

Rotates elements about an axis by an angle in degrees. By default the axis is vertical (Z) through the centre of the selection's bounding box.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `elementIds` | integer[] | yes | Element ids to rotate. |
| `angleDegrees` | number | yes | Rotation angle in degrees, counter-clockwise seen from +Z. |
| `axisPoint` | object | no | A point on the rotation axis (mm). Defaults to the selection centre. |
| `axisDirection` | object | no | Axis direction vector. Defaults to vertical (0,0,1). |

### `revit_copy_elements`

Copies elements by a translation vector in millimetres, optionally repeating the offset to create an array of copies.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `elementIds` | integer[] | yes | Element ids to copy. |
| `translation` | object | yes | Offset applied to each successive copy, in mm. |
| `count` | integer | no | Number of copies to create. Default 1. (default `1`) |

### `revit_create_view`

Creates a new view: a floor plan or ceiling plan on a level, a default 3D view, or a drafting view. Optionally applies a view template and sets the scale.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `viewType` | `FloorPlan` \| `CeilingPlan` \| `ThreeD` \| `Drafting` \| `StructuralPlan` \| `AreaPlan` | yes | Kind of view to create. |
| `levelName` | string | no | Level name - required for FloorPlan, CeilingPlan, StructuralPlan and AreaPlan. |
| `name` | string | no | Name for the new view. Must be unique. Omit to let Revit name it. |
| `viewTemplateName` | string | no | View template to apply after creation. |
| `scale` | integer | no | View scale denominator, e.g. 50 for 1:50. |
| `detailLevel` | `Coarse` \| `Medium` \| `Fine` | no | Detail level. |

### `revit_create_sheet`

Creates a drawing sheet with a number and name, using a title block type. Use this to build a drawing register - call it once per sheet.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `sheetNumber` | string | yes | Sheet number, e.g. "A-101". Must be unique. |
| `name` | string | yes | Sheet name, e.g. "Ground Floor Plan". |
| `titleBlockTypeName` | string | no | Title block type name. Omit to use the first available title block. |
| `parameters` | object[] | no | Extra sheet parameters to populate (drawn by, checked by, revision, ...). |

### `revit_place_view_on_sheet`

Places a view onto a sheet as a viewport. Position is in millimetres from the sheet origin; omit it to centre the view on the sheet. Fails cleanly if the view is already placed or cannot be placed.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `sheetId` | integer | no | Target sheet element id. Provide this or sheetNumber. |
| `sheetNumber` | string | no | Target sheet number. Provide this or sheetId. |
| `viewId` | integer | no | View element id to place. Provide this or viewName. |
| `viewName` | string | no | View name to place. Provide this or viewId. |
| `position` | object | no | Viewport centre on the sheet, mm from the sheet origin. Omit to centre automatically. |

### `revit_set_element_material`

Assigns a project material to elements by writing it into a material-valued parameter (Structural Material by default). The material must already exist in the project.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `elementIds` | integer[] | yes | Element ids to update. |
| `materialName` | string | yes | Existing project material name (exact, case-insensitive). |
| `parameterName` | string | no | Material parameter to write. Omit to use Structural Material, falling back to the element's first material-valued parameter. |
| `applyToType` | boolean | no | Write the material onto the element TYPE instead of the instance. Default false. (default `false`) |

### `revit_set_element_workset`

Moves elements onto a named user workset. Only valid in a workshared model; elements owned by another user are reported as skipped.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `elementIds` | integer[] | yes | Element ids to reassign. |
| `worksetName` | string | yes | Target user workset name (exact, case-insensitive). |

### `revit_set_selection`

Sets the user's selection in the Revit UI and optionally zooms the active view to fit those elements. Useful for showing a human exactly which elements an answer refers to.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `elementIds` | integer[] | yes | Element ids to select. Pass an empty array to clear the selection. |
| `zoomTo` | boolean | no | Zoom the active view to the selection. Default true. (default `true`) |

### `revit_create_duct`

Creates a straight duct run between two points. Coordinates and sizes are in millimetres. Give width+height for a rectangular duct, or diameter for a round one - the duct type decides which shape applies, so use a Rectangular Duct type with width/height and a Round Duct type with diameter.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `start` | object | yes | Start of the duct centreline (mm). |
| `end` | object | yes | End of the duct centreline (mm). |
| `levelName` | string | yes | Reference level name (exact, case-insensitive). |
| `ductTypeName` | string | no | Duct type name, e.g. "Mitered Elbows / Tees". Omit to use the first available duct type. |
| `familyName` | string | no | Duct family, e.g. "Rectangular Duct" or "Round Duct" - use this to disambiguate a type name shared by several families. |
| `systemTypeName` | string | no | Duct system type, e.g. "Supply Air". Omit for the first available. |
| `width` | number | no | Rectangular duct width in mm. |
| `height` | number | no | Rectangular duct height in mm. |
| `diameter` | number | no | Round duct diameter in mm. |
| `offset` | number | no | Centreline height above the reference level, in mm. (default `0`) |

### `revit_create_pipe`

Creates a straight pipe run between two points. Coordinates, diameter and offset are in millimetres.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `start` | object | yes | Start of the pipe centreline (mm). |
| `end` | object | yes | End of the pipe centreline (mm). |
| `levelName` | string | yes | Reference level name. |
| `pipeTypeName` | string | no | Pipe type name. Omit to use the first available. |
| `systemTypeName` | string | no | Piping system type, e.g. "Domestic Cold Water". Omit for the first available. |
| `diameter` | number | no | Nominal diameter in mm. Revit snaps this to the nearest size the pipe type allows. |
| `offset` | number | no | Centreline height above the reference level, in mm. (default `0`) |

### `revit_create_cable_tray`

Creates a straight cable tray run between two points. Coordinates and sizes in millimetres.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `start` | object | yes | Start of the tray centreline (mm). |
| `end` | object | yes | End of the tray centreline (mm). |
| `levelName` | string | yes | Reference level name. |
| `trayTypeName` | string | no | Cable tray type name. Omit to use the first available. |
| `width` | number | no | Tray width in mm. |
| `height` | number | no | Tray height in mm. |
| `offset` | number | no | Centreline height above the reference level, in mm. (default `0`) |

### `revit_create_conduit`

Creates a straight conduit run between two points. Coordinates, diameter and offset in millimetres.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `start` | object | yes | Start of the conduit centreline (mm). |
| `end` | object | yes | End of the conduit centreline (mm). |
| `levelName` | string | yes | Reference level name. |
| `conduitTypeName` | string | no | Conduit type name. Omit to use the first available. |
| `diameter` | number | no | Nominal diameter in mm. |
| `offset` | number | no | Centreline height above the reference level, in mm. (default `0`) |

### `revit_create_text_note`

Places a text note in a view. Position is in millimetres, in the view's own coordinates (for a sheet that is millimetres from the sheet origin).

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `text` | string | yes | The text to place. Use \n for a line break. |
| `point` | object | yes | Where to place the note (mm). |
| `viewId` | integer | no | Target view id. Omit for the active view. |
| `viewName` | string | no | Target view name. Omit for the active view. |
| `typeName` | string | no | Text note type name. Omit for the document default. |
| `width` | number | no | Wrap width in mm. Omit for an unwrapped note. |
| `horizontalAlign` | `Left` \| `Center` \| `Right` | no | Horizontal alignment. (default `Left`) |

### `revit_tag_elements`

Places a tag on each of the given elements in a view. Tags are annotation, so they only exist in the view you place them in. Elements that have no loaded tag family for their category are reported as skipped.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `elementIds` | integer[] | yes | Elements to tag. |
| `viewId` | integer | no | View to place the tags in. Omit for the active view. |
| `viewName` | string | no | View name to place the tags in. Omit for the active view. |
| `tagTypeName` | string | no | Tag type name. Omit to use the first tag type for each category. |
| `addLeader` | boolean | no | Draw a leader line. Default false. (default `false`) |
| `orientation` | `Horizontal` \| `Vertical` | no | Tag orientation. (default `Horizontal`) |
| `offset` | object | no | Offset of the tag from the element, in mm. |

### `revit_create_detail_line`

Draws a detail line in a view. Detail lines are view-specific annotation - they do not appear in any other view. Coordinates in millimetres.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `start` | object | yes | Line start (mm). |
| `end` | object | yes | Line end (mm). |
| `viewId` | integer | no | View to draw in. Omit for the active view. |
| `viewName` | string | no | View name to draw in. Omit for the active view. |
| `lineStyleName` | string | no | Line style name, e.g. "Thin Lines". Omit for the default. |

### `revit_create_schedule`

Creates a schedule for a category and adds the requested fields, in order. Use revit_list_categories to find the category name, and check the response for which requested fields were actually available.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `category` | string | yes | Revit category, either the display name ("Walls", "Structural Columns") or the BuiltInCategory enum name ("OST_Walls"). Case-insensitive. |
| `name` | string | no | Schedule name. Omit to let Revit name it. |
| `fields` | string[] | no | Columns to add, in order. Names must match the schedulable field names for that category. |
| `sortByField` | string | no | Field name to sort by. |
| `isItemized` | boolean | no | Show every instance rather than grouped totals. Default true. (default `true`) |

### `revit_duplicate_view`

Duplicates a view, optionally with detailing or as a dependent view.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `viewId` | integer | no | View to duplicate. Omit for the active view. |
| `viewName` | string | no | View name to duplicate. Omit for the active view. |
| `newName` | string | no | Name for the copy. Must be unique. |
| `mode` | `Duplicate` \| `WithDetailing` \| `AsDependent` | no | Duplication mode. (default `Duplicate`) |

### `revit_apply_view_template`

Applies a view template to one or more views. A template can lock scale, detail level and visibility settings, so later changes to those may be refused.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `templateName` | string | yes | View template name (exact, case-insensitive). |
| `viewIds` | integer[] | yes | Views to apply it to. |

### `revit_set_element_visibility`

Hides, unhides or isolates elements in a single view. This changes only that view - the elements stay in the model. Isolation here is permanent view state, not Revit's temporary Isolate mode.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `elementIds` | integer[] | yes | Elements to act on. |
| `action` | `hide` \| `unhide` \| `isolate` | yes | What to do. |
| `viewId` | integer | no | View to change. Omit for the active view. |
| `viewName` | string | no | View name to change. Omit for the active view. |

### `revit_override_element_graphics`

Overrides the graphics of elements in one view - colour, transparency, line weight, halftone. Use this to colour-code a model by any criterion you have already queried. Pass reset:true to clear overrides instead.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `elementIds` | integer[] | yes | Elements to override. |
| `viewId` | integer | no | View to change. Omit for the active view. |
| `viewName` | string | no | View name to change. Omit for the active view. |
| `color` | object | no | RGB colour, 0-255 per channel. |
| `surfaceTransparency` | integer | no | Surface transparency, 0 (opaque) to 100 (invisible). |
| `halftone` | boolean | no | Draw the elements halftone. |
| `lineWeight` | integer | no | Projection line weight, 1-16. |
| `reset` | boolean | no | Clear all overrides for these elements instead. Default false. (default `false`) |

### `revit_set_view_section_box`

Sets or clears the section box of a 3D view, in millimetres. Use this to crop a 3D view to an area of interest before exporting an image or handing it to a coordinator.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `viewId` | integer | no | 3D view id. Omit for the active view. |
| `viewName` | string | no | 3D view name. Omit for the active view. |
| `min` | object | no | Lower corner of the box (mm). |
| `max` | object | no | Upper corner of the box (mm). |
| `elementIds` | integer[] | no | Fit the box around these elements instead of giving min/max. |
| `paddingMm` | number | no | Extra margin around the fitted box, in mm. Default 500. (default `500`) |
| `clear` | boolean | no | Turn the section box off instead. Default false. (default `false`) |

### `revit_mirror_elements`

Mirrors elements about a vertical plane. Give either an axis ('x' or 'y' through a point) or an explicit plane normal. Set copy:true to keep the originals.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `elementIds` | integer[] | yes | Elements to mirror. |
| `axis` | `x` \| `y` | no | Mirror about a vertical plane running along this axis, through 'point'. Use this or 'normal'. |
| `point` | object | no | A point the mirror plane passes through (mm). Defaults to the centre of the selection. |
| `normal` | object | no | Explicit plane normal. Use this instead of 'axis' for an angled mirror. Z is forced to 0 - the plane is always vertical. |
| `copy` | boolean | no | Keep the originals and mirror a copy. Default true. (default `true`) |

### `revit_array_elements`

Creates a linear array of copies at a fixed spacing. This is a plain repeated copy, not a Revit parametric array element, so each copy is independent.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `elementIds` | integer[] | yes | Elements to array. |
| `direction` | object | yes | Direction and spacing of each step, in mm. |
| `count` | integer | yes | Number of COPIES to create, not counting the original. |
| `includeOriginal` | boolean | no | Report the original in the result set. Default false. (default `false`) |

### `revit_group_elements`

Combines elements into a Revit model group, optionally naming it. Grouping makes the set repeatable and editable as one unit.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `elementIds` | integer[] | yes | Elements to group. |
| `name` | string | no | Name for the new group type. Must be unique. |

### `revit_join_geometry`

Joins (or unjoins) the geometry of element pairs, which is what makes walls, floors and columns clean up against each other and report correct material quantities. Every element in 'elementIds' is joined to every element in 'targetIds'.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `elementIds` | integer[] | yes | First set of elements. |
| `targetIds` | integer[] | yes | Second set of elements. |
| `action` | `join` \| `unjoin` | no | Join or unjoin. (default `join`) |

### `revit_create_reference_plane`

Draws a named reference plane in a view. Reference planes are the usual way to set out work before modelling.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `start` | object | yes | Start point (mm). |
| `end` | object | yes | End point (mm). |
| `name` | string | no | Name for the plane. Must be unique if given. |
| `viewId` | integer | no | View to draw in. Omit for the active view. |
| `viewName` | string | no | View name to draw in. Omit for the active view. |

### `revit_load_family`

Loads a .rfa family file into the project. Give a full path to a file that exists on this machine. Existing families with the same name are overwritten with the newer definition.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `filePath` | string | yes | Full path to the .rfa file, e.g. C:\\Families\\Door.rfa. |
| `activateAllTypes` | boolean | no | Activate every type in the family so it can be placed immediately. Default true. (default `true`) |

### `revit_create_ceiling`

Creates a ceiling from a closed boundary on a level. Requires Revit 2022 or newer - earlier releases have no ceiling creation API and the call will report that clearly.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `boundary` | object[] | yes | Ordered boundary points forming a closed loop (minimum 3). |
| `levelName` | string | yes | Level the ceiling belongs to. |
| `ceilingTypeName` | string | no | Ceiling type name. Omit for the default. |
| `heightOffset` | number | no | Height above the level in mm. Default 2700. (default `2700`) |

### `revit_export`

Exports to IFC, DWG, PDF or an image. The output folder must already exist and be writable. PDF export requires Revit 2022 or newer. Nothing is uploaded anywhere - files are written to the local path you give.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `format` | `ifc` \| `dwg` \| `pdf` \| `image` | yes | Output format. |
| `folder` | string | yes | Existing local folder to write into, e.g. C:\\Exports. |
| `fileName` | string | no | Base file name without extension. Defaults to the model name. |
| `viewIds` | integer[] | no | Views to export. Required for dwg, pdf and image; ignored for ifc. |
| `viewNames` | string[] | no | Views to export, by name. Alternative to viewIds. |
| `imageWidthPixels` | integer | no | Image width in pixels, for format 'image'. Default 1920. (default `1920`) |

### `revit_split_element`

Splits a straight, curve-driven element at a point - walls, beams, lines, ducts, pipes, cable trays and conduit. The ORIGINAL ELEMENT IS NEVER DELETED: it keeps its ElementId and takes the first segment, so tags, dimensions and schedule rows stay attached to it, and a new element takes the second segment. Doors and windows hosted in a split wall are carried onto whichever side they fall on. Ducts and pipes use Revit's own break routine, so their system connectivity is preserved.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `elementId` | integer | yes | The curve-driven element to split. |
| `point` | object | yes | Where to split (mm). Projected onto the element's centreline, so it does not have to be exact. |
| `gap` | number | no | Gap to leave between the two pieces, in mm. Default 0 (a clean split). (default `0`) |

### `revit_trim_extend_elements`

Trims or extends straight curve-driven elements to meet. 'corner' brings BOTH elements to their intersection (Revit's Trim/Extend to Corner). 'extend' moves only the first element's nearest end onto the second element's line, leaving the second untouched. Straight lines only - arcs are reported as skipped.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `elementId` | integer | yes | Element to modify. |
| `targetId` | integer | yes | Element to meet. |
| `mode` | `corner` \| `extend` | no | Which ends move. (default `corner`) |

### `revit_align_elements`

Moves elements so that a chosen edge or centre lines up on one axis - the scripted equivalent of Revit's Align. Give a target coordinate, or a reference element to align to. Nothing is rotated; each element only translates along that one axis.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `elementIds` | integer[] | yes | Elements to move. |
| `axis` | `x` \| `y` \| `z` | yes | Axis to align along. |
| `edge` | `min` \| `center` \| `max` | no | Which part of each element lines up. (default `center`) |
| `targetCoordinate` | number | no | Coordinate to align to, in mm. Use this or referenceElementId. |
| `referenceElementId` | integer | no | Align to this element's own edge/centre instead of a coordinate. |

### `revit_offset_elements`

Offsets straight curve-driven elements perpendicular to their own direction, in the horizontal plane - Revit's Offset tool. Set copy:true to leave the originals in place.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `elementIds` | integer[] | yes | Curve-driven elements to offset. |
| `distance` | number | yes | Offset distance in mm. Negative offsets to the other side. |
| `copy` | boolean | no | Offset a copy and keep the original. Default false. (default `false`) |

### `revit_pin_elements`

Pins or unpins elements. Pinned elements cannot be moved or deleted by accident - worth doing to grids, levels and links before letting anything loose on a model.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `elementIds` | integer[] | yes | Elements to pin or unpin. |
| `pinned` | boolean | yes | true to pin, false to unpin. |

### `revit_cut_geometry`

Makes one element cut a void out of another (Revit's Cut Geometry), or removes an existing cut. Both elements must actually overlap and be of kinds Revit permits to cut - the response says which pairs it refused and why.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `elementIds` | integer[] | yes | Elements to BE cut. |
| `cuttingIds` | integer[] | yes | Elements doing the cutting. |
| `action` | `cut` \| `uncut` | no | Cut or uncut. (default `cut`) |

### `revit_set_element_phase`

Sets the phase an element is created in, or the phase it is demolished in. Demolishing is how existing fabric is shown as removed in a refurbishment model - it is a phase change, not a deletion, so the element stays in the model.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `elementIds` | integer[] | yes | Elements to update. |
| `createdPhase` | string | no | Phase name the element is created in. |
| `demolishedPhase` | string | no | Phase name the element is demolished in. Pass "none" to un-demolish. |

## Destructive tools

These refuse to run unless the caller passes `"confirm": true`. The interlock is enforced by the server AND independently by the Revit add-in. Most also accept `"dryRun": true`, which performs the operation, reports exactly what it would affect, and then rolls the model back.

### `revit_delete_elements`

Permanently deletes elements from the model. Deleting a host also deletes what it hosts (deleting a wall removes its doors and windows), so the response reports every id Revit actually removed. Run with dryRun first to preview the blast radius.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `elementIds` | integer[] | yes | Element ids to delete. |
| `confirm` | boolean | yes | Must be exactly true. Safety interlock for a destructive operation - ask the human before setting it. (default `false`) |
| `dryRun` | boolean | no | Report what would be deleted and roll back without changing the model. Default false. (default `false`) |

### `revit_purge_unused`

Removes unused families, types, materials, filters and other unreferenced definitions - the same operation as Manage > Purge Unused. Repeats until no further items are found, up to maxPasses. Use dryRun to see the count first.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `confirm` | boolean | yes | Must be exactly true. Safety interlock for a destructive operation - ask the human before setting it. (default `false`) |
| `dryRun` | boolean | no | Report the purgeable item count without deleting anything. Default false. (default `false`) |
| `maxPasses` | integer | no | How many purge passes to run. Default 3. (default `3`) |

### `revit_unload_links`

Unloads RVT links so they stop consuming memory and stop appearing in views. The link definitions stay in the model and can be reloaded from Manage Links, so this is reversible - but it changes every view for every user on a workshared model.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `confirm` | boolean | yes | Must be exactly true. Safety interlock for a destructive operation - ask the human before setting it. (default `false`) |
| `linkTypeIds` | integer[] | no | Specific link type ids to unload. Omit to unload every loaded RVT link. |
| `nameContains` | string | no | Only unload links whose name contains this text (case-insensitive). |

### `revit_remove_links`

Deletes RVT/CAD link definitions from the model entirely. This is NOT reversible from Manage Links - the link must be re-inserted and re-positioned. Prefer revit_unload_links unless the user explicitly wants the link gone.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `linkTypeIds` | integer[] | yes | Link type ids to delete. |
| `confirm` | boolean | yes | Must be exactly true. Safety interlock for a destructive operation - ask the human before setting it. (default `false`) |
| `dryRun` | boolean | no | Report what would be removed without changing the model. Default false. (default `false`) |

### `revit_delete_views`

Deletes views and their sheet placements. Views that are the last plan of a level, or that Revit otherwise protects, are reported as skipped rather than failing the whole call.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `viewIds` | integer[] | yes | View ids to delete. |
| `confirm` | boolean | yes | Must be exactly true. Safety interlock for a destructive operation - ask the human before setting it. (default `false`) |
| `dryRun` | boolean | no | Report what would be deleted without changing the model. Default false. (default `false`) |

### `revit_execute_code`

Compiles and runs C# against the open model, for anything the other tools do not cover. Your code is the BODY of a method with `UIApplication uiApp` and `Document doc` in scope; return any value and it comes back as JSON. A transaction is already open, so do not start one. This is disabled by default and must be enabled in three separate places - if it is refused, prefer an existing tool rather than asking the human to unlock it. Requires Revit 2020-2024 (.NET Framework); Revit 2025+ has no in-box C# compiler.

| Argument | Type | Required | Description |
| --- | --- | --- | --- |
| `code` | string | yes | C# statements. Example: return new FilteredElementCollector(doc).OfClass(typeof(Wall)).GetElementCount(); |
| `confirm` | boolean | yes | Must be exactly true. Safety interlock for a destructive operation - ask the human before setting it. (default `false`) |
| `description` | string | no | One line saying what this code does, recorded in the log so a human can audit what ran. |
| `usings` | string[] | no | Extra namespaces to import beyond the Revit and System defaults. |


