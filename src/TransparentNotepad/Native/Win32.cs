using System.Runtime.InteropServices;

// Harden native loading for every P/Invoke in this assembly: system DLLs come from System32 only, never the app folder.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace TransparentNotepad.Native;

/// <summary>All Win32 P/Invoke in one place.</summary>
internal static class Win32
{
    public const uint WDA_NONE = 0x0, WDA_MONITOR = 0x1, WDA_EXCLUDEFROMCAPTURE = 0x11;
    public const int WM_HOTKEY = 0x0312, WM_NCHITTEST = 0x0084, WM_NCLBUTTONDOWN = 0x00A1;
    public const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
                     HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17, HTTRANSPARENT = -1;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    public const uint VK_RETURN = 0x0D, VK_H = 0x48, VK_S = 0x53;
    public const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, GWL_EXSTYLE = -20;

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint pdwAffinity);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>Toggle WS_EX_NOACTIVATE at runtime (window can be clicked without stealing focus / firing blur in other apps).</summary>
    public static void SetNoActivate(IntPtr hwnd, bool noActivate)
    {
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        long next = noActivate ? ex | WS_EX_NOACTIVATE : ex & ~(long)WS_EX_NOACTIVATE;
        if (next != ex) SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)next);
    }

    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
    private const uint GA_ROOT = 2;

    /// <summary>
    /// Stealth entry point for any window handle. Display affinity only exists on top-level windows; a child control
    /// (e.g. the toast label) is already covered by its top-level parent, so it is reported as covered instead of failing.
    /// </summary>
    public static (bool Ok, string Mode) ApplyStealth(IntPtr hwnd, bool exclude)
    {
        if (hwnd == IntPtr.Zero) return (false, "no handle");
        if (GetAncestor(hwnd, GA_ROOT) != hwnd) return (true, "child window: covered by its top-level window");
        return ApplyCaptureExclusion(hwnd, exclude);
    }

    /// <summary>Single entry point for every top-level window we own (main form, dialogs, menus).</summary>
    public static (bool Ok, string Mode) ApplyCaptureExclusion(IntPtr hwnd, bool exclude)
    {
        if (hwnd == IntPtr.Zero) return (false, "no handle");
        if (exclude) return HideFromCapture(hwnd);
        return SetWindowDisplayAffinity(hwnd, WDA_NONE) ? (true, "visible to screen capture") : (false, $"affinity reset failed (Win32 error {Marshal.GetLastWin32Error()})");
    }

    public static uint GetAffinity(IntPtr hwnd) => GetWindowDisplayAffinity(hwnd, out var a) ? a : uint.MaxValue;

    /// <summary>Excludes the window from screen capture (Zoom/Teams/Meet/OBS). Falls back to WDA_MONITOR (black box) on Windows older than 10 2004.</summary>
    public static (bool Ok, string Mode) HideFromCapture(IntPtr hwnd)
    {
        if (SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE) && GetWindowDisplayAffinity(hwnd, out var a) && a == WDA_EXCLUDEFROMCAPTURE)
            return (true, "excluded from capture");
        if (SetWindowDisplayAffinity(hwnd, WDA_MONITOR)) return (true, "black box in captures (Windows < 10 2004)");
        return (false, $"capture exclusion failed (Win32 error {Marshal.GetLastWin32Error()})");
    }

    public static void SetRedraw(IntPtr hwnd, bool on) => SendMessage(hwnd, 0x000B, on ? (IntPtr)1 : IntPtr.Zero, IntPtr.Zero); // WM_SETREDRAW

    /// <summary>Start a native window drag (borderless title bar).</summary>
    public static void BeginDrag(IntPtr hwnd) { ReleaseCapture(); SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero); }
}
