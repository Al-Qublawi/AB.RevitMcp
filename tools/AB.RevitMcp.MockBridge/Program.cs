using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;
using AB.RevitMcp.Contracts.Tools;
using AB.RevitMcp.Ipc;

namespace AB.RevitMcp.MockBridge
{
    /// <summary>
    /// Pretends to be the Revit add-in. It speaks the real IPC protocol on a real named pipe and
    /// publishes a real discovery endpoint, but serves canned answers instead of touching Revit.
    ///
    ///   * proves an MCP client's configuration works before Revit enters the picture
    ///   * exercises framing, discovery, reconnection and error mapping in CI
    /// </summary>
    public static class Program
    {
        private static readonly DateTime Started = DateTime.UtcNow;
        private static long _requests;

        public static async Task<int> Main(string[] args)
        {
            string revitVersion = "0000";     // deliberately not a real release
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--revit-version") revitVersion = args[i + 1];

            int pid = Process.GetCurrentProcess().Id;
            string pipeName = IpcConstants.BuildPipeName(revitVersion, pid);

            var server = new PipeServer(pipeName, HandleAsync,
                delegate (string message, Exception ex)
                {
                    Console.Error.WriteLine("[mock] " + message + (ex != null ? " :: " + ex.Message : string.Empty));
                });

            server.ConnectionsChanged += delegate
            {
                Console.Error.WriteLine("[mock] connections: " + server.ActiveConnections);
            };

            server.Start();

            EndpointRegistry.Publish(new BridgeEndpoint
            {
                PipeName = pipeName,
                ProcessId = pid,
                RevitVersion = revitVersion,
                RevitBuild = "mock",
                DocumentTitle = "MockProject.rvt",
                UserName = Environment.UserName,
                StartedUtc = Started
            });

            Console.Error.WriteLine("[mock] listening on " + pipeName);
            Console.Error.WriteLine("[mock] endpoint published in " + IpcConstants.EndpointDirectory);
            Console.Error.WriteLine("[mock] press Ctrl+C to stop");

            var stop = new TaskCompletionSource<bool>();
            Console.CancelKeyPress += delegate (object s, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                stop.TrySetResult(true);
            };

            int seconds = 0;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--seconds") int.TryParse(args[i + 1], out seconds);
            if (seconds > 0)
            {
                var ignored = Task.Delay(TimeSpan.FromSeconds(seconds))
                    .ContinueWith(delegate { stop.TrySetResult(true); });
            }

            await stop.Task.ConfigureAwait(false);

            server.Stop();
            EndpointRegistry.Withdraw(pid);
            Console.Error.WriteLine("[mock] stopped after serving " + _requests + " request(s)");
            return 0;
        }

        private static Task<string> HandleAsync(string payload, CancellationToken ct)
        {
            Interlocked.Increment(ref _requests);

            JsonValue json;
            if (!JsonValue.TryParse(payload, out json))
            {
                return Task.FromResult(BridgeResponse
                    .Failure(null, BridgeErrorCodes.ProtocolError, "Malformed JSON.")
                    .ToJson().ToJson());
            }

            BridgeRequest request = BridgeRequest.FromJson(json);
            Console.Error.WriteLine("[mock] -> " + request.Tool);

            BridgeResponse response = Dispatch(request);
            return Task.FromResult(response.ToJson().ToJson());
        }

        private static BridgeResponse Dispatch(BridgeRequest request)
        {
            switch (request.Tool)
            {
                case IpcConstants.OpPing:
                    return BridgeResponse.Success(request.Id,
                        J.O("pong", true, "revitVersion", "mock", "protocolVersion", IpcConstants.ProtocolVersion), 1);

                case IpcConstants.OpDescribe:
                    return BridgeResponse.Success(request.Id,
                        J.O("toolCount", ToolCatalog.Count, "tools", ToolCatalog.ToDescriptorArray()), 1);

                case IpcConstants.OpStatus:
                case "revit_bridge_status":
                    return BridgeResponse.Success(request.Id, J.O(
                        "running", true,
                        "mock", true,
                        "uptimeSeconds", Math.Round((DateTime.UtcNow - Started).TotalSeconds, 1),
                        "requestsServed", Interlocked.Read(ref _requests),
                        "toolsInCatalog", ToolCatalog.Count), 1);

                case "revit_get_model_info":
                    return BridgeResponse.Success(request.Id, J.O(
                        "title", "MockProject.rvt",
                        "revitVersion", "mock",
                        "isWorkshared", false,
                        "counts", J.O("elements", 12345, "levels", 3, "sheets", 8),
                        "units", J.O("length", "mm", "area", "m2", "volume", "m3", "angle", "degrees"),
                        "note", "This is the MOCK bridge. Start Revit and press Start Bridge for real data."), 3);

                case "revit_list_levels":
                    return BridgeResponse.Success(request.Id, J.O(
                        "levels", J.A(
                            J.O("id", 311L, "name", "Ground Floor", "elevationMm", 0),
                            J.O("id", 312L, "name", "Level 01", "elevationMm", 3600),
                            J.O("id", 313L, "name", "Roof", "elevationMm", 7200)),
                        "count", 3,
                        "unit", "mm"), 2);

                case "revit_delete_elements":
                    // Proves the confirmation interlock is enforced independently at both ends.
                    if (!request.Arguments["confirm"].AsBool(false))
                    {
                        return BridgeResponse.Failure(request.Id, BridgeErrorCodes.ConfirmationRequired,
                            "Destructive tool called without confirm:true.");
                    }
                    return BridgeResponse.Success(request.Id,
                        J.O("deletedCount", 0, "mock", true,
                            "note", "The mock bridge never deletes anything."), 1);

                default:
                    {
                        ToolDescriptor descriptor = ToolCatalog.Find(request.Tool);
                        if (descriptor == null)
                        {
                            return BridgeResponse.Failure(request.Id, BridgeErrorCodes.UnknownTool,
                                "Unknown tool '" + request.Tool + "'.");
                        }
                        return BridgeResponse.Failure(request.Id, BridgeErrorCodes.NoActiveDocument,
                            "'" + request.Tool + "' is a real tool, but the MOCK bridge does not implement it. " +
                            "Start Revit and press Start Bridge on the AB Adv Tools ribbon to use it.");
                    }
            }
        }
    }
}
