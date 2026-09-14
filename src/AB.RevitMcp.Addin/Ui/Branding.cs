using System;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace AB.RevitMcp.Addin.Ui
{
    /// <summary>Author and product identity, in one place so nothing drifts.</summary>
    public static class Branding
    {
        public const string Author = "Abdullah Lotfy";
        public const string LinkedInUrl = "https://www.linkedin.com/in/abdullahalqublawi/";
        public const string LinkedInCaption = "Abdullah Lotfy - LinkedIn";
        public const string ProductName = "AB Revit MCP Bridge";

        /// <summary>
        /// From the compiled assembly (Directory.Build.props). This used to be a constant, and it
        /// said 1.1.0 for two releases after 1.3.0 shipped.
        /// </summary>
        public static string Version
        {
            get { return ABAdvTools.AdvToolsProduct.VersionOf(typeof(Branding).Assembly); }
        }

        public static string AboutLine
        {
            get { return ProductName + " " + Version + "  -  " + Author; }
        }

        /// <summary>
        /// Loads an embedded PNG. Embedding rather than copying loose files keeps the add-in folder
        /// to three DLLs, which matters because that folder sits beside the .addin manifest in
        /// every user's roaming profile. WPF decodes PNG natively, so the logo needs no .ico.
        /// </summary>
        public static BitmapSource LoadPng(string resourceName)
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null) return null;

                    var decoder = new PngBitmapDecoder(
                        stream,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);

                    if (decoder.Frames.Count == 0) return null;
                    BitmapSource frame = decoder.Frames[0];
                    frame.Freeze();
                    return frame;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static BitmapSource LogoSmall { get { return LoadPng("AB.RevitMcp.Addin.Resources.logo_16.png"); } }
        public static BitmapSource LogoLarge { get { return LoadPng("AB.RevitMcp.Addin.Resources.logo_32.png"); } }
    }
}
