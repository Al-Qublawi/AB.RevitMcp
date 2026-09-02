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
        public const string Version = "1.1.0";

        public static string AboutLine
        {
            get { return ProductName + " " + Version + "  -  " + Author; }
        }

        /// <summary>
        /// Loads an icon that ships inside the assembly. Embedding rather than copying loose files
        /// keeps the add-in folder to three DLLs, which matters because that folder sits beside the
        /// .addin manifest in every user's roaming profile.
        /// </summary>
        public static BitmapSource LoadIcon(string resourceName)
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null) return null;

                    var decoder = new IconBitmapDecoder(
                        stream,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);

                    if (decoder.Frames.Count == 0) return null;
                    BitmapSource frame = decoder.Frames[0];
                    frame.Freeze();     // the ribbon may touch it from another thread
                    return frame;
                }
            }
            catch (Exception)
            {
                return null;    // a missing icon must never stop the ribbon from building
            }
        }

        public static BitmapSource LinkedInSmall { get { return LoadIcon("AB.RevitMcp.Addin.Resources.linkedin_16.ico"); } }
        public static BitmapSource LinkedInLarge { get { return LoadIcon("AB.RevitMcp.Addin.Resources.linkedin_32.ico"); } }

        /// <summary>Loads an embedded PNG. WPF decodes PNG natively, so the logo needs no .ico.</summary>
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
