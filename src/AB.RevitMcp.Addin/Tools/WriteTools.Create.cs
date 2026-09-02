using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AB.RevitMcp.Addin.Bridge;
using AB.RevitMcp.Addin.Revit;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;

namespace AB.RevitMcp.Addin.Tools
{
    /// <summary>
    /// WRITE tools, part 1: element creation. Every handler runs inside the router's transaction
    /// group, so a failure half-way through leaves the model exactly as it was found.
    /// </summary>
    public static partial class WriteTools
    {
        // ==================================================================
        //  revit_create_wall
        // ==================================================================
        public static JsonValue CreateWall(ToolContext ctx)
        {
            Document doc = ctx.Doc;

            XYZ start = Args.PointMm(ctx.Args, "start");
            XYZ end = Args.PointMm(ctx.Args, "end");
            Level level = Selectors.FindLevel(doc, Args.Str(ctx.Args, "levelName", true));
            double heightMm = Args.Num(ctx.Args, "height", false, 3000, 1, 1000000);
            double baseOffsetMm = Args.Num(ctx.Args, "baseOffset", false, 0);
            bool structural = Args.Bool(ctx.Args, "structural", false);
            bool flipped = Args.Bool(ctx.Args, "flipped", false);
            string wallTypeName = Args.Str(ctx.Args, "wallTypeName");

            // Reject a degenerate wall before Revit does, with a message that explains itself.
            XYZ flatStart = new XYZ(start.X, start.Y, level.Elevation);
            XYZ flatEnd = new XYZ(end.X, end.Y, level.Elevation);
            double lengthMm = Metric.FeetToMm(flatStart.DistanceTo(flatEnd));
            if (lengthMm < 1.0)
                throw ToolException.Invalid("end", "the start and end points are " +
                    Metric.R(lengthMm, 3) + " mm apart. A wall needs a measurable length.");

            WallType wallType = Selectors.FindElementType<WallType>(doc, wallTypeName, true)
                                ?? Selectors.DefaultType<WallType>(doc, ElementTypeGroup.WallType);
            if (wallType == null)
                throw new ToolException(BridgeErrorCodes.NotFound, "This model contains no wall types.");

            Wall wall = ctx.InTransaction("Create wall", delegate
            {
                Line line = Line.CreateBound(flatStart, flatEnd);
                return Wall.Create(doc, line, wallType.Id, level.Id,
                                   Metric.MmToFeet(heightMm), Metric.MmToFeet(baseOffsetMm),
                                   flipped, structural);
            });

            if (wall == null)
                throw new ToolException(BridgeErrorCodes.RevitApi, "Revit did not return a wall.");

            return J.O(
                "created", Describe(wall),
                "lengthMm", Metric.R(lengthMm),
                "heightMm", Metric.R(heightMm),
                "wallType", wallType.Name,
                "level", level.Name,
                "structural", structural);
        }

        // ==================================================================
        //  revit_create_column
        // ==================================================================
        public static JsonValue CreateColumn(ToolContext ctx)
        {
            Document doc = ctx.Doc;

            XYZ point = Args.PointMm(ctx.Args, "point");
            Level level = Selectors.FindLevel(doc, Args.Str(ctx.Args, "levelName", true));
            string topLevelName = Args.Str(ctx.Args, "topLevelName");
            double heightMm = Args.Num(ctx.Args, "height", false, 3000, 1, 1000000);
            bool structural = Args.Bool(ctx.Args, "structural", true);
            double rotationDegrees = Args.Num(ctx.Args, "rotationDegrees", false, 0, -360, 360);

            Level topLevel = string.IsNullOrEmpty(topLevelName) ? null : Selectors.FindLevel(doc, topLevelName);

            BuiltInCategory category = structural
                ? BuiltInCategory.OST_StructuralColumns
                : BuiltInCategory.OST_Columns;

            FamilySymbol symbol = Selectors.FindFamilySymbol(doc, category,
                Args.Str(ctx.Args, "typeName"), Args.Str(ctx.Args, "familyName"));

            XYZ insertion = new XYZ(point.X, point.Y, level.Elevation);

            FamilyInstance column = ctx.InTransaction("Create column", delegate
            {
                Activate(doc, symbol);
                FamilyInstance created = doc.Create.NewFamilyInstance(insertion, symbol, level,
                    structural ? StructuralType.Column : StructuralType.NonStructural);

                doc.Regenerate();

                // Height: either "to a level" or "this many millimetres".
                if (topLevel != null)
                {
                    SetIdParam(created, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, topLevel.Id);
                    SetDoubleParam(created, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM, 0);
                }
                else
                {
                    SetIdParam(created, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, level.Id);
                    SetDoubleParam(created, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM, Metric.MmToFeet(heightMm));
                }

                if (Math.Abs(rotationDegrees) > 1e-9)
                {
                    Line axis = Line.CreateBound(insertion, insertion + XYZ.BasisZ);
                    ElementTransformUtils.RotateElement(doc, created.Id, axis, Metric.DegToRad(rotationDegrees));
                }
                return created;
            });

            return J.O(
                "created", Describe(column),
                "type", symbol.Name,
                "family", symbol.Family != null ? symbol.Family.Name : null,
                "baseLevel", level.Name,
                "topLevel", topLevel != null ? topLevel.Name : level.Name,
                "heightMm", topLevel != null
                    ? Metric.R(Metric.FeetToMm(topLevel.Elevation - level.Elevation))
                    : Metric.R(heightMm),
                "structural", structural);
        }

        // ==================================================================
        //  revit_create_beam
        // ==================================================================
        public static JsonValue CreateBeam(ToolContext ctx)
        {
            Document doc = ctx.Doc;

            XYZ start = Args.PointMm(ctx.Args, "start");
            XYZ end = Args.PointMm(ctx.Args, "end");
            Level level = Selectors.FindLevel(doc, Args.Str(ctx.Args, "levelName", true));

            double lengthMm = Metric.FeetToMm(start.DistanceTo(end));
            if (lengthMm < 1.0)
                throw ToolException.Invalid("end", "the start and end points are " +
                    Metric.R(lengthMm, 3) + " mm apart. A beam needs a measurable length.");

            FamilySymbol symbol = Selectors.FindFamilySymbol(doc, BuiltInCategory.OST_StructuralFraming,
                Args.Str(ctx.Args, "typeName"), Args.Str(ctx.Args, "familyName"));

            FamilyInstance beam = ctx.InTransaction("Create beam", delegate
            {
                Activate(doc, symbol);
                Line line = Line.CreateBound(start, end);
                return doc.Create.NewFamilyInstance(line, symbol, level, StructuralType.Beam);
            });

            return J.O(
                "created", Describe(beam),
                "type", symbol.Name,
                "family", symbol.Family != null ? symbol.Family.Name : null,
                "referenceLevel", level.Name,
                "lengthMm", Metric.R(lengthMm));
        }

        // ==================================================================
        //  revit_create_floor
        // ==================================================================
        public static JsonValue CreateFloor(ToolContext ctx)
        {
            Document doc = ctx.Doc;

            List<XYZ> points = Args.PointListMm(ctx.Args, "boundary", 3, 500);
            Level level = Selectors.FindLevel(doc, Args.Str(ctx.Args, "levelName", true));
            double heightOffsetMm = Args.Num(ctx.Args, "heightOffset", false, 0);
            bool structural = Args.Bool(ctx.Args, "structural", false);

            FloorType floorType = Selectors.FindElementType<FloorType>(doc, Args.Str(ctx.Args, "floorTypeName"), true)
                                  ?? Selectors.DefaultType<FloorType>(doc, ElementTypeGroup.FloorType);
            if (floorType == null)
                throw new ToolException(BridgeErrorCodes.NotFound, "This model contains no floor types.");

            // Flatten to the level and close the loop, so the caller does not have to.
            double z = level.Elevation;
            var flat = new List<XYZ>();
            for (int i = 0; i < points.Count; i++) flat.Add(new XYZ(points[i].X, points[i].Y, z));
            if (flat[0].DistanceTo(flat[flat.Count - 1]) > 1e-6) flat.Add(flat[0]);

            var curves = new List<Curve>();
            for (int i = 0; i < flat.Count - 1; i++)
            {
                if (flat[i].DistanceTo(flat[i + 1]) < 1e-6)
                    throw ToolException.Invalid("boundary",
                        "points " + i + " and " + (i + 1) + " are coincident, so the boundary is not a valid loop.");
                curves.Add(Line.CreateBound(flat[i], flat[i + 1]));
            }

            Element floor = ctx.InTransaction("Create floor", delegate
            {
                Element created = Compat.CreateFloor(doc, curves, floorType, level, structural);
                if (created != null && Math.Abs(heightOffsetMm) > 1e-9)
                    SetDoubleParam(created, BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM, Metric.MmToFeet(heightOffsetMm));
                return created;
            });

            if (floor == null)
                throw new ToolException(BridgeErrorCodes.RevitApi, "Revit did not create a floor from that boundary.");

            JsonValue result = J.O(
                "created", Describe(floor),
                "floorType", floorType.Name,
                "level", level.Name,
                "boundaryPoints", flat.Count - 1,
                "structural", structural);

            try
            {
                Parameter area = floor.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                if (area != null && area.HasValue)
                    result.Set("areaM2", Metric.R(Metric.SqFeetToSqM(area.AsDouble()), 3));
            }
            catch (Exception) { }

            return result;
        }

        // ==================================================================
        //  revit_place_family_instance
        // ==================================================================
        public static JsonValue PlaceFamilyInstance(ToolContext ctx)
        {
            Document doc = ctx.Doc;

            XYZ point = Args.PointMm(ctx.Args, "point");
            string typeName = Args.Str(ctx.Args, "typeName", true);
            string familyName = Args.Str(ctx.Args, "familyName");
            string categoryName = Args.Str(ctx.Args, "category");
            string levelName = Args.Str(ctx.Args, "levelName");
            long hostId = Args.Long(ctx.Args, "hostElementId", false, 0);
            double rotationDegrees = Args.Num(ctx.Args, "rotationDegrees", false, 0, -360, 360);

            BuiltInCategory category = string.IsNullOrEmpty(categoryName)
                ? BuiltInCategory.INVALID
                : Selectors.ResolveCategory(doc, categoryName);

            FamilySymbol symbol = Selectors.FindFamilySymbol(doc, category, typeName, familyName);

            Level level = !string.IsNullOrEmpty(levelName)
                ? Selectors.FindLevel(doc, levelName)
                : Selectors.NearestLevelBelow(doc, point.Z);
            if (level == null)
                throw new ToolException(BridgeErrorCodes.NotFound, "This model has no levels to host the instance.");

            Element host = null;
            if (hostId > 0)
            {
                host = doc.GetElement(Compat.ToId(hostId));
                if (host == null)
                    throw ToolException.NotFound("Host element", hostId.ToString(CultureInfo.InvariantCulture));
            }

            FamilyInstance instance = ctx.InTransaction("Place family instance", delegate
            {
                Activate(doc, symbol);
                FamilyInstance created = host != null
                    ? doc.Create.NewFamilyInstance(point, symbol, host, level, StructuralType.NonStructural)
                    : doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural);

                if (Math.Abs(rotationDegrees) > 1e-9)
                {
                    doc.Regenerate();
                    Line axis = Line.CreateBound(point, point + XYZ.BasisZ);
                    ElementTransformUtils.RotateElement(doc, created.Id, axis, Metric.DegToRad(rotationDegrees));
                }
                return created;
            });

            return J.O(
                "created", Describe(instance),
                "type", symbol.Name,
                "family", symbol.Family != null ? symbol.Family.Name : null,
                "level", level.Name,
                "hostId", host != null ? (object)Compat.IdValue(host.Id) : null,
                "rotationDegrees", Metric.R(rotationDegrees, 3));
        }

        // ==================================================================
        //  revit_create_door / revit_create_window
        // ==================================================================
        public static JsonValue CreateDoor(ToolContext ctx)
        {
            return CreateWallOpening(ctx, BuiltInCategory.OST_Doors, "door", false);
        }

        public static JsonValue CreateWindow(ToolContext ctx)
        {
            return CreateWallOpening(ctx, BuiltInCategory.OST_Windows, "window", true);
        }

        private static JsonValue CreateWallOpening(ToolContext ctx, BuiltInCategory category,
                                                   string label, bool supportsSillHeight)
        {
            Document doc = ctx.Doc;

            long hostWallId = Args.Long(ctx.Args, "hostWallId", true);
            XYZ requested = Args.PointMm(ctx.Args, "point");

            Wall wall = doc.GetElement(Compat.ToId(hostWallId)) as Wall;
            if (wall == null)
                throw new ToolException(BridgeErrorCodes.NotFound,
                    "Element " + hostWallId + " is not a wall. A " + label + " must be hosted by a wall. " +
                    "Use revit_query_elements with category \"Walls\" to find one.");

            LocationCurve locationCurve = wall.Location as LocationCurve;
            if (locationCurve == null || locationCurve.Curve == null)
                throw new ToolException(BridgeErrorCodes.RevitApi, "The host wall has no location curve.");

            // Project the requested point onto the wall centreline so the caller can be approximate.
            XYZ onWall;
            try
            {
                IntersectionResult projection = locationCurve.Curve.Project(requested);
                onWall = projection != null ? projection.XYZPoint : requested;
            }
            catch (Exception) { onWall = requested; }

            Level level = !string.IsNullOrEmpty(Args.Str(ctx.Args, "levelName"))
                ? Selectors.FindLevel(doc, Args.Str(ctx.Args, "levelName"))
                : doc.GetElement(wall.LevelId) as Level;
            if (level == null) level = Selectors.NearestLevelBelow(doc, onWall.Z);
            if (level == null)
                throw new ToolException(BridgeErrorCodes.NotFound, "Could not determine a level for the " + label + ".");

            FamilySymbol symbol = Selectors.FindFamilySymbol(doc, category,
                Args.Str(ctx.Args, "typeName"), Args.Str(ctx.Args, "familyName"));

            double sillHeightMm = supportsSillHeight
                ? Args.Num(ctx.Args, "sillHeight", false, double.NaN, 0, 100000)
                : double.NaN;

            XYZ insertion = new XYZ(onWall.X, onWall.Y, level.Elevation);

            FamilyInstance opening = ctx.InTransaction("Create " + label, delegate
            {
                Activate(doc, symbol);
                FamilyInstance created = doc.Create.NewFamilyInstance(
                    insertion, symbol, wall, level, StructuralType.NonStructural);

                if (!double.IsNaN(sillHeightMm))
                {
                    doc.Regenerate();
                    if (!SetDoubleParam(created, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM, Metric.MmToFeet(sillHeightMm)))
                        ctx.AddWarning("This " + label + " type does not expose a Sill Height parameter; " +
                                       "the type default was kept.");
                }
                return created;
            });

            JsonValue result = J.O(
                "created", Describe(opening),
                "type", symbol.Name,
                "family", symbol.Family != null ? symbol.Family.Name : null,
                "hostWallId", Compat.IdValue(wall.Id),
                "level", level.Name,
                "placedAt", Metric.PointToMm(insertion));

            double offsetMm = Metric.FeetToMm(requested.DistanceTo(onWall));
            if (offsetMm > 1.0)
                result.Set("snappedFromRequestedPointMm", Metric.R(offsetMm));

            return result;
        }

        // ==================================================================
        //  revit_create_level
        // ==================================================================
        public static JsonValue CreateLevel(ToolContext ctx)
        {
            Document doc = ctx.Doc;

            double elevationMm = Args.Num(ctx.Args, "elevation", true);
            string name = Args.Str(ctx.Args, "name", true);
            bool createPlan = Args.Bool(ctx.Args, "createFloorPlan", true);

            if (Selectors.FindLevel(doc, name, false) != null)
                throw ToolException.Invalid("name", "a level named '" + name + "' already exists.");

            string createdPlanName = null;

            Level level = ctx.InTransaction("Create level", delegate
            {
                Level created = Level.Create(doc, Metric.MmToFeet(elevationMm));
                if (created == null) return null;

                try { created.Name = name; }
                catch (Exception ex)
                {
                    ctx.AddWarning("The level was created but could not be named '" + name + "': " + ex.Message);
                }

                if (createPlan)
                {
                    ViewFamilyType planType = Selectors.FindViewFamilyType(doc, ViewFamily.FloorPlan);
                    if (planType != null)
                    {
                        try
                        {
                            ViewPlan plan = ViewPlan.Create(doc, planType.Id, created.Id);
                            createdPlanName = plan.Name;
                        }
                        catch (Exception ex)
                        {
                            ctx.AddWarning("The level was created but its floor plan was not: " + ex.Message);
                        }
                    }
                    else
                    {
                        ctx.AddWarning("No floor plan view family type exists in this model, so no plan was created.");
                    }
                }
                return created;
            });

            if (level == null)
                throw new ToolException(BridgeErrorCodes.RevitApi, "Revit did not create the level.");

            return J.O(
                "created", Describe(level),
                "elevationMm", Metric.R(elevationMm),
                "floorPlanCreated", createdPlanName != null,
                "floorPlanName", createdPlanName);
        }

        // ==================================================================
        //  revit_create_grid
        // ==================================================================
        public static JsonValue CreateGrid(ToolContext ctx)
        {
            Document doc = ctx.Doc;

            XYZ start = Args.PointMm(ctx.Args, "start");
            XYZ end = Args.PointMm(ctx.Args, "end");
            string name = Args.Str(ctx.Args, "name", true);

            // Grids are 2D annotation: force both ends to the same elevation.
            XYZ flatStart = new XYZ(start.X, start.Y, 0);
            XYZ flatEnd = new XYZ(end.X, end.Y, 0);

            double lengthMm = Metric.FeetToMm(flatStart.DistanceTo(flatEnd));
            if (lengthMm < 1.0)
                throw ToolException.Invalid("end", "the grid endpoints are only " +
                    Metric.R(lengthMm, 3) + " mm apart.");

            Grid grid = ctx.InTransaction("Create grid", delegate
            {
                Grid created = Grid.Create(doc, Line.CreateBound(flatStart, flatEnd));
                if (created == null) return null;
                try { created.Name = name; }
                catch (Exception ex)
                {
                    ctx.AddWarning("The grid was created but could not be named '" + name +
                                   "' (the name may already be in use): " + ex.Message);
                }
                return created;
            });

            if (grid == null)
                throw new ToolException(BridgeErrorCodes.RevitApi, "Revit did not create the grid.");

            return J.O("created", Describe(grid), "lengthMm", Metric.R(lengthMm));
        }

        // ==================================================================
        //  revit_create_room
        // ==================================================================
        public static JsonValue CreateRoom(ToolContext ctx)
        {
            Document doc = ctx.Doc;

            XYZ point = Args.PointMm(ctx.Args, "point");
            Level level = Selectors.FindLevel(doc, Args.Str(ctx.Args, "levelName", true));
            string name = Args.Str(ctx.Args, "name");
            string number = Args.Str(ctx.Args, "number");
            string department = Args.Str(ctx.Args, "department");

            Room room = ctx.InTransaction("Create room", delegate
            {
                Room created = doc.Create.NewRoom(level, new UV(point.X, point.Y));
                if (created == null) return null;

                if (!string.IsNullOrEmpty(name))
                {
                    try { created.Name = name; }
                    catch (Exception ex) { ctx.AddWarning("Could not set the room name: " + ex.Message); }
                }
                if (!string.IsNullOrEmpty(number))
                {
                    try { created.Number = number; }
                    catch (Exception ex) { ctx.AddWarning("Could not set the room number: " + ex.Message); }
                }
                if (!string.IsNullOrEmpty(department))
                    SetStringParam(created, BuiltInParameter.ROOM_DEPARTMENT, department);

                doc.Regenerate();
                return created;
            });

            if (room == null)
                throw new ToolException(BridgeErrorCodes.RevitApi,
                    "Revit did not create a room at that point. The point must lie inside a closed, " +
                    "room-bounding area on that level.");

            double areaSqM = 0;
            try { areaSqM = Metric.SqFeetToSqM(room.Area); } catch (Exception) { }

            JsonValue result = J.O(
                "created", Describe(room),
                "level", level.Name,
                "areaM2", Metric.R(areaSqM, 3),
                "number", SafeRoomNumber(room),
                "bounded", areaSqM > 1e-9);

            if (areaSqM <= 1e-9)
                ctx.AddWarning("The room was created but is UNBOUNDED (zero area) - the point is not " +
                               "inside a closed loop of room-bounding elements.");

            return result;
        }

        private static string SafeRoomNumber(Room room)
        {
            try { return room.Number; } catch (Exception) { return null; }
        }

        // ==================================================================
        //  shared helpers
        // ==================================================================

        /// <summary>A family symbol must be activated before its first instance can be placed.</summary>
        internal static void Activate(Document doc, FamilySymbol symbol)
        {
            if (symbol == null) return;
            if (symbol.IsActive) return;
            symbol.Activate();
            doc.Regenerate();
        }

        internal static JsonValue Describe(Element element)
        {
            if (element == null) return JsonValue.Null;
            return ElementSerializer.Summarize(element, new SerializeOptions { IncludeLocation = true });
        }

        internal static bool SetDoubleParam(Element element, BuiltInParameter bip, double internalValue)
        {
            try
            {
                Parameter p = element.get_Parameter(bip);
                if (p == null || p.IsReadOnly) return false;
                return p.Set(internalValue);
            }
            catch (Exception) { return false; }
        }

        internal static bool SetIdParam(Element element, BuiltInParameter bip, ElementId value)
        {
            try
            {
                Parameter p = element.get_Parameter(bip);
                if (p == null || p.IsReadOnly) return false;
                return p.Set(value);
            }
            catch (Exception) { return false; }
        }

        internal static bool SetStringParam(Element element, BuiltInParameter bip, string value)
        {
            try
            {
                Parameter p = element.get_Parameter(bip);
                if (p == null || p.IsReadOnly) return false;
                return p.Set(value);
            }
            catch (Exception) { return false; }
        }
    }
}
