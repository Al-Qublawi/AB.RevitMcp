// AB Adv Tools shared kit - see AdvToolsBrand.cs for how the kit is shared.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Reflection;
using System.Text;

namespace ABAdvTools.Navisworks
{
    /// <summary>
    /// Folds every AB plugin's "AB Adv Tools" tab into one tab, keeps exactly one shared panel,
    /// and puts that panel last.
    ///
    /// How Navisworks builds the ribbon (confirmed against navisworks.gui.roamer 2026 and 2027,
    /// NWRibbonControl.LoadPluginTabs / NWRibbonButton / NWRibbonUtil.UpdateVisibleProps):
    ///   - each plugin's XAML becomes its own AdWindows RibbonTab, and every tab and button id is
    ///     prefixed with the plugin id - "ABSwitchBack.Ribbon.ABSB.ID_ABAdvTools"
    ///   - a button executes through CommandManager.FindCommand(its own id), so moving its panel
    ///     to another tab does not change what it does
    ///   - a plugin tab's visibility comes from its plugin's CanExecuteRibbonTab, not from how
    ///     many panels it has, so an emptied tab must be taken off the ribbon, not just emptied
    ///   - LoadRibbonContent clears and rebuilds every tab, so the merge re-runs whenever the tab
    ///     collection changes
    ///
    /// Everything is done by reflection against AdWindows. Compiling against AdWindows.dll would
    /// tie each build to one Navisworks release's copy of it; reflection works across releases,
    /// and if a future release changes these types the merge simply does nothing - every AB
    /// plugin still shows, on its own tab titled AB Adv Tools, fully working.
    /// </summary>
    internal static class NavisworksRibbonMerger
    {
        private const string MergeJob = "navisworks.ribbonMerge";
        private const int PollIntervalMs = 500;
        private const int DebounceMs = 300;
        private const int MaxPollAttempts = 360;   // three minutes for a slow start

        private static System.Windows.Forms.Timer _timer;
        private static int _attempts;
        private static bool _merging;
        private static bool _loggedLayout;
        private static INotifyCollectionChanged _observed;

        /// <summary>Only the first AB plugin to call this does the work; the rest return at once.</summary>
        public static void Start(string claimant)
        {
            string owner;
            if (!AdvToolsRegistry.TryClaim(MergeJob, claimant, out owner))
            {
                AdvToolsLog.Info("Ribbon merge is handled by " + owner + ".");
                return;
            }

            _timer = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
            _timer.Tick += OnTick;
            _timer.Start();
        }

        public static void Stop()
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
                _timer = null;
            }
            if (_observed != null)
            {
                _observed.CollectionChanged -= OnTabsChanged;
                _observed = null;
            }
        }

        private static void OnTick(object sender, EventArgs e)
        {
            _timer.Stop();
            _attempts++;

            bool ribbonReady;
            try
            {
                EnsureMerged(out ribbonReady);
            }
            catch (Exception ex)
            {
                AdvToolsLog.Error("Ribbon merge failed.", ex);
                ribbonReady = true;   // do not retry a failure in a loop
            }

            if (ribbonReady)
            {
                // From here on, react to changes instead of polling.
                _timer.Interval = DebounceMs;
                return;
            }

            if (_attempts < MaxPollAttempts) _timer.Start();
            else AdvToolsLog.Warn("The AB Adv Tools tab never appeared; ribbon merge gave up.");
        }

        private static void OnTabsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (_merging || _timer == null) return;

            // Navisworks adds tabs one at a time while it rebuilds; wait for it to finish, and give
            // the rebuilt tabs a fresh polling budget in case they arrive after the first retry.
            _attempts = 0;
            _timer.Stop();
            _timer.Interval = DebounceMs;
            _timer.Start();
        }

        /// <summary>Idempotent. Safe to call at any time on the UI thread.</summary>
        internal static void EnsureMerged(out bool ribbonReady)
        {
            ribbonReady = false;

            object ribbon = FindRibbon();
            if (ribbon == null) return;

            var tabs = GetProperty(ribbon, "Tabs") as IList;
            if (tabs == null) return;

            Observe(tabs);

            List<object> abTabs = new List<object>();
            int firstIndex = int.MaxValue;
            for (int i = 0; i < tabs.Count; i++)
            {
                object tab = tabs[i];
                if (!IsAbTab(tab)) continue;
                abTabs.Add(tab);
                if (i < firstIndex) firstIndex = i;
            }

            // The ribbon exists but our plugins' tabs are not on it yet.
            if (abTabs.Count == 0) return;
            ribbonReady = true;

            // A stable order: tool panels grouped by plugin id, alphabetically.
            abTabs.Sort(delegate (object a, object b)
            {
                return string.Compare(GetString(a, "Id"), GetString(b, "Id"), StringComparison.OrdinalIgnoreCase);
            });

            object primary = abTabs[0];
            IList primaryPanels = GetProperty(primary, "Panels") as IList;
            if (primaryPanels == null) return;

            _merging = true;
            bool changed = false;
            try
            {
                for (int t = 1; t < abTabs.Count; t++)
                {
                    object other = abTabs[t];
                    var otherPanels = GetProperty(other, "Panels") as IList;
                    if (otherPanels != null)
                    {
                        var moving = new List<object>();
                        foreach (object panel in otherPanels) moving.Add(panel);

                        foreach (object panel in moving)
                        {
                            otherPanels.Remove(panel);
                            primaryPanels.Add(panel);
                        }
                    }

                    tabs.Remove(other);
                    changed = true;
                }

                // Keep the merged tab where the first AB tab was.
                int current = tabs.IndexOf(primary);
                if (firstIndex != int.MaxValue && current > firstIndex && firstIndex < tabs.Count)
                {
                    Move(tabs, current, firstIndex);
                    changed = true;
                }

                if (NormaliseSharedPanel(primaryPanels)) changed = true;

                if (GetString(primary, "Title") != NavisworksAdvTools.RibbonTabTitle)
                {
                    SetProperty(primary, "Title", NavisworksAdvTools.RibbonTabTitle);
                    changed = true;
                }
            }
            finally
            {
                _merging = false;
            }

            if (changed || !_loggedLayout)
            {
                _loggedLayout = true;
                AdvToolsLog.Info("AB Adv Tools tab: " + Describe(primary));
            }
        }

        /// <summary>One shared panel, and last. Returns true if anything moved.</summary>
        private static bool NormaliseSharedPanel(IList panels)
        {
            var shared = new List<object>();
            foreach (object panel in panels)
            {
                if (IsSharedPanel(panel)) shared.Add(panel);
            }
            if (shared.Count == 0) return false;

            bool changed = false;
            for (int i = 1; i < shared.Count; i++)
            {
                panels.Remove(shared[i]);
                changed = true;
            }

            int index = panels.IndexOf(shared[0]);
            if (index >= 0 && index != panels.Count - 1)
            {
                Move(panels, index, panels.Count - 1);
                changed = true;
            }
            return changed;
        }

        private static bool IsAbTab(object tab)
        {
            string id = GetString(tab, "Id");
            if (string.IsNullOrEmpty(id)) return false;

            return id == NavisworksAdvTools.RibbonTabId ||
                   id.EndsWith("." + NavisworksAdvTools.RibbonTabId, StringComparison.Ordinal);
        }

        private static bool IsSharedPanel(object panel)
        {
            object source = GetProperty(panel, "Source");
            return source != null &&
                   string.Equals(GetString(source, "Id"), NavisworksAdvTools.SharedPanelSourceId, StringComparison.Ordinal);
        }

        private static void Observe(IList tabs)
        {
            var observable = tabs as INotifyCollectionChanged;
            if (observable == null || ReferenceEquals(observable, _observed)) return;

            if (_observed != null) _observed.CollectionChanged -= OnTabsChanged;
            _observed = observable;
            _observed.CollectionChanged += OnTabsChanged;
        }

        // ------------------------------------------------------------ reflection

        /// <summary>
        /// NWRibbonControl.Instance is the Navisworks ribbon. ComponentManager.Ribbon is the
        /// generic AdWindows route, tried second in case a release stops exposing the first.
        /// </summary>
        private static object FindRibbon()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name;
                try { name = assembly.GetName().Name; }
                catch { continue; }

                if (string.Equals(name, "navisworks.gui.roamer", StringComparison.OrdinalIgnoreCase))
                {
                    object ribbon = StaticProperty(assembly, "Autodesk.Navisworks.Gui.Roamer.AIRLook.NWRibbonControl", "Instance");
                    if (ribbon != null) return ribbon;
                }
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name;
                try { name = assembly.GetName().Name; }
                catch { continue; }

                if (string.Equals(name, "AdWindows", StringComparison.OrdinalIgnoreCase))
                    return StaticProperty(assembly, "Autodesk.Windows.ComponentManager", "Ribbon");
            }
            return null;
        }

        private static object StaticProperty(Assembly assembly, string typeName, string property)
        {
            try
            {
                Type type = assembly.GetType(typeName, false);
                if (type == null) return null;
                PropertyInfo info = type.GetProperty(property, BindingFlags.Public | BindingFlags.Static);
                return info == null ? null : info.GetValue(null, null);
            }
            catch
            {
                return null;
            }
        }

        private static object GetProperty(object target, string property)
        {
            if (target == null) return null;
            try
            {
                PropertyInfo info = target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
                return info == null ? null : info.GetValue(target, null);
            }
            catch
            {
                return null;
            }
        }

        private static string GetString(object target, string property)
        {
            return GetProperty(target, property) as string;
        }

        private static void SetProperty(object target, string property, object value)
        {
            try
            {
                PropertyInfo info = target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
                if (info != null && info.CanWrite) info.SetValue(target, value, null);
            }
            catch { }
        }

        /// <summary>ObservableCollection.Move keeps the item alive; Remove + Insert would re-create its UI.</summary>
        private static void Move(IList list, int from, int to)
        {
            MethodInfo move = list.GetType().GetMethod("Move", new[] { typeof(int), typeof(int) });
            if (move != null)
            {
                move.Invoke(list, new object[] { from, to });
                return;
            }

            object item = list[from];
            list.RemoveAt(from);
            list.Insert(to, item);
        }

        private static string Describe(object tab)
        {
            var sb = new StringBuilder();
            sb.Append(GetString(tab, "Id"));
            var panels = GetProperty(tab, "Panels") as IList;
            if (panels == null) return sb.ToString();

            sb.Append(" -> ");
            for (int i = 0; i < panels.Count; i++)
            {
                object source = GetProperty(panels[i], "Source");
                if (i > 0) sb.Append(" | ");
                sb.Append(GetString(source, "Title") ?? "?");
            }
            return sb.ToString();
        }
    }
}
