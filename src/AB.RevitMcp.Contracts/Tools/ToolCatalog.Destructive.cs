using AB.RevitMcp.Contracts.Json;

namespace AB.RevitMcp.Contracts.Tools
{
    public static partial class ToolCatalog
    {
        // ============================================================================
        //  DESTRUCTIVE TOOLS
        //  Every one of these refuses to run unless the caller passes confirm: true.
        //  The interlock is enforced server-side in the add-in, not just documented here.
        // ============================================================================
        private static void RegisterDestructiveTools()
        {
            Add("revit_delete_elements", ToolCategory.Destructive, "Delete elements",
                "Permanently deletes elements from the model. Deleting a host also deletes what it hosts (deleting a " +
                "wall removes its doors and windows), so the response reports every id Revit actually removed. " +
                "Run with dryRun first to preview the blast radius.",
                Sch.Obj("Deletion parameters.", new[] { "elementIds", "confirm" },
                    "elementIds", Sch.ElementIds("Element ids to delete.", 5000),
                    "confirm", Sch.Confirm(),
                    "dryRun", Sch.Bool("Report what would be deleted and roll back without changing the model. " +
                                       "Default false.", false)));

            Add("revit_purge_unused", ToolCategory.Destructive, "Purge unused",
                "Removes unused families, types, materials, filters and other unreferenced definitions - the same " +
                "operation as Manage > Purge Unused. Repeats until no further items are found, up to maxPasses. " +
                "Use dryRun to see the count first.",
                Sch.Obj("Purge parameters.", new[] { "confirm" },
                    "confirm", Sch.Confirm(),
                    "dryRun", Sch.Bool("Report the purgeable item count without deleting anything. Default false.", false),
                    "maxPasses", Sch.Int("How many purge passes to run. Default 3.", 1, 10, 3)));

            Add("revit_unload_links", ToolCategory.Destructive, "Unload linked models",
                "Unloads RVT links so they stop consuming memory and stop appearing in views. The link definitions " +
                "stay in the model and can be reloaded from Manage Links, so this is reversible - but it changes " +
                "every view for every user on a workshared model.",
                Sch.Obj("Unload parameters.", new[] { "confirm" },
                    "confirm", Sch.Confirm(),
                    "linkTypeIds", Sch.Arr(Sch.Int("RevitLinkType element id.", 1),
                        "Specific link type ids to unload. Omit to unload every loaded RVT link.", null, 500),
                    "nameContains", Sch.Str("Only unload links whose name contains this text (case-insensitive).")));

            Add("revit_remove_links", ToolCategory.Destructive, "Remove linked models",
                "Deletes RVT/CAD link definitions from the model entirely. This is NOT reversible from Manage Links - " +
                "the link must be re-inserted and re-positioned. Prefer revit_unload_links unless the user explicitly " +
                "wants the link gone.",
                Sch.Obj("Link removal parameters.", new[] { "linkTypeIds", "confirm" },
                    "linkTypeIds", Sch.Arr(Sch.Int("Link type element id.", 1),
                        "Link type ids to delete.", 1, 500),
                    "confirm", Sch.Confirm(),
                    "dryRun", Sch.Bool("Report what would be removed without changing the model. Default false.", false)));

            Add("revit_delete_views", ToolCategory.Destructive, "Delete views",
                "Deletes views and their sheet placements. Views that are the last plan of a level, or that Revit " +
                "otherwise protects, are reported as skipped rather than failing the whole call.",
                Sch.Obj("View deletion parameters.", new[] { "viewIds", "confirm" },
                    "viewIds", Sch.Arr(Sch.Int("View element id.", 1), "View ids to delete.", 1, 2000),
                    "confirm", Sch.Confirm(),
                    "dryRun", Sch.Bool("Report what would be deleted without changing the model. Default false.", false)));
        }
    }
}
