using System.Reflection;
using TransparentNotepad.Config;
using TransparentNotepad.Native;
using TransparentNotepad.UI;
using Xunit;

namespace TransparentNotepad.Tests.Windows;

/// <summary>Real Win32 handles on an STA thread (no window is ever shown): capture exclusion and window styles.</summary>
public class StealthAndWin32Tests
{
    private static T Sta<T>(Func<T> body)
    {
        T result = default!; Exception? error = null;
        var t = new Thread(() => { try { result = body(); } catch (Exception e) { error = e; } });
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        if (error != null) throw new InvalidOperationException("STA body failed: " + error.Message, error);
        return result;
    }

    private static int ExStyleOf(Form f) =>
        ((CreateParams)typeof(Form).GetProperty("CreateParams", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f)!).ExStyle;

    [Fact]
    public void MainForm_is_excluded_from_capture_and_never_activates()
    {
        var (affinity, createStyle, liveStyle) = Sta(() =>
        {
            using var f = new MainForm();
            var h = f.Handle;                                     // creates the native window (OnHandleCreated applies stealth)
            return (Win32.GetAffinity(h), ExStyleOf(f), (int)Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE).ToInt64());
        });
        Assert.Equal(Win32.WDA_EXCLUDEFROMCAPTURE, affinity);                       // 0x11
        Assert.NotEqual(0, createStyle & Win32.WS_EX_TOOLWINDOW);
        Assert.NotEqual(0, createStyle & Win32.WS_EX_NOACTIVATE);
        Assert.NotEqual(0, liveStyle & Win32.WS_EX_NOACTIVATE);
        Assert.NotEqual(0, liveStyle & Win32.WS_EX_TOOLWINDOW);
    }

    [Fact]
    public void SettingsForm_follows_the_stealth_setting()
    {
        var (on, off) = Sta(() =>
        {
            using var a = new SettingsForm(new AppConfig { ExcludeFromCapture = true }); _ = a.Handle;
            using var b = new SettingsForm(new AppConfig { ExcludeFromCapture = false }); _ = b.Handle;
            return (Win32.GetAffinity(a.Handle), Win32.GetAffinity(b.Handle));
        });
        Assert.Equal(Win32.WDA_EXCLUDEFROMCAPTURE, on);
        Assert.Equal(Win32.WDA_NONE, off);
    }

    [Fact]
    public void ApplyCaptureExclusion_toggles_and_NoActivate_round_trips()
    {
        var (excluded, visible, na1, na2) = Sta(() =>
        {
            using var f = new Form(); var h = f.Handle;
            Win32.ApplyCaptureExclusion(h, true); var a = Win32.GetAffinity(h);
            Win32.ApplyCaptureExclusion(h, false); var b = Win32.GetAffinity(h);
            Win32.SetNoActivate(h, true); var s1 = (int)Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE).ToInt64();
            Win32.SetNoActivate(h, false); var s2 = (int)Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE).ToInt64();
            return (a, b, s1, s2);
        });
        Assert.Equal(Win32.WDA_EXCLUDEFROMCAPTURE, excluded);
        Assert.Equal(Win32.WDA_NONE, visible);
        Assert.NotEqual(0, na1 & Win32.WS_EX_NOACTIVATE);
        Assert.Equal(0, na2 & Win32.WS_EX_NOACTIVATE);
    }

    [Fact]
    public void Popup_menus_are_excluded_when_opened()
    {
        var affinity = Sta(() =>
        {
            using var host = new MainForm(); _ = host.Handle;
            var menu = (ContextMenuStrip)typeof(MainForm).GetField("_menu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host)!;
            _ = menu.Handle;                                       // HandleCreated hook applies the exclusion
            return Win32.GetAffinity(menu.Handle);
        });
        Assert.Equal(Win32.WDA_EXCLUDEFROMCAPTURE, affinity);
    }
}
