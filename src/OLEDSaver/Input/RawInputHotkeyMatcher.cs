namespace OLEDSaver.Input;

/// <summary>
/// Decides when a raw-input keyboard event should trigger the global hotkey.
/// This is the fallback path for games that suppress WM_HOTKEY delivery while
/// focused (titles registering raw input with RIDEV_NOHOTKEYS).
///
/// Modifier state is tracked from the raw-input stream itself rather than sampled
/// at processing time, so a queued event is matched against the modifiers that
/// were held when the key was actually pressed. The matcher also deduplicates
/// against WM_HOTKEY: both paths observe the same physical press, and
/// <see cref="TryHandleHotkeyMessage"/> plus the press/release cycle guarantee
/// exactly one trigger per press regardless of arrival order.
///
/// Pure state machine so it can be unit tested without user32.
/// </summary>
public sealed class RawInputHotkeyMatcher
{
    /// <summary>
    /// How long the "this press is already claimed" state survives without any
    /// further raw-input activity for the hotkey key before it is dropped.
    ///
    /// Both the claim and the auto-repeat guard are cleared by the key-up, and a
    /// key-up that never arrives would otherwise disable the hotkey for the rest
    /// of the session — a session lock, the UAC secure desktop, fast user
    /// switching and a game grabbing the device can all swallow one. Keyboard
    /// auto-repeat never leaves a gap anywhere near this long (the longest
    /// configurable repeat delay is one second), so a key-down after this much
    /// silence is a genuinely fresh press rather than a repeat.
    /// </summary>
    public static readonly TimeSpan StalePressTimeout = TimeSpan.FromSeconds(2);

    private readonly Func<DateTime> _clock;

    private HotkeyModifiers _modifiers;
    private uint _virtualKey;

    // Each physical modifier key is tracked separately so holding both Ctrl keys
    // and releasing one keeps the Ctrl group pressed.
    private readonly HashSet<uint> _heldModifierVks = new();
    private bool _hotkeyKeyHeld;
    private bool _currentPressHandled;
    private DateTime _pressActivityUtc;

    /// <param name="clock">Injectable so the staleness recovery is testable without waiting.</param>
    public RawInputHotkeyMatcher(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <param name="pressedModifierVks">
    /// Virtual keys of the modifier keys physically held right now. Seeds the
    /// tracked state so modifiers pressed before the matcher started listening
    /// are not missed.
    /// </param>
    /// <param name="hotkeyKeyAlreadyHeld">
    /// Whether the hotkey key itself is physically held right now. True while the
    /// user is still holding the key they just recorded; seeding it stops the
    /// queued key-down or the next auto-repeat from counting as a fresh press and
    /// blanking the screen the moment the combination is recorded.
    /// </param>
    public void Configure(
        HotkeyDefinition hotkey,
        IEnumerable<uint>? pressedModifierVks = null,
        bool hotkeyKeyAlreadyHeld = false)
    {
        ArgumentNullException.ThrowIfNull(hotkey);

        _modifiers = hotkey.Modifiers;
        _virtualKey = hotkey.VirtualKey;
        _hotkeyKeyHeld = hotkeyKeyAlreadyHeld;
        _currentPressHandled = false;

        // Seeded state gets a start time so it can go stale like any other; a
        // key-up missed straight after recording a combination must not disable
        // the hotkey that was just recorded.
        _pressActivityUtc = hotkeyKeyAlreadyHeld ? _clock() : default;

        _heldModifierVks.Clear();
        if (pressedModifierVks != null)
        {
            foreach (uint modifierVk in pressedModifierVks)
            {
                if (GetModifierFlag(modifierVk) != HotkeyModifiers.None)
                    _heldModifierVks.Add(modifierVk);
            }
        }
    }

    /// <summary>
    /// Feeds one keyboard event from the raw-input stream. Returns true when the
    /// configured hotkey was freshly pressed with exactly the configured modifiers
    /// held and no other path has handled this press yet. Keyboard auto-repeat
    /// delivers repeated key-downs without a key-up in between; those must not
    /// re-trigger (this mirrors MOD_NOREPEAT).
    /// </summary>
    public bool ProcessKeyEvent(uint vk, bool isKeyDown)
    {
        HotkeyModifiers flag = GetModifierFlag(vk);
        if (flag != HotkeyModifiers.None)
        {
            if (isKeyDown)
                _heldModifierVks.Add(vk);
            else
                _heldModifierVks.Remove(vk);
        }

        if (_virtualKey == 0 || vk != _virtualKey)
            return false;

        if (!isKeyDown)
        {
            _hotkeyKeyHeld = false;
            _currentPressHandled = false;
            _pressActivityUtc = default;
            return false;
        }

        // Every key-down for the hotkey key counts as activity, suppressed
        // auto-repeats included, so a continuous hold never looks stale.
        DateTime now = _clock();
        if (IsPressStateStale(now))
        {
            _hotkeyKeyHeld = false;
            _currentPressHandled = false;
        }

        _pressActivityUtc = now;

        if (_hotkeyKeyHeld)
            return false;

        // Held even on a non-matching press, so adding the missing modifier
        // mid-hold cannot fire on an auto-repeat like a fresh press would.
        _hotkeyKeyHeld = true;

        if (ComputePressedModifiers() != _modifiers)
            return false;

        if (_currentPressHandled)
            return false;

        _currentPressHandled = true;
        return true;
    }

    /// <summary>
    /// Claims the current physical press for a WM_HOTKEY message. Returns false
    /// when the raw-input path already triggered for this press; the claim is
    /// released when the raw-input stream reports the key-up, or when it goes
    /// stale because that key-up never arrived.
    ///
    /// MOD_NOREPEAT means Windows posts exactly one WM_HOTKEY per physical press,
    /// so a message arriving after <see cref="StalePressTimeout"/> of raw-input
    /// silence can only be a new press — never a repeat of the claimed one.
    /// </summary>
    public bool TryHandleHotkeyMessage()
    {
        DateTime now = _clock();

        if (_currentPressHandled && !IsPressStateStale(now))
            return false;

        _hotkeyKeyHeld = false;
        _currentPressHandled = true;
        _pressActivityUtc = now;
        return true;
    }

    private bool IsPressStateStale(DateTime nowUtc) =>
        _pressActivityUtc != default && nowUtc - _pressActivityUtc >= StalePressTimeout;

    private HotkeyModifiers ComputePressedModifiers()
    {
        HotkeyModifiers modifiers = HotkeyModifiers.None;
        foreach (uint vk in _heldModifierVks)
            modifiers |= GetModifierFlag(vk);

        return modifiers;
    }

    private static HotkeyModifiers GetModifierFlag(uint vk)
    {
        // RawInputInterop normalizes generic modifier codes to their left/right
        // variants, but the generic codes are accepted too in case a driver or
        // remapper delivers one that bypassed normalization.
        return vk switch
        {
            0x10 or 0xA0 or 0xA1 => HotkeyModifiers.Shift,
            0x11 or 0xA2 or 0xA3 => HotkeyModifiers.Control,
            0x12 or 0xA4 or 0xA5 => HotkeyModifiers.Alt,
            0x5B or 0x5C => HotkeyModifiers.Windows,
            _ => HotkeyModifiers.None
        };
    }
}
