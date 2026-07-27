using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OLEDSaver.Input;
using OLEDSaver.Models;
using OLEDSaver.Services;

namespace OLEDSaver.ViewModels;

/// <summary>
/// The single view model behind the settings window. It also serves as the
/// options source the blackout and the idle watcher read, so there is exactly one
/// copy of every setting in the process.
///
/// Every setter writes through to <see cref="AppSettings"/> and asks for a
/// debounced save: the UI has sliders and checkboxes, and rewriting the file on
/// every tick of a drag would be a lot of disk churn for no benefit.
/// </summary>
public sealed class MainViewModel : ObservableObject, IBlackoutOptions, IIdleBlackoutOptions, IDisposable
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(600);

    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _saveTimer;

    private bool _isBlackoutActive;
    private bool _isHotkeyRegistered = true;
    private bool _isRecordingHotkey;
    private bool _disposed;

    public MainViewModel(SettingsStore? store = null)
    {
        _store = store ?? new SettingsStore();
        _settings = _store.Load();
        _store.EnsureSaved(_settings);

        _saveTimer = new DispatcherTimer { Interval = SaveDebounce };
        _saveTimer.Tick += OnSaveTimerTick;

        Displays = new ObservableCollection<DisplayOptionViewModel>();
        RefreshDisplays();
    }

    public ObservableCollection<DisplayOptionViewModel> Displays { get; }

    public string SettingsFilePath => _store.SettingsFilePath;

    // ---------------------------------------------------------------- hotkey

    public HotkeyDefinition Hotkey
    {
        get => _settings.GetHotkey();
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            if (Hotkey.Equals(value))
                return;

            _settings.SetHotkey(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(HotkeyDisplayText));
            OnPropertyChanged(nameof(HotkeyWarning));
            OnPropertyChanged(nameof(HasHotkeyWarning));
            RequestSave();
        }
    }

    public string HotkeyDisplayText => Hotkey.DisplayText;

    /// <summary>True while the settings window is listening for a new combination.</summary>
    public bool IsRecordingHotkey
    {
        get => _isRecordingHotkey;
        set => SetProperty(ref _isRecordingHotkey, value);
    }

    /// <summary>Set by the window after each RegisterHotKey attempt.</summary>
    public bool IsHotkeyRegistered
    {
        get => _isHotkeyRegistered;
        set
        {
            if (SetProperty(ref _isHotkeyRegistered, value))
            {
                OnPropertyChanged(nameof(HotkeyWarning));
                OnPropertyChanged(nameof(HasHotkeyWarning));
            }
        }
    }

    public bool HasHotkeyWarning => HotkeyWarning.Length > 0;

    public string HotkeyWarning
    {
        get
        {
            if (!Hotkey.IsSet)
                return "No hotkey is set — record one, or use the tray icon to blank the screen.";

            if (!IsHotkeyRegistered)
                return "Windows refused this combination. Another application already owns it — record a different one.";

            if (Hotkey.HasNoModifier)
                return "No modifier key: this takes the key away from every other application while OLED Saver runs.";

            return string.Empty;
        }
    }

    // -------------------------------------------------------------- displays

    public DisplayTargetMode DisplayTargetMode
    {
        get => _settings.DisplayTargetMode;
        set
        {
            if (_settings.DisplayTargetMode == value)
                return;

            _settings.DisplayTargetMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAllDisplaysMode));
            OnPropertyChanged(nameof(IsPrimaryOnlyMode));
            OnPropertyChanged(nameof(IsSelectedDisplaysMode));
            RequestSave();
        }
    }

    // Three bools rather than a converter: RadioButton.IsChecked binds to them
    // directly, and the "unset" half of a radio change needs no handling.
    public bool IsAllDisplaysMode
    {
        get => DisplayTargetMode == DisplayTargetMode.AllDisplays;
        set
        {
            if (value)
                DisplayTargetMode = DisplayTargetMode.AllDisplays;
        }
    }

    public bool IsPrimaryOnlyMode
    {
        get => DisplayTargetMode == DisplayTargetMode.PrimaryOnly;
        set
        {
            if (value)
                DisplayTargetMode = DisplayTargetMode.PrimaryOnly;
        }
    }

    public bool IsSelectedDisplaysMode
    {
        get => DisplayTargetMode == DisplayTargetMode.SelectedDisplays;
        set
        {
            if (value)
                DisplayTargetMode = DisplayTargetMode.SelectedDisplays;
        }
    }

    public IReadOnlyCollection<string> SelectedDisplayIds => _settings.SelectedDisplayIds;

    /// <summary>
    /// Rebuilds the monitor list from Windows. Called at startup and whenever the
    /// display layout changes, so unplugging the OLED does not leave a stale row
    /// in the settings window.
    ///
    /// Ticks in <see cref="AppSettings.SelectedDisplayIds"/> are kept for monitors
    /// that are not currently attached: a display that is merely switched off
    /// should still be selected when it comes back.
    /// </summary>
    public void RefreshDisplays()
    {
        Displays.Clear();

        foreach (DisplayInfo display in DisplayService.GetDisplays())
        {
            bool isSelected = _settings.SelectedDisplayIds.Contains(display.Id, StringComparer.OrdinalIgnoreCase);
            Displays.Add(new DisplayOptionViewModel(display, isSelected, OnDisplaySelectionChanged));
        }

        OnPropertyChanged(nameof(Displays));
    }

    private void OnDisplaySelectionChanged(DisplayOptionViewModel option)
    {
        if (option.IsSelected)
        {
            if (!_settings.SelectedDisplayIds.Contains(option.Id, StringComparer.OrdinalIgnoreCase))
                _settings.SelectedDisplayIds.Add(option.Id);
        }
        else
        {
            _settings.SelectedDisplayIds.RemoveAll(id => string.Equals(id, option.Id, StringComparison.OrdinalIgnoreCase));
        }

        // Ticking a monitor is what the user means by "these displays", so the
        // mode follows along instead of making them click the radio button too.
        //
        // Unticking the last one has to move the radio button back, because an
        // empty selection resolves to every display: leaving the UI on "only the
        // ones I tick below" while the next blackout covers the LCD as well is the
        // one state where the window would be lying about what it will do.
        DisplayTargetMode = _settings.SelectedDisplayIds.Count > 0
            ? DisplayTargetMode.SelectedDisplays
            : DisplayTargetMode.AllDisplays;

        RequestSave();
    }

    // -------------------------------------------------------------- dismissal

    public bool DismissOnKeyPress
    {
        get => _settings.DismissOnKeyPress;
        set => SetSetting(_settings.DismissOnKeyPress, value, v => _settings.DismissOnKeyPress = v, nameof(DismissTriggers));
    }

    public bool DismissOnMouseMove
    {
        get => _settings.DismissOnMouseMove;
        set => SetSetting(_settings.DismissOnMouseMove, value, v => _settings.DismissOnMouseMove = v, nameof(DismissTriggers));
    }

    public bool DismissOnMouseClick
    {
        get => _settings.DismissOnMouseClick;
        set => SetSetting(_settings.DismissOnMouseClick, value, v => _settings.DismissOnMouseClick = v, nameof(DismissTriggers));
    }

    public int MouseMoveThresholdPixels
    {
        get => _settings.MouseMoveThresholdPixels;
        set => SetSetting(
            _settings.MouseMoveThresholdPixels,
            Math.Clamp(value, AppSettings.MinMouseMoveThresholdPixels, AppSettings.MaxMouseMoveThresholdPixels),
            v => _settings.MouseMoveThresholdPixels = v);
    }

    public DismissTriggers DismissTriggers => _settings.GetDismissTriggers();

    public bool ShowHintOnBlackout
    {
        get => _settings.ShowHintOnBlackout;
        set => SetSetting(_settings.ShowHintOnBlackout, value, v => _settings.ShowHintOnBlackout = v);
    }

    public bool KeepDisplaysAwake
    {
        get => _settings.KeepDisplaysAwake;
        set => SetSetting(_settings.KeepDisplaysAwake, value, v => _settings.KeepDisplaysAwake = v);
    }

    // ------------------------------------------------------------ idle / startup

    public bool IdleBlackoutEnabled
    {
        get => _settings.IdleBlackoutEnabled;
        set => SetSetting(_settings.IdleBlackoutEnabled, value, v => _settings.IdleBlackoutEnabled = v);
    }

    public int IdleBlackoutMinutes
    {
        get => _settings.IdleBlackoutMinutes;
        set => SetSetting(
            _settings.IdleBlackoutMinutes,
            Math.Clamp(value, AppSettings.MinIdleBlackoutMinutes, AppSettings.MaxIdleBlackoutMinutes),
            v => _settings.IdleBlackoutMinutes = v);
    }

    public bool SkipIdleBlackoutWhenFullscreen
    {
        get => _settings.SkipIdleBlackoutWhenFullscreen;
        set => SetSetting(_settings.SkipIdleBlackoutWhenFullscreen, value, v => _settings.SkipIdleBlackoutWhenFullscreen = v);
    }

    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set => SetSetting(_settings.StartWithWindows, value, v => _settings.StartWithWindows = v);
    }

    // ---------------------------------------------------------------- status

    /// <summary>Mirrors the controller, so the window can show state and swap its button label.</summary>
    public bool IsBlackoutActive
    {
        get => _isBlackoutActive;
        set
        {
            if (SetProperty(ref _isBlackoutActive, value))
            {
                OnPropertyChanged(nameof(BlackoutButtonText));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string BlackoutButtonText => IsBlackoutActive ? "Turn the screen back on" : "Black out now";

    public string StatusText => IsBlackoutActive
        ? "Blacked out"
        : Hotkey.IsSet && IsHotkeyRegistered
            ? $"Ready — press {Hotkey.DisplayText}"
            : "Ready";

    // ------------------------------------------------------------ persistence

    private void SetSetting<T>(
        T current,
        T value,
        Action<T> apply,
        string? alsoNotify = null,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
            return;

        apply(value);
        OnPropertyChanged(propertyName);

        if (alsoNotify != null)
            OnPropertyChanged(alsoNotify);

        RequestSave();
    }

    private void RequestSave()
    {
        OnPropertyChanged(nameof(StatusText));

        if (_disposed)
            return;

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnSaveTimerTick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        _store.Save(_settings);
    }

    /// <summary>Flushes a pending debounced save. Called on exit.</summary>
    public void SaveNow()
    {
        _saveTimer.Stop();
        _store.Save(_settings);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _saveTimer.Tick -= OnSaveTimerTick;
        SaveNow();
    }
}
