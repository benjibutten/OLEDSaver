using System.Runtime.InteropServices;

namespace OLEDSaver.Helpers;

/// <summary>Keyboard event lifted out of a WM_INPUT packet.</summary>
public readonly record struct RawKeyboardEvent(uint VirtualKey, bool IsKeyDown);

/// <summary>
/// Mouse event lifted out of a WM_INPUT packet. Movement is a relative delta for
/// ordinary mice; tablets and remote-desktop sessions report absolute positions
/// instead, which the consumer has to difference itself.
/// </summary>
public readonly record struct RawMouseEvent(int X, int Y, bool IsAbsolute, bool ButtonPressed, bool WheelMoved)
{
    public bool Moved => X != 0 || Y != 0 || IsAbsolute;
}

/// <summary>
/// Raw Input (WM_INPUT) plumbing. Two things need input the app cannot get from
/// WPF events:
///
/// <list type="bullet">
/// <item>The hotkey fallback: some games register raw input with RIDEV_NOHOTKEYS,
/// which suppresses WM_HOTKEY delivery while they are focused. Raw input comes
/// straight from the keyboard driver stack and cannot be blocked that way.</item>
/// <item>Dismissing the blackout: the overlay usually has focus, but a toast or
/// an installer can steal it, and then key presses never reach the overlay.
/// A registered input sink still sees them.</item>
/// </list>
///
/// The keyboard sink runs for the whole session (the hotkey needs it). The mouse
/// sink is only registered while the blackout is up, because an idle tray app has
/// no reason to receive a message for every mouse movement.
/// </summary>
public static class RawInputInterop
{
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDEV_REMOVE = 0x00000001;
    private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    private const ushort HID_USAGE_GENERIC_MOUSE = 0x02;
    private const ushort HID_USAGE_GENERIC_KEYBOARD = 0x06;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIM_TYPEMOUSE = 0;
    private const uint RIM_TYPEKEYBOARD = 1;
    private const ushort RI_KEY_BREAK = 0x0001;
    private const ushort RI_KEY_E0 = 0x0002;

    private const ushort MOUSE_MOVE_ABSOLUTE = 0x0001;

    // Any button going down counts as "the user is back"; which button it was
    // does not matter.
    private const ushort RI_MOUSE_BUTTON_DOWN_MASK =
        0x0001  // left
        | 0x0004  // right
        | 0x0010  // middle
        | 0x0040  // button 4
        | 0x0100; // button 5
    private const ushort RI_MOUSE_WHEEL = 0x0400;
    private const ushort RI_MOUSE_HWHEEL = 0x0800;

    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;
    private const ushort VK_LSHIFT = 0xA0;
    private const ushort VK_RSHIFT = 0xA1;
    private const ushort VK_LCONTROL = 0xA2;
    private const ushort VK_RCONTROL = 0xA3;
    private const ushort VK_LMENU = 0xA4;
    private const ushort VK_RMENU = 0xA5;

    // Scan code of the right shift key; raw input reports both shift keys as the
    // generic VK_SHIFT and only the make code tells them apart.
    private const ushort SC_RSHIFT = 0x36;

    // Keyboard driver escape value carrying no key information.
    private const ushort VK_NONE = 0xFF;

    private static readonly int[] ModifierVirtualKeys =
    {
        VK_LSHIFT, VK_RSHIFT, VK_LCONTROL, VK_RCONTROL, VK_LMENU, VK_RMENU, VK_LWIN, VK_RWIN
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWMOUSE
    {
        public ushort usFlags;
        // The native struct has a ULONG union here; the two-byte hole in front of
        // it is what the alignment of that ULONG produces.
        private readonly ushort _alignmentPadding;
        public ushort usButtonFlags;
        public ushort usButtonData;
        public uint ulRawButtons;
        public int lLastX;
        public int lLastY;
        public uint ulExtraInformation;
    }

    /// <summary>
    /// The native RAWINPUT payload union. Reading both device kinds through one
    /// struct means a single buffer size is always large enough, whichever kind
    /// of packet arrives.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct RAWINPUTDATA
    {
        [FieldOffset(0)]
        public RAWMOUSE mouse;

        [FieldOffset(0)]
        public RAWKEYBOARD keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUT
    {
        public RAWINPUTHEADER header;
        public RAWINPUTDATA data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [In] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(
        IntPtr hRawInput, uint uiCommand, out RAWINPUT pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>
    /// Registers the window to receive WM_INPUT for all keyboard input, including
    /// while another window (such as a game) has focus.
    /// </summary>
    public static bool RegisterKeyboardSink(IntPtr hwnd) =>
        RegisterSink(hwnd, HID_USAGE_GENERIC_KEYBOARD, "keyboard");

    public static bool RegisterMouseSink(IntPtr hwnd) =>
        RegisterSink(hwnd, HID_USAGE_GENERIC_MOUSE, "mouse");

    public static void UnregisterKeyboardSink() => RemoveSink(HID_USAGE_GENERIC_KEYBOARD, "keyboard");

    public static void UnregisterMouseSink() => RemoveSink(HID_USAGE_GENERIC_MOUSE, "mouse");

    private static bool RegisterSink(IntPtr hwnd, ushort usage, string description)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_GENERIC,
                usUsage = usage,
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = hwnd
            }
        };

        bool registered = RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        if (!registered)
        {
            AppDiagnostics.Warning(
                $"Failed to register the raw-input {description} sink (lastError={Marshal.GetLastWin32Error()}).");
        }

        return registered;
    }

    private static void RemoveSink(ushort usage, string description)
    {
        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_GENERIC,
                usUsage = usage,
                dwFlags = RIDEV_REMOVE,
                // RIDEV_REMOVE requires a null target window.
                hwndTarget = IntPtr.Zero
            }
        };

        // A sink that would not come off leaves the app receiving a message for
        // every keystroke on the system; logged for the same reason registration
        // failures are, since neither is visible from the tray.
        if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            AppDiagnostics.Warning(
                $"Failed to remove the raw-input {description} sink (lastError={Marshal.GetLastWin32Error()}).");
        }
    }

    /// <summary>
    /// Extracts the keyboard event from a WM_INPUT lParam. Returns false for
    /// mouse packets and for driver escape events that carry no key. Generic
    /// modifier codes are normalized to their left/right variants so each
    /// physical modifier key can be tracked separately.
    /// </summary>
    public static bool TryGetKeyboardEvent(IntPtr lParam, out RawKeyboardEvent keyboardEvent)
    {
        keyboardEvent = default;

        if (!TryGetRawInput(lParam, out RAWINPUT data) || data.header.dwType != RIM_TYPEKEYBOARD)
            return false;

        if (data.data.keyboard.VKey == VK_NONE)
            return false;

        keyboardEvent = new RawKeyboardEvent(
            NormalizeVirtualKey(data.data.keyboard.VKey, data.data.keyboard.MakeCode, data.data.keyboard.Flags),
            (data.data.keyboard.Flags & RI_KEY_BREAK) == 0);
        return true;
    }

    /// <summary>Extracts the mouse event from a WM_INPUT lParam. Returns false for keyboard packets.</summary>
    public static bool TryGetMouseEvent(IntPtr lParam, out RawMouseEvent mouseEvent)
    {
        mouseEvent = default;

        if (!TryGetRawInput(lParam, out RAWINPUT data) || data.header.dwType != RIM_TYPEMOUSE)
            return false;

        RAWMOUSE mouse = data.data.mouse;
        mouseEvent = new RawMouseEvent(
            mouse.lLastX,
            mouse.lLastY,
            (mouse.usFlags & MOUSE_MOVE_ABSOLUTE) != 0,
            (mouse.usButtonFlags & RI_MOUSE_BUTTON_DOWN_MASK) != 0,
            (mouse.usButtonFlags & (RI_MOUSE_WHEEL | RI_MOUSE_HWHEEL)) != 0);
        return true;
    }

    private static bool TryGetRawInput(IntPtr lParam, out RAWINPUT data)
    {
        uint size = (uint)Marshal.SizeOf<RAWINPUT>();
        uint copied = GetRawInputData(lParam, RID_INPUT, out data, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
        return copied != unchecked((uint)-1);
    }

    private static uint NormalizeVirtualKey(ushort vKey, ushort makeCode, ushort flags)
    {
        bool isExtended = (flags & RI_KEY_E0) != 0;
        return vKey switch
        {
            VK_SHIFT => makeCode == SC_RSHIFT ? VK_RSHIFT : VK_LSHIFT,
            VK_CONTROL => isExtended ? VK_RCONTROL : VK_LCONTROL,
            VK_MENU => isExtended ? VK_RMENU : VK_LMENU,
            _ => vKey
        };
    }

    /// <summary>
    /// Virtual keys of the modifier keys physically held right now. Used only to
    /// seed tracked state when a matcher starts; live matching follows the
    /// raw-input stream so queued events keep the modifiers they were pressed with.
    /// </summary>
    public static IReadOnlyList<uint> GetPressedModifierVirtualKeys()
    {
        var pressed = new List<uint>();
        foreach (int vk in ModifierVirtualKeys)
        {
            if (IsKeyDown(vk))
                pressed.Add((uint)vk);
        }

        return pressed;
    }

    /// <summary>Whether the given key is physically held right now.</summary>
    public static bool IsKeyPressed(uint vk) => vk != 0 && IsKeyDown((int)vk);

    /// <summary>Lowest keyboard virtual key. Below this are the mouse buttons, which are not keys.</summary>
    private const int FirstKeyboardVirtualKey = 0x08;

    /// <summary>
    /// Every keyboard virtual key physically held right now. Used when the
    /// blackout appears: those keys are still on their way up (the hotkey
    /// combination itself, or a game's movement keys), and their auto-repeat must
    /// not be mistaken for the user asking for the screen back.
    ///
    /// GetAsyncKeyState rather than GetKeyboardState, because the latter reports
    /// the state synchronized with the calling thread's message queue and lags
    /// behind for a background app like this one.
    /// </summary>
    public static IReadOnlyList<uint> GetPressedVirtualKeys()
    {
        var pressed = new List<uint>();
        for (int vk = FirstKeyboardVirtualKey; vk <= 0xFE; vk++)
        {
            if (IsKeyDown(vk))
                pressed.Add((uint)vk);
        }

        return pressed;
    }

    private static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
}
