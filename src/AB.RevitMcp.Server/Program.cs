using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AB.RevitMcp.Contracts.Tools;
using AB.RevitMcp.Server.Bridge;
using AB.RevitMcp.Server.Mcp;

namespace AB.RevitMcp.Server
{
    public static class Program
    {
        private static bool _verbose;

        public static async Task<int> Main(string[] args)
        {
            // stdout belongs to the protocol. Force UTF-8 without a BOM on both streams so a
            // non-English Windows code page cannot mangle element names.
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.InputEncoding = new UTF8Encoding(false);

            string error;
            ServerOptions options = ServerOptions.Parse(args, out error);
            _verbose = options.Verbose;

            if (error != null)
            {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine();
                Console.Error.WriteLine(ServerOptions.HelpText());
                return 2;
            }

            if (options.ShowHelp)
            {
                Console.Error.WriteLine(ServerOptions.HelpText());
                return 0;
            }

            if (!string.IsNullOrEmpty(options.PrintConfigClient))
            {
                // Documentation mode, not protocol mode - stdout is correct here.
                Console.Out.WriteLine(ConfigTemplates.Render(options.PrintConfigClient));
                return 0;
            }

            if (options.RunDoctor)
            {
                // Diagnostic mode, not protocol mode - stdout is correct here.
                return await AB.RevitMcp.Server.Doctor.DoctorRunner.RunAsync(args).ConfigureAwait(false);
            }

            if (options.PrintTools)
            {
                // Documentation mode, not protocol mode - writing to stdout is correct here.
                Console.Out.WriteLine(ToolDocumentation.ToMarkdown());
                return 0;
            }

            LogToStderr(McpServer.ServerName + " " + McpServer.ServerVersion +
                        " starting (" + ToolCatalog.Count + " tools, transport " +
                        options.Transport.ToString().ToLowerInvariant() + ")");

            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e)
                {
                    e.Cancel = true;                 // shut down cleanly rather than being killed
                    try { cts.Cancel(); } catch (ObjectDisposedException) { }
                };

                AppDomain.CurrentDomain.ProcessExit += delegate
                {
                    try { cts.Cancel(); } catch (ObjectDisposedException) { }
                };

                using (var bridge = new BridgeConnection(options, LogToStderr))
                {
                    var server = new McpServer(bridge, options, LogToStderr);

                    try
                    {
                        if (options.Transport == TransportKind.Http)
                        {
                            await new HttpTransport(server, options, LogToStderr)
                                .RunAsync(cts.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            await new StdioTransport(server, LogToStderr)
                                .RunAsync(cts.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        LogToStderr("Cancelled.");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("Fatal: " + ex);
                        return 1;
                    }
                }
            }

            LogToStderr("Stopped.");
            return 0;
        }

        /// <summary>
        /// Every diagnostic goes here. Writing any of this to stdout would corrupt the JSON-RPC
        /// stream and break the client connection.
        /// </summary>
        public static void LogToStderr(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            try
            {
                Console.Error.WriteLine("[" +
                    DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) +
                    "] " + message);
            }
            catch (Exception) { }
        }

        public static bool Verbose { get { return _verbose; } }
    }
}
