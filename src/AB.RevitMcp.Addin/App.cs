using System;
using AB.RevitMcp.Addin.Bridge;
using AB.RevitMcp.Addin.Ui;
using AB.RevitMcp.Contracts.Json;
using ABAdvTools;
using ABAdvTools.Revit;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;

namespace AB.RevitMcp.Addin
{
    /// <summary>
    /// Add-in entry point.
    ///
    /// OnStartup runs on Revit's main thread before any document exists, which is exactly where an
    /// ExternalEvent must be created - so the dispatcher is initialised here and the bridge is
    /// ready long before the first MCP request arrives.
    /// </summary>
    public sealed class App : IExternalApplication
    {
        private BridgeService _service;
        private ControlledApplication _controlled;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                // The AB Adv Tools suite: shared ribbon tab and About panel, and a background check
                // for a newer release on GitHub. Never allowed to stop the bridge from loading.
                try
                {
                    RevitAdvTools.Initialize(application, Product);
                }
                catch (Exception) { }

                RibbonBuilder.Build(application);

                _service = new BridgeService(application);
                BridgeService.SetCurrent(_service);
                _service.StatusChanged += OnBridgeStatusChanged;

                _controlled = application.ControlledApplication;
                _controlled.ApplicationInitialized += OnApplicationInitialized;
                _controlled.DocumentOpened += OnDocumentChanged;
                _controlled.DocumentClosed += OnDocumentChanged;

                RibbonBuilder.UpdateStatus(_service);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                // A failed add-in startup should explain itself rather than silently vanish.
                TaskDialog.Show("AB MCP AI",
                    "The MCP bridge add-in failed to start.\n\n" + ex.GetType().Name + ": " + ex.Message +
                    "\n\nRevit will continue to run normally without it.");
                return Result.Failed;
            }
        }

        /// <summary>
        /// The bridge as the AB Adv Tools suite knows it. Details and the log-folder button carry
        /// what the bridge's own About dialog showed before it joined the suite.
        /// </summary>
        private static AdvToolsProduct Product
        {
            get
            {
                var product = new AdvToolsProduct(
                    "RevitMcp", Branding.ProductName,
                    "Lets any MCP-compatible AI client query and edit the open Revit model",
                    AdvToolsHost.Revit, "AB.RevitMcp", typeof(App).Assembly);

                product.Details = delegate
                {
                    BridgeService service = BridgeService.Current;
                    string nl = Environment.NewLine;
                    return "A universal Model Context Protocol bridge for Autodesk Revit 2020-2026. Any MCP-compatible " +
                           "AI client - Claude, Cursor, VS Code, DeepSeek, a local model - can query and edit the open " +
                           "Revit model through " + AB.RevitMcp.Contracts.Tools.ToolCatalog.Count + " tools." + nl +
                           (service != null ? "Revit " + service.RevitVersion + "   |   " : string.Empty) +
                           "Units: millimetres, m2, m3, degrees";
                };

                product.AddAction("Open the log folder", delegate
                {
                    string folder = AB.RevitMcp.Contracts.Protocol.IpcConstants.LogDirectory;
                    System.IO.Directory.CreateDirectory(folder);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
                });

                return product;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                if (_controlled != null)
                {
                    _controlled.ApplicationInitialized -= OnApplicationInitialized;
                    _controlled.DocumentOpened -= OnDocumentChanged;
                    _controlled.DocumentClosed -= OnDocumentChanged;
                }

                RevitAdvTools.Shutdown(application);

                if (_service != null)
                {
                    _service.StatusChanged -= OnBridgeStatusChanged;
                    _service.Dispose();          // stops the pipe and withdraws the endpoint file
                    BridgeService.SetCurrent(null);
                    _service = null;
                }
            }
            catch (Exception)
            {
                // Never block Revit from closing.
            }
            return Result.Succeeded;
        }

        /// <summary>
        /// Fired once, after Revit is fully up. This is the first moment an
        /// <see cref="Application"/> object exists, and the right place to honour auto-start.
        /// </summary>
        private void OnApplicationInitialized(object sender, ApplicationInitializedEventArgs e)
        {
            try
            {
                _service.AttachApplication(sender as Application);

                if (BridgeService.ReadAutoStart())
                {
                    _service.Start();
                    _service.Log.Info("Auto-start enabled; bridge started with Revit.",
                        J.O("pipeName", _service.PipeName));
                }
            }
            catch (Exception ex)
            {
                if (_service != null) _service.Log.Error("Auto-start failed.", ex);
            }
        }

        private void OnDocumentChanged(object sender, EventArgs e)
        {
            // Keep the ribbon caption honest when the user switches models.
            try { RibbonBuilder.UpdateStatus(_service); } catch (Exception) { }
        }

        private void OnBridgeStatusChanged(object sender, EventArgs e)
        {
            RibbonBuilder.UpdateStatus(_service);
        }
    }
}
