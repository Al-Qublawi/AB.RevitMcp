using System;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using AB.RevitMcp.Addin.Bridge;
using AB.RevitMcp.Contracts.Tools;
using ABAdvTools.Revit;
using Autodesk.Revit.UI;

namespace AB.RevitMcp.Addin.Ui
{
    /// <summary>
    /// Builds and maintains the MCP Bridge panel on the shared "AB Adv Tools" ribbon tab. The
    /// toggle button doubles as the status light: colour and caption always reflect what the
    /// bridge is actually doing. About and LinkedIn live on the suite's shared panel.
    /// </summary>
    public static class RibbonBuilder
    {
        /// <summary>Panel names must be unique across every AB add-in on the tab.</summary>
        public const string PanelName = "MCP Bridge";

        public static string TabName
        {
            get { return RevitAdvTools.TabName; }
        }

        private static PushButton _toggleButton;
        private static PushButton _autoStartButton;
        private static PushButton _codeExecButton;
        private static Dispatcher _uiDispatcher;

        public static void Build(UIControlledApplication application)
        {
            _uiDispatcher = Dispatcher.CurrentDispatcher;

            RibbonPanel panel = RevitAdvTools.GetToolPanel(application, PanelName);
            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            // ---- primary toggle ----
            var toggleData = new PushButtonData(
                "ABMcpToggle", "Start\nBridge", assemblyPath, typeof(Commands.ToggleBridgeCommand).FullName);
            toggleData.ToolTip = "Start or stop the MCP bridge that lets an AI assistant read and edit this model.";
            toggleData.LongDescription =
                "Opens a local named pipe that the AB Revit MCP server connects to. " +
                "Nothing leaves this machine over the pipe, and no AI can reach Revit while the bridge is stopped.";
            _toggleButton = panel.AddItem(toggleData) as PushButton;
            if (_toggleButton != null)
            {
                _toggleButton.LargeImage = IconFactory.LogoWithStatus(Branding.LogoLarge, IconFactory.Disconnected, 32);
                _toggleButton.Image = IconFactory.LogoWithStatus(Branding.LogoSmall, IconFactory.Disconnected, 16);
            }

            panel.AddSeparator();

            // ---- stacked utilities ----
            var statusData = new PushButtonData(
                "ABMcpStatus", "Status", assemblyPath, typeof(Commands.ShowStatusCommand).FullName);
            statusData.ToolTip = "Show connection state, request counts and recent activity.";
            statusData.Image = IconFactory.Badge(16, IconFactory.Accent);

            var toolsData = new PushButtonData(
                "ABMcpTools", "Tools", assemblyPath, typeof(Commands.ShowToolsCommand).FullName);
            toolsData.ToolTip = "List the MCP tools this bridge exposes, grouped by risk category.";
            toolsData.Image = IconFactory.Badge(16, IconFactory.Accent);

            var configData = new PushButtonData(
                "ABMcpConfig", "Copy config", assemblyPath, typeof(Commands.CopyConfigCommand).FullName);
            configData.ToolTip = "Copy a ready-to-paste mcpServers JSON block for Claude, Cursor, VS Code or any " +
                                 "other MCP client.";
            configData.Image = IconFactory.Badge(16, IconFactory.Accent);

            panel.AddStackedItems(statusData, toolsData, configData);

            var logsData = new PushButtonData(
                "ABMcpLogs", "Open logs", assemblyPath, typeof(Commands.OpenLogsCommand).FullName);
            logsData.ToolTip = "Open the folder holding the structured JSON request logs.";
            logsData.Image = IconFactory.Badge(16, IconFactory.Accent);

            var autoStartData = new PushButtonData(
                "ABMcpAutoStart", "Auto-start: off", assemblyPath, typeof(Commands.ToggleAutoStartCommand).FullName);
            autoStartData.ToolTip = "Start the bridge automatically the next time Revit opens.";
            autoStartData.Image = IconFactory.Badge(16, IconFactory.Disconnected);

            var codeExecData = new PushButtonData(
                "ABMcpCodeExec", "Code exec: off", assemblyPath,
                typeof(Commands.ToggleCodeExecutionCommand).FullName);
            codeExecData.ToolTip = "Allow a connected AI to compile and run arbitrary Revit API C#.";
            codeExecData.LongDescription =
                "OFF by default, and deliberately hard to turn on: the MCP server must also be " +
                "started with --allow-code-execution, and every call needs an explicit confirmation.";
            codeExecData.Image = IconFactory.Badge(16, IconFactory.Disconnected);

            System.Collections.Generic.IList<RibbonItem> stacked =
                panel.AddStackedItems(logsData, autoStartData, codeExecData);
            if (stacked != null && stacked.Count > 1) _autoStartButton = stacked[1] as PushButton;
            if (stacked != null && stacked.Count > 2) _codeExecButton = stacked[2] as PushButton;

            // About, Check for Updates and LinkedIn are on the AB Adv Tools shared panel.

            UpdateAutoStartCaption(BridgeService.ReadAutoStart());
            UpdateCodeExecutionCaption(BridgeService.ReadAllowCodeExecution());
        }

        /// <summary>
        /// Repaints the toggle button. Safe to call from ANY thread - pipe connection events fire
        /// on the thread pool, and touching a ribbon control off the UI thread would crash Revit.
        /// </summary>
        public static void UpdateStatus(BridgeService service)
        {
            if (_toggleButton == null) return;

            if (_uiDispatcher != null && !_uiDispatcher.CheckAccess())
            {
                try { _uiDispatcher.BeginInvoke(new Action(delegate { ApplyStatus(service); })); }
                catch (Exception) { }
                return;
            }
            ApplyStatus(service);
        }

        private static void ApplyStatus(BridgeService service)
        {
            try
            {
                bool running = service != null && service.IsRunning;
                int connections = service != null ? service.ConnectionCount : 0;

                System.Windows.Media.Color colour =
                    !running ? IconFactory.Disconnected :
                    connections > 0 ? IconFactory.Connected : IconFactory.Busy;

                _toggleButton.ItemText = running ? "Stop\nBridge" : "Start\nBridge";
                _toggleButton.LargeImage = IconFactory.LogoWithStatus(Branding.LogoLarge, colour, 32);
                _toggleButton.Image = IconFactory.LogoWithStatus(Branding.LogoSmall, colour, 16);

                string state = !running
                    ? "STOPPED - no AI client can reach this model."
                    : connections > 0
                        ? "CONNECTED - " + connections + " MCP client(s) attached."
                        : "LISTENING - waiting for an MCP client to connect.";

                _toggleButton.ToolTip = state;
                _toggleButton.LongDescription = service == null
                    ? state
                    : state + "\n\nPipe: " + service.PipeName +
                      "\nRevit: " + service.RevitVersion +
                      "\nRequests served: " + service.RequestCount +
                      " (" + service.ErrorCount + " error(s))" +
                      "\nTools: " + service.Router.HandlerCount + " of " + ToolCatalog.Count + " implemented";
            }
            catch (Exception)
            {
                // A ribbon repaint must never take Revit down.
            }
        }

        public static void UpdateAutoStartCaption(bool enabled)
        {
            if (_autoStartButton == null) return;

            Action apply = delegate
            {
                try
                {
                    _autoStartButton.ItemText = enabled ? "Auto-start: on" : "Auto-start: off";
                    _autoStartButton.Image = IconFactory.Badge(16,
                        enabled ? IconFactory.Connected : IconFactory.Disconnected);
                }
                catch (Exception) { }
            };

            if (_uiDispatcher != null && !_uiDispatcher.CheckAccess())
            {
                try { _uiDispatcher.BeginInvoke(apply); } catch (Exception) { }
            }
            else apply();
        }

        /// <summary>Repaints the code-execution toggle. Red when on - it should look alarming.</summary>
        public static void UpdateCodeExecutionCaption(bool enabled)
        {
            if (_codeExecButton == null) return;

            Action apply = delegate
            {
                try
                {
                    _codeExecButton.ItemText = enabled ? "Code exec: ON" : "Code exec: off";
                    _codeExecButton.Image = IconFactory.Badge(16,
                        enabled ? IconFactory.Danger : IconFactory.Disconnected);
                }
                catch (Exception) { }
            };

            if (_uiDispatcher != null && !_uiDispatcher.CheckAccess())
            {
                try { _uiDispatcher.BeginInvoke(apply); } catch (Exception) { }
            }
            else apply();
        }

        /// <summary>
        /// Locates the MCP server executable so the "Copy config" button can emit a working
        /// absolute path. Checks the layouts the installer produces, then falls back to the first
        /// candidate so the dialog can still show the user where to put it.
        /// </summary>
        public static string ExpectedServerPath()
        {
            var candidates = new System.Collections.Generic.List<string>();
            try
            {
                string addinDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(addinDir))
                {
                    // side-by-side (developer build output)
                    candidates.Add(Path.GetFullPath(Path.Combine(addinDir, "Server", "AB.RevitMcp.Server.exe")));
                    // installed layout: <root>\bin\Revit20xx\  ->  <root>\Server\
                    candidates.Add(Path.GetFullPath(Path.Combine(addinDir, "..", "..", "Server", "AB.RevitMcp.Server.exe")));
                    // repository layout: artifacts\<config>\Revit20xx\ -> artifacts\<config>\Server\
                    candidates.Add(Path.GetFullPath(Path.Combine(addinDir, "..", "Server", "AB.RevitMcp.Server.exe")));
                }

                candidates.Add(Path.Combine(
                    AB.RevitMcp.Contracts.Protocol.IpcConstants.DataRoot, "Server", "AB.RevitMcp.Server.exe"));

                for (int i = 0; i < candidates.Count; i++)
                    if (File.Exists(candidates[i])) return candidates[i];
            }
            catch (Exception) { }

            return candidates.Count > 0 ? candidates[0] : null;
        }
    }
}
