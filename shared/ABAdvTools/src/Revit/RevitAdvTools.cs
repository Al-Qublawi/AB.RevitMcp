// AB Adv Tools shared kit - see AdvToolsBrand.cs for how the kit is shared.
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Media.Imaging;
using ABAdvTools.UI;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace ABAdvTools.Revit
{
    /// <summary>
    /// Puts a Revit add-in on the shared "AB Adv Tools" ribbon tab and gives it the suite's
    /// About panel and release notifications.
    ///
    /// Usage, from IExternalApplication.OnStartup:
    ///
    ///     RevitAdvTools.Initialize(application, product);
    ///     RibbonPanel panel = RevitAdvTools.GetToolPanel(application, "My Tool");
    ///     ... add buttons to panel ...
    ///
    /// and RevitAdvTools.Shutdown(application) from OnShutdown.
    ///
    /// Layout: each add-in adds its own panel(s) during startup, in whatever order Revit loads
    /// the add-ins. The shared "AB Adv Tools" panel (About, Check for Updates, LinkedIn) is
    /// created once, by whichever AB add-in gets there first, from ApplicationInitialized - after
    /// every add-in's OnStartup has run - so it always sits at the end of the tab.
    /// </summary>
    internal static class RevitAdvTools
    {
        private const string SharedPanelJob = "revit.sharedPanel";

        private static AdvToolsProduct _product;
        private static UIControlledApplication _application;
        private static volatile UpdateResult _pendingNotice;

        public static string TabName
        {
            get { return AdvToolsBrand.RibbonTabName; }
        }

        public static void Initialize(UIControlledApplication application, AdvToolsProduct product)
        {
            if (application == null) throw new ArgumentNullException("application");
            if (product == null) throw new ArgumentNullException("product");

            _application = application;
            _product = product;

            AdvToolsLog.Source = product.Id;
            AdvToolsRegistry.Register(product);
            EnsureTab(application);

            try { application.ControlledApplication.ApplicationInitialized += OnApplicationInitialized; }
            catch (Exception ex) { AdvToolsLog.Warn("ApplicationInitialized subscription failed: " + ex.Message); }

            try { application.ViewActivated += OnViewActivated; }
            catch (Exception ex) { AdvToolsLog.Warn("ViewActivated subscription failed: " + ex.Message); }

            StartAutomaticUpdateCheck(product);
        }

        public static void Shutdown(UIControlledApplication application)
        {
            if (application == null) return;
            try { application.ControlledApplication.ApplicationInitialized -= OnApplicationInitialized; } catch { }
            try { application.ViewActivated -= OnViewActivated; } catch { }
        }

        /// <summary>
        /// Returns the named panel on the AB Adv Tools tab, creating the tab and the panel as
        /// needed. Panel names must be unique across the whole suite, so use the tool's own name.
        /// </summary>
        public static RibbonPanel GetToolPanel(UIControlledApplication application, string panelName)
        {
            EnsureTab(application);

            foreach (RibbonPanel existing in application.GetRibbonPanels(TabName))
            {
                if (string.Equals(existing.Name, panelName, StringComparison.Ordinal)) return existing;
            }
            return application.CreateRibbonPanel(TabName, panelName);
        }

        private static void EnsureTab(UIControlledApplication application)
        {
            try
            {
                application.CreateRibbonTab(TabName);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // Another AB add-in created it first - exactly what should happen.
            }
            catch (Exception ex)
            {
                AdvToolsLog.Warn("CreateRibbonTab: " + ex.Message);
            }
        }

        // ------------------------------------------------------------ shared panel

        private static void OnApplicationInitialized(object sender, ApplicationInitializedEventArgs e)
        {
            try
            {
                string owner;
                string me = typeof(RevitAdvTools).Assembly.GetName().Name;
                if (!AdvToolsRegistry.TryClaim(SharedPanelJob, me, out owner))
                {
                    AdvToolsLog.Info("Shared panel is built by " + owner + ".");
                    return;
                }

                BuildSharedPanel(_application);
            }
            catch (Exception ex)
            {
                AdvToolsLog.Error("Could not build the shared AB Adv Tools panel.", ex);
            }
        }

        private static void BuildSharedPanel(UIControlledApplication application)
        {
            if (application == null) return;

            foreach (RibbonPanel existing in application.GetRibbonPanels(TabName))
            {
                // An add-in built on an older kit may have made it already.
                if (string.Equals(existing.Name, AdvToolsBrand.SharedPanelTitle, StringComparison.Ordinal)) return;
            }

            RibbonPanel panel = application.CreateRibbonPanel(TabName, AdvToolsBrand.SharedPanelTitle);
            string assemblyPath = typeof(RevitAdvTools).Assembly.Location;

            var about = new PushButtonData("ABAdvTools_About", "About", assemblyPath, typeof(AboutCommand).FullName);
            about.ToolTip = "About " + AdvToolsBrand.SuiteName + ": the AB tools installed, and their versions.";
            about.LongDescription = "Lists every AB tool loaded in this Revit session and checks GitHub for newer releases.";
            about.AvailabilityClassName = typeof(AlwaysAvailable).FullName;
            about.LargeImage = LoadPng("abadv_about_32.png");
            about.Image = LoadPng("abadv_about_16.png");
            panel.AddItem(about);

            var updates = new PushButtonData("ABAdvTools_Updates", "Check for Updates", assemblyPath, typeof(CheckForUpdatesCommand).FullName);
            updates.ToolTip = "Check GitHub for new releases of every AB tool installed.";
            updates.AvailabilityClassName = typeof(AlwaysAvailable).FullName;
            updates.Image = LoadPng("abadv_update_16.png");
            updates.LargeImage = LoadPng("abadv_update_32.png");

            var linkedIn = new PushButtonData("ABAdvTools_LinkedIn", "LinkedIn", assemblyPath, typeof(LinkedInCommand).FullName);
            linkedIn.ToolTip = AdvToolsBrand.LinkedInCaption;
            linkedIn.LongDescription = AdvToolsBrand.LinkedInUrl;
            linkedIn.AvailabilityClassName = typeof(AlwaysAvailable).FullName;
            linkedIn.Image = LoadPng("abadv_linkedin_16.png");
            linkedIn.LargeImage = LoadPng("abadv_linkedin_32.png");

            panel.AddStackedItems(updates, linkedIn);

            AdvToolsLog.Info("Shared panel built.");
        }

        // ------------------------------------------------------------ release notices

        private static void StartAutomaticUpdateCheck(AdvToolsProduct product)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateResult result = UpdateChecker.CheckAutomatically(product);
                if (result != null && result.ShouldNotify) _pendingNotice = result;
            });
        }

        /// <summary>
        /// The notice waits for a view to activate: Revit is idle with a document open, a safe
        /// moment for a dialog. One raised during startup can stall the splash screen or sit
        /// hidden behind it.
        /// </summary>
        private static void OnViewActivated(object sender, ViewActivatedEventArgs e)
        {
            UpdateResult notice = _pendingNotice;
            if (notice == null) return;
            _pendingNotice = null;

            try
            {
                var uiApplication = sender as UIApplication;
                IntPtr handle = uiApplication != null ? uiApplication.MainWindowHandle : IntPtr.Zero;
                AdvToolsUi.ShowUpdateNotice(HostWindow.FromHandle(handle), notice);
            }
            catch (Exception ex)
            {
                AdvToolsLog.Warn("Update notice failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------ images

        internal static BitmapSource LoadPng(string fileName)
        {
            try
            {
                Assembly assembly = typeof(RevitAdvTools).Assembly;
                using (Stream stream = assembly.GetManifestResourceStream(AdvToolsUi.AssetPrefix + fileName))
                {
                    if (stream == null) return null;

                    var decoder = new PngBitmapDecoder(stream,
                        BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count == 0) return null;

                    BitmapSource frame = decoder.Frames[0];
                    frame.Freeze();   // the ribbon may touch it from another thread
                    return frame;
                }
            }
            catch
            {
                return null;   // a missing icon is cosmetic; it must never stop the ribbon building
            }
        }
    }
}
