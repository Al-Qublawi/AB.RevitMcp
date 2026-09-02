using System;
using AB.RevitMcp.Contracts.Json;
using Autodesk.Revit.DB;

namespace AB.RevitMcp.Addin.Revit
{
    /// <summary>The metric unit a value is expressed in when it crosses the MCP boundary.</summary>
    public enum MetricUnit
    {
        None = 0,        // unitless number / integer / boolean
        Millimetres,
        SquareMetres,
        CubicMetres,
        Degrees,
        Internal         // measurable, but not one of the four we normalise - raw internal value
    }

    /// <summary>
    /// Unit boundary. Revit stores lengths in decimal feet, areas in square feet, volumes in cubic
    /// feet and angles in radians. NOTHING in those units is allowed to reach an MCP client, and
    /// nothing metric is allowed to reach the Revit API unconverted - every crossing goes through
    /// this class.
    ///
    /// The four conversion factors are exact by definition (1 ft = 0.3048 m), so they are applied
    /// directly rather than through UnitUtils. That also sidesteps the DisplayUnitType ->
    /// ForgeTypeId break in Revit 2021.
    /// </summary>
    public static class Metric
    {
        public const double MmPerFoot = 304.8;
        public const double SqMPerSqFoot = 0.09290304;
        public const double CuMPerCuFoot = 0.028316846592;

        // ---------------- scalars ----------------

        public static double MmToFeet(double mm) { return mm / MmPerFoot; }
        public static double FeetToMm(double feet) { return feet * MmPerFoot; }

        public static double SqFeetToSqM(double sqFeet) { return sqFeet * SqMPerSqFoot; }
        public static double SqMToSqFeet(double sqM) { return sqM / SqMPerSqFoot; }

        public static double CuFeetToCuM(double cuFeet) { return cuFeet * CuMPerCuFoot; }
        public static double CuMToCuFeet(double cuM) { return cuM / CuMPerCuFoot; }

        public static double RadToDeg(double radians) { return radians * (180.0 / Math.PI); }
        public static double DegToRad(double degrees) { return degrees * (Math.PI / 180.0); }

        /// <summary>Rounds for presentation: sub-micrometre noise in a double is not information.</summary>
        public static double R(double value, int decimals = 2)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
            return Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        }

        // ---------------- points and vectors ----------------

        /// <summary>Metric millimetre triple -> Revit internal XYZ (feet).</summary>
        public static XYZ PointFromMm(double xMm, double yMm, double zMm)
        {
            return new XYZ(MmToFeet(xMm), MmToFeet(yMm), MmToFeet(zMm));
        }

        /// <summary>Revit internal XYZ (feet) -> {x,y,z} in millimetres.</summary>
        public static JsonValue PointToMm(XYZ p)
        {
            if (p == null) return JsonValue.Null;
            return J.O("x", R(FeetToMm(p.X)), "y", R(FeetToMm(p.Y)), "z", R(FeetToMm(p.Z)));
        }

        /// <summary>Bounding box in millimetres, plus its size, for AI-friendly reasoning.</summary>
        public static JsonValue BoundingBoxToMm(BoundingBoxXYZ bb)
        {
            if (bb == null) return JsonValue.Null;
            XYZ min = bb.Min, max = bb.Max;
            return J.O(
                "min", PointToMm(min),
                "max", PointToMm(max),
                "sizeMm", J.O(
                    "x", R(FeetToMm(max.X - min.X)),
                    "y", R(FeetToMm(max.Y - min.Y)),
                    "z", R(FeetToMm(max.Z - min.Z))));
        }

        // ---------------- parameter-aware conversion ----------------

        /// <summary>Classifies a parameter's data type into the metric unit we report it in.</summary>
        public static MetricUnit UnitOf(Definition definition)
        {
            if (definition == null) return MetricUnit.None;

            try
            {
#if REVIT2022_OR_GREATER
                ForgeTypeId spec = definition.GetDataType();
                return FromSpecId(spec);
#elif REVIT2021_OR_GREATER
                ForgeTypeId spec = definition.GetSpecTypeId();
                return FromSpecId(spec);
#else
                switch (definition.ParameterType)
                {
                    case ParameterType.Length: return MetricUnit.Millimetres;
                    case ParameterType.Area: return MetricUnit.SquareMetres;
                    case ParameterType.Volume: return MetricUnit.CubicMetres;
                    case ParameterType.Angle: return MetricUnit.Degrees;
                    case ParameterType.Number:
                    case ParameterType.Integer:
                    case ParameterType.YesNo:
                    case ParameterType.Text:
                    case ParameterType.Material:
                    case ParameterType.Invalid:
                        return MetricUnit.None;
                    default:
                        return MetricUnit.Internal;
                }
#endif
            }
            catch (Exception)
            {
                return MetricUnit.None;
            }
        }

#if REVIT2021_OR_GREATER
        private static MetricUnit FromSpecId(ForgeTypeId spec)
        {
            if (spec == null || string.IsNullOrEmpty(spec.TypeId)) return MetricUnit.None;

            // Compare by TypeId string: ForgeTypeId equality semantics have shifted between
            // releases, the identifier string has not.
            string id = spec.TypeId;
            if (id == SpecTypeId.Length.TypeId) return MetricUnit.Millimetres;
            if (id == SpecTypeId.Area.TypeId) return MetricUnit.SquareMetres;
            if (id == SpecTypeId.Volume.TypeId) return MetricUnit.CubicMetres;
            if (id == SpecTypeId.Angle.TypeId) return MetricUnit.Degrees;
            if (id == SpecTypeId.Number.TypeId) return MetricUnit.None;
#if REVIT2022_OR_GREATER
            // Non-measurable specs only exist from 2022, when GetDataType() replaced GetSpecTypeId().
            if (id == SpecTypeId.Int.Integer.TypeId) return MetricUnit.None;
            if (id == SpecTypeId.Boolean.YesNo.TypeId) return MetricUnit.None;
            if (id == SpecTypeId.String.Text.TypeId) return MetricUnit.None;
            if (id == SpecTypeId.Reference.Material.TypeId) return MetricUnit.None;
#endif
            return MetricUnit.Internal;
        }
#endif

        /// <summary>
        /// True when an integer-storage parameter is really a Yes/No checkbox, so it can be
        /// reported as a JSON boolean instead of 0/1.
        /// </summary>
        public static bool IsYesNo(Definition definition)
        {
            if (definition == null) return false;
            try
            {
#if REVIT2022_OR_GREATER
                ForgeTypeId spec = definition.GetDataType();
                return spec != null && spec.TypeId == SpecTypeId.Boolean.YesNo.TypeId;
#else
                // ParameterType exists in Revit 2020 and 2021 (deprecated in 2021, removed in 2023).
                return definition.ParameterType == ParameterType.YesNo;
#endif
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Revit internal value -> the metric value we publish.</summary>
        public static double FromInternal(double internalValue, MetricUnit unit)
        {
            switch (unit)
            {
                case MetricUnit.Millimetres: return FeetToMm(internalValue);
                case MetricUnit.SquareMetres: return SqFeetToSqM(internalValue);
                case MetricUnit.CubicMetres: return CuFeetToCuM(internalValue);
                case MetricUnit.Degrees: return RadToDeg(internalValue);
                default: return internalValue;
            }
        }

        /// <summary>Metric value supplied by a client -> the Revit internal value.</summary>
        public static double ToInternal(double metricValue, MetricUnit unit)
        {
            switch (unit)
            {
                case MetricUnit.Millimetres: return MmToFeet(metricValue);
                case MetricUnit.SquareMetres: return SqMToSqFeet(metricValue);
                case MetricUnit.CubicMetres: return CuMToCuFeet(metricValue);
                case MetricUnit.Degrees: return DegToRad(metricValue);
                default: return metricValue;
            }
        }

        /// <summary>Short label used in responses so the AI never has to guess the unit.</summary>
        public static string Label(MetricUnit unit)
        {
            switch (unit)
            {
                case MetricUnit.Millimetres: return "mm";
                case MetricUnit.SquareMetres: return "m2";
                case MetricUnit.CubicMetres: return "m3";
                case MetricUnit.Degrees: return "deg";
                case MetricUnit.Internal: return "revit-internal";
                default: return null;
            }
        }

        /// <summary>The unit contract advertised in revit_get_model_info.</summary>
        public static JsonValue Convention()
        {
            return J.O(
                "length", "mm",
                "area", "m2",
                "volume", "m3",
                "angle", "degrees",
                "coordinates", "project coordinates, millimetres",
                "note", "All values in and out of this server are metric. Revit's internal " +
                        "decimal feet are converted at the boundary.");
        }
    }
}
