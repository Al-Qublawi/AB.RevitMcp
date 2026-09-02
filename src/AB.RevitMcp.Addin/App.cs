using System;
using AB.RevitMcp.Addin.Bridge;
using AB.RevitMcp.Addin.Ui;
using AB.RevitMcp.Contracts.Json;
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
