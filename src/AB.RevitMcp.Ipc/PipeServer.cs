using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
#if NETFRAMEWORK
using System.Security.AccessControl;
using System.Security.Principal;
#endif

namespace AB.RevitMcp.Ipc
{
    /// <summary>
    /// Named-pipe listener hosted inside the Revit process.
    ///
    /// IMPORTANT: every callback raised by this class runs on a THREAD-POOL thread, never on
    /// Revit's UI thread. The handler must therefore not touch the Revit API directly - it hands
    /// work to the ExternalEvent dispatcher and awaits the result.
    /// </summary>
    public sealed class PipeServer : IDisposable
    {
        private readonly string _pipeName;
        private readonly int _maxInstances;
        private readonly Func<string, CancellationToken, Task<string>> _handler;
        private readonly Action<string, Exception> _log;

        private CancellationTokenSource _cts;
        private Task _listenerTask;
        private int _activeConnections;
        private int _running;
        private NamedPipeServerStream _pending;

        public PipeServer(string pipeName,
                          Func<string, CancellationToken, Task<string>> handler,
                          Action<string, Exception> log = null,
                          int maxInstances = 8)
        {
            if (string.IsNullOrEmpty(pipeName)) throw new ArgumentNullException("pipeName");
            if (handler == null) throw new ArgumentNullException("handler");
            _pipeName = pipeName;
            _handler = handler;
            _log = log ?? delegate { };
            _maxInstances = Math.Max(1, Math.Min(maxInstances, 64));
        }

        public string PipeName { get { return _pipeName; } }
        public bool IsRunning { get { return Volatile.Read(ref _running) == 1; } }
        public int ActiveConnections { get { return Volatile.Read(ref _activeConnections); } }

        /// <summary>Raised (on a background thread) whenever a client connects or disconnects.</summary>
        public event EventHandler ConnectionsChanged;

        public void Start()
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) == 1) return;
            _cts = new CancellationTokenSource();
            _listenerTask = Task.Factory.StartNew(
                () => ListenLoopAsync(_cts.Token),
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
        }

        public void Stop()
        {
            if (Interlocked.CompareExchange(ref _running, 0, 1) == 0) return;
            try { if (_cts != null) _cts.Cancel(); }
            catch (ObjectDisposedException) { }

            // WaitForConnectionAsync on .NET Framework does not always observe the token, so also
            // tear down the stream that is currently parked waiting for a client.
            NamedPipeServerStream pending = Interlocked.Exchange(ref _pending, null);
            if (pending != null)
            {
                try { pending.Dispose(); } catch (Exception) { }
            }

            try
            {
                if (_listenerTask != null && !_listenerTask.Wait(2000))
                    _log("Pipe listener did not stop within 2s; abandoning it.", null);
            }
            catch (AggregateException) { /* cancellation */ }
            catch (Exception ex) { _log("Error while stopping pipe listener.", ex); }

            try { if (_cts != null) _cts.Dispose(); } catch (Exception) { }
            _cts = null;
            _listenerTask = null;
            RaiseConnectionsChanged();
        }

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = CreateServerStream();
                    Interlocked.Exchange(ref _pending, server);

                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    Interlocked.CompareExchange(ref _pending, null, server);

                    NamedPipeServerStream connected = server;
                    server = null; // ownership transfers to the connection task

                    Interlocked.Increment(ref _activeConnections);
                    RaiseConnectionsChanged();

                    // Fire and forget: one long-lived task per client connection.
                    var ignored = Task.Run(() => ServeConnectionAsync(connected, ct), CancellationToken.None);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (IOException ex)
                {
                    _log("Pipe listener I/O error; retrying.", ex);
                    await DelayQuiet(250, ct).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException ex)
                {
                    // Another process already owns this pipe name - fatal for this listener.
                    _log("Pipe name '" + _pipeName + "' is already in use by another process.", ex);
                    break;
                }
                catch (Exception ex)
                {
                    _log("Unexpected pipe listener error; retrying.", ex);
                    await DelayQuiet(500, ct).ConfigureAwait(false);
                }
                finally
                {
                    if (server != null)
                    {
                        try { server.Dispose(); } catch (Exception) { }
                    }
                }
            }
        }

        private NamedPipeServerStream CreateServerStream()
        {
#if NETFRAMEWORK
            // Restrict the pipe to the interactive user that launched Revit.
            var security = new PipeSecurity();
            var currentUser = WindowsIdentity.GetCurrent().User;
            security.AddAccessRule(new PipeAccessRule(currentUser,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));

            return new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                _maxInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough,
                64 * 1024,
                64 * 1024,
                security);
#else
            // PipeOptions.CurrentUserOnly applies an equivalent owner-only ACL.
            return new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                _maxInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly,
                64 * 1024,
                64 * 1024);
#endif
        }

        private async Task ServeConnectionAsync(NamedPipeServerStream stream, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && stream.IsConnected)
                {
                    string request;
                    try
                    {
                        request = await FrameCodec.ReadFrameAsync(stream, ct).ConfigureAwait(false);
                    }
                    catch (IOException) { break; }        // client vanished
                    catch (ObjectDisposedException) { break; }
                    catch (OperationCanceledException) { break; }

                    if (request == null) break;           // clean disconnect

                    string response;
                    try
                    {
                        response = await _handler(request, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log("Request handler threw; returning a protocol error frame.", ex);
                        response = "{\"v\":1,\"id\":\"\",\"ok\":false,\"error\":{\"code\":\"INTERNAL_ERROR\"," +
                                   "\"message\":\"" + Escape(ex.Message) + "\"}}";
                    }

                    if (response == null) continue;       // notification, no reply expected

                    try
                    {
                        await FrameCodec.WriteFrameAsync(stream, response, ct).ConfigureAwait(false);
                    }
                    catch (IOException) { break; }
                    catch (ObjectDisposedException) { break; }
                }
            }
            catch (Exception ex)
            {
                _log("Connection loop failed.", ex);
            }
            finally
            {
                try { if (stream.IsConnected) stream.Disconnect(); } catch (Exception) { }
                try { stream.Dispose(); } catch (Exception) { }
                Interlocked.Decrement(ref _activeConnections);
                RaiseConnectionsChanged();
            }
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        }

        private static async Task DelayQuiet(int ms, CancellationToken ct)
        {
            try { await Task.Delay(ms, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        private void RaiseConnectionsChanged()
        {
            EventHandler h = ConnectionsChanged;
            if (h == null) return;
            try { h(this, EventArgs.Empty); } catch (Exception ex) { _log("ConnectionsChanged handler threw.", ex); }
        }

        public void Dispose() { Stop(); }
    }
}
