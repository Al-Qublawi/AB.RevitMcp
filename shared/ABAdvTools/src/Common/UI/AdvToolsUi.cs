// AB Adv Tools shared kit - see AdvToolsBrand.cs for how the kit is shared.
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace ABAdvTools.UI
{
    /// <summary>Wraps a raw window handle so WinForms dialogs can be owned by the host window.</summary>
    internal sealed class HostWindow : IWin32Window
    {
        private readonly IntPtr _handle;

        public HostWindow(IntPtr handle) { _handle = handle; }

        public IntPtr Handle { get { return _handle; } }

        public static IWin32Window FromHandle(IntPtr handle)
        {
            return handle == IntPtr.Zero ? null : new HostWindow(handle);
        }
    }

    /// <summary>
    /// The dialogs every AB add-in shares. Plain WinForms, so the same code runs in Revit on
    /// .NET Framework and .NET 8 and in Navisworks.
    /// </summary>
    internal static class AdvToolsUi
    {
        /// <summary>Resource prefix the host projects embed kit assets under.</summary>
        public const string AssetPrefix = "ABAdvTools.Assets.";

        public static readonly Color BannerBack = Color.FromArgb(23, 34, 48);
        public static readonly Color BannerSubtitle = Color.FromArgb(150, 175, 205);
        public static readonly Color Accent = Color.FromArgb(31, 111, 235);
        public static readonly Color LinkBlue = Color.FromArgb(10, 102, 194);
        public static readonly Color UpdateGreen = Color.FromArgb(46, 160, 67);

        /// <summary>The About dialog: every AB tool loaded here, its version, and update status.</summary>
        public static void ShowAbout(IWin32Window owner, AdvToolsHost host, bool checkNow)
        {
            try
            {
                using (var form = new AboutForm(host, AdvToolsRegistry.Products(), checkNow))
                {
                    if (owner != null) form.ShowDialog(owner);
                    else form.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                AdvToolsLog.Error("About dialog failed.", ex);
                MessageBox.Show(owner, ex.Message, AdvToolsBrand.SuiteName,
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>The once-per-version notice shown when an automatic check finds a release.</summary>
        public static void ShowUpdateNotice(IWin32Window owner, UpdateResult result)
        {
            if (result == null || result.Product == null) return;

            try
            {
                // Mark first: if the dialog itself fails, the user is not re-nagged every start.
                UpdateChecker.MarkNotified(result.Product, result.LatestVersion);

                string body =
                    result.Product.Name + " " + result.LatestVersion + " is available." + Environment.NewLine +
                    "You are running " + result.Product.Version + "." + Environment.NewLine + Environment.NewLine +
                    "Open the download page now?" + Environment.NewLine + Environment.NewLine +
                    "Release notifications for all AB Adv Tools can be switched off from " +
                    AdvToolsBrand.RibbonTabName + " > About" +
                    (string.IsNullOrEmpty(result.Product.UpdateSettingsHint)
                        ? "."
                        : ", or for " + result.Product.Name + " alone in " + result.Product.UpdateSettingsHint + ".");

                DialogResult answer = MessageBox.Show(owner, body,
                    result.Product.Name + " - update available",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information);

                if (answer == DialogResult.Yes) AdvToolsBrand.OpenUrl(result.ReleaseUrl);
            }
            catch (Exception ex)
            {
                AdvToolsLog.Warn("Could not show the update notice: " + ex.Message);
            }
        }

        public static void OpenLinkedIn(IWin32Window owner)
        {
            if (AdvToolsBrand.OpenUrl(AdvToolsBrand.LinkedInUrl)) return;

            // Showing the address beats failing silently.
            MessageBox.Show(owner, AdvToolsBrand.LinkedInUrl, AdvToolsBrand.LinkedInCaption,
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>Loads an image embedded in the calling add-in under ABAdvTools.Assets.*.</summary>
        public static Image LoadImage(string fileName)
        {
            try
            {
                Assembly assembly = typeof(AdvToolsUi).Assembly;
                using (Stream stream = assembly.GetManifestResourceStream(AssetPrefix + fileName))
                {
                    if (stream == null) return null;

                    // Image.FromStream needs its stream for the image's lifetime; give it a copy.
                    var copy = new MemoryStream();
                    stream.CopyTo(copy);
                    copy.Position = 0;
                    return Image.FromStream(copy);
                }
            }
            catch
            {
                return null;   // artwork is decoration; it must never stop a dialog opening
            }
        }
    }
}
