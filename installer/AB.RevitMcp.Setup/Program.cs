using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using ABAdvTools.Setup;

namespace AB.RevitMcp.Setup
{
    internal static class Program
    {
        /// <summary>
        /// Entry point. The window by default; see SetupArguments for the command line. The switches
        /// this installer always had keep working: /silent, and /noclients to skip configuring AI
        /// clients.
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
            // Must be wired up BEFORE any method that touches a Contracts type is JIT-compiled,
            // which is why the real body lives in Run() with inlining suppressed.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbeddedAssembly;
            return Run(args);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string[] args)
        {
            return SetupProgram.Run(new RevitMcpSetup(args), args);
        }

        /// <summary>Serves AB.RevitMcp.Contracts out of this executable's own resources.</summary>
        private static Assembly ResolveEmbeddedAssembly(object sender, ResolveEventArgs args)
        {
            string requested = new AssemblyName(args.Name).Name;
            if (requested != "AB.RevitMcp.Contracts") return null;

            using (Stream stream = Assembly.GetExecutingAssembly()
                       .GetManifestResourceStream("AB.RevitMcp.Setup.Lib.AB.RevitMcp.Contracts.dll"))
            {
                if (stream == null) return null;
                var bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int n = stream.Read(bytes, read, bytes.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                return Assembly.Load(bytes);
            }
        }
    }
}
