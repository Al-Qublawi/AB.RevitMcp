using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AB.RevitMcp.Server.Mcp
{
    /// <summary>
    /// The transport every MCP client uses by default: newline-delimited JSON-RPC on stdin/stdout.
    ///
    /// STDOUT IS SACRED. One stray Console.WriteLine corrupts the protocol stream and the client
    /// disconnects with an unhelpful parse error, so every diagnostic in this process goes to
    /// stderr - see <see cref="Program.LogToStderr"/>.
    /// </summary>
    public sealed class StdioTransport
    {
        private readonly McpServer _server;
        private readonly Action<string> _log;

        public StdioTransport(McpServer server, Action<string> log)
        {
            _server = server;
            _log = log ?? delegate { };
        }

        public async Task RunAsync(CancellationToken ct)
        {
            var utf8 = new UTF8Encoding(false);

            using (Stream input = Console.OpenStandardInput())
            using (Stream output = Console.OpenStandardOutput())
            using (var reader = new StreamReader(input, utf8, false, 64 * 1024))
            using (var writer = new StreamWriter(output, utf8, 64 * 1024))
            {
                writer.AutoFlush = false;
                _log("MCP server ready on stdio.");

                while (!ct.IsCancellationRequested)
                {
                    string line;
                    try
                    {
                        line = await reader.ReadLineAsync().ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        break;      // the client closed the pipe
                    }

                    if (line == null) break;                    // end of stream: the client exited
                    if (line.Length == 0) continue;             // keep-alive blank line

                    string response;
                    try
                    {
                        response = await _server.HandleMessageAsync(line, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _log("Message handling failed: " + ex);
                        continue;
                    }

                    if (response == null) continue;             // notification - no reply

                    // Responses must be exactly one line: strip any newline the payload smuggled in.
                    await writer.WriteAsync(response.Replace("\r", string.Empty).Replace("\n", string.Empty))
                                .ConfigureAwait(false);
                    await writer.WriteAsync('\n').ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                }

                _log("stdin closed; shutting down.");
            }
        }
    }
}
