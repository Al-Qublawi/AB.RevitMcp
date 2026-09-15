using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using AB.RevitMcp.Addin.Bridge;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;
using AB.RevitMcp.Contracts.Tools;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace AB.RevitMcp.Addin.Ui
{
    /// <summary>Ribbon button handlers. Each is a standard Revit external command.</summary>
    public static class Commands
    {
        // ==================================================================
        //  Start / stop
        // ==================================================================
        [Transaction(TransactionMode.Manual)]
        public class ToggleBridgeCommand : IExternalCommand
        {
            public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
            {
                BridgeService service = BridgeService.Current;
                if (service == null)
                {
                    message = "The MCP bridge did not initialise. Check the Revit journal and the add-in log.";
                    return Result.Failed;
                }

                try
                {
                    bool wasRunning = service.IsRunning;
                    service.Toggle();

                    var dialog = new TaskDialog("AB MCP AI");
                    dialog.MainInstruction = service.IsRunning ? "Bridge started" : "Bridge stopped";
                    dialog.MainContent = service.IsRunning
                        ? "Revit is now listening on:\n\n    " + service.PipeName +
                          "\n\nStart your MCP client (Claude Desktop, Claude Code, Cursor, VS Code, ...). " +
                          "It will discover this session automatically.\n\n" +
                          service.Router.HandlerCount + " tools are available. Destructive tools still require " +
                          "an explicit confirmation from the AI on every call."
                        : "The named pipe is closed. No AI client can read or modify this model until the " +
                          "bridge is started again.";
                    dialog.CommonButtons = TaskDialogCommonButtons.Close;
                    dialog.Show();

                    if (!wasRunning && service.IsRunning) service.Log.Info("Bridge started from the ribbon.");
                    return Result.Succeeded;
                }
                catch (Exception ex)
                {
                    message = "Could not toggle the bridge: " + ex.Message;
                    return Result.Failed;
                }
            }
        }

        // ==================================================================
        //  Status
        // ==================================================================
        [Transaction(TransactionMode.Manual)]
        public class ShowStatusCommand : IExternalCommand
        {
            public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
            {
                BridgeService service = BridgeService.Current;
                if (service == null)
                {
                    TaskDialog.Show("AB MCP AI", "The bridge did not initialise in this Revit session.");
                    return Result.Failed;
                }

                JsonValue status = service.StatusJson();
                var sb = new StringBuilder();

                sb.AppendLine(service.IsRunning
                    ? (service.ConnectionCount > 0
                        ? "CONNECTED - " + service.ConnectionCount + " client(s) attached"
                        : "LISTENING - no client attached yet")
                    : "STOPPED");
                sb.AppendLine();
                sb.AppendLine("Pipe            " + service.PipeName);
                sb.AppendLine("Revit           " + service.RevitVersion + "  (build " + service.RevitBuild + ")");
                sb.AppendLine("Add-in built for Revit " + status["compiledFor"].AsString("?"));
                sb.AppendLine("Protocol        v" + IpcConstants.ProtocolVersion);
                sb.AppendLine();
                sb.AppendLine("Requests served " + service.RequestCount + "  (" + service.ErrorCount + " error(s))");
                sb.AppendLine("Queue depth     " + status["queueDepth"].AsInt(0));
                sb.AppendLine("Avg execution   " + status["averageExecutionMs"].AsDouble(0) + " ms");
                sb.AppendLine("Timed out       " + status["timedOut"].AsLong(0));
                sb.AppendLine("Last tool       " + (service.LastRequestTool ?? "(none yet)"));
                sb.AppendLine();
                sb.AppendLine("Tools           " + service.Router.HandlerCount + " of " + ToolCatalog.Count +
                              " catalog entries implemented");

                string lastError = status["lastDispatcherError"].AsString(null);
                if (!string.IsNullOrEmpty(lastError))
                {
                    sb.AppendLine();
                    sb.AppendLine("Last error      " + lastError);
                }

                var dialog = new TaskDialog("AB MCP AI - Bridge status");
                dialog.MainInstruction = "Bridge status";
                dialog.MainContent = sb.ToString();
                dialog.ExpandedContent = string.Join(Environment.NewLine,
                    service.Log.RecentEntries(12).ToArray());
                dialog.CommonButtons = TaskDialogCommonButtons.Close;
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the log folder");
                TaskDialogResult result = dialog.Show();

                if (result == TaskDialogResult.CommandLink1) OpenFolder(IpcConstants.LogDirectory);
                return Result.Succeeded;
            }
        }

        // ==================================================================
        //  Tool inventory
        // ==================================================================
        [Transaction(TransactionMode.Manual)]
        public class ShowToolsCommand : IExternalCommand
        {
            public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
            {
                var read = new List<string>();
                var write = new List<string>();
                var destructive = new List<string>();

                foreach (ToolDescriptor descriptor in ToolCatalog.All)
                {
                    switch (descriptor.Category)
                    {
                        case ToolCategory.Read: read.Add(descriptor.Name); break;
                        case ToolCategory.Write: write.Add(descriptor.Name); break;
                        default: destructive.Add(descriptor.Name); break;
                    }
                }

                var sb = new StringBuilder();
                sb.AppendLine("READ (" + read.Count + ") - never open a transaction:");
                sb.AppendLine("  " + string.Join(", ", read.ToArray()));
                sb.AppendLine();
                sb.AppendLine("WRITE (" + write.Count + ") - one undo step each:");
                sb.AppendLine("  " + string.Join(", ", write.ToArray()));
                sb.AppendLine();
                sb.AppendLine("DESTRUCTIVE (" + destructive.Count + ") - require confirm:true:");
                sb.AppendLine("  " + string.Join(", ", destructive.ToArray()));

                var dialog = new TaskDialog("AB MCP AI - Tools");
                dialog.MainInstruction = ToolCatalog.Count + " MCP tools";
                dialog.MainContent = "All lengths are millimetres, areas m2, volumes m3, angles degrees.";
                dialog.ExpandedContent = sb.ToString();
                dialog.CommonButtons = TaskDialogCommonButtons.Close;
                dialog.Show();
                return Result.Succeeded;
            }
        }

        // ==================================================================
        //  Copy the mcpServers configuration block
        // ==================================================================
        [Transaction(TransactionMode.Manual)]
        public class CopyConfigCommand : IExternalCommand
        {
            public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
            {
                BridgeService service = BridgeService.Current;
                string serverPath = RibbonBuilder.ExpectedServerPath();
                bool serverPresent = !string.IsNullOrEmpty(serverPath) && File.Exists(serverPath);

                JsonValue env = JsonValue.NewObject();
                if (service != null)
                {
                    env.Set(IpcConstants.RevitVersionEnvVar, service.RevitVersion);
                }

                JsonValue server = JsonValue.NewObject();
                if (serverPresent)
                {
                    // Started the way the AI Clients window configures it: through dotnet.exe when that
                    // works, so Defender's rule against unknown executables cannot block the server.
                    AiClients.LaunchSpec spec = AiClients.LaunchSpec.For(serverPath, null);
                    server.Set("command", spec.Command);
                    server.Set("args", spec.ArgsJson());
                }
                else
                {
                    server.Set("command", serverPath ?? "C:\\\\Program Files\\\\ABRevitMcp\\\\Server\\\\AB.RevitMcp.Server.exe");
                    server.Set("args", JsonValue.NewArray());
                }
                server.Set("env", env);

                JsonValue servers = JsonValue.NewObject();
                servers.Set("revit", server);

                JsonValue root = JsonValue.NewObject();
                root.Set("mcpServers", servers);

                string json = root.ToJson(true);

                bool copied = false;
                try { System.Windows.Clipboard.SetText(json); copied = true; }
                catch (Exception) { }

                var dialog = new TaskDialog("AB MCP AI - MCP client configuration");
                dialog.MainInstruction = copied
                    ? "Configuration copied to the clipboard"
                    : "Configuration (copy manually)";
                dialog.MainContent =
                    "Paste this into your MCP client's configuration file:\n\n" +
                    "  Claude Desktop   %APPDATA%\\Claude\\claude_desktop_config.json\n" +
                    "  Claude Code      .mcp.json in the project, or `claude mcp add`\n" +
                    "  Cursor           %USERPROFILE%\\.cursor\\mcp.json\n" +
                    "  VS Code          .vscode\\mcp.json\n\n" +
                    (serverPresent
                        ? "The server was found. AI Clients on this panel writes these files for you."
                        : "WARNING: the server executable was not found at the path below. " +
                          "Install it, then edit the \"command\" value to match.");
                dialog.ExpandedContent = json;
                dialog.CommonButtons = TaskDialogCommonButtons.Close;
                dialog.Show();
                return Result.Succeeded;
            }
        }

        // ==================================================================
        //  Logs
        // ==================================================================
        [Transaction(TransactionMode.Manual)]
        public class OpenLogsCommand : IExternalCommand
        {
            public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
            {
                OpenFolder(IpcConstants.LogDirectory);
                return Result.Succeeded;
            }
        }

        // ==================================================================
        //  Auto-start preference
        // ==================================================================
        [Transaction(TransactionMode.Manual)]
        public class ToggleAutoStartCommand : IExternalCommand
        {
            public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
            {
                bool enabled = !BridgeService.ReadAutoStart();
                BridgeService.WriteAutoStart(enabled);
                RibbonBuilder.UpdateAutoStartCaption(enabled);

                TaskDialog.Show("AB MCP AI",
                    enabled
                        ? "The bridge will start automatically when Revit opens.\n\n" +
                          "Only enable this on a machine you control: any MCP client running as your " +
                          "Windows user will be able to reach the open model."
                        : "Auto-start is off. The bridge will stay stopped until you start it from the ribbon.");
                return Result.Succeeded;
            }
        }

        // ==================================================================
        //  Code execution gate
        // ==================================================================
        [Transaction(TransactionMode.Manual)]
        public class ToggleCodeExecutionCommand : IExternalCommand
        {
            public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
            {
                bool enabling = !BridgeService.ReadAllowCodeExecution();

                if (enabling)
                {
                    var confirm = new TaskDialog(Branding.ProductName);
                    confirm.MainInstruction = "Enable AI code execution?";
                    confirm.MainContent =
                        "This lets a connected AI compile and run ARBITRARY C# against the open " +
                        "model. There is no schema validation and no limit on what the code can do - " +
                        "it can delete, overwrite or corrupt anything the Revit API can reach." +
                        Environment.NewLine + Environment.NewLine +
                        "Every other tool here is bounded and validated. Only enable this if you " +
                        "genuinely need something none of them cover, and turn it off afterwards." +
                        Environment.NewLine + Environment.NewLine +
                        "The MCP server must ALSO be started with --allow-code-execution, and every " +
                        "call still needs an explicit confirmation.";
                    confirm.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                    confirm.DefaultButton = TaskDialogResult.No;

                    if (confirm.Show() != TaskDialogResult.Yes) return Result.Cancelled;
                }

                BridgeService.WriteAllowCodeExecution(enabling);
                RibbonBuilder.UpdateCodeExecutionCaption(enabling);

                BridgeService service = BridgeService.Current;
                if (service != null)
                {
                    service.Log.Warn("Code execution " + (enabling ? "ENABLED" : "disabled") +
                                     " from the ribbon.");
                }

                TaskDialog.Show(Branding.ProductName,
                    enabling
                        ? "Code execution is ON for this machine. Turn it off when you are done."
                        : "Code execution is OFF. revit_execute_code will refuse every call.");
                return Result.Succeeded;
            }
        }

        // ==================================================================
        //  AI clients and Verify (Setup.exe's window until 1.4.0)
        // ==================================================================
        [Transaction(TransactionMode.Manual)]
        public class AiClientsCommand : IExternalCommand
        {
            public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
            {
                AiClients.AiClientsForm.ShowWindow(false);
                return Result.Succeeded;
            }
        }

        [Transaction(TransactionMode.Manual)]
        public class VerifyCommand : IExternalCommand
        {
            public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
            {
                AiClients.AiClientsForm.ShowWindow(true);
                return Result.Succeeded;
            }
        }

        // About, Check for Updates and LinkedIn moved to the AB Adv Tools shared panel
        // (ABAdvTools.Revit), which every AB add-in shares.

        // ==================================================================
        //  helper
        // ==================================================================
        private static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                TaskDialog.Show("AB MCP AI", "Could not open the folder:\n" + path + "\n\n" + ex.Message);
            }
        }
    }
}
