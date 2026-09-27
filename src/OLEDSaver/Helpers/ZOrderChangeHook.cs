using System.Runtime.InteropServices;

namespace OLEDSaver.Helpers;

/// <summary>
/// Calls back whenever another process shows a window, takes the foreground or
/// changes the order of windows or of objects inside one, until disposed.
///
/// The callback runs on the thread that created the hook, which must pump
/// messages. Events raised by this process are not reported, so the callback can
/// reorder this process's own windows without triggering itself.
/// </summary>
public sealed class ZOrderChangeHook : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const uint EVENT_OBJECT_REORDER = 0x8004;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;
    private const int CHILDID_SELF = 0;

    private readonly Action _onChange;

    // Held in a field: the native side keeps only a function pointer, and a
    // collected delegate turns the next event into a crash.
    private readonly WinEventProc _callback;

    private readonly IntPtr _foregroundHook;
    private readonly IntPtr _showAndReorderHook;

    public ZOrderChangeHook(Action onChange)
    {
        _onChange = onChange;
        _callback = OnWinEvent;

        const uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _callback, 0, 0, flags);
        _showAndReorderHook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_REORDER, IntPtr.Zero, _callback, 0, 0, flags);

        if (_foregroundHook == IntPtr.Zero || _showAndReorderHook == IntPtr.Zero)
            AppDiagnostics.Warning("Failed to hook window z-order changes; other topmost windows may cover the blackout.");
    }

    private void OnWinEvent(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        // The hooked range includes HIDE, which cannot put anything on top.
        if (eventType == EVENT_OBJECT_HIDE)
            return;

        // Carets, cursors and other objects inside a window raise SHOW constantly and
        // cannot cover anything. REORDER is deliberately left unfiltered: a window
        // re-asserting HWND_TOPMOST reports it with OBJID_CLIENT.
        if (eventType == EVENT_OBJECT_SHOW && (idObject != OBJID_WINDOW || idChild != CHILDID_SELF))
            return;

        _onChange();
    }

    public void Dispose()
    {
        if (_foregroundHook != IntPtr.Zero)
            UnhookWinEvent(_foregroundHook);

        if (_showAndReorderHook != IntPtr.Zero)
            UnhookWinEvent(_showAndReorderHook);
    }

    private delegate void WinEventProc(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc callback, uint idProcess, uint idThread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);
}
