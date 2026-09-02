using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AB.RevitMcp.Addin.Ui
{
    /// <summary>
    /// Ribbon icons drawn in code rather than shipped as PNG resources - one less file to lose in
    /// a deployment, and the icon can change colour to reflect connection state.
    /// </summary>
    public static class IconFactory
    {
        public static readonly Color Connected = Color.FromRgb(0x2E, 0xA0, 0x43);   // green
        public static readonly Color Disconnected = Color.FromRgb(0x9A, 0xA0, 0xA6); // grey
        public static readonly Color Busy = Color.FromRgb(0xE8, 0x9C, 0x1C);         // amber
        public static readonly Color Accent = Color.FromRgb(0x1F, 0x6F, 0xEB);       // blue
        public static readonly Color Danger = Color.FromRgb(0xD1, 0x34, 0x38);       // red

        /// <summary>
        /// A filled disc with a lighter inner core - reads clearly at both 16 px and 32 px, which
        /// the classic "detailed glyph" approach does not.
        /// </summary>
        public static BitmapSource Status(int size, Color colour, bool hollow = false)
        {
            return Render(size, delegate (double nx, double ny, out Color pixel)
            {
                double distance = Math.Sqrt(nx * nx + ny * ny);
                pixel = colour;

                if (distance > 1.0) return 0.0;                       // outside the disc
                if (hollow && distance < 0.55) return 0.0;            // ring only

                if (!hollow && distance < 0.45)
                {
                    pixel = Lighten(colour, 0.45);                    // inner core
                    return 1.0;
                }

                // Anti-alias the outer edge over roughly one pixel.
                double edge = 2.0 / size;
                if (distance > 1.0 - edge) return (1.0 - distance) / edge;
                return 1.0;
            });
        }

        /// <summary>A rounded square badge - used for the neutral utility buttons.</summary>
        public static BitmapSource Badge(int size, Color colour)
        {
            return Render(size, delegate (double nx, double ny, out Color pixel)
            {
                pixel = colour;
                double ax = Math.Abs(nx), ay = Math.Abs(ny);
                double radius = 0.35;
                double dx = Math.Max(ax - (1.0 - radius), 0);
                double dy = Math.Max(ay - (1.0 - radius), 0);
                double distance = Math.Sqrt(dx * dx + dy * dy);

                if (ax > 1.0 || ay > 1.0) return 0.0;
                if (distance > radius) return 0.0;

                double edge = 2.0 / size;
                if (distance > radius - edge) return (radius - distance) / edge;

                if (ax < 0.55 && ay < 0.18) { pixel = Colors.White; return 1.0; }   // horizontal bar
                if (ay < 0.55 && ax < 0.18) { pixel = Colors.White; return 1.0; }   // vertical bar
                return 1.0;
            });
        }

        /// <summary>
        /// Composites the product logo with a small status dot in the corner, so the one ribbon
        /// button carries both the brand and the live connection state. Falls back to the plain
        /// status disc if the logo resource is unavailable for any reason.
        /// </summary>
        public static BitmapSource LogoWithStatus(BitmapSource logo, Color colour, int size)
        {
            if (logo == null) return Status(size, colour);

            try
            {
                var visual = new DrawingVisual();
                using (DrawingContext dc = visual.RenderOpen())
                {
                    dc.DrawImage(logo, new Rect(0, 0, size, size));

                    double radius = size * 0.26;
                    double inset = radius + size * 0.04;
                    var centre = new Point(size - inset, size - inset);

                    // A light ring keeps the dot readable against the dark logo.
                    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
                                   null, centre, radius * 1.25, radius * 1.25);
                    dc.DrawEllipse(new SolidColorBrush(colour), null, centre, radius, radius);
                }

                var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
                target.Render(visual);
                target.Freeze();
                return target;
            }
            catch (Exception)
            {
                return Status(size, colour);
            }
        }

        private delegate double PixelShader(double nx, double ny, out Color colour);

        private static BitmapSource Render(int size, PixelShader shader)
        {
            if (size < 8) size = 8;
            int stride = size * 4;
            byte[] pixels = new byte[stride * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Normalise to [-1, 1] using pixel centres.
                    double nx = ((x + 0.5) / size) * 2.0 - 1.0;
                    double ny = ((y + 0.5) / size) * 2.0 - 1.0;

                    Color colour;
                    double alpha = shader(nx, ny, out colour);
                    if (alpha <= 0) continue;
                    if (alpha > 1) alpha = 1;

                    int offset = y * stride + x * 4;
                    byte a = (byte)Math.Round(alpha * 255);
                    // BGRA32 is pre-multiplied only for Pbgra32; Bgra32 is straight alpha.
                    pixels[offset + 0] = colour.B;
                    pixels[offset + 1] = colour.G;
                    pixels[offset + 2] = colour.R;
                    pixels[offset + 3] = a;
                }
            }

            BitmapSource source = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            source.Freeze();   // required: the ribbon may touch it from another thread
            return source;
        }

        private static Color Lighten(Color colour, double amount)
        {
            return Color.FromRgb(
                (byte)Math.Min(255, colour.R + (255 - colour.R) * amount),
                (byte)Math.Min(255, colour.G + (255 - colour.G) * amount),
                (byte)Math.Min(255, colour.B + (255 - colour.B) * amount));
        }
    }
}
