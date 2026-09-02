using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;
using AB.RevitMcp.Ipc;

namespace AB.RevitMcp.Server.Bridge
{
    /// <summary>
    /// The MCP server's link to Revit: endpoint discovery, connection management, reconnection,
    /// and one clear error message per failure mode.
    ///
    /// Connection is lazy and self-healing on purpose. Revit is usually started AFTER the MCP
    /// client launches this process, and it may be restarted several times during a session -
    /// neither should require the user to restart their AI client.
    /// </summary>
    public sealed class BridgeConnection : IDisposable
    {
        private readonly SemaphoreSlim _connectGate = new SemaphoreSlim(1, 1);
        private readonly ServerOptions _options;
        private readonly Action<string> _log;

        private PipeClient _client;
        private BridgeEndpoint _endpoint;

        public BridgeConnection(ServerOptions options, Action<string> log)
        {
            _options = options ?? new ServerOptions();
            _log = log ?? delegate { };
        }

        public bool IsConnected { get { return _client != null && _client.IsConnected; } }
        public BridgeEndpoint Endpoint { get { return _endpoint; } }

        /// <summary>
        /// Sends a tool request to Revit. Throws <see cref="BridgeUnavailableException"/> with an
        /// actionable message when Revit cannot be reached - the caller turns that into an MCP
        /// tool error rather than a protocol error, so the AI can explain it to the user.
        /// </summary>
        public async Task<BridgeResponse> SendAsync(BridgeRequest request, CancellationToken ct)
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);

            // The client-side wait is deliberately longer than the add-in's own budget, so the
            // add-in gets the chance to answer with a structured TIMEOUT instead of the pipe
            // simply going quiet.
            int waitMs = Math.Min(request.TimeoutMs + 5000, IpcConstants.MaxRequestTimeoutMs + 5000);

            try
            {
                string responseText = await _client
                    .SendAsync(request.ToJson().ToJson(), waitMs, ct)
                    .ConfigureAwait(false);

                JsonValue json;
                if (!JsonValue.TryParse(responseText, out json))
                    throw new BridgeUnavailableException("Revit returned a malformed response frame.");

                return BridgeResponse.FromJson(json);
            }
            catch (TimeoutException ex)
            {
                Drop();
                throw new BridgeUnavailableException(
                    "Revit did not answer within " + waitMs + " ms. It may be busy with a long " +
                    "operation or showing a modal dialog. " + ex.Message);
            }
            catch (IOException ex)
            {
                Drop();
                throw new BridgeUnavailableException(
                    "The connection to Revit was lost (" + ex.Message + "). " +
                    "If Revit is still open, press Stop then Start on the AB MCP AI ribbon and retry.");
            }
            catch (ObjectDisposedException)
            {
                Drop();
                throw new BridgeUnavailableException("The connection to Revit was closed. Retry the request.");
            }
        }

        private async Task EnsureConnectedAsync(CancellationToken ct)
        {
            if (IsConnected) return;

            await _connectGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (IsConnected) return;

                string pipeName = ResolvePipeName();

                var client = new PipeClient();
                try
                {
                    await client.ConnectAsync(pipeName, IpcConstants.ConnectTimeoutMs, ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    client.Dispose();
                    throw new BridgeUnavailableException(NotListeningMessage(pipeName));
                }
                catch (IOException ex)
                {
                    client.Dispose();
                    throw new BridgeUnavailableException(NotListeningMessage(pipeName) + " (" + ex.Message + ")");
                }
                catch (UnauthorizedAccessException ex)
                {
                    client.Dispose();
                    throw new BridgeUnavailableException(
                        "Access to the Revit bridge pipe was denied (" + ex.Message + "). " +
                        "Revit and this MCP server must run as the same Windows user - if Revit is " +
                        "elevated (Run as administrator) and your AI client is not, they cannot connect.");
                }

                _client = client;
                _log("Connected to Revit bridge on pipe " + pipeName +
                     (_endpoint != null ? " (Revit " + _endpoint.RevitVersion + ")" : string.Empty));
            }
            finally
            {
                _connectGate.Release();
            }
        }

        /// <summary>
        /// Picks the pipe to talk to: an explicit override wins, otherwise the newest live Revit
        /// session found in the discovery folder.
        /// </summary>
        private string ResolvePipeName()
        {
            if (!string.IsNullOrEmpty(_options.PipeName))
            {
                _endpoint = null;
                return _options.PipeName;
            }

            List<BridgeEndpoint> endpoints = EndpointRegistry.Discover(_options.RevitVersion);
            if (endpoints.Count == 0)
            {
                throw new BridgeUnavailableException(
                    "No running Revit session is exposing the MCP bridge" +
                    (string.IsNullOrEmpty(_options.RevitVersion)
                        ? "."
                        : " for Revit " + _options.RevitVersion + ".") +
                    "\n\nTo fix this:\n" +
                    "  1. Open Autodesk Revit and load a project.\n" +
                    "  2. Go to the 'AB MCP AI' ribbon tab and press 'Start Bridge'.\n" +
                    "  3. Retry this request - no need to restart your AI client.\n\n" +
                    "Discovery folder: " + IpcConstants.EndpointDirectory);
            }

            _endpoint = endpoints[0];

            if (endpoints.Count > 1)
            {
                var names = new List<string>();
                for (int i = 0; i < endpoints.Count; i++)
                    names.Add("Revit " + endpoints[i].RevitVersion + " (pid " + endpoints[i].ProcessId + ")");
                _log("Several Revit sessions are available: " + string.Join(", ", names) +
                     ". Using the most recently started. Set " + IpcConstants.RevitVersionEnvVar +
                     " or " + IpcConstants.PipeNameEnvVar + " to pin one.");
            }

            return _endpoint.PipeName;
        }

        private static string NotListeningMessage(string pipeName)
        {
            return "Could not connect to the Revit bridge on pipe '" + pipeName + "'. " +
                   "Revit may have closed, or the bridge may have been stopped. " +
                   "Open Revit, go to the 'AB MCP AI' ribbon tab and press 'Start Bridge'.";
        }

        private void Drop()
        {
            PipeClient client = _client;
            _client = null;
            if (client != null)
            {
                try { client.Dispose(); } catch (Exception) { }
            }
        }

        public JsonValue DescribeConnection()
        {
            JsonValue o = JsonValue.NewObject();
            o.Set("connected", IsConnected);
            o.Set("discoveryFolder", IpcConstants.EndpointDirectory);
            if (!string.IsNullOrEmpty(_options.PipeName)) o.Set("pinnedPipe", _options.PipeName);
            if (!string.IsNullOrEmpty(_options.RevitVersion)) o.Set("pinnedRevitVersion", _options.RevitVersion);
            if (_endpoint != null)
            {
                o.Set("pipeName", _endpoint.PipeName);
                o.Set("revitVersion", _endpoint.RevitVersion);
                o.Set("processId", _endpoint.ProcessId);
                o.Set("documentTitle", _endpoint.DocumentTitle);
            }
            return o;
        }

        public void Dispose()
        {
            Drop();
            try { _connectGate.Dispose(); } catch (Exception) { }
        }
    }

    /// <summary>Revit is not reachable. Always carries a message the end user can act on.</summary>
    public sealed class BridgeUnavailableException : Exception
    {
        public BridgeUnavailableException(string message) : base(message) { }
    }
}
