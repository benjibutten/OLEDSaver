namespace OLEDSaver.Input;

/// <summary>What kinds of input take the blackout back down.</summary>
[Flags]
public enum DismissTriggers
{
    None = 0,
    KeyPress = 1,
    MouseMove = 2,
    MouseClick = 4,

    All = KeyPress | MouseMove | MouseClick
}

/// <summary>
/// Decides when input should take the blackout down again. Fed from the
/// raw-input stream, so it sees input even when something stole focus from the
/// overlay.
///
/// Two problems it exists to solve:
///
/// <list type="bullet">
/// <item>The keys that switched the blackout on are still held when it appears.
/// Auto-repeat would deliver more key-downs and dismiss it immediately, so keys
/// held at that moment are ignored until they are released, and there is a short
/// grace period on top for the release itself.</item>
/// <item>An optical mouse on a shiny desk reports small movements forever.
/// Displacement is therefore measured as a vector sum from the position the
/// blackout started at — jitter cancels itself out, while a deliberate nudge in
/// one direction crosses the threshold.</item>
/// </list>
///
/// Pure state machine so all of it is unit testable.
/// </summary>
public sealed class BlackoutDismissEvaluator
{
    /// <summary>Escape always works, whatever the configured triggers are.</summary>
    private const uint VkEscape = 0x1B;

    public static readonly TimeSpan DefaultArmDelay = TimeSpan.FromMilliseconds(450);

    private readonly HashSet<uint> _keysToIgnoreUntilRelease = new();

    private DismissTriggers _triggers = DismissTriggers.All;
    private int _mouseMoveThreshold = 50;
    private TimeSpan _armDelay = DefaultArmDelay;

    private bool _isArmed;
    private DateTime _armedAtUtc;
    private long _movedX;
    private long _movedY;
    private bool _hasAbsoluteOrigin;
    private int _absoluteOriginX;
    private int _absoluteOriginY;

    public void Configure(DismissTriggers triggers, int mouseMoveThreshold, TimeSpan? armDelay = null)
    {
        _triggers = triggers;
        _mouseMoveThreshold = Math.Max(1, mouseMoveThreshold);
        _armDelay = armDelay ?? DefaultArmDelay;
    }

    /// <summary>
    /// Starts watching for dismissal input.
    /// </summary>
    /// <param name="keysHeldAtStart">
    /// Virtual keys held right now — typically the hotkey combination the user is
    /// still pressing. Their auto-repeat is ignored until they are released.
    /// </param>
    public void Arm(DateTime nowUtc, IEnumerable<uint>? keysHeldAtStart = null)
    {
        _isArmed = true;
        _armedAtUtc = nowUtc;
        _movedX = 0;
        _movedY = 0;
        _hasAbsoluteOrigin = false;

        _keysToIgnoreUntilRelease.Clear();
        if (keysHeldAtStart != null)
        {
            foreach (uint vk in keysHeldAtStart)
                _keysToIgnoreUntilRelease.Add(vk);
        }
    }

    public void Disarm()
    {
        _isArmed = false;
        _keysToIgnoreUntilRelease.Clear();
    }

    /// <summary>True once the grace period has passed and input counts.</summary>
    public bool IsListening(DateTime nowUtc) => _isArmed && nowUtc - _armedAtUtc >= _armDelay;

    /// <summary>Returns true when this key event should dismiss the blackout.</summary>
    public bool ProcessKey(uint virtualKey, bool isKeyDown, DateTime nowUtc)
    {
        if (!_isArmed)
            return false;

        if (!isKeyDown)
        {
            // The release is what clears the ignore, so the *next* press of the
            // same key counts normally.
            _keysToIgnoreUntilRelease.Remove(virtualKey);
            return false;
        }

        // A modifier on its own is never someone saying "I'm back" — but it is the
        // first half of every hotkey. Without this, pressing the hotkey to switch
        // the blackout off would dismiss it on the Ctrl key and the hotkey would
        // then switch it straight back on.
        if (HotkeyDefinition.IsModifierKey(virtualKey))
            return false;

        if (_keysToIgnoreUntilRelease.Contains(virtualKey))
            return false;

        if (!IsListening(nowUtc))
            return false;

        if (virtualKey == VkEscape)
            return true;

        return _triggers.HasFlag(DismissTriggers.KeyPress);
    }

    /// <summary>
    /// Returns true when accumulated mouse movement should dismiss the blackout.
    /// Absolute reports (tablets, remote desktop) are differenced against the
    /// first position seen after arming.
    /// </summary>
    public bool ProcessMouseMove(int x, int y, bool isAbsolute, DateTime nowUtc)
    {
        if (!_isArmed || !_triggers.HasFlag(DismissTriggers.MouseMove))
            return false;

        if (isAbsolute)
        {
            if (!_hasAbsoluteOrigin)
            {
                _hasAbsoluteOrigin = true;
                _absoluteOriginX = x;
                _absoluteOriginY = y;
                return false;
            }

            // Accumulated as a delta against the previous report rather than
            // assigned from the origin, so a desktop that mixes an ordinary mouse
            // with a tablet or an RDP session does not have the relative
            // displacement wiped by every absolute packet. For a stream that is
            // purely absolute the two are identical: the deltas sum to
            // (current - origin).
            _movedX += x - _absoluteOriginX;
            _movedY += y - _absoluteOriginY;
            _absoluteOriginX = x;
            _absoluteOriginY = y;
        }
        else
        {
            _movedX += x;
            _movedY += y;
        }

        if (!IsListening(nowUtc))
        {
            // Movement during the grace period is discarded rather than banked,
            // so bumping the mouse while pressing the hotkey does not dismiss
            // the blackout the instant it arms.
            _movedX = 0;
            _movedY = 0;
            _hasAbsoluteOrigin = false;
            return false;
        }

        long threshold = _mouseMoveThreshold;
        return (_movedX * _movedX) + (_movedY * _movedY) >= threshold * threshold;
    }

    /// <summary>Returns true when a mouse button (or the wheel) should dismiss the blackout.</summary>
    public bool ProcessMouseButton(DateTime nowUtc)
    {
        if (!_isArmed || !_triggers.HasFlag(DismissTriggers.MouseClick))
            return false;

        return IsListening(nowUtc);
    }
}
