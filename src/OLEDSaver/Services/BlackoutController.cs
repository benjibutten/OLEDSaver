using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
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
    /// <summary>
    /// How many overlays are kept alive between blackouts. One per monitor is the
    /// point; the cap only exists so an unusual desktop cannot leave the process
    /// holding a dozen idle render surfaces.
    /// </summary>
    private const int MaxPooledWindows = 8;

    /// <summary>
    /// How long the overlay may take to actually reach the screen before that is
    /// worth a line in the log. Show() returning is not the same as the monitor
    /// being black — WPF composes the frame afterwards — and this is the number
    /// that matches what the delay feels like. Roughly two frames at 60 Hz, so a
    /// blackout that goes up promptly says nothing and a sluggish one is on the
    /// record. Set well above the ~45 ms the first blackout of a session costs,
    /// which is the pre-built overlay being moved onto its monitor for the first
    /// time and is not what "sluggish" means.
    /// </summary>
    private static readonly TimeSpan SlowFirstFrameThreshold = TimeSpan.FromMilliseconds(100);

    private readonly IBlackoutOptions _options;
    private readonly Func<IntPtr> _rawInputHostHandle;
    private readonly BlackoutDismissEvaluator _dismissEvaluator = new();
    private readonly List<BlackoutWindow> _windows = new();

    /// <summary>
    /// Overlays that have been built, shown once and hidden again, waiting to be
    /// shown over the same monitor. Reusing them is what makes the hotkey feel
    /// instant: building a WPF window, creating its handle and letting WPF
    /// allocate a render surface the size of the monitor is most of the delay
    /// between the key going down and the screen going black, and none of it has
    /// to happen more than once.
    /// </summary>
    private readonly List<BlackoutWindow> _pool = new();

    private IntPtr _previousForegroundWindow;
    private bool _mouseSinkRegistered;
    private bool _displayRequested;
    private bool _prewarmed;
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

        long startedAt = Stopwatch.GetTimestamp();

        IReadOnlyList<DisplayInfo> targets = ResolveCurrentTargets();

        if (targets.Count == 0)
        {
            AppDiagnostics.Warning("Blackout requested but Windows reported no displays.");
            return;
        }

        // Captured before the overlay takes focus, so dismissing puts the
        // keyboard back where the user left it.
        _previousForegroundWindow = NativeInterop.GetCurrentForegroundWindow();

        ShowWindows(targets);

        // Taking focus is deliberate: with the screen black, keystrokes must not
        // keep landing in whatever was in front a moment ago.
        _windows[0].Activate();
        NativeInterop.ForceForeground(_windows[0]);

        _dismissEvaluator.Configure(_options.DismissTriggers, _options.MouseMoveThresholdPixels);
        _dismissEvaluator.Arm(DateTime.UtcNow, RawInputInterop.GetPressedVirtualKeys());

        RegisterMouseSink();
        ApplyDisplayRequest(_options.KeepDisplaysAwake);

        // The elapsed time is logged because "it feels sluggish" is otherwise
        // unmeasurable after the fact, and the number tells a cold first blackout
        // apart from a warm one that reused its overlays.
        AppDiagnostics.Info(
            $"Blackout on ({trigger}) covering {Describe(targets)} "
            + $"in {Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:F1} ms.");

        ReportSlowFirstFrame(startedAt);
        ActiveChanged?.Invoke(this, true);
    }

    /// <summary>
    /// Which monitors the blackout went onto, for the log.
    ///
    /// The count on its own cannot answer the only question worth asking after
    /// "it blanked the wrong screen": which panel did it actually cover, and was
    /// that the one the settings window had ticked. Naming them is what turns
    /// that from an argument into a lookup.
    /// </summary>
    private static string Describe(IReadOnlyList<DisplayInfo> targets) =>
        $"{targets.Count} display(s) ["
        + string.Join(", ", targets.Select(target =>
            $"{target.Name} @ {target.X},{target.Y} {target.Width}x{target.Height}"))
        + "]";

    /// <summary>
    /// Logs how long the blackout took to reach the screen, but only when that was
    /// slow enough to feel like it.
    ///
    /// The work above is synchronous; the frame is not. WPF composes and presents
    /// on its own thread after the dispatcher yields, so a Show() that returns in
    /// two milliseconds can still leave the desktop visible for another fifty.
    /// The first Rendering tick after the overlays go up is the closest thing to
    /// "the screen is black now" that is observable from here.
    /// </summary>
    private void ReportSlowFirstFrame(long startedAt)
    {
        EventHandler? onRendering = null;

        onRendering = (_, _) =>
        {
            // One frame only. Left attached it would run for every frame the app
            // ever composes.
            CompositionTarget.Rendering -= onRendering;

            TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);
            if (elapsed < SlowFirstFrameThreshold)
                return;

            AppDiagnostics.Warning($"Blackout took {elapsed.TotalMilliseconds:F1} ms to reach the screen.");
        };

        CompositionTarget.Rendering += onRendering;
    }

    /// <summary>
    /// Builds the overlays and reads the display layout before anyone asks for a
    /// blackout, so the first press of the hotkey costs no more than the tenth.
    ///
    /// Everything expensive about the first blackout happens exactly once per
    /// process: parsing the window's compiled XAML, creating its handle, WPF
    /// spinning up its render thread and D3D device, resolving the Segoe UI glyph
    /// typeface for the hint, and asking Windows what monitors are attached.
    /// Doing it here spends an idle moment after startup instead of a visible
    /// stall on the hotkey. The windows are parked far off every monitor while
    /// this runs, so nothing appears on screen.
    /// </summary>
    public void Prewarm()
    {
        if (_disposed || _prewarmed || IsActive)
            return;

        _prewarmed = true;

        try
        {
            IReadOnlyList<DisplayInfo> targets = ResolveCurrentTargets();
            if (targets.Count == 0)
                return;

            int count = Math.Min(targets.Count, MaxPooledWindows);
            var warming = new List<BlackoutWindow>(count);

            for (int index = 0; index < count; index++)
            {
                BlackoutWindow window = CreateWindow();

                // The real hint, visible: laying the TextBlock out is what makes
                // WPF resolve the Segoe UI glyph typeface, and leaving that to the
                // first blackout costs it a good 20 ms. Nothing is on screen to
                // see it — the window is parked off every monitor.
                window.PrepareForShow(BuildHintText(), showHint: true);

                // Parked at the size it will be shown at, so the maximize on the
                // first real blackout is a move rather than a reallocation.
                window.ParkOffScreen(targets[index].Width, targets[index].Height);
                window.ShowForPrewarm();

                warming.Add(window);
            }

            // Hidden only once the dispatcher has gone idle, which is after WPF
            // has laid each window out and composed a frame for it. Hiding in this
            // same turn would skip the render, and the render is the part most
            // worth paying for early.
            warming[0].Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                foreach (BlackoutWindow window in warming)
                {
                    // A blackout that started while the prewarm was in flight owns
                    // this window now.
                    if (_windows.Contains(window))
                        continue;

                    window.Hide();

                    // Shown and hidden a second time, because the path that shows
                    // an existing hidden window is not the path that shows a new
                    // one, and every blackout after the first takes it. Running it
                    // once here is what gets it compiled before the hotkey needs it.
                    window.ShowForPrewarm();
                    window.Hide();
                    window.StopHint();

                    if (_pool.Count < MaxPooledWindows)
                        _pool.Add(window);
                    else
                        CloseWindow(window);
                }

                AppDiagnostics.Info($"Blackout overlays pre-built for {warming.Count} display(s).");
            });
        }
        catch (Exception ex)
        {
            // A failed prewarm costs nothing but the speed it was meant to buy.
            AppDiagnostics.Warning("Failed to pre-build the blackout overlays.", ex);
        }
    }

    private IReadOnlyList<DisplayInfo> ResolveCurrentTargets() => DisplayService.ResolveTargets(
        DisplayService.GetDisplays(),
        _options.DisplayTargetMode,
        _options.SelectedDisplayIds);

    public void Hide(BlackoutDismissReason reason)
    {
        if (!IsActive)
            return;

        AppDiagnostics.Info($"Blackout off ({reason}).");
        HideWindows();
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

        IReadOnlyList<DisplayInfo> targets = ResolveCurrentTargets();

        if (targets.Count == 0)
        {
            Hide(BlackoutDismissReason.NoDisplays);
            return;
        }

        // WM_DISPLAYCHANGE also arrives for changes that move nothing the blackout
        // cares about — a refresh rate, or a monitor that is already covered
        // waking up. Re-covering anyway would drop every overlay and put it
        // straight back, which is a full-screen flash of desktop for no reason.
        if (_windows.Count == targets.Count && targets.All(IsAlreadyCovered))
            return;

        HideWindows();
        ShowWindows(targets);
        _windows[0].Activate();
    }

    private bool IsAlreadyCovered(DisplayInfo target) =>
        _windows.Exists(window => window.IsCovering(target));

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

    private void ShowWindows(IReadOnlyList<DisplayInfo> targets)
    {
        string hint = BuildHintText();
        bool showHint = _options.ShowHintOnBlackout;

        foreach (DisplayInfo target in targets)
        {
            BlackoutWindow window = RentWindow(target);
            window.PrepareForShow(hint, showHint);

            // The fast path, and the reason the pool exists: this overlay is still
            // sized and positioned over exactly this monitor from last time, so
            // there is nothing to move, nothing to lay out again and no render
            // surface to allocate — only a ShowWindow call.
            bool alreadyCovering = window.IsCovering(target);

            if (!alreadyCovering)
            {
                // Parked far off every monitor first, because a WPF window is
                // placed and sized by its own properties on the way up and would
                // otherwise appear briefly on the primary monitor. CoverDisplay
                // then moves it onto the monitor it belongs to and maximizes it
                // there.
                window.ParkOffScreen(target.Width, target.Height);
            }

            window.Show();

            if (!alreadyCovering)
                window.CoverDisplay(target);

            _windows.Add(window);
        }
    }

    /// <summary>
    /// An overlay for this monitor: the pooled one that already covers it where
    /// there is one, any pooled one otherwise, and a new window only when the pool
    /// is empty.
    /// </summary>
    private BlackoutWindow RentWindow(DisplayInfo target)
    {
        int match = _pool.FindIndex(window => window.IsCovering(target));
        if (match < 0)
            match = _pool.Count - 1;

        if (match < 0)
            return CreateWindow();

        BlackoutWindow pooled = _pool[match];
        _pool.RemoveAt(match);
        return pooled;
    }

    private BlackoutWindow CreateWindow()
    {
        var window = new BlackoutWindow();
        window.KeyObserved += OnWindowKeyObserved;
        window.MouseButtonObserved += OnWindowMouseButtonObserved;
        window.Closed += OnWindowClosedFromOutside;
        return window;
    }

    /// <summary>
    /// Takes the overlays down without destroying them. They keep their handle,
    /// their render surface and their placement over the monitor they were
    /// covering, which is exactly what the next blackout reuses.
    /// </summary>
    private void HideWindows()
    {
        foreach (BlackoutWindow window in _windows)
        {
            // A hidden window animating its hint would keep WPF composing frames
            // for something nobody can see.
            window.StopHint();
            window.Hide();

            if (_pool.Count < MaxPooledWindows)
                _pool.Add(window);
            else
                CloseWindow(window);
        }

        _windows.Clear();
    }

    private void CloseWindow(BlackoutWindow window)
    {
        window.KeyObserved -= OnWindowKeyObserved;
        window.MouseButtonObserved -= OnWindowMouseButtonObserved;
        window.Closed -= OnWindowClosedFromOutside;
        window.Close();
    }

    private void CloseAllWindows()
    {
        foreach (BlackoutWindow window in _pool)
            CloseWindow(window);

        _pool.Clear();

        foreach (BlackoutWindow window in _windows)
            CloseWindow(window);

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
        {
            // A pooled overlay is hidden off-screen and covering nothing. Losing
            // one costs the next blackout a rebuild and nothing else, so it must
            // not bring a live blackout down with it.
            if (_pool.Remove(window))
                return;

            _windows.Remove(window);
        }

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
        CloseAllWindows();
    }
}
