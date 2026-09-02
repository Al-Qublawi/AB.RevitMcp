using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AB.RevitMcp.Addin.Revit;
using AB.RevitMcp.Contracts.Json;
using AB.RevitMcp.Contracts.Protocol;
using AB.RevitMcp.Contracts.Tools;
using AB.RevitMcp.Ipc;
using Autodesk.Revit.UI;

namespace AB.RevitMcp.Addin.Bridge
{
    /// <summary>
    /// Owns the whole bridge lifecycle inside Revit: the named-pipe listener, the UI-thread
    /// dispatcher, the tool router, the log and the discovery file the MCP server reads.
    ///
    /// Threading contract:
    ///   * Start / Stop are called from the Revit UI thread (ribbon buttons, app startup/shutdown)
    ///   * HandleRequestAsync runs on thread-pool threads and NEVER touches the Revit API directly
    /// </summary>
    public sealed class BridgeService : IDisposable
    {
        private static BridgeService _current;

        private readonly RevitDispatcher _dispatcher = new RevitDispatcher();
        private readonly RequestLog _log;
        private readonly ToolRouter _router;
        private readonly string _revitVersion;
        private readonly string _revitBuild;
        private readonly string _userName;
        private readonly int _processId;
        private readonly string _pipeName;
        private readonly DateTime _createdUtc = DateTime.UtcNow;

        private PipeServer _pipeServer;
        private DateTime _startedUtc;
        private long _requestCount;
        private long _errorCount;
        private string _lastRequestTool;
        private DateTime _lastRequestUtc;

        public BridgeService(UIControlledApplication application)
        {
            _processId = Process.GetCurrentProcess().Id;
            _revitVersion = SafeVersion(application);
            _revitBuild = SafeBuild(application);
            _userName = SafeUser(application);
            _pipeName = IpcConstants.BuildPipeName(_revitVersion, _processId);

            _log = new RequestLog(_processId, _revitVersion);
            _router = new ToolRouter(_log);
            _dispatcher.Initialize();

            _log.Info("Bridge created.", J.O(
                "pipeName", _pipeName,
                "revitVersion", _revitVersion,
                "revitBuild", _revitBuild,
                "compiledFor", Compat.RevitReleaseName,
                "toolsInCatalog", ToolCatalog.Count,
                "toolsImplemented", _router.HandlerCount));
        }

        public static BridgeService Current { get { return _current; } }
        public static void SetCurrent(BridgeService service) { _current = service; }

        public RevitDispatcher Dispatcher { get { return _dispatcher; } }
        public ToolRouter Router { get { return _router; } }
        public RequestLog Log { get { return _log; } }
        public string PipeName { get { return _pipeName; } }
        public string RevitVersion { get { return _revitVersion; } }
        public string RevitBuild { get { return _revitBuild; } }
        public int ProcessId { get { return _processId; } }
        public DateTime StartedUtc { get { return _startedUtc; } }
        public long RequestCount { get { return Interlocked.Read(ref _requestCount); } }
        public long ErrorCount { get { return Interlocked.Read(ref _errorCount); } }
        public string LastRequestTool { get { return _lastRequestTool; } }
        public DateTime LastRequestUtc { get { return _lastRequestUtc; } }

        public bool IsRunning { get { return _pipeServer != null && _pipeServer.IsRunning; } }
        public int ConnectionCount { get { return _pipeServer != null ? _pipeServer.ActiveConnections : 0; } }

        /// <summary>Raised whenever the connection state changes, so the ribbon can repaint.</summary>
        public event EventHandler StatusChanged;

        // ------------------------------------------------------------------
        //  lifecycle
        // ------------------------------------------------------------------

        public void Start()
        {
            if (IsRunning) return;

            _pipeServer = new PipeServer(_pipeName, HandleRequestAsync, PipeLog, 8);
            _pipeServer.ConnectionsChanged += delegate { RaiseStatusChanged(); };
            _pipeServer.Start();
            _startedUtc = DateTime.UtcNow;

            try
            {
                EndpointRegistry.Publish(new BridgeEndpoint
                {
                    PipeName = _pipeName,
                    ProcessId = _processId,
                    RevitVersion = _revitVersion,
                    RevitBuild = _revitBuild,
                    DocumentTitle = CurrentDocumentTitle(),
                    UserName = _userName,
                    StartedUtc = _startedUtc
                });
            }
            catch (Exception ex)
            {
                _log.Error("Could not publish the endpoint descriptor. The MCP server will need " +
                           "AB_REVITMCP_PIPE=" + _pipeName + " to find this session.", ex);
            }

            _log.Info("Bridge started.", J.O("pipeName", _pipeName));
            RaiseStatusChanged();
        }

        public void Stop()
        {
            if (_pipeServer != null)
            {
                try { _pipeServer.Stop(); } catch (Exception ex) { _log.Error("Error stopping the pipe server.", ex); }
                try { _pipeServer.Dispose(); } catch (Exception) { }
                _pipeServer = null;
            }

            EndpointRegistry.Withdraw(_processId);
            _dispatcher.DrainAndFail("The bridge was stopped.");
            _log.Info("Bridge stopped.", J.O("requestsServed", RequestCount, "errors", ErrorCount));
            RaiseStatusChanged();
        }

        public void Toggle()
        {
            if (IsRunning) Stop(); else Start();
        }

        // ------------------------------------------------------------------
        //  request handling (BACKGROUND THREAD - no Revit API here)
        // ------------------------------------------------------------------

        private async Task<string> HandleRequestAsync(string payload, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            BridgeRequest request = null;

            try
            {
                JsonValue json;
                if (!JsonValue.TryParse(payload, out json))
                {
                    return BridgeResponse.Failure(null, BridgeErrorCodes.ProtocolError,
                        "The request was not valid JSON.").ToJson().ToJson();
                }

                request = BridgeRequest.FromJson(json);
                Interlocked.Increment(ref _requestCount);
                _lastRequestTool = request.Tool;
                _lastRequestUtc = DateTime.UtcNow;

                // Bridge-level operations are answered here; they need no Revit API access, so
                // they still work while Revit is busy with a long command.
                BridgeResponse local = HandleBridgeOperation(request);
                if (local != null) return Finish(request, local, sw);

                if (string.IsNullOrEmpty(request.Tool))
                {
                    return Finish(request, BridgeResponse.Failure(request.Id, BridgeErrorCodes.ProtocolError,
                        "The request did not name a tool."), sw);
                }

                // Hand the work to Revit's UI thread and wait for it.
                BridgeResponse response;
                try
                {
                    response = await _dispatcher.EnqueueAsync(
                        request.Tool,
                        delegate (UIApplication app) { return _router.Execute(app, request); },
                        request.TimeoutMs,
                        ct).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    response = BridgeResponse.Failure(request.Id, BridgeErrorCodes.Timeout, ex.Message,
                        J.O("timeoutMs", request.TimeoutMs, "queueDepth", _dispatcher.PendingCount));
                }
                catch (OperationCanceledException)
                {
                    response = BridgeResponse.Failure(request.Id, BridgeErrorCodes.Cancelled,
                        "The request was cancelled before Revit could run it.");
                }
                catch (InvalidOperationException ex)
                {
                    response = BridgeResponse.Failure(request.Id, BridgeErrorCodes.NotConnected, ex.Message);
                }

                return Finish(request, response, sw);
            }
            catch (Exception ex)
            {
                _log.Error("Failed to handle a bridge request.", ex);
                Interlocked.Increment(ref _errorCount);
                return BridgeResponse.Failure(request != null ? request.Id : null,
                    BridgeErrorCodes.Internal, "Internal bridge error: " + ex.Message)
                    .ToJson().ToJson();
            }
        }

        private string Finish(BridgeRequest request, BridgeResponse response, Stopwatch sw)
        {
            sw.Stop();
            if (response.DurationMs <= 0) response.DurationMs = sw.Elapsed.TotalMilliseconds;

            string text = response.ToJson().ToJson();
            if (!response.Ok) Interlocked.Increment(ref _errorCount);

            ToolDescriptor descriptor = ToolCatalog.Find(request.Tool);
            _log.LogRequest(request.Id, request.Tool,
                descriptor != null ? descriptor.Category.ToString().ToLowerInvariant() : "bridge",
                response.Ok, sw.Elapsed.TotalMilliseconds,
                response.ErrorCode, response.ErrorMessage, text.Length, request.ClientName);

            RaiseStatusChanged();
            return text;
        }

        /// <summary>Handles bridge/* control operations without entering the Revit API.</summary>
        private BridgeResponse HandleBridgeOperation(BridgeRequest request)
        {
            if (string.Equals(request.Tool, IpcConstants.OpPing, StringComparison.OrdinalIgnoreCase))
            {
                return BridgeResponse.Success(request.Id, J.O(
                    "pong", true,
                    "protocolVersion", IpcConstants.ProtocolVersion,
                    "revitVersion", _revitVersion,
                    "pipeName", _pipeName), 0);
            }

            if (string.Equals(request.Tool, IpcConstants.OpDescribe, StringComparison.OrdinalIgnoreCase))
            {
                JsonValue implemented = JsonValue.NewArray();
                foreach (ToolDescriptor d in ToolCatalog.All)
                    if (_router.IsImplemented(d.Name)) implemented.Add(d.ToDescriptorJson());

                return BridgeResponse.Success(request.Id, J.O(
                    "protocolVersion", IpcConstants.ProtocolVersion,
                    "revitVersion", _revitVersion,
                    "revitBuild", _revitBuild,
                    "compiledFor", Compat.RevitReleaseName,
                    "toolCount", implemented.Count,
                    "tools", implemented), 0);
            }

            if (string.Equals(request.Tool, IpcConstants.OpStatus, StringComparison.OrdinalIgnoreCase))
            {
                return BridgeResponse.Success(request.Id, StatusJson(), 0);
            }

            return null;   // not a bridge operation - route it to Revit
        }

        /// <summary>Diagnostics payload, shared by bridge/status and the revit_bridge_status tool.</summary>
        public JsonValue StatusJson()
        {
            return J.O(
                "running", IsRunning,
                "pipeName", _pipeName,
                "processId", _processId,
                "revitVersion", _revitVersion,
                "revitBuild", _revitBuild,
                "compiledFor", Compat.RevitReleaseName,
                "protocolVersion", IpcConstants.ProtocolVersion,
                "activeConnections", ConnectionCount,
                "uptimeSeconds", IsRunning ? Math.Round((DateTime.UtcNow - _startedUtc).TotalSeconds, 1) : 0d,
                "createdUtc", _createdUtc.ToString("o"),
                "requestsServed", RequestCount,
                "errors", ErrorCount,
                "queueDepth", _dispatcher.PendingCount,
                "completedOnUiThread", _dispatcher.CompletedCount,
                "timedOut", _dispatcher.TimedOutCount,
                "averageExecutionMs", _dispatcher.AverageExecutionMs,
                "lastRequestTool", _lastRequestTool,
                "lastRequestUtc", _lastRequestUtc == DateTime.MinValue ? null : _lastRequestUtc.ToString("o"),
                "lastDispatcherError", _dispatcher.LastError,
                "toolsInCatalog", ToolCatalog.Count,
                "toolsImplemented", _router.HandlerCount,
                "logDirectory", _log.LogDirectory,
                "units", Metric.Convention());
        }

        // ------------------------------------------------------------------
        //  helpers
        // ------------------------------------------------------------------

        private void PipeLog(string message, Exception ex)
        {
            if (ex != null) _log.Error(message, ex);
            else _log.Warn(message);
        }

        private string CurrentDocumentTitle()
        {
            // Called from Start(), which runs on the UI thread, so this is safe.
            try
            {
                UIApplication uiApp = new UIApplication(GetApplication());
                if (uiApp.ActiveUIDocument != null && uiApp.ActiveUIDocument.Document != null)
                    return uiApp.ActiveUIDocument.Document.Title;
            }
            catch (Exception) { }
            return null;
        }

        private Autodesk.Revit.ApplicationServices.Application _application;
        public void AttachApplication(Autodesk.Revit.ApplicationServices.Application application)
        {
            _application = application;
        }
        private Autodesk.Revit.ApplicationServices.Application GetApplication() { return _application; }

        private static string SafeVersion(UIControlledApplication app)
        {
            try { return app.ControlledApplication.VersionNumber; }
            catch (Exception) { return Compat.RevitReleaseName; }
        }

        private static string SafeBuild(UIControlledApplication app)
        {
            try { return app.ControlledApplication.VersionBuild; }
            catch (Exception) { return null; }
        }

        private static string SafeUser(UIControlledApplication app)
        {
            // ControlledApplication does not expose Username on every release, and the Application
            // object does not exist yet at OnStartup - the Windows user is the reliable answer.
            try { return Environment.UserName; } catch (Exception) { return null; }
        }

        private void RaiseStatusChanged()
        {
            EventHandler h = StatusChanged;
            if (h == null) return;
            try { h(this, EventArgs.Empty); }
            catch (Exception ex) { _log.Error("StatusChanged handler threw.", ex); }
        }

        public void Dispose()
        {
            try { Stop(); } catch (Exception) { }
            try { _dispatcher.Dispose(); } catch (Exception) { }
        }

        // ------------------------------------------------------------------
        //  persisted settings (auto-start)
        // ------------------------------------------------------------------

        private static string SettingsPath
        {
            get { return Path.Combine(IpcConstants.DataRoot, "settings.json"); }
        }

        public static bool ReadAutoStart()
        {
            try
            {
                string fromEnv = Environment.GetEnvironmentVariable("AB_REVITMCP_AUTOSTART");
                if (!string.IsNullOrEmpty(fromEnv))
                    return fromEnv == "1" || string.Equals(fromEnv, "true", StringComparison.OrdinalIgnoreCase);

                if (!File.Exists(SettingsPath)) return false;
                JsonValue v;
                if (JsonValue.TryParse(File.ReadAllText(SettingsPath), out v)) return v["autoStart"].AsBool(false);
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// Gate 2 of 3 for revit_execute_code. Off unless a human turned it on in this install.
        /// </summary>
        public static bool ReadAllowCodeExecution()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return false;
                JsonValue v;
                if (JsonValue.TryParse(File.ReadAllText(SettingsPath), out v))
                    return v["allowCodeExecution"].AsBool(false);
            }
            catch (Exception) { }
            return false;
        }

        public static void WriteAllowCodeExecution(bool allow)
        {
            WriteSettings(ReadAutoStart(), allow);
        }

        public static void WriteAutoStart(bool autoStart)
        {
            WriteSettings(autoStart, ReadAllowCodeExecution());
        }

        private static void WriteSettings(bool autoStart, bool allowCodeExecution)
        {
            try
            {
                Directory.CreateDirectory(IpcConstants.DataRoot);
                File.WriteAllText(SettingsPath,
                    J.O("autoStart", autoStart,
                        "allowCodeExecution", allowCodeExecution).ToJson(true));
            }
            catch (Exception) { }
        }
    }
}
