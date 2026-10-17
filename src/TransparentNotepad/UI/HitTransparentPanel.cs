using TransparentNotepad.Native;

namespace TransparentNotepad.UI;

/// <summary>Lets the borderless form's WM_NCHITTEST (resize grips) see the outer edge band.</summary>
internal sealed class HitTransparentPanel : Panel
{
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32.WM_NCHITTEST) { m.Result = (IntPtr)Win32.HTTRANSPARENT; return; }
        base.WndProc(ref m);
    }
}
