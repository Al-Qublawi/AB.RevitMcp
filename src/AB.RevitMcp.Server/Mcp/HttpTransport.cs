using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AB.RevitMcp.Server.Mcp
{
    /// <summary>
    /// Streamable HTTP transport for clients that prefer a URL to a child process (n8n, web IDEs,
    /// a remote VS Code). Implemented on HttpListener so the server keeps its zero-dependency
    /// footprint.
    ///
    /// Bound to loopback by default, and the Origin header is validated on every request: an
    /// unauthenticated local HTTP endpoint that can modify a BIM model is exactly the kind of
    /// thing a malicious web page would love to reach through DNS rebinding.
    /// </summary>
    public sealed class HttpTransport
    {
        private readonly McpServer _server;
        private readonly ServerOptions _options;
        private readonly Action<string> _log;

        public HttpTransport(McpServer server, ServerOptions options, Action<string> log)
        {
            _server = server;
            _options = options;
            _log = log ?? delegate { };
        }

        public async Task RunAsync(CancellationToken ct)
        {
            string prefix = "http://" + _options.HttpHost + ":" + _options.HttpPort + "/";

            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);

            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                _log("Could not bind " + prefix + ": " + ex.Message +
                     (_options.HttpHost != "127.0.0.1" && _options.HttpHost != "localhost"
                         ? " Binding a non-loopback address usually needs an elevated prompt or a " +
                           "netsh http add urlacl reservation."
                         : string.Empty));
                throw;
            }

            _log("MCP server listening on " + prefix + "mcp");

            using (ct.Register(delegate { try { listener.Stop(); } catch (Exception) { } }))
            {
                while (!ct.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (HttpListenerException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (InvalidOperationException) { break; }

                    // One task per request; the MCP server itself serialises at the pipe.
                    var ignored = Task.Run(() => HandleAsync(context, ct), CancellationToken.None);
                }
            }

            try { listener.Close(); } catch (Exception) { }
        }

        private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;

            try
            {
                if (!IsOriginAllowed(request))
                {
                    await WriteAsync(response, 403, "application/json",
                        "{\"error\":\"Forbidden origin. This server only accepts local requests.\"}")
                        .ConfigureAwait(false);
                    return;
                }

                response.Headers["Access-Control-Allow-Origin"] = request.Headers["Origin"] ?? "*";
                response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Mcp-Session-Id, MCP-Protocol-Version";
                response.Headers["Access-Control-Allow-Methods"] = "POST, GET, OPTIONS";

                if (request.HttpMethod == "OPTIONS")
                {
                    await WriteAsync(response, 204, null, null).ConfigureAwait(false);
                    return;
                }

                string path = request.Url != null ? request.Url.AbsolutePath.TrimEnd('/') : string.Empty;

                if (request.HttpMethod == "GET")
                {
                    if (path == "/health")
                    {
                        await WriteAsync(response, 200, "application/json",
                            "{\"status\":\"ok\",\"server\":\"" + McpServer.ServerName +
                            "\",\"version\":\"" + McpServer.ServerVersion + "\"}").ConfigureAwait(false);
                        return;
                    }

                    // No server-initiated SSE stream is offered; the spec allows refusing GET.
                    await WriteAsync(response, 405, "application/json",
                        "{\"error\":\"This server does not open a server-to-client stream. POST JSON-RPC to /mcp.\"}")
                        .ConfigureAwait(false);
                    return;
                }

                if (request.HttpMethod != "POST")
                {
                    await WriteAsync(response, 405, "application/json",
                        "{\"error\":\"Method not allowed.\"}").ConfigureAwait(false);
                    return;
                }

                if (path != "/mcp" && path != string.Empty)
                {
                    await WriteAsync(response, 404, "application/json",
                        "{\"error\":\"Not found. POST JSON-RPC messages to /mcp.\"}").ConfigureAwait(false);
                    return;
                }

                string body;
                using (var reader = new StreamReader(request.InputStream,
                           request.ContentEncoding ?? new UTF8Encoding(false)))
                {
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                string result = await _server.HandleMessageAsync(body, ct).ConfigureAwait(false);

                if (result == null)
                {
                    // Notification: 202 Accepted with no body, per the Streamable HTTP transport.
                    await WriteAsync(response, 202, null, null).ConfigureAwait(false);
                    return;
                }

                await WriteAsync(response, 200, "application/json", result).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log("HTTP request failed: " + ex.Message);
                try
                {
                    await WriteAsync(response, 500, "application/json",
                        "{\"error\":\"Internal server error.\"}").ConfigureAwait(false);
                }
                catch (Exception) { }
            }
        }

        /// <summary>DNS-rebinding guard: only loopback origins (or no Origin at all) are accepted.</summary>
        private static bool IsOriginAllowed(HttpListenerRequest request)
        {
            string origin = request.Headers["Origin"];
            if (string.IsNullOrEmpty(origin)) return true;      // native clients send no Origin

            Uri uri;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out uri)) return false;

            string host = uri.Host;
            return host == "127.0.0.1" || host == "::1" ||
                   string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task WriteAsync(HttpListenerResponse response, int status,
                                             string contentType, string body)
        {
            response.StatusCode = status;

            if (body == null)
            {
                response.ContentLength64 = 0;
                response.Close();
                return;
            }

            byte[] bytes = new UTF8Encoding(false).GetBytes(body);
            response.ContentType = contentType + "; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            response.OutputStream.Close();
            response.Close();
        }
    }
}
