using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using OLEDSaver.Helpers;
using OLEDSaver.Input;
using OLEDSaver.Services;
using OLEDSaver.ViewModels;

using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;

namespace OLEDSaver;

/// <summary>
/// The settings window, and the host for everything that has to live on a window
/// handle: the global hotkey, the raw-input sinks and the display-change
/// notification. It stays alive for the whole session — closing it only hides it
/// to the tray — so those registrations never have to move.
/// </summary>
public partial class MainWindow : Window
{
    private const int HotkeyId = 9100;

    private readonly MainViewModel _viewModel = new();
    private readonly HotkeyRegistrationController _hotkeyController = new();
    private readonly RawInputHotkeyMatcher _rawInputHotkeyMatcher = new();
    private readonly StartupRegistrySyncService _startupRegistrySyncService = new();
    private readonly BlackoutController _blackoutController;
    private readonly IdleBlackoutWatcher _idleBlackoutWatcher;
    private readonly bool _startHiddenInTray;

    private HwndSource? _hwndSource;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _trayIconImage;

    // The raw-input fallback only runs when we actually own the hotkey via
    // RegisterHotKey; otherwise it would bypass another app's ownership of the
    // same combination and both apps would react to it.
    private bool _globalHotkeyRegistered;
    private bool _rawInputSinkRegistered;
    private bool _isExiting;
    private bool _isTornDown;

    public MainWindow()
        : this(startHiddenInTray: false)
    {
    }

    public MainWindow(bool startHiddenInTray)
    {
        _startHiddenInTray = startHiddenInTray;
        DataContext = _viewModel;
        InitializeComponent();

        VersionText.Text = AppVersion.DisplayText;

        _blackoutController = new BlackoutController(_viewModel, () => new WindowInteropHelper(this).Handle);
        _blackoutController.ActiveChanged += OnBlackoutActiveChanged;

        _idleBlackoutWatcher = new IdleBlackoutWatcher(
            _viewModel,
            () => _blackoutController.Show(BlackoutTrigger.Idle),
            () => _blackoutController.IsActive);

        InitializeTrayIcon();
        SyncStartWithWindows();

        _viewModel.PropertyChanged += ViewModel_PropertyChanged;

        if (_startHiddenInTray)
            ShowInTaskbar = false;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);

        // Fallback path for games that suppress WM_HOTKEY delivery while focused,
        // and the input source that dismisses the blackout even when something
        // stole focus from the overlay.
        _rawInputSinkRegistered = RawInputInterop.RegisterKeyboardSink(hwnd);

        ApplyHotkey();
        _idleBlackoutWatcher.Sync();

        if (_startHiddenInTray)
            HideToTray();
    }

    // ------------------------------------------------------------------ hotkey

    private void ApplyHotkey()
    {
        if (_hwndSource == null)
            return;

        HotkeyDefinition hotkey = _viewModel.Hotkey;

        _globalHotkeyRegistered = _hotkeyController.ReRegister(this, HotkeyId, hotkey);
        _viewModel.IsHotkeyRegistered = !hotkey.IsSet || _globalHotkeyRegistered;

        // Seeded with what is held right now: the user is still pressing the
        // combination they just recorded, and its key-down must not count.
        _rawInputHotkeyMatcher.Configure(
            hotkey,
            RawInputInterop.GetPressedModifierVirtualKeys(),
            RawInputInterop.IsKeyPressed(hotkey.VirtualKey));
    }

    private void RecordHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsRecordingHotkey)
        {
            StopRecordingHotkey();
            return;
        }

        _viewModel.IsRecordingHotkey = true;

        // Released while recording, so pressing the current combination records it
        // again instead of blanking the screen mid-recording.
        _hotkeyController.Unregister(this, HotkeyId);
        _globalHotkeyRegistered = false;

        Keyboard.ClearFocus();
        Focus();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_viewModel.IsRecordingHotkey)
            return;

        e.Handled = true;

        // Alt-qualified presses arrive as Key.System with the real key in SystemKey.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            StopRecordingHotkey();
            return;
        }

        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);

        // Keep listening while only modifiers are down — the user is on their way
        // to "Ctrl + Alt + B", not asking to bind Ctrl.
        if (virtualKey == 0 || HotkeyDefinition.IsModifierKey(virtualKey))
            return;

        var recorded = new HotkeyDefinition(HotkeyDefinition.FromWpfModifiers(Keyboard.Modifiers), virtualKey);
        if (!recorded.IsSet)
            return;

        _viewModel.Hotkey = recorded;
        StopRecordingHotkey();
    }

    private void StopRecordingHotkey()
    {
        _viewModel.IsRecordingHotkey = false;
        ApplyHotkey();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        // Recording holds the hotkey unregistered. Clicking away mid-recording
        // would otherwise leave the app with no working hotkey and no sign of why.
        if (_viewModel.IsRecordingHotkey)
            StopRecordingHotkey();

        base.OnDeactivated(e);
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Hotkey):
                // While recording, StopRecordingHotkey owns re-registration.
                if (!_viewModel.IsRecordingHotkey)
                    ApplyHotkey();
                break;

            case nameof(MainViewModel.StartWithWindows):
                SyncStartWithWindows();
                break;

            case nameof(MainViewModel.IdleBlackoutEnabled):
                _idleBlackoutWatcher.Sync();
                break;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeInterop.WM_HOTKEY when wParam.ToInt32() == HotkeyId:
                if (!_viewModel.IsRecordingHotkey)
                {
                    // The raw-input path may already have toggled for this physical
                    // press; the matcher claims each press exactly once. Without a
                    // working sink there is no duplicate source, so toggle directly.
                    if (!_rawInputSinkRegistered || _rawInputHotkeyMatcher.TryHandleHotkeyMessage())
                        _blackoutController.Toggle(BlackoutTrigger.Hotkey);
                }

                handled = true;
                break;

            case NativeInterop.WM_INPUT:
                HandleRawInput(lParam);
                // handled stays false so WPF runs DefWindowProc, which performs the
                // required WM_INPUT cleanup.
                break;

            case NativeInterop.WM_DISPLAYCHANGE:
            case NativeInterop.WM_DPICHANGED:
                // A monitor was plugged in, unplugged, rearranged or rescaled.
                _viewModel.RefreshDisplays();
                _blackoutController.HandleDisplayChange();
                break;
        }

        return IntPtr.Zero;
    }

    private void HandleRawInput(IntPtr lParam)
    {
        if (RawInputInterop.TryGetKeyboardEvent(lParam, out RawKeyboardEvent keyboardEvent))
        {
            // Only act on the hotkey here when we own it via RegisterHotKey. If
            // another app owns the combination, registration failed and reacting
            // anyway would ignore that ownership.
            bool hotkeyPressed = _rawInputHotkeyMatcher.ProcessKeyEvent(keyboardEvent.VirtualKey, keyboardEvent.IsKeyDown);

            // The hotkey takes precedence over dismissal: this press means
            // "toggle", and letting it also count as ordinary input would switch
            // the blackout off and immediately back on.
            if (hotkeyPressed && _globalHotkeyRegistered && !_viewModel.IsRecordingHotkey)
            {
                _blackoutController.Toggle(BlackoutTrigger.Hotkey);
                return;
            }

            _blackoutController.HandleKeyEvent(keyboardEvent.VirtualKey, keyboardEvent.IsKeyDown);
            return;
        }

        if (RawInputInterop.TryGetMouseEvent(lParam, out RawMouseEvent mouseEvent))
            _blackoutController.HandleMouseEvent(mouseEvent);
    }

    // ----------------------------------------------------------------- actions

    /// <summary>Blanks the screen on request from another instance (<c>--blackout</c>).</summary>
    public void ShowBlackoutFromCommandLine() => _blackoutController.Show(BlackoutTrigger.CommandLine);

    /// <summary>Blanks the screen, or takes it back, on request from another instance (<c>--toggle</c>).</summary>
    public void ToggleBlackoutFromCommandLine() => _blackoutController.Toggle(BlackoutTrigger.CommandLine);

    private void BlackoutNow_Click(object sender, RoutedEventArgs e)
    {
        _blackoutController.Toggle(BlackoutTrigger.Manual);
    }

    private void RefreshDisplays_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.RefreshDisplays();
    }

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        string? folder = Path.GetDirectoryName(_viewModel.SettingsFilePath);
        if (string.IsNullOrWhiteSpace(folder))
            return;

        Directory.CreateDirectory(folder);
        OpenWithShell(folder);
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(AppDiagnostics.LogPath))
        {
            MessageBox.Show(this, "Nothing has been logged yet.", "OLED Saver", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OpenWithShell(AppDiagnostics.LogPath);
    }

    private void OpenWithShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warning($"Failed to open '{path}'.", ex);
        }
    }

    private void OnBlackoutActiveChanged(object? sender, bool isActive)
    {
        _viewModel.IsBlackoutActive = isActive;

        if (_trayIcon != null)
            _trayIcon.Text = isActive ? "OLED Saver — blacked out" : "OLED Saver";
    }

    private void SyncStartWithWindows()
    {
        try
        {
            _startupRegistrySyncService.Sync(_viewModel.StartWithWindows, Environment.ProcessPath);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warning("Failed to synchronize the Start with Windows registry setting.", ex);
        }
    }

    // -------------------------------------------------------------- tray / show

    private void InitializeTrayIcon()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "OLED Saver",
            Visible = true
        };

        _trayIconImage = TryLoadTrayIcon();
        _trayIcon.Icon = _trayIconImage ?? System.Drawing.SystemIcons.Application;

        var contextMenu = new System.Windows.Forms.ContextMenuStrip();
        contextMenu.Items.Add("Black out now", null, (_, _) => _blackoutController.Show(BlackoutTrigger.Manual));
        contextMenu.Items.Add("Settings…", null, (_, _) => ShowAndActivate());
        contextMenu.Items.Add("-");
        contextMenu.Items.Add("Quit", null, (_, _) => ExitApplication());
        _trayIcon.ContextMenuStrip = contextMenu;
        _trayIcon.DoubleClick += (_, _) => ShowAndActivate();
    }

    private static System.Drawing.Icon? TryLoadTrayIcon()
    {
        try
        {
            // The executable's own icon works in dev and in a single-file publish
            // without shipping a separate content file.
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                return System.Drawing.Icon.ExtractAssociatedIcon(exePath);
        }
        catch
        {
            // The caller falls back to the system icon.
        }

        return null;
    }

    public void StartHiddenInTray()
    {
        ShowInTaskbar = false;

        // Forces the handle (and with it OnSourceInitialized, the hotkey and the
        // input sink) to exist even though no window is ever shown.
        new WindowInteropHelper(this).EnsureHandle();
        HideToTray();
    }

    public void ShowAndActivate()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();

        // Toggling Topmost once is what reliably brings a window back to the
        // foreground from the tray.
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
    }

    // Minimize really minimizes: it carries the standard minimize glyph, and two
    // title-bar buttons that look different and do the same thing are a puzzle.
    // Hiding to the tray is what the close button is for, and its tooltip says so.
    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void Quit_Click(object sender, RoutedEventArgs e) => ExitApplication();

    /// <summary>
    /// Releases everything the process holds, without shutting the application
    /// down. Called on the session-ending path, where the process disappears
    /// without <see cref="OnClosed"/> ever running.
    /// </summary>
    public void PrepareForShutdown() => Teardown();

    private void ExitApplication()
    {
        _isExiting = true;

        // Torn down before Shutdown rather than relying on OnClosed: a tray icon
        // that outlives its process leaves a ghost in the notification area until
        // the user hovers over it.
        Teardown();

        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>
    /// Releases the hotkey, the input sinks, the tray icon and the blackout, and
    /// flushes any pending save. Idempotent, because it runs on the way out
    /// through either the tray or the window.
    /// </summary>
    private void Teardown()
    {
        if (_isTornDown)
            return;

        _isTornDown = true;

        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _blackoutController.ActiveChanged -= OnBlackoutActiveChanged;

        _idleBlackoutWatcher.Dispose();
        _blackoutController.Dispose();

        _hotkeyController.Unregister(this, HotkeyId);
        RawInputInterop.UnregisterKeyboardSink();
        _hwndSource?.RemoveHook(WndProc);

        DisposeTrayIcon();

        // Flushes any debounced save before the process goes away.
        _viewModel.Dispose();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closing the window means "get out of my way", not "stop working": the
        // hotkey has to keep working from the tray.
        if (!_isExiting)
        {
            e.Cancel = true;
            HideToTray();
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        Teardown();
        base.OnClosed(e);
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayIconImage?.Dispose();
        _trayIconImage = null;
    }
}
