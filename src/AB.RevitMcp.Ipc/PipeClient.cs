using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using AB.RevitMcp.Contracts.Protocol;

namespace AB.RevitMcp.Ipc
{
    /// <summary>
    /// Client half of the bridge, used by the MCP server process. One connection is kept open and
    /// reused; requests are serialized behind a semaphore because a single duplex pipe cannot
    /// interleave responses (Revit executes serially on its UI thread anyway).
    /// </summary>
    public sealed class PipeClient : IDisposable
    {
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private NamedPipeClientStream _stream;
        private string _pipeName;
        private bool _disposed;

        public string PipeName { get { return _pipeName; } }

        public bool IsConnected
        {
            get
            {
                NamedPipeClientStream s = _stream;
                return s != null && s.IsConnected;
            }
        }

        public async Task ConnectAsync(string pipeName, int timeoutMs, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(pipeName)) throw new ArgumentNullException("pipeName");

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                DisposeStream();

                var stream = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    // Deliberately NOT PipeOptions.CurrentUserOnly: when Revit runs elevated the
                    // pipe's owner can be the Administrators group rather than the user SID, which
                    // would make that flag reject a perfectly legitimate connection. Access is
                    // already restricted by the ACL the server applies when it creates the pipe.
                    PipeOptions.Asynchronous | PipeOptions.WriteThrough,
                    System.Security.Principal.TokenImpersonationLevel.None);

                await stream.ConnectAsync(timeoutMs <= 0 ? IpcConstants.ConnectTimeoutMs : timeoutMs, ct)
                            .ConfigureAwait(false);
                stream.ReadMode = PipeTransmissionMode.Byte;

                _stream = stream;
                _pipeName = pipeName;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Sends one request frame and awaits its reply. Throws <see cref="TimeoutException"/> if
        /// the bridge does not answer inside <paramref name="timeoutMs"/>; the connection is then
        /// dropped because the stream can no longer be trusted to be in sync.
        /// </summary>
        public async Task<string> SendAsync(string payload, int timeoutMs, CancellationToken ct)
        {
            if (_disposed) throw new ObjectDisposedException("PipeClient");

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                NamedPipeClientStream stream = _stream;
                if (stream == null || !stream.IsConnected)
                    throw new IOException("Not connected to the Revit bridge.");

                using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    if (timeoutMs > 0) timeoutCts.CancelAfter(timeoutMs);

                    try
                    {
                        await FrameCodec.WriteFrameAsync(stream, payload, timeoutCts.Token).ConfigureAwait(false);
                        string response = await FrameCodec.ReadFrameAsync(stream, timeoutCts.Token).ConfigureAwait(false);
                        if (response == null)
                        {
                            DisposeStream();
                            throw new IOException("The Revit bridge closed the connection.");
                        }
                        return response;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Our own timeout fired. The pipe may still deliver a late frame, so the
                        // only safe recovery is to reconnect.
                        DisposeStream();
                        throw new TimeoutException("Revit did not respond within " + timeoutMs + " ms.");
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Disconnect()
        {
            _gate.Wait();
            try { DisposeStream(); }
            finally { _gate.Release(); }
        }

        private void DisposeStream()
        {
            NamedPipeClientStream s = _stream;
            _stream = null;
            if (s == null) return;
            try { s.Dispose(); } catch (Exception) { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { DisposeStream(); } catch (Exception) { }
            try { _gate.Dispose(); } catch (Exception) { }
        }
    }
}
