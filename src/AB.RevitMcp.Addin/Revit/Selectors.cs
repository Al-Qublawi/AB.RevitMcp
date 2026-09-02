using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace AB.RevitMcp.Addin.Revit
{
    /// <summary>
    /// Name -> Revit object lookups. Everything an AI client refers to by name (a category, a
    /// level, a wall type, a material) is resolved here, with case-insensitive matching and error
    /// messages that list near-misses so the model can self-correct on the next call.
    /// </summary>
    public static class Selectors
    {
        // ---------------- categories ----------------

        /// <summary>
        /// Resolves "Walls", "walls", "OST_Walls" or "Structural Columns" to a BuiltInCategory.
        /// Throws a NOT_FOUND ToolException listing similar names when nothing matches.
        /// </summary>
        public static BuiltInCategory ResolveCategory(Document doc, string name)
        {
            if (string.IsNullOrEmpty(name)) return BuiltInCategory.INVALID;

            string trimmed = name.Trim();

            // 1. BuiltInCategory enum name, e.g. "OST_Walls".
            if (trimmed.StartsWith("OST_", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string enumName in Enum.GetNames(typeof(BuiltInCategory)))
                {
                    if (string.Equals(enumName, trimmed, StringComparison.OrdinalIgnoreCase))
                        return (BuiltInCategory)Enum.Parse(typeof(BuiltInCategory), enumName);
                }
            }

            // 2. Display name from the document's category table.
            foreach (Category c in doc.Settings.Categories)
            {
                if (c == null) continue;
                if (string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    BuiltInCategory bic = Compat.GetBuiltInCategory(c);
                    if (bic != BuiltInCategory.INVALID) return bic;
                }
            }

            // 3. "Structural Columns" -> OST_StructuralColumns
            string collapsed = "OST_" + trimmed.Replace(" ", string.Empty).Replace("-", string.Empty);
            foreach (string enumName in Enum.GetNames(typeof(BuiltInCategory)))
            {
                if (string.Equals(enumName, collapsed, StringComparison.OrdinalIgnoreCase))
                    return (BuiltInCategory)Enum.Parse(typeof(BuiltInCategory), enumName);
            }

            throw ToolException.NotFound("Category", name)
                .WithSuggestions(SimilarCategoryNames(doc, trimmed));
        }

        private static List<string> SimilarCategoryNames(Document doc, string needle)
        {
            var hits = new List<string>();
            string lower = needle.ToLowerInvariant();
            foreach (Category c in doc.Settings.Categories)
            {
                if (c == null || string.IsNullOrEmpty(c.Name)) continue;
                string cl = c.Name.ToLowerInvariant();
                if (cl.Contains(lower) || lower.Contains(cl)) hits.Add(c.Name);
                if (hits.Count >= 10) break;
            }
            return hits;
        }

        public static string CategoryName(Element element)
        {
            if (element == null) return null;
            Category c = null;
            try { c = element.Category; } catch (Exception) { }
            return c == null ? null : c.Name;
        }

        // ---------------- collectors ----------------

        public static FilteredElementCollector Collector(Document doc, ElementId viewId = null)
        {
            return (viewId != null && Compat.IsValid(viewId))
                ? new FilteredElementCollector(doc, viewId)
                : new FilteredElementCollector(doc);
        }

        // ---------------- levels ----------------

        public static List<Level> AllLevels(Document doc)
        {
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .ToList();
            levels.Sort(delegate (Level a, Level b) { return a.Elevation.CompareTo(b.Elevation); });
            return levels;
        }

        public static Level FindLevel(Document doc, string name, bool required = true)
        {
            if (string.IsNullOrEmpty(name))
            {
                if (!required) return null;
                throw ToolException.Missing("levelName");
            }

            List<Level> levels = AllLevels(doc);
            for (int i = 0; i < levels.Count; i++)
                if (string.Equals(levels[i].Name, name, StringComparison.OrdinalIgnoreCase)) return levels[i];

            if (!required) return null;

            var names = new List<string>();
            for (int i = 0; i < levels.Count && i < 20; i++) names.Add(levels[i].Name);
            throw ToolException.NotFound("Level", name).WithSuggestions(names);
        }

        /// <summary>Highest level at or below the given internal elevation; falls back to the lowest level.</summary>
        public static Level NearestLevelBelow(Document doc, double elevationFeet)
        {
            List<Level> levels = AllLevels(doc);
            if (levels.Count == 0) return null;
            Level best = levels[0];
            for (int i = 0; i < levels.Count; i++)
                if (levels[i].Elevation <= elevationFeet + 1e-6) best = levels[i];
            return best;
        }

        // ---------------- element types ----------------

        /// <summary>Finds a loadable-family symbol by type name, optionally disambiguated by family name.</summary>
        public static FamilySymbol FindFamilySymbol(Document doc, BuiltInCategory category,
                                                    string typeName, string familyName, bool required = true)
        {
            var collector = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol));
            if (category != BuiltInCategory.INVALID) collector = collector.OfCategory(category);

            List<FamilySymbol> symbols = collector.Cast<FamilySymbol>().ToList();
            if (symbols.Count == 0)
            {
                if (!required) return null;
                throw new ToolException(Contracts.Protocol.BridgeErrorCodes.NotFound,
                    "No loadable family types are loaded for category '" + category + "'. " +
                    "Load a family into the project first.");
            }

            List<FamilySymbol> candidates = symbols;
            if (!string.IsNullOrEmpty(familyName))
            {
                candidates = candidates.Where(s => s.Family != null &&
                    string.Equals(s.Family.Name, familyName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (candidates.Count == 0)
                {
                    if (!required) return null;
                    throw ToolException.NotFound("Family", familyName)
                        .WithSuggestions(symbols.Where(s => s.Family != null)
                                                .Select(s => s.Family.Name).Distinct().Take(15).ToList());
                }
            }

            if (!string.IsNullOrEmpty(typeName))
            {
                FamilySymbol exact = candidates.FirstOrDefault(
                    s => string.Equals(s.Name, typeName, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;

                if (!required) return null;
                throw ToolException.NotFound("Family type", typeName)
                    .WithSuggestions(candidates.Select(s =>
                        (s.Family != null ? s.Family.Name + " : " : string.Empty) + s.Name).Take(15).ToList());
            }

            return candidates[0]; // caller asked for "any type of this category"
        }

        /// <summary>Finds a system-family type (WallType, FloorType, ...) by name, or the default.</summary>
        public static T FindElementType<T>(Document doc, string typeName, bool required) where T : ElementType
        {
            List<T> types = new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().ToList();
            if (types.Count == 0)
            {
                if (!required) return null;
                throw new ToolException(Contracts.Protocol.BridgeErrorCodes.NotFound,
                    "This model contains no " + typeof(T).Name + " definitions.");
            }

            if (string.IsNullOrEmpty(typeName)) return null; // caller will use the document default

            T match = types.FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;

            if (!required) return null;
            throw ToolException.NotFound(typeof(T).Name, typeName)
                .WithSuggestions(types.Select(t => t.Name).Take(20).ToList());
        }

        /// <summary>The document's default type for a family kind, used when the caller omits a type name.</summary>
        public static T DefaultType<T>(Document doc, ElementTypeGroup group) where T : ElementType
        {
            try
            {
                ElementId id = doc.GetDefaultElementTypeId(group);
                if (Compat.IsValid(id)) return doc.GetElement(id) as T;
            }
            catch (Exception) { }
            return new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().FirstOrDefault();
        }

        // ---------------- materials, worksets, views ----------------

        public static Material FindMaterial(Document doc, string name, bool required = true)
        {
            if (string.IsNullOrEmpty(name))
            {
                if (!required) return null;
                throw ToolException.Missing("materialName");
            }

            List<Material> materials = new FilteredElementCollector(doc)
                .OfClass(typeof(Material)).Cast<Material>().ToList();

            Material match = materials.FirstOrDefault(
                m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;

            if (!required) return null;
            string lower = name.ToLowerInvariant();
            throw ToolException.NotFound("Material", name)
                .WithSuggestions(materials.Where(m => m.Name != null && m.Name.ToLowerInvariant().Contains(lower))
                                          .Select(m => m.Name).Take(15).ToList());
        }

        public static Workset FindWorkset(Document doc, string name, bool required = true)
        {
            if (!doc.IsWorkshared)
                throw new ToolException(Contracts.Protocol.BridgeErrorCodes.InvalidArgument,
                    "This model is not workshared, so it has no user worksets.");

            List<Workset> worksets = new FilteredWorksetCollector(doc)
                .OfKind(WorksetKind.UserWorkset).ToWorksets().ToList();

            Workset match = worksets.FirstOrDefault(
                w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;

            if (!required) return null;
            throw ToolException.NotFound("Workset", name)
                .WithSuggestions(worksets.Select(w => w.Name).Take(30).ToList());
        }

        public static View FindView(Document doc, string name, bool required = true)
        {
            if (string.IsNullOrEmpty(name))
            {
                if (!required) return null;
                throw ToolException.Missing("viewName");
            }

            List<View> views = new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();

            View match = views.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;

            if (!required) return null;
            string lower = name.ToLowerInvariant();
            throw ToolException.NotFound("View", name)
                .WithSuggestions(views.Where(v => v.Name != null && v.Name.ToLowerInvariant().Contains(lower))
                                      .Select(v => v.Name).Take(15).ToList());
        }

        public static View FindViewTemplate(Document doc, string name, bool required = true)
        {
            if (string.IsNullOrEmpty(name)) return null;

            List<View> templates = new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).ToList();

            View match = templates.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;

            if (!required) return null;
            throw ToolException.NotFound("View template", name)
                .WithSuggestions(templates.Select(v => v.Name).Take(20).ToList());
        }

        public static ViewSheet FindSheet(Document doc, string sheetNumber, bool required = true)
        {
            if (string.IsNullOrEmpty(sheetNumber))
            {
                if (!required) return null;
                throw ToolException.Missing("sheetNumber");
            }

            List<ViewSheet> sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();

            ViewSheet match = sheets.FirstOrDefault(
                s => string.Equals(s.SheetNumber, sheetNumber, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;

            if (!required) return null;
            throw ToolException.NotFound("Sheet", sheetNumber)
                .WithSuggestions(sheets.Select(s => s.SheetNumber).Take(30).ToList());
        }

        public static ViewFamilyType FindViewFamilyType(Document doc, ViewFamily family)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == family);
        }

        /// <summary>Finds a parameter on an element by name, instance first then type.</summary>
        public static Parameter FindParameter(Element element, string name, bool searchType = true)
        {
            if (element == null || string.IsNullOrEmpty(name)) return null;

            Parameter p = null;
            try { p = element.LookupParameter(name); } catch (Exception) { }
            if (p != null) return p;

            // LookupParameter is case-sensitive; fall back to a case-insensitive scan.
            foreach (Parameter candidate in element.Parameters)
            {
                if (candidate == null || candidate.Definition == null) continue;
                if (string.Equals(candidate.Definition.Name, name, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            if (!searchType) return null;
            try
            {
                Element type = element.Document.GetElement(element.GetTypeId());
                if (type != null) return FindParameter(type, name, false);
            }
            catch (Exception) { }
            return null;
        }
    }
}
