using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace OLEDSaver.Helpers;

/// <summary>
/// The user32/kernel32 surface the app needs: global hotkeys, placing the
/// blackout windows in raw screen pixels, reading how long the session has been
/// idle, and keeping the displays awake while blacked out.
/// </summary>
public static class NativeInterop
{
    // Without MOD_NOREPEAT, holding the hotkey makes Windows post repeated
    // WM_HOTKEY messages (keyboard auto-repeat), which would toggle the blackout
    // several times per press and look like a flicker. This flag delivers exactly
    // one message per physical press.
    private const uint MOD_NOREPEAT = 0x4000;

    public const int WM_HOTKEY = 0x0312;
    public const int WM_INPUT = 0x00FF;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_DPICHANGED = 0x02E0;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private const int SW_RESTORE = 9;

    // SetThreadExecutionState flags. ES_CONTINUOUS makes the request stick until
    // it is cleared, rather than resetting the idle timer once.
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public static bool RegisterGlobalHotkey(Window window, int id, uint modifiers, uint vk)
    {
        if (vk == 0)
            return false;

        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        bool registered = RegisterHotKey(hwnd, id, modifiers | MOD_NOREPEAT, vk);
        if (!registered)
        {
            AppDiagnostics.Warning(
                $"Failed to register global hotkey (id={id}, modifiers=0x{modifiers:X}, vk=0x{vk:X}, lastError={Marshal.GetLastWin32Error()}). " +
                "Another application may already own this key combination.");
        }

        return registered;
    }

    public static void UnregisterGlobalHotkey(Window window, int id)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
            UnregisterHotKey(hwnd, id);
    }

    /// <summary>
    /// Places a window over an exact rectangle of physical screen pixels and
    /// pins it above the taskbar.
    ///
    /// WPF sizes windows in device-independent units, so on a mixed-DPI desktop
    /// setting Left/Top/Width/Height would land the window in the wrong place or
    /// leave a lit strip of desktop showing. Positioning through SetWindowPos
    /// takes the monitor rectangle exactly as Windows reports it. The blackout
    /// window has no layout that depends on the size, so nothing else needs to
    /// know about the scale factor.
    /// </summary>
    public static void PlaceWindowAtDeviceBounds(Window window, int x, int y, int width, int height)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        SetWindowPos(hwnd, HWND_TOPMOST, x, y, width, height, SWP_SHOWWINDOW | SWP_NOACTIVATE);
    }

    public static IntPtr GetCurrentForegroundWindow() => GetForegroundWindow();

    /// <summary>
    /// Restores the foreground window the blackout took over from, so dismissing
    /// it puts the keyboard back where it was instead of on the desktop.
    /// Attaching to the other threads' input queues is what makes
    /// SetForegroundWindow succeed from a background app.
    /// </summary>
    public static void RestoreForegroundWindow(IntPtr targetHwnd)
    {
        if (targetHwnd == IntPtr.Zero || !IsWindowVisible(targetHwnd))
            return;

        IntPtr currentForeground = GetForegroundWindow();
        uint currentThreadId = GetCurrentThreadId();
        uint foregroundThreadId = GetWindowThreadProcessId(currentForeground, out _);
        uint targetThreadId = GetWindowThreadProcessId(targetHwnd, out _);

        if (currentThreadId != foregroundThreadId)
            AttachThreadInput(currentThreadId, foregroundThreadId, true);
        if (currentThreadId != targetThreadId)
            AttachThreadInput(currentThreadId, targetThreadId, true);

        // SW_RESTORE restores a *maximized* window to its normal size just as
        // readily as it un-minimizes a minimized one, so it is only used when the
        // target actually is minimized. Blacking out over a maximized browser and
        // coming back must not leave it in windowed mode.
        if (IsIconic(targetHwnd))
            ShowWindow(targetHwnd, SW_RESTORE);

        BringWindowToTop(targetHwnd);
        SetForegroundWindow(targetHwnd);

        if (currentThreadId != foregroundThreadId)
            AttachThreadInput(currentThreadId, foregroundThreadId, false);
        if (currentThreadId != targetThreadId)
            AttachThreadInput(currentThreadId, targetThreadId, false);
    }

    public static void ForceForeground(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
            SetForegroundWindow(hwnd);
    }

    /// <summary>
    /// How long the session has had no keyboard or mouse input. Drives the
    /// idle blackout; unlike a WPF input hook it also sees input that went to
    /// other applications.
    /// </summary>
    public static TimeSpan GetIdleDuration()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
            return TimeSpan.Zero;

        // Unsigned subtraction so the ~49-day tick count wrap cannot produce a
        // huge idle time and blank the screen out of nowhere.
        uint elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }

    /// <summary>
    /// Keeps the displays powered while the blackout is up (or releases the
    /// request when <paramref name="keepAwake"/> is false). A true-black screen
    /// already draws almost no power on OLED, and letting Windows power the panel
    /// down instead can make monitors renegotiate the link and reshuffle windows.
    /// </summary>
    public static void SetDisplayRequired(bool keepAwake)
    {
        uint flags = keepAwake ? ES_CONTINUOUS | ES_DISPLAY_REQUIRED : ES_CONTINUOUS;
        if (SetThreadExecutionState(flags) == 0)
            AppDiagnostics.Warning($"SetThreadExecutionState(0x{flags:X}) failed.");
    }

    /// <summary>
    /// True when the foreground window covers a whole monitor — a game or a
    /// full-screen video. Used to hold back the idle blackout: a movie playing
    /// with no keyboard input is exactly the case where blanking is unwanted.
    /// </summary>
    public static bool IsForegroundWindowFullscreen()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return false;

        // The desktop and the shell always "fill" a monitor; they are not a
        // full-screen application.
        var className = new StringBuilder(256);
        if (GetClassNameW(hwnd, className, className.Capacity) > 0)
        {
            string name = className.ToString();
            if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "#32769")
                return false;
        }

        if (!GetWindowRect(hwnd, out RECT windowRect))
            return false;

        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return false;

        var monitorInfo = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref monitorInfo))
            return false;

        RECT bounds = monitorInfo.rcMonitor;
        return windowRect.Left <= bounds.Left
            && windowRect.Top <= bounds.Top
            && windowRect.Right >= bounds.Right
            && windowRect.Bottom >= bounds.Bottom;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
}
