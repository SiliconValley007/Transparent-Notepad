using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using TransparentNotepad.Logging;

namespace TransparentNotepad.UI;

/// <summary>Captures a monitor into memory as a JPEG (never touches disk), sized for free-tier vision endpoints.</summary>
internal static class ScreenCapture
{
    public const int MaxWidth = 1920;
    public const long TargetBytes = 300_000;
    private static readonly Color Fallback = Color.FromArgb(24, 24, 28);

    /// <summary>Test seam: replaces the screen grab (e.g. to simulate a headless / locked desktop). Null = real CopyFromScreen.</summary>
    internal static Action<Graphics, Rectangle>? GrabOverride;

    /// <param name="overlayToHide">Our window, blanked (Opacity = 0) around the BitBlt so the overlay can never screenshot itself; always restored.</param>
    public static byte[] CaptureJpeg(Rectangle bounds, int maxWidth = MaxWidth, long quality = 80, Form? overlayToHide = null)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("Invalid screen bounds");
        double previous = overlayToHide?.Opacity ?? 1.0; bool hidden = false;
        try
        {
            if (overlayToHide is { IsHandleCreated: true, Visible: true })
            {
                overlayToHide.Opacity = 0; hidden = true; overlayToHide.Update();
                Thread.Sleep(60);                                   // let DWM drop the window from the composed frame
            }
            using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(full))
            {
                try
                {
                    if (GrabOverride != null) GrabOverride(g, bounds);
                    else g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                }
                catch (Exception ex) when (ex is Win32Exception or ExternalException)
                {   // headless session 0, locked workstation or secure desktop: no pixels available -> neutral frame instead of a crash
                    Log.Warn("screen.grab_failed", ("error", ex.Message));
                    g.Clear(Fallback);
                }
            }
            if (hidden) { overlayToHide!.Opacity = previous; hidden = false; }   // restore as early as possible (encoding can take a moment)
            return Encode(full, maxWidth, quality, TargetBytes);
        }
        finally { if (hidden) overlayToHide!.Opacity = previous; }
    }

    /// <summary>JPEG-encodes, downscaling to at most maxWidth and then lowering quality / size until it fits maxBytes.</summary>
    internal static byte[] Encode(Bitmap src, int maxWidth, long quality, long maxBytes)
    {
        int width = Math.Min(src.Width, maxWidth);
        byte[] last = Array.Empty<byte>();
        while (true)
        {
            Bitmap? scaled = null;
            try
            {
                var bmp = src;
                if (width != src.Width)
                {
                    int h = Math.Max(1, (int)Math.Round(src.Height * (width / (double)src.Width)));
                    scaled = new Bitmap(width, h, PixelFormat.Format24bppRgb);
                    using var g = Graphics.FromImage(scaled);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(src, 0, 0, width, h);
                    bmp = scaled;
                }
                for (long q = quality; q >= 50; q -= 10)
                {
                    last = Jpeg(bmp, q);
                    if (last.Length <= maxBytes) return last;
                }
            }
            finally { scaled?.Dispose(); }
            if (width <= 640) return last;                           // smallest useful size: return best effort
            width = (int)(width * 0.8);
        }
    }

    private static byte[] Jpeg(Bitmap bmp, long quality)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        using var ms = new MemoryStream();
        bmp.Save(ms, codec, ep);
        return ms.ToArray();
    }
}
