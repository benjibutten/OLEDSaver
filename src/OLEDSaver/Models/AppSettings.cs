using System.Text.Json.Serialization;
using OLEDSaver.Input;

namespace OLEDSaver.Models;

/// <summary>Which monitors the blackout covers.</summary>
public enum DisplayTargetMode
{
    /// <summary>Every connected monitor.</summary>
    AllDisplays,

    /// <summary>Only the primary monitor.</summary>
    PrimaryOnly,

    /// <summary>Only the monitors listed in <see cref="AppSettings.SelectedDisplayIds"/>.</summary>
    SelectedDisplays
}

/// <summary>
/// Everything the app persists, as plain data so it round-trips through
/// System.Text.Json without converters.
/// </summary>
public sealed class AppSettings
{
    public const int MinIdleBlackoutMinutes = 1;
    public const int MaxIdleBlackoutMinutes = 240;
    public const int MinMouseMoveThresholdPixels = 5;
    public const int MaxMouseMoveThresholdPixels = 400;

    /// <summary>The shape this build writes. <see cref="Normalize"/> stamps it after migrating.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// What a file carrying no <see cref="SchemaVersion"/> is. The property itself
    /// arrived with v2, so every file the first release wrote is missing it — and a
    /// missing version means the oldest shape, never the newest.
    /// </summary>
    public const int LegacySchemaVersion = 1;

    /// <summary>
    /// The version at which <see cref="SelectedDisplayIds"/> started holding
    /// monitor device paths rather than GDI slot names.
    /// </summary>
    public const int StableDisplayIdSchemaVersion = 2;

    /// <summary>
    /// Defaults to <see cref="LegacySchemaVersion"/>, not to the current version: this
    /// is the value a v1 file deserializes with, and treating those as already migrated
    /// would strand their display selection on slot names that no longer match anything.
    /// <see cref="SettingsStore.CreateDefault"/> is what keeps a fresh install from
    /// being written to disk as v1.
    /// </summary>
    public int SchemaVersion { get; set; } = LegacySchemaVersion;

    /// <summary>
    /// The version the file was written with, kept because <see cref="Normalize"/>
    /// stamps <see cref="SchemaVersion"/> over it. A migration that needs
    /// something only the running app can see — which monitors are attached —
    /// cannot happen inside Normalize, and branches on this instead.
    /// </summary>
    [JsonIgnore]
    public int LoadedSchemaVersion { get; private set; } = LegacySchemaVersion;

    /// <summary>RegisterHotKey fsModifiers value; see <see cref="HotkeyModifiers"/>.</summary>
    public uint HotkeyModifiers { get; set; } = (uint)HotkeyDefinition.Default.Modifiers;

    public uint HotkeyVirtualKey { get; set; } = HotkeyDefinition.Default.VirtualKey;

    public DisplayTargetMode DisplayTargetMode { get; set; } = DisplayTargetMode.AllDisplays;

    /// <summary>Device names (<c>\\.\DISPLAY1</c>) of the monitors to blank in <see cref="DisplayTargetMode.SelectedDisplays"/>.</summary>
    public List<string> SelectedDisplayIds { get; set; } = new();

    public bool DismissOnKeyPress { get; set; } = true;

    /// <summary>
    /// Off by default. Plenty of setups report mouse movement with nobody
    /// touching the mouse — a controller mapped to the pointer, a game that keeps
    /// hold of it, an optical sensor on a glossy desk — and on those machines the
    /// blackout would lift a second after every press. A key or a click is an
    /// unambiguous "I'm back"; drifting is not.
    /// </summary>
    public bool DismissOnMouseMove { get; set; }

    public bool DismissOnMouseClick { get; set; } = true;

    /// <summary>How far the mouse has to travel, in raw mouse units, before the blackout lifts.</summary>
    public int MouseMoveThresholdPixels { get; set; } = 50;

    /// <summary>
    /// Fades a dim one-line reminder in when the blackout appears. It disappears
    /// after a couple of seconds and leaves the screen at pure black.
    /// </summary>
    public bool ShowHintOnBlackout { get; set; } = true;

    /// <summary>
    /// Holds the displays awake while blacked out. Off by default: a black OLED
    /// already draws almost nothing, so letting Windows power the panels down
    /// normally is the better default — turn this on if your monitors reshuffle
    /// your windows when they wake up.
    /// </summary>
    public bool KeepDisplaysAwake { get; set; }

    public bool StartWithWindows { get; set; }

    public bool IdleBlackoutEnabled { get; set; }

    public int IdleBlackoutMinutes { get; set; } = 10;

    /// <summary>
    /// Holds the idle blackout back while a full-screen app owns the foreground —
    /// a film with no keyboard input is exactly when blanking is unwanted.
    /// </summary>
    public bool SkipIdleBlackoutWhenFullscreen { get; set; } = true;

    public HotkeyDefinition GetHotkey() => new((HotkeyModifiers)HotkeyModifiers, HotkeyVirtualKey);

    public void SetHotkey(HotkeyDefinition hotkey)
    {
        ArgumentNullException.ThrowIfNull(hotkey);
        HotkeyModifiers = (uint)hotkey.Modifiers;
        HotkeyVirtualKey = hotkey.VirtualKey;
    }

    public DismissTriggers GetDismissTriggers()
    {
        DismissTriggers triggers = DismissTriggers.None;
        if (DismissOnKeyPress)
            triggers |= DismissTriggers.KeyPress;
        if (DismissOnMouseMove)
            triggers |= DismissTriggers.MouseMove;
        if (DismissOnMouseClick)
            triggers |= DismissTriggers.MouseClick;

        return triggers;
    }

    /// <summary>
    /// Pulls hand-edited or older files back into range. Called after loading so
    /// the rest of the app can treat every value as sane.
    /// </summary>
    public void Normalize()
    {
        // Any migration between schema versions branches here, on the value as it
        // was read — the stamp belongs at the end of this method, not the start,
        // or the version a file was written with is gone before anything can look
        // at it.
        //
        // v1 → v2 turned SelectedDisplayIds from GDI slot names into monitor
        // device paths. That one needs the attached monitors to resolve, which is
        // not knowable here, so it runs in MainViewModel off LoadedSchemaVersion.
        LoadedSchemaVersion = SchemaVersion;

        // A file with a modifier as the hotkey key, or no key at all, would leave
        // the app with no way to toggle; fall back to the shipped default.
        HotkeyDefinition hotkey = GetHotkey();
        SetHotkey(hotkey.IsSet ? hotkey : HotkeyDefinition.Default);

        if (!Enum.IsDefined(DisplayTargetMode))
            DisplayTargetMode = DisplayTargetMode.AllDisplays;

        SelectedDisplayIds = SelectedDisplayIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // "Only the displays I tick" with nothing ticked resolves to every
        // display, so the mode is corrected to say so rather than leaving a
        // hand-edited file describing something the app will not do.
        if (DisplayTargetMode == DisplayTargetMode.SelectedDisplays && SelectedDisplayIds.Count == 0)
            DisplayTargetMode = DisplayTargetMode.AllDisplays;

        MouseMoveThresholdPixels = Math.Clamp(
            MouseMoveThresholdPixels,
            MinMouseMoveThresholdPixels,
            MaxMouseMoveThresholdPixels);

        IdleBlackoutMinutes = Math.Clamp(IdleBlackoutMinutes, MinIdleBlackoutMinutes, MaxIdleBlackoutMinutes);

        SchemaVersion = CurrentSchemaVersion;
    }
}
