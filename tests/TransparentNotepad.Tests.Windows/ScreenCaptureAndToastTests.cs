using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using TransparentNotepad.Native;
using TransparentNotepad.UI;
using Xunit;

namespace TransparentNotepad.Tests.Windows;

public class ScreenCaptureAndToastTests
{
    private static T Sta<T>(Func<T> body)
    {
        T result = default!; Exception? error = null;
        var t = new Thread(() => { try { result = body(); } catch (Exception e) { error = e; } });
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        if (error != null) throw new InvalidOperationException("STA body failed: " + error.Message, error);
        return result;
    }

    private static bool IsJpeg(byte[] b) => b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8 && b[^2] == 0xFF && b[^1] == 0xD9;

    [Fact]
    public void Encode_downscales_wide_screens_to_1920_jpeg_under_300KB()
    {
        var (bytes, width) = Sta(() =>
        {
            using var big = new Bitmap(3840, 2160, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(big))
            {
                using var br = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 3840, 2160), Color.Navy, Color.Orange, 45f);
                g.FillRectangle(br, 0, 0, 3840, 2160); g.DrawString("hello vision", new Font("Segoe UI", 80f), Brushes.White, 200, 200);
            }
            var b = ScreenCapture.Encode(big, ScreenCapture.MaxWidth, 80, ScreenCapture.TargetBytes);
            using var decoded = Image.FromStream(new MemoryStream(b));
            return (b, decoded.Width);
        });
        Assert.True(IsJpeg(bytes), "must be a JPEG, not PNG");
        Assert.Equal(1920, width);
        Assert.True(bytes.Length <= ScreenCapture.TargetBytes, $"{bytes.Length} bytes");
    }

    [Fact]
    public void Encode_keeps_incompressible_screens_under_300KB_by_shrinking_further()
    {
        var (bytes, width) = Sta(() =>
        {
            using var noise = new Bitmap(1920, 1080, PixelFormat.Format24bppRgb);
            var rnd = new Random(42); var raw = new byte[1920 * 1080 * 3]; rnd.NextBytes(raw);
            var data = noise.LockBits(new Rectangle(0, 0, 1920, 1080), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            for (int y = 0; y < 1080; y++) System.Runtime.InteropServices.Marshal.Copy(raw, y * 1920 * 3, data.Scan0 + y * data.Stride, 1920 * 3);
            noise.UnlockBits(data);
            var b = ScreenCapture.Encode(noise, ScreenCapture.MaxWidth, 80, ScreenCapture.TargetBytes);
            using var decoded = Image.FromStream(new MemoryStream(b));
            return (b, decoded.Width);
        });
        Assert.True(IsJpeg(bytes)); Assert.True(width < 1920, "had to downscale"); Assert.True(bytes.Length <= ScreenCapture.TargetBytes, $"{bytes.Length} bytes");
    }

    [Fact]
    public void CaptureJpeg_headless_or_locked_desktop_falls_back_to_a_valid_small_jpeg_and_restores_opacity()
    {
        var (opacityAfter, jpeg, isDark) = Sta(() =>
        {
            using var f = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Location = new Point(60, 60), Size = new Size(320, 220), BackColor = Color.Red, ShowInTaskbar = false };
            f.Show(); Application.DoEvents();
            ScreenCapture.GrabOverride = (_, _) => throw new Win32Exception(5, "simulated: no interactive desktop");
            try
            {
                var bytes = ScreenCapture.CaptureJpeg(new Rectangle(0, 0, 1280, 720), overlayToHide: f);
                using var img = new Bitmap(new MemoryStream(bytes));
                var px = img.GetPixel(img.Width / 2, img.Height / 2);
                return (f.Opacity, bytes, px.R < 80 && px.G < 80 && px.B < 80);
            }
            finally { ScreenCapture.GrabOverride = null; }
        });
        Assert.Equal(1.0, opacityAfter);
        Assert.True(IsJpeg(jpeg)); Assert.True(jpeg.Length <= ScreenCapture.TargetBytes, $"{jpeg.Length} bytes");
        Assert.True(isDark, "fallback frame is a neutral dark background");
    }

    [Fact]
    public void CaptureJpeg_hides_the_overlay_while_capturing_and_restores_opacity()
    {
        var (opacityAfter, isRed, jpeg) = Sta(() =>
        {
            using var f = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Location = new Point(60, 60), Size = new Size(320, 220), BackColor = Color.Red, ShowInTaskbar = false };
            f.Show(); Application.DoEvents(); Thread.Sleep(150); Application.DoEvents();
            var screen = Screen.PrimaryScreen!.Bounds;
            var bytes = ScreenCapture.CaptureJpeg(screen, overlayToHide: f);
            using var img = new Bitmap(new MemoryStream(bytes));
            double scale = img.Width / (double)screen.Width;
            var px = img.GetPixel((int)((60 + 160) * scale), (int)((60 + 110) * scale));
            return (f.Opacity, px.R > 200 && px.G < 60 && px.B < 60, bytes);
        });
        Assert.Equal(1.0, opacityAfter);
        Assert.False(isRed, "the overlay must not appear in its own screenshot (a black/headless desktop also satisfies this)");
        Assert.True(IsJpeg(jpeg)); Assert.True(jpeg.Length <= ScreenCapture.TargetBytes, $"{jpeg.Length} bytes");
    }

    [Fact]
    public void ToastLabel_never_activates_and_stealth_handles_child_windows()
    {
        var (style, childResult, topResult) = Sta(() =>
        {
            using var host = new Form(); _ = host.Handle;
            var toast = new ToastLabel(); host.Controls.Add(toast); _ = toast.Handle;
            int ex = ((CreateParams)typeof(Control).GetProperty("CreateParams", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(toast)!).ExStyle;
            return (ex, Win32.ApplyStealth(toast.Handle, true), Win32.ApplyStealth(host.Handle, true));
        });
        Assert.NotEqual(0, style & Win32.WS_EX_NOACTIVATE);
        Assert.NotEqual(0, style & Win32.WS_EX_TOOLWINDOW);
        Assert.True(childResult.Ok);
        Assert.True(topResult.Ok);
    }
}
