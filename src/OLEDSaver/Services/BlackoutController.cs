using OLEDSaver.Helpers;
using OLEDSaver.Input;
using OLEDSaver.Models;
using OLEDSaver.Views;

namespace OLEDSaver.Services;

/// <summary>The settings the blackout reads. Implemented by the view model.</summary>
public interface IBlackoutOptions
{
    HotkeyDefinition Hotkey { get; }

    DismissTriggers DismissTriggers { get; }

    int MouseMoveThresholdPixels { get; }

    bool ShowHintOnBlackout { get; }

    bool KeepDisplaysAwake { get; }

    DisplayTargetMode DisplayTargetMode { get; }

    IReadOnlyCollection<string> SelectedDisplayIds { get; }
}

/// <summary>Why a blackout was switched on. Only used for the log.</summary>
public enum BlackoutTrigger
{
    Hotkey,
    Manual,
    Idle,
    CommandLine
}

/// <summary>
/// Why a blackout came down. Logged, because "the screen will not stay black" is
/// the one failure users cannot see for themselves — the log is the only way to
/// tell a twitchy mouse from a keyboard macro.
/// </summary>
public enum BlackoutDismissReason
{
    Hotkey,
    Manual,
    KeyPress,
    MouseMove,
    MouseClick,
    CommandLine,
    NoDisplays,
    WindowClosed,
    Shutdown
}

/// <summary>
/// Owns the blackout: one true-black window per targeted monitor, the dismissal
/// rules, and the power request that goes with it.
///
/// Dismissal is decided here rather than in the windows because the input that
/// matters arrives on the main window's raw-input sink, which sees keyboard and
/// mouse activity regardless of which application has focus. The overlay's own
/// key and click events are a second, focused path into the same evaluator, so a
/// machine where registering the raw-input sink failed still responds to a
/// keystroke.
/// </summary>
public sealed class BlackoutController : IDisposable
{
    private readonly IBlackoutOptions _options;
    private readonly Func<IntPtr> _rawInputHostHandle;
    private readonly BlackoutDismissEvaluator _dismissEvaluator = new();
    private readonly List<BlackoutWindow> _windows = new();

    private IntPtr _previousForegroundWindow;
    private bool _mouseSinkRegistered;
    private bool _displayRequested;
    private bool _disposed;

    public BlackoutController(IBlackoutOptions options, Func<IntPtr> rawInputHostHandle)
    {
        _options = options;
        _rawInputHostHandle = rawInputHostHandle;
    }

    /// <summary>Raised with true when the blackout goes up and false when it comes down.</summary>
    public event EventHandler<bool>? ActiveChanged;

    public bool IsActive => _windows.Count > 0;

    public void Toggle(BlackoutTrigger trigger)
    {
        if (IsActive)
            Hide(DismissReasonFor(trigger));
        else
            Show(trigger);
    }

    /// <summary>Keeps the log honest about which route took the blackout back down.</summary>
    private static BlackoutDismissReason DismissReasonFor(BlackoutTrigger trigger) => trigger switch
    {
        BlackoutTrigger.Hotkey => BlackoutDismissReason.Hotkey,
        BlackoutTrigger.CommandLine => BlackoutDismissReason.CommandLine,
        _ => BlackoutDismissReason.Manual
    };

    public void Show(BlackoutTrigger trigger)
    {
        if (_disposed || IsActive)
            return;

        IReadOnlyList<DisplayInfo> targets = DisplayService.ResolveTargets(
            DisplayService.GetDisplays(),
            _options.DisplayTargetMode,
            _options.SelectedDisplayIds);

        if (targets.Count == 0)
        {
            AppDiagnostics.Warning("Blackout requested but Windows reported no displays.");
            return;
        }

        // Captured before the overlay takes focus, so dismissing puts the
        // keyboard back where the user left it.
        _previousForegroundWindow = NativeInterop.GetCurrentForegroundWindow();

        CreateWindows(targets);

        // Taking focus is deliberate: with the screen black, keystrokes must not
        // keep landing in whatever was in front a moment ago.
        _windows[0].Activate();
        NativeInterop.ForceForeground(_windows[0]);

        _dismissEvaluator.Configure(_options.DismissTriggers, _options.MouseMoveThresholdPixels);
        _dismissEvaluator.Arm(DateTime.UtcNow, RawInputInterop.GetPressedVirtualKeys());

        RegisterMouseSink();
        ApplyDisplayRequest(_options.KeepDisplaysAwake);

        AppDiagnostics.Info($"Blackout on ({trigger}) covering {targets.Count} display(s).");
        ActiveChanged?.Invoke(this, true);
    }

    public void Hide(BlackoutDismissReason reason)
    {
        if (!IsActive)
            return;

        AppDiagnostics.Info($"Blackout off ({reason}).");
        DestroyWindows();
        ReleaseBlackoutState();
    }

    /// <summary>
    /// Everything that has to be undone once the overlays are gone. Separate from
    /// <see cref="Hide"/> because the windows can also disappear without going
    /// through it.
    /// </summary>
    private void ReleaseBlackoutState()
    {
        _dismissEvaluator.Disarm();
        UnregisterMouseSink();
        ApplyDisplayRequest(false);

        NativeInterop.RestoreForegroundWindow(_previousForegroundWindow);
        _previousForegroundWindow = IntPtr.Zero;

        ActiveChanged?.Invoke(this, false);
    }

    /// <summary>
    /// Re-covers the monitors after the display layout changed. A monitor that
    /// was hot-plugged, resized or rearranged while the blackout was up would
    /// otherwise leave a lit strip of desktop visible.
    /// </summary>
    public void HandleDisplayChange()
    {
        if (!IsActive)
            return;

        IReadOnlyList<DisplayInfo> targets = DisplayService.ResolveTargets(
            DisplayService.GetDisplays(),
            _options.DisplayTargetMode,
            _options.SelectedDisplayIds);

        if (targets.Count == 0)
        {
            Hide(BlackoutDismissReason.NoDisplays);
            return;
        }

        DestroyWindows();
        CreateWindows(targets);
        _windows[0].Activate();
    }

    /// <summary>Feeds a keyboard event from the global raw-input sink.</summary>
    public void HandleKeyEvent(uint virtualKey, bool isKeyDown)
    {
        if (!IsActive)
            return;

        if (_dismissEvaluator.ProcessKey(virtualKey, isKeyDown, DateTime.UtcNow))
            Hide(BlackoutDismissReason.KeyPress);
    }

    /// <summary>Feeds a mouse event from the global raw-input sink.</summary>
    public void HandleMouseEvent(RawMouseEvent mouseEvent)
    {
        if (!IsActive)
            return;

        DateTime now = DateTime.UtcNow;

        if ((mouseEvent.ButtonPressed || mouseEvent.WheelMoved) && _dismissEvaluator.ProcessMouseButton(now))
        {
            Hide(BlackoutDismissReason.MouseClick);
            return;
        }

        if (mouseEvent.Moved
            && _dismissEvaluator.ProcessMouseMove(mouseEvent.X, mouseEvent.Y, mouseEvent.IsAbsolute, now))
        {
            Hide(BlackoutDismissReason.MouseMove);
        }
    }

    private void CreateWindows(IReadOnlyList<DisplayInfo> targets)
    {
        string hint = BuildHintText();
        bool showHint = _options.ShowHintOnBlackout;

        foreach (DisplayInfo target in targets)
        {
            var window = new BlackoutWindow(hint, showHint);
            window.KeyObserved += OnWindowKeyObserved;
            window.MouseButtonObserved += OnWindowMouseButtonObserved;
            window.Closed += OnWindowClosedFromOutside;

            // Shown far off-screen first, because a WPF window is placed and sized
            // by its own properties on the way up and would otherwise appear
            // briefly on the primary monitor. CoverDisplay then moves it onto the
            // monitor it belongs to and maximizes it there.
            window.Left = -32000;
            window.Top = -32000;
            window.Width = 200;
            window.Height = 200;
            window.Show();
            window.CoverDisplay(target);

            _windows.Add(window);
        }
    }

    private void DestroyWindows()
    {
        foreach (BlackoutWindow window in _windows)
        {
            window.KeyObserved -= OnWindowKeyObserved;
            window.MouseButtonObserved -= OnWindowMouseButtonObserved;
            window.Closed -= OnWindowClosedFromOutside;
            window.Close();
        }

        _windows.Clear();
    }

    /// <summary>
    /// An overlay was closed by something other than this controller — Alt+F4
    /// reaching it, or a task manager. Half a blackout is worse than none: one
    /// monitor would be left lit with no obvious way to get the rest back, so the
    /// whole thing comes down.
    /// </summary>
    private void OnWindowClosedFromOutside(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        if (sender is BlackoutWindow window)
            _windows.Remove(window);

        // Any windows left are torn down through the normal path; if that was the
        // last one there is nothing to close, only state to release.
        if (IsActive)
        {
            Hide(BlackoutDismissReason.WindowClosed);
            return;
        }

        AppDiagnostics.Info($"Blackout off ({BlackoutDismissReason.WindowClosed}).");
        ReleaseBlackoutState();
    }

    private string BuildHintText()
    {
        HotkeyDefinition hotkey = _options.Hotkey;
        string keyLine = hotkey.IsSet
            ? $"Press Esc or {hotkey.DisplayText} to return"
            : "Press Esc to return";

        if (_options.DismissTriggers.HasFlag(DismissTriggers.MouseMove))
            keyLine += " — or move the mouse";

        return $"OLED blackout\n{keyLine}";
    }

    private void OnWindowKeyObserved(object? sender, BlackoutKeyEventArgs e)
    {
        // The hotkey's own key belongs to the hotkey paths (WM_HOTKEY and the
        // raw-input matcher). Counting it as ordinary input here would dismiss the
        // blackout a moment before the hotkey toggled it back on.
        if (e.VirtualKey == _options.Hotkey.VirtualKey)
            return;

        HandleKeyEvent(e.VirtualKey, e.IsKeyDown);
    }

    private void OnWindowMouseButtonObserved(object? sender, EventArgs e)
    {
        if (_dismissEvaluator.ProcessMouseButton(DateTime.UtcNow))
            Hide(BlackoutDismissReason.MouseClick);
    }

    /// <summary>
    /// The mouse sink only runs while the blackout is up. An app sitting in the
    /// tray has no business receiving a window message for every mouse movement
    /// on the system.
    /// </summary>
    private void RegisterMouseSink()
    {
        if (_mouseSinkRegistered)
            return;

        // Cheap to skip when nothing mouse-driven can dismiss the blackout.
        if (!_options.DismissTriggers.HasFlag(DismissTriggers.MouseMove)
            && !_options.DismissTriggers.HasFlag(DismissTriggers.MouseClick))
        {
            return;
        }

        _mouseSinkRegistered = RawInputInterop.RegisterMouseSink(_rawInputHostHandle());
    }

    private void UnregisterMouseSink()
    {
        if (!_mouseSinkRegistered)
            return;

        RawInputInterop.UnregisterMouseSink();
        _mouseSinkRegistered = false;
    }

    private void ApplyDisplayRequest(bool keepAwake)
    {
        if (keepAwake == _displayRequested)
            return;

        NativeInterop.SetDisplayRequired(keepAwake);
        _displayRequested = keepAwake;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _dismissEvaluator.Disarm();
        UnregisterMouseSink();
        ApplyDisplayRequest(false);
        DestroyWindows();
    }
}
