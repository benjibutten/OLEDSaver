using System.Text;
using System.Windows.Input;

namespace OLEDSaver.Input;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,

    // The values are RegisterHotKey's fsModifiers, so the definition can be
    // handed to user32 without a translation step.
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008
}

/// <summary>
/// One global hotkey: a key plus any combination of Ctrl / Alt / Shift / Win.
///
/// A pure value type with no interop, so recording, describing and round-tripping
/// combinations are all unit testable. The blackout is a toggle rather than
/// something you hold, which is why this models a RegisterHotKey combination
/// (StreamDecky's <c>InputHotkey</c> models a held trigger instead, and cannot
/// express "Ctrl + Alt + B").
/// </summary>
public sealed class HotkeyDefinition : IEquatable<HotkeyDefinition>
{
    private const uint VkShift = 0x10;
    private const uint VkControl = 0x11;
    private const uint VkMenu = 0x12;
    private const uint VkLWin = 0x5B;
    private const uint VkRWin = 0x5C;

    public HotkeyDefinition(HotkeyModifiers modifiers, uint virtualKey)
    {
        // A modifier cannot be the key of the combination: Windows would never
        // deliver it, and "Ctrl + Ctrl" is not a thing a user can press.
        if (IsModifierKey(virtualKey) || virtualKey > 0xFF)
            virtualKey = 0;

        Modifiers = virtualKey == 0 ? HotkeyModifiers.None : modifiers;
        VirtualKey = virtualKey;
    }

    /// <summary>Nothing mapped. Never registers and never fires.</summary>
    public static HotkeyDefinition None { get; } = new(HotkeyModifiers.None, 0);

    /// <summary>Ships as the default: three-key, unlikely to collide, mnemonic for "black".</summary>
    public static HotkeyDefinition Default { get; } = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x42);

    public HotkeyModifiers Modifiers { get; }

    public uint VirtualKey { get; }

    public bool IsSet => VirtualKey != 0;

    /// <summary>
    /// True when the combination is a bare key with no modifier. Registering one
    /// still works, but it takes that key away from every other application for
    /// as long as the app runs, so the UI warns about it.
    /// </summary>
    public bool HasNoModifier => IsSet && Modifiers == HotkeyModifiers.None;

    public string DisplayText => Describe();

    public static HotkeyDefinition FromKey(Key key, ModifierKeys modifiers)
    {
        // Alt-qualified presses arrive as Key.System with the real key in SystemKey;
        // callers pass that through, so only the plain mapping is needed here.
        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        return new HotkeyDefinition(FromWpfModifiers(modifiers), virtualKey);
    }

    public static HotkeyModifiers FromWpfModifiers(ModifierKeys modifiers)
    {
        HotkeyModifiers result = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control))
            result |= HotkeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt))
            result |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift))
            result |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows))
            result |= HotkeyModifiers.Windows;

        return result;
    }

    public static bool IsModifierKey(uint virtualKey) =>
        virtualKey is VkShift or VkControl or VkMenu or VkLWin or VkRWin
            or >= 0xA0 and <= 0xA5;

    private string Describe()
    {
        if (!IsSet)
            return "Not set";

        var builder = new StringBuilder();
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
            builder.Append("Ctrl + ");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
            builder.Append("Alt + ");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
            builder.Append("Shift + ");
        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
            builder.Append("Win + ");

        return builder.Append(DescribeVirtualKey(VirtualKey)).ToString();
    }

    public static string DescribeVirtualKey(uint virtualKey)
    {
        if (virtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9')
            return ((char)virtualKey).ToString();

        if (virtualKey is >= 0x60 and <= 0x69)
            return $"Numpad {virtualKey - 0x60}";

        if (virtualKey is >= 0x70 and <= 0x87)
            return $"F{virtualKey - 0x70 + 1}";

        return virtualKey switch
        {
            0x03 => "Break",
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x13 => "Pause",
            0x14 => "Caps Lock",
            0x1B => "Escape",
            0x20 => "Space",
            0x21 => "Page Up",
            0x22 => "Page Down",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2C => "Print Screen",
            0x2D => "Insert",
            0x2E => "Delete",
            0x6A => "Numpad *",
            0x6B => "Numpad +",
            0x6D => "Numpad -",
            0x6E => "Numpad .",
            0x6F => "Numpad /",
            0x90 => "Num Lock",
            0x91 => "Scroll Lock",
            0xBA => ";",
            0xBB => "+",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            0xDF => "Oem8",
            0xE2 => "Oem102",
            _ => KeyInterop.KeyFromVirtualKey((int)virtualKey) switch
            {
                Key.None => $"Key {virtualKey}",
                var key => key.ToString()
            }
        };
    }

    public bool Equals(HotkeyDefinition? other) =>
        other != null && other.Modifiers == Modifiers && other.VirtualKey == VirtualKey;

    public override bool Equals(object? obj) => Equals(obj as HotkeyDefinition);

    public override int GetHashCode() => HashCode.Combine(Modifiers, VirtualKey);

    public override string ToString() => DisplayText;
}
