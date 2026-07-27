using System.Runtime.InteropServices;
using OLEDSaver.Models;

using Screen = System.Windows.Forms.Screen;

namespace OLEDSaver.Services;

/// <summary>
/// One monitor. <see cref="Bounds"/> is in physical screen pixels, which is what
/// the blackout windows are positioned with.
/// </summary>
public sealed record DisplayInfo(string Id, string Name, int X, int Y, int Width, int Height, bool IsPrimary)
{
    /// <summary>What the settings list shows, e.g. "LG OLED42C4 — 3840 × 2160 (primary)".</summary>
    public string Label
    {
        get
        {
            string primary = IsPrimary ? " (primary)" : string.Empty;
            return $"{Name} — {Width} × {Height}{primary}";
        }
    }
}

/// <summary>Enumerates monitors and works out which ones a blackout should cover.</summary>
public static class DisplayService
{
    public static IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var displays = new List<DisplayInfo>();
        int index = 0;

        foreach (Screen screen in Screen.AllScreens)
        {
            index++;

            displays.Add(new DisplayInfo(
                screen.DeviceName,
                ResolveFriendlyName(screen.DeviceName, index),
                // Bounds, not WorkingArea: the blackout has to cover the taskbar too.
                screen.Bounds.X,
                screen.Bounds.Y,
                screen.Bounds.Width,
                screen.Bounds.Height,
                screen.Primary));
        }

        return displays;
    }

    /// <summary>
    /// The monitors to blank. Pure so the fallback behaviour is testable:
    /// a selection that matches nothing (the OLED was unplugged, or it is off)
    /// falls back to every monitor rather than blanking nothing at all, because a
    /// hotkey that appears to do nothing is worse than blanking too much.
    /// </summary>
    public static IReadOnlyList<DisplayInfo> ResolveTargets(
        IReadOnlyList<DisplayInfo> displays,
        DisplayTargetMode mode,
        IReadOnlyCollection<string> selectedIds)
    {
        if (displays.Count == 0)
            return displays;

        switch (mode)
        {
            case DisplayTargetMode.PrimaryOnly:
                DisplayInfo primary = displays.FirstOrDefault(display => display.IsPrimary) ?? displays[0];
                return new[] { primary };

            case DisplayTargetMode.SelectedDisplays:
                var selected = displays
                    .Where(display => selectedIds.Contains(display.Id, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                return selected.Count > 0 ? selected : displays;

            default:
                return displays;
        }
    }

    /// <summary>
    /// The monitor's own name where the driver reports one. Falls back to
    /// "Display N", since a lot of monitors only ever report "Generic PnP Monitor".
    /// </summary>
    private static string ResolveFriendlyName(string adapterDeviceName, int index)
    {
        string fallback = $"Display {index}";

        try
        {
            var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(adapterDeviceName, 0, ref monitor, 0))
                return fallback;

            string name = monitor.DeviceString.Trim();
            if (string.IsNullOrEmpty(name) || name.Equals("Generic PnP Monitor", StringComparison.OrdinalIgnoreCase))
                return fallback;

            return $"{fallback}: {name}";
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public uint StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(
        string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);
}
