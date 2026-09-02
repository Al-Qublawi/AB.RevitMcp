using System;
using System.Collections.Generic;
using System.Linq;
using AB.RevitMcp.Addin.Bridge;
using AB.RevitMcp.Addin.Revit;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;
using Autodesk.Revit.DB;

namespace AB.RevitMcp.Addin.Tools
{
    /// <summary>
    /// DESTRUCTIVE tools.
    ///
    /// The confirm:true interlock is enforced by <see cref="ToolRouter"/> BEFORE any of these run,
    /// so a handler here can assume it was approved. Each one also supports dryRun, which executes
    /// the real operation and then rolls the transaction group back - giving the caller an exact
    /// blast radius instead of an estimate.
    /// </summary>
    public static class DestructiveTools
    {
        // ==================================================================
        //  revit_delete_elements
        // ==================================================================
        public static JsonValue DeleteElements(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            List<long> ids = Args.IdValues(ctx.Args, "elementIds", true, 5000);
            bool dryRun = Args.Bool(ctx.Args, "dryRun", false);

            List<long> missing;
            List<Element> elements = Args.Elements(doc, ids, out missing);
            if (elements.Count == 0)
                throw new ToolException(BridgeErrorCodes.NotFound,
                    "None of the supplied element ids exist in this model. Nothing was deleted.");

            // Capture identity BEFORE deletion - afterwards the elements are gone.
            JsonValue requested = JsonValue.NewArray();
            var toDelete = new List<ElementId>();
            for (int i = 0; i < elements.Count; i++)
            {
                requested.Add(J.O(
                    "id", Compat.IdValue(elements[i].Id),
                    "name", ElementSerializer.SafeName(elements[i]),
                    "category", Selectors.CategoryName(elements[i])));
                toDelete.Add(elements[i].Id);
            }

            var actuallyDeleted = new List<long>();

            ctx.InTransaction("Delete elements", delegate
            {
                ICollection<ElementId> deleted = doc.Delete(toDelete);
                foreach (ElementId id in deleted) actuallyDeleted.Add(Compat.IdValue(id));
            });

            if (dryRun) ctx.RequestRollback();

            // Revit cascades: deleting a wall also deletes its hosted doors and windows.
            var requestedIds = new HashSet<long>();
            for (int i = 0; i < toDelete.Count; i++) requestedIds.Add(Compat.IdValue(toDelete[i]));
            var collateral = actuallyDeleted.Where(id => !requestedIds.Contains(id)).ToList();

            JsonValue result = J.O(
                "requested", requested,
                "requestedCount", toDelete.Count,
                "deletedCount", actuallyDeleted.Count,
                "deletedIds", J.ALongs(actuallyDeleted),
                "collateralCount", collateral.Count,
                "collateralIds", J.ALongs(collateral),
                "dryRun", dryRun);

            if (collateral.Count > 0)
            {
                result.Set("collateralNote",
                    collateral.Count + " additional element(s) were removed because they depended on the " +
                    "elements you deleted (hosted doors and windows, dimensions, tags, and so on).");
            }
            if (missing.Count > 0) result.Set("notFound", J.ALongs(missing));
            if (dryRun) result.Set("note", "DRY RUN - the model was restored. Re-run with dryRun:false to apply.");

            return result;
        }

        // ==================================================================
        //  revit_purge_unused
        // ==================================================================
        public static JsonValue PurgeUnused(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            bool dryRun = Args.Bool(ctx.Args, "dryRun", false);
            int maxPasses = Args.Int(ctx.Args, "maxPasses", false, 3, 1, 10);

            JsonValue passes = JsonValue.NewArray();
            int totalPurged = 0;

            for (int pass = 1; pass <= maxPasses; pass++)
            {
                // IMPORTANT: the purgeable set is computed OUTSIDE any open transaction. On
                // pre-2024 releases this runs through PerformanceAdviser.ExecuteRules, which the
                // Revit API refuses to execute while a transaction is open. The enclosing
                // TransactionGroup is fine - a group is not a transaction.
                ICollection<ElementId> purgeable = Compat.GetPurgeableElements(doc);

                if (purgeable == null || purgeable.Count == 0)
                {
                    passes.Add(J.O("pass", pass, "found", 0, "deleted", 0,
                                   "note", "Nothing further to purge."));
                    break;
                }

                // Summarise what is going before it goes.
                JsonValue byCategory = SummariseByCategory(doc, purgeable);
                ICollection<ElementId> batch = purgeable;
                int deleted = 0;

                try
                {
                    ctx.InTransaction("Purge unused (pass " + pass + ")", delegate
                    {
                        ICollection<ElementId> removed = doc.Delete(batch);
                        deleted = removed != null ? removed.Count : 0;
                    });
                }
                catch (Exception ex)
                {
                    ctx.AddWarning("Purge pass " + pass + " could not delete every item: " + ex.Message);
                }

                totalPurged += deleted;
                passes.Add(J.O("pass", pass, "found", purgeable.Count, "deleted", deleted, "byKind", byCategory));

                if (deleted == 0) break;   // no progress - stop rather than spin
            }

            if (dryRun) ctx.RequestRollback();

            JsonValue result = J.O(
                "totalPurged", totalPurged,
                "passes", passes,
                "passCount", passes.Count,
                "dryRun", dryRun,
                "method",
#if REVIT2024_OR_GREATER
                "Document.GetUnusedElements (Revit 2024+ API)"
#else
                "PerformanceAdviser purge rule (pre-2024 API)"
#endif
            );

            if (dryRun)
                result.Set("note", "DRY RUN - nothing was removed. " + totalPurged +
                                   " item(s) would be purged. Re-run with dryRun:false to apply.");

            return result;
        }

        private static JsonValue SummariseByCategory(Document doc, ICollection<ElementId> ids)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (ElementId id in ids)
            {
                string key;
                try
                {
                    Element element = doc.GetElement(id);
                    if (element == null) { key = "(already gone)"; }
                    else
                    {
                        Category category = null;
                        try { category = element.Category; } catch (Exception) { }
                        key = category != null && !string.IsNullOrEmpty(category.Name)
                            ? category.Name
                            : element.GetType().Name;
                    }
                }
                catch (Exception) { key = "(unreadable)"; }

                int current;
                counts[key] = counts.TryGetValue(key, out current) ? current + 1 : 1;
            }

            var sorted = counts.ToList();
            sorted.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            {
                return b.Value.CompareTo(a.Value);
            });

            JsonValue json = JsonValue.NewArray();
            for (int i = 0; i < sorted.Count && i < 25; i++)
                json.Add(J.O("kind", sorted[i].Key, "count", sorted[i].Value));
            return json;
        }

        // ==================================================================
        //  revit_unload_links
        // ==================================================================
        public static JsonValue UnloadLinks(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            List<long> requestedIds = Args.IdValues(ctx.Args, "linkTypeIds", false, 500);
            string nameContains = Args.Str(ctx.Args, "nameContains");

            List<RevitLinkType> linkTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().ToList();

            if (linkTypes.Count == 0)
                return J.O("unloaded", 0, "note", "This model contains no Revit links.");

            var targets = new List<RevitLinkType>();
            if (requestedIds.Count > 0)
            {
                var wanted = new HashSet<long>(requestedIds);
                targets.AddRange(linkTypes.Where(t => wanted.Contains(Compat.IdValue(t.Id))));

                var found = new HashSet<long>(targets.Select(t => Compat.IdValue(t.Id)));
                foreach (long id in requestedIds)
                    if (!found.Contains(id)) ctx.AddWarning("Link type id " + id + " was not found.");
            }
            else
            {
                targets.AddRange(linkTypes.Where(t => Paging.Matches(ElementSerializer.SafeName(t), nameContains)));
            }

            JsonValue unloaded = JsonValue.NewArray();
            JsonValue skipped = JsonValue.NewArray();

            // Unload runs OUTSIDE a transaction on purpose: RevitLinkType.Unload manages its own
            // document state and throws if it is called inside one.
            for (int i = 0; i < targets.Count; i++)
            {
                RevitLinkType linkType = targets[i];
                long id = Compat.IdValue(linkType.Id);
                string name = ElementSerializer.SafeName(linkType);

                try
                {
                    LinkedFileStatus status = linkType.GetLinkedFileStatus();
                    if (status != LinkedFileStatus.Loaded)
                    {
                        skipped.Add(J.O("linkTypeId", id, "name", name,
                                        "reason", "Already " + status + "."));
                        continue;
                    }

                    linkType.Unload(null);
                    unloaded.Add(J.O("linkTypeId", id, "name", name));
                }
                catch (Exception ex)
                {
                    skipped.Add(J.O("linkTypeId", id, "name", name, "reason", ex.Message));
                }
            }

            return J.O(
                "unloaded", unloaded.Count,
                "unloadedLinks", unloaded,
                "skipped", skipped.Count,
                "skippedDetail", skipped,
                "modelChanged", unloaded.Count > 0,
                "reversible", true,
                "note", "Unloaded links stay in the model and can be reloaded from Manage > Manage Links. " +
                        "On a workshared model this affects every user after synchronisation.");
        }

        // ==================================================================
        //  revit_remove_links
        // ==================================================================
        public static JsonValue RemoveLinks(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            List<long> ids = Args.IdValues(ctx.Args, "linkTypeIds", true, 500);
            bool dryRun = Args.Bool(ctx.Args, "dryRun", false);

            var targets = new List<ElementId>();
            JsonValue described = JsonValue.NewArray();

            for (int i = 0; i < ids.Count; i++)
            {
                Element element = null;
                try { element = doc.GetElement(Compat.ToId(ids[i])); } catch (Exception) { }

                if (element == null)
                {
                    ctx.AddWarning("Link type id " + ids[i] + " was not found.");
                    continue;
                }
                if (!(element is RevitLinkType) && !(element is CADLinkType))
                {
                    ctx.AddWarning("Element " + ids[i] + " is not a link type (" + element.GetType().Name +
                                   ") and was ignored.");
                    continue;
                }

                targets.Add(element.Id);
                described.Add(J.O(
                    "linkTypeId", ids[i],
                    "name", ElementSerializer.SafeName(element),
                    "kind", element is RevitLinkType ? "RevitLink" : "CADLink"));
            }

            if (targets.Count == 0)
                throw new ToolException(BridgeErrorCodes.NotFound,
                    "None of the supplied ids refer to a link type. Nothing was removed.");

            var deleted = new List<long>();
            ctx.InTransaction("Remove links", delegate
            {
                ICollection<ElementId> removed = doc.Delete(targets);
                foreach (ElementId id in removed) deleted.Add(Compat.IdValue(id));
            });

            if (dryRun) ctx.RequestRollback();

            JsonValue result = J.O(
                "removed", described,
                "linkTypesRequested", targets.Count,
                "elementsDeleted", deleted.Count,
                "dryRun", dryRun,
                "reversible", false,
                "note", "Removed links must be re-inserted and re-positioned manually. " +
                        "revit_unload_links is the reversible alternative.");

            if (dryRun) result.Set("dryRunNote", "DRY RUN - the model was restored. Re-run with dryRun:false to apply.");
            return result;
        }

        // ==================================================================
        //  revit_delete_views
        // ==================================================================
        public static JsonValue DeleteViews(ToolContext ctx)
        {
            Document doc = ctx.Doc;
            List<long> ids = Args.IdValues(ctx.Args, "viewIds", true, 2000);
            bool dryRun = Args.Bool(ctx.Args, "dryRun", false);

            var targets = new List<View>();
            JsonValue skipped = JsonValue.NewArray();

            for (int i = 0; i < ids.Count; i++)
            {
                Element element = null;
                try { element = doc.GetElement(Compat.ToId(ids[i])); } catch (Exception) { }

                View view = element as View;
                if (view == null)
                {
                    skipped.Add(J.O("id", ids[i], "reason", element == null
                        ? "No element with this id exists."
                        : "Element " + ids[i] + " is a " + element.GetType().Name + ", not a view."));
                    continue;
                }
                if (view.Id == doc.ActiveView.Id)
                {
                    skipped.Add(J.O("id", ids[i], "name", view.Name,
                                    "reason", "This is the active view; Revit cannot delete it. " +
                                              "Switch to another view first."));
                    continue;
                }
                targets.Add(view);
            }

            if (targets.Count == 0)
                throw new ToolException(BridgeErrorCodes.NotFound,
                    "None of the supplied ids refer to a deletable view. Nothing was deleted.");

            JsonValue deleted = JsonValue.NewArray();
            int totalElementsRemoved = 0;

            ctx.InTransaction("Delete views", delegate
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    View view = targets[i];
                    long id = Compat.IdValue(view.Id);
                    string name = view.Name;
                    string type = view.ViewType.ToString();

                    try
                    {
                        ICollection<ElementId> removed = doc.Delete(view.Id);
                        totalElementsRemoved += removed != null ? removed.Count : 0;
                        deleted.Add(J.O("id", id, "name", name, "viewType", type));
                    }
                    catch (Exception ex)
                    {
                        // Revit protects the last plan of a level, system browsers, and more.
                        skipped.Add(J.O("id", id, "name", name, "reason", ex.Message));
                    }
                }
            });

            if (dryRun) ctx.RequestRollback();

            JsonValue result = J.O(
                "deleted", deleted.Count,
                "deletedViews", deleted,
                "skipped", skipped.Count,
                "skippedDetail", skipped,
                "totalElementsRemoved", totalElementsRemoved,
                "dryRun", dryRun);

            if (dryRun) result.Set("note", "DRY RUN - the model was restored. Re-run with dryRun:false to apply.");
            return result;
        }
    }
}
