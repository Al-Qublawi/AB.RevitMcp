// AB Adv Tools shared kit - see AdvToolsBrand.cs for how the kit is shared.
using System;
using System.Threading;
using System.Windows.Forms;
using ABAdvTools.UI;

namespace ABAdvTools.Navisworks
{
    /// <summary>
    /// Puts a Navisworks plugin on the shared "AB Adv Tools" ribbon tab and gives it the suite's
    /// About panel and release notifications.
    ///
    /// Navisworks builds one tab per plugin from that plugin's ribbon XAML; it has no notion of
    /// two plugins sharing a tab. So every AB plugin declares its tab with the same id and title
    /// (see RibbonTabId) plus a copy of the shared panel, and NavisworksRibbonMerger folds those
    /// tabs into one at runtime. Buttons keep working after the move because Navisworks routes a
    /// click through the button's plugin-prefixed command id, never through the tab it sits on.
    ///
    /// Wiring, per plugin:
    ///   1. In the ribbon XAML: &lt;RibbonTab Id="ID_ABAdvTools" Title="AB Adv Tools"&gt;, the tool's
    ///      own panels, then the shared panel from templates\Navisworks.SharedPanel.xaml.
    ///   2. On the CommandHandlerPlugin: [RibbonTab(RibbonTabId, ...)] and the three shared
    ///      [Command] attributes (constants below), and at the top of ExecuteCommand:
    ///          if (NavisworksAdvTools.TryExecuteSharedCommand(commandId)) return 0;
    ///   3. An EventWatcherPlugin whose OnLoaded calls NavisworksAdvTools.Start(product) - event
    ///      watchers are the only plugin kind Navisworks loads at startup.
    /// </summary>
    internal static class NavisworksAdvTools
    {
        /// <summary>Local tab id. Navisworks prefixes it with the plugin id when it loads the XAML.</summary>
        public const string RibbonTabId = "ID_ABAdvTools";
        public const string RibbonTabTitle = AdvToolsBrand.RibbonTabName;

        /// <summary>RibbonPanelSource Id marking the shared panel in each plugin's XAML.</summary>
        public const string SharedPanelSourceId = "ABADV_SharedPanel";

        public const string AboutCommandId = "ID_ABADV_About";
        public const string AboutText = "About";
        public const string AboutToolTip = "About AB Adv Tools: the AB tools installed, and their versions.";
        public const string AboutIcon = "abadv_about_16.ico";
        public const string AboutLargeIcon = "abadv_about_32.ico";

        public const string UpdatesCommandId = "ID_ABADV_Updates";
        public const string UpdatesText = "Check for Updates";
        public const string UpdatesToolTip = "Check GitHub for new releases of every AB tool installed.";
        public const string UpdatesIcon = "abadv_update_16.ico";
        public const string UpdatesLargeIcon = "abadv_update_32.ico";

        public const string LinkedInCommandId = "ID_ABADV_LinkedIn";
        public const string LinkedInText = "LinkedIn";
        public const string LinkedInToolTip = "Open Abdullah Lotfy's LinkedIn profile.";
        public const string LinkedInIcon = "abadv_linkedin_16.ico";
        public const string LinkedInLargeIcon = "abadv_linkedin_32.ico";

        private static readonly object Gate = new object();
        private static AdvToolsProduct _product;
        private static SynchronizationContext _ui;
        private static bool _started;

        /// <summary>Call from an EventWatcherPlugin's OnLoaded, which runs on the UI thread.</summary>
        public static void Start(AdvToolsProduct product)
        {
            if (product == null) return;

            lock (Gate)
            {
                if (_started) return;
                _started = true;
            }

            try
            {
                _product = product;
                AdvToolsLog.Source = product.Id;
                AdvToolsRegistry.Register(product);

                _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

                NavisworksRibbonMerger.Start(typeof(NavisworksAdvTools).Assembly.GetName().Name);
                StartAutomaticUpdateCheck(product);
            }
            catch (Exception ex)
            {
                AdvToolsLog.Error("AB Adv Tools failed to start for " + product.Id + ".", ex);
            }
        }

        public static void Stop()
        {
            try { NavisworksRibbonMerger.Stop(); }
            catch { }
        }

        public static bool IsSharedCommand(string commandId)
        {
            return commandId == AboutCommandId || commandId == UpdatesCommandId || commandId == LinkedInCommandId;
        }

        /// <summary>Handles the shared panel's buttons. Returns false for any other command.</summary>
        public static bool TryExecuteSharedCommand(string commandId)
        {
            switch (commandId)
            {
                case AboutCommandId:
                    AdvToolsUi.ShowAbout(MainWindow(), AdvToolsHost.Navisworks, false);
                    return true;

                case UpdatesCommandId:
                    AdvToolsUi.ShowAbout(MainWindow(), AdvToolsHost.Navisworks, true);
                    return true;

                case LinkedInCommandId:
                    AdvToolsUi.OpenLinkedIn(MainWindow());
                    return true;

                default:
                    return false;
            }
        }

        internal static IWin32Window MainWindow()
        {
            try { return Autodesk.Navisworks.Api.Application.Gui.MainWindow; }
            catch { return null; }
        }

        private static void StartAutomaticUpdateCheck(AdvToolsProduct product)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateResult result = UpdateChecker.CheckAutomatically(product);
                if (result == null || !result.ShouldNotify) return;

                PostToUi(delegate { ShowNoticeWhenSettled(result); });
            });
        }

        /// <summary>
        /// Holds the notice back a few seconds so it lands on the finished main window rather than
        /// behind the splash screen or in the middle of the ribbon being built.
        /// </summary>
        private static void ShowNoticeWhenSettled(UpdateResult result)
        {
            var timer = new System.Windows.Forms.Timer { Interval = 8000 };
            timer.Tick += delegate
            {
                timer.Stop();
                timer.Dispose();
                AdvToolsUi.ShowUpdateNotice(MainWindow(), result);
            };
            timer.Start();
        }

        private static void PostToUi(Action action)
        {
            try
            {
                if (_ui != null) _ui.Post(delegate { SafeRun(action); }, null);
                else SafeRun(action);
            }
            catch (Exception ex)
            {
                AdvToolsLog.Warn("Could not reach the UI thread: " + ex.Message);
            }
        }

        private static void SafeRun(Action action)
        {
            try { action(); }
            catch (Exception ex) { AdvToolsLog.Error("UI callback failed.", ex); }
        }
    }
}
