// AB Adv Tools shared kit - see AdvToolsBrand.cs for how the kit is shared.
using System;
using System.Collections;
using System.Collections.Generic;

namespace ABAdvTools
{
    /// <summary>
    /// The list of AB add-ins loaded in this Revit or Navisworks process.
    ///
    /// Every add-in compiles its own private copy of the kit, so a static field here would only
    /// ever see the add-in it lives in. The About dialog has to list all of them, so the list is
    /// kept in AppDomain data instead, built from framework types only: nothing else can be shared
    /// between assemblies that each define their own copy of these classes. That also holds on
    /// .NET 8, where Revit loads each add-in into its own AssemblyLoadContext but the AppDomain is
    /// still one per process.
    /// </summary>
    internal static class AdvToolsRegistry
    {
        // Bump the suffix if the entry layout ever changes, so old and new kits never misread
        // each other's entries.
        private const string SlotName = "ABAdvTools.Products.v2";

        // Entry layout: object[] of
        //   0-6  strings: id, name, tagline, host, GitHub owner, repository, version
        //   7    Func<string>  details (or null)
        //   8    object[]      actions, as { string text, Action run } pairs (or null)
        //   9    string        update settings hint (or null)
        // Framework types only, so any add-in's copy of the kit can read any other's. Readers
        // take what they understand and ignore anything past it, so fields are only ever appended.
        private const int FieldCount = 7;

        private static readonly object CreateGate = new object();

        public static void Register(AdvToolsProduct product)
        {
            if (product == null) return;
            try
            {
                var actions = new object[product.Actions.Count];
                for (int i = 0; i < product.Actions.Count; i++)
                    actions[i] = new object[] { product.Actions[i].Text, product.Actions[i].Run };

                Hashtable table = Table();
                lock (table.SyncRoot)
                {
                    table[product.Id] = new object[]
                    {
                        product.Id,
                        product.Name,
                        product.Tagline,
                        product.Host.ToString(),
                        product.GitHubOwner,
                        product.GitHubRepository,
                        product.Version,
                        product.Details,
                        actions,
                        product.UpdateSettingsHint
                    };
                }
            }
            catch (Exception ex)
            {
                AdvToolsLog.Warn("Could not register " + product.Id + ": " + ex.Message);
            }
        }

        /// <summary>Every AB add-in registered in this process, sorted by name.</summary>
        public static List<AdvToolsProduct> Products()
        {
            var result = new List<AdvToolsProduct>();
            try
            {
                Hashtable table = Table();
                lock (table.SyncRoot)
                {
                    foreach (DictionaryEntry entry in table)
                    {
                        var fields = entry.Value as object[];
                        if (fields == null || fields.Length < FieldCount) continue;

                        AdvToolsHost host;
                        if (!Enum.TryParse(fields[3] as string, out host)) host = AdvToolsHost.Revit;

                        var product = new AdvToolsProduct(fields[0] as string, fields[1] as string, fields[2] as string,
                                                          host, fields[4] as string, fields[5] as string, fields[6] as string);

                        if (fields.Length > 7) product.Details = fields[7] as Func<string>;

                        var actions = fields.Length > 8 ? fields[8] as object[] : null;
                        if (actions != null)
                        {
                            foreach (object item in actions)
                            {
                                var pair = item as object[];
                                if (pair != null && pair.Length >= 2) product.AddAction(pair[0] as string, pair[1] as Action);
                            }
                        }

                        if (fields.Length > 9) product.UpdateSettingsHint = fields[9] as string;

                        result.Add(product);
                    }
                }
            }
            catch (Exception ex)
            {
                AdvToolsLog.Warn("Could not read the product list: " + ex.Message);
            }

            result.Sort(delegate (AdvToolsProduct a, AdvToolsProduct b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        /// <summary>
        /// Claims a one-per-process job, e.g. building the shared ribbon panel. The first add-in
        /// to ask gets true; every later add-in gets false. Returns the owner's id either way.
        /// </summary>
        public static bool TryClaim(string job, string claimant, out string owner)
        {
            owner = claimant;
            try
            {
                Hashtable table = Table();
                string key = "claim:" + job;
                lock (table.SyncRoot)
                {
                    var existing = table[key] as string;
                    if (existing != null)
                    {
                        owner = existing;
                        return string.Equals(existing, claimant, StringComparison.Ordinal);
                    }
                    table[key] = claimant;
                    return true;
                }
            }
            catch
            {
                // If the shared slot is unusable, act alone rather than not at all.
                return true;
            }
        }

        private static Hashtable Table()
        {
            AppDomain domain = AppDomain.CurrentDomain;
            var table = domain.GetData(SlotName) as Hashtable;
            if (table != null) return table;

            // Assemblies each have their own CreateGate, so this can race between two add-ins
            // starting on different threads. Both hosts start add-ins on their UI thread, and the
            // second check below narrows it further; a lost race only drops one early entry.
            lock (CreateGate)
            {
                table = domain.GetData(SlotName) as Hashtable;
                if (table == null)
                {
                    table = Hashtable.Synchronized(new Hashtable(StringComparer.Ordinal));
                    domain.SetData(SlotName, table);
                }
                return table;
            }
        }
    }
}
