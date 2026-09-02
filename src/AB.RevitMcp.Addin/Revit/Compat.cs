using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace AB.RevitMcp.Addin.Revit
{
    /// <summary>
    /// Every Revit API breaking change between 2020 and 2027 is isolated here. Tool code calls
    /// these helpers and stays version-agnostic.
    /// </summary>
    public static class Compat
    {
        // ------------------------------------------------------------------
        //  ElementId: int -> long (Revit 2024)
        // ------------------------------------------------------------------

        /// <summary>Numeric value of an element id, as a long on every supported release.</summary>
        public static long IdValue(ElementId id)
        {
            if (id == null) return -1;
#if REVIT2024_OR_GREATER
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }

        /// <summary>Constructs an ElementId from a 64-bit value supplied by a client.</summary>
        public static ElementId ToId(long value)
        {
#if REVIT2024_OR_GREATER
            return new ElementId(value);
#else
            if (value > int.MaxValue || value < int.MinValue)
                throw new ToolException(Contracts.Protocol.BridgeErrorCodes.InvalidArgument,
                    "Element id " + value + " is out of range for Revit " + RevitReleaseName + ".");
            return new ElementId((int)value);
#endif
        }

        public static ElementId InvalidId { get { return ElementId.InvalidElementId; } }

        public static bool IsValid(ElementId id)
        {
            return id != null && id != ElementId.InvalidElementId && IdValue(id) > 0;
        }

        // ------------------------------------------------------------------
        //  Category
        // ------------------------------------------------------------------

        /// <summary>BuiltInCategory of a category, or OST_Invalid for a custom subcategory.</summary>
        public static BuiltInCategory GetBuiltInCategory(Category category)
        {
            if (category == null) return BuiltInCategory.INVALID;
#if REVIT2023_OR_GREATER
            try { return category.BuiltInCategory; }
            catch (Exception) { return BuiltInCategory.INVALID; }
#else
            try
            {
                int raw = category.Id.IntegerValue;
                if (Enum.IsDefined(typeof(BuiltInCategory), raw)) return (BuiltInCategory)raw;
                return BuiltInCategory.INVALID;
            }
            catch (Exception) { return BuiltInCategory.INVALID; }
#endif
        }

        // ------------------------------------------------------------------
        //  Purge
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns the ids Revit considers unused - the same set that Manage &gt; Purge Unused offers.
        /// Revit 2024 added a first-class API; earlier releases only expose it through the
        /// PerformanceAdviser rule that backs the purge dialog.
        /// </summary>
        public static ICollection<ElementId> GetPurgeableElements(Document doc)
        {
            if (doc == null) return new List<ElementId>();

#if REVIT2024_OR_GREATER
            try
            {
                return doc.GetUnusedElements(new HashSet<ElementId>());
            }
            catch (Exception)
            {
                return new List<ElementId>();
            }
#else
            // The purge rule's GUID is stable across every release that lacks GetUnusedElements.
            var purgeGuid = new Guid("e8c63650-70b7-435a-9010-ec97660c1bda");
            var result = new List<ElementId>();
            try
            {
                PerformanceAdviser adviser = PerformanceAdviser.GetPerformanceAdviser();
                IList<PerformanceAdviserRuleId> allRules = adviser.GetAllRuleIds();
                var purgeRules = new List<PerformanceAdviserRuleId>();
                foreach (PerformanceAdviserRuleId ruleId in allRules)
                {
                    if (ruleId.Guid == purgeGuid) purgeRules.Add(ruleId);
                }
                if (purgeRules.Count == 0) return result;

                IList<FailureMessage> failures = adviser.ExecuteRules(doc, purgeRules);
                for (int i = 0; i < failures.Count; i++)
                {
                    foreach (ElementId id in failures[i].GetFailingElements()) result.Add(id);
                }
            }
            catch (Exception)
            {
                // Nothing purgeable, or the adviser refused - report an empty set rather than throw.
            }
            return result;
#endif
        }

        // ------------------------------------------------------------------
        //  Floors
        // ------------------------------------------------------------------

        /// <summary>Creates a floor from a closed loop of curves, using whichever API the release offers.</summary>
        public static Element CreateFloor(Document doc, IList<Curve> boundary, FloorType floorType, Level level, bool structural)
        {
#if REVIT2022_OR_GREATER
            var loop = new CurveLoop();
            for (int i = 0; i < boundary.Count; i++) loop.Append(boundary[i]);
            var loops = new List<CurveLoop> { loop };
            return Floor.Create(doc, loops, floorType.Id, level.Id, structural, null, 0.0);
#else
            var array = new CurveArray();
            for (int i = 0; i < boundary.Count; i++) array.Append(boundary[i]);
            return doc.Create.NewFloor(array, floorType, level, structural);
#endif
        }

        // ------------------------------------------------------------------
        //  Misc
        // ------------------------------------------------------------------

        /// <summary>The Revit release this assembly was compiled against, e.g. "2024".</summary>
        public static string RevitReleaseName
        {
            get
            {
#if REVIT2027
                return "2027";
#elif REVIT2026
                return "2026";
#elif REVIT2025
                return "2025";
#elif REVIT2024
                return "2024";
#elif REVIT2023
                return "2023";
#elif REVIT2022
                return "2022";
#elif REVIT2021
                return "2021";
#elif REVIT2020
                return "2020";
#else
                return "unknown";
#endif
            }
        }

        /// <summary>True when the document can be modified by a transaction right now.</summary>
        public static bool CanModify(Document doc)
        {
            if (doc == null) return false;
            if (doc.IsReadOnly) return false;
            if (doc.IsLinked) return false;
            return true;
        }
    }
}
