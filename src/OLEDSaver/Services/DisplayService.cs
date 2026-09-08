using System.Runtime.InteropServices;
using OLEDSaver.Helpers;
using OLEDSaver.Models;

using Screen = System.Windows.Forms.Screen;

namespace OLEDSaver.Services;

/// <summary>
/// One monitor. The bounds are in physical screen pixels, which is what the
/// blackout windows are positioned with.
/// </summary>
/// <param name="Id">
/// The GDI device name, <c>\\.\DISPLAY1</c>. A slot in the current session's
/// layout, not a monitor: Windows hands these out again, so it is only good for
/// matching against what <see cref="Screen"/> reports right now.
/// </param>
/// <param name="HardwareId">
/// The monitor's own device path, which stays with the physical panel. Empty
/// when Windows would not report one.
/// </param>
public sealed record DisplayInfo(
    string Id,
    string HardwareId,
    string Name,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsPrimary)
{
    /// <summary>
    /// What a saved selection is written and matched against: the hardware id
    /// where there is one, and the GDI name only as a last resort, on a machine
    /// that reports no path for the monitor at all.
    /// </summary>
    public string StableId => HardwareId.Length > 0 ? HardwareId : Id;

    /// <summary>What the settings list shows, e.g. "Display 2: LG OLED42C4 — 3840 × 2160 (primary)".</summary>
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
    // Identities are read fresh on every enumeration, deliberately.
    //
    // They can only be looked up by the GDI slot name, and Windows reassigns
    // those slots: the monitor that was \\.\DISPLAY2 before it slept,
    // was switched off at the panel or had its driver restart can come back
    // as \\.\DISPLAY1, with something else in the slot it left. A slot →
    // panel mapping held from one blackout to the next therefore outlives the
    // layout it described, and hands one monitor’s device path to another. The
    // saved selection then matches the wrong monitor and the blackout covers a
    // screen the user never ticked, while the settings window goes on showing
    // the right one ticked — the single worst way this app can fail.
    //
    // There is nothing to buy by caching it either: the whole query measures
    // ~0.04 ms, against a blackout that costs 6-11 ms end to end.

    public static IReadOnlyList<DisplayInfo> GetDisplays()
    {
        IReadOnlyDictionary<string, MonitorIdentity> identities = DisplayConfigInterop.GetMonitorIdentities();

        var displays = new List<(int Number, DisplayInfo Display)>();
        int index = 0;

        foreach (Screen screen in Screen.AllScreens)
        {
            index++;

            MonitorIdentity identity = ResolveIdentity(identities, screen.DeviceName);
            int number = ResolveDisplayNumber(screen.DeviceName, index);

            displays.Add((number, new DisplayInfo(
                screen.DeviceName,
                identity.DevicePath,
                BuildName(number, identity.ModelName),
                // Bounds, not WorkingArea: the blackout has to cover the taskbar too.
                screen.Bounds.X,
                screen.Bounds.Y,
                screen.Bounds.Width,
                screen.Bounds.Height,
                screen.Primary)));
        }

        // Screen.AllScreens comes back in whatever order Windows enumerated the
        // adapters, which is not slot order: "Display 2" above "Display 1" reads
        // as a bug in the list even though both rows are labelled correctly.
        return displays
            .OrderBy(entry => entry.Number)
            .Select(entry => entry.Display)
            .ToList();
    }

    /// <summary>
    /// The device path and model name behind one GDI slot, as the layout stands
    /// at this moment.
    /// </summary>
    private static MonitorIdentity ResolveIdentity(
        IReadOnlyDictionary<string, MonitorIdentity> identities, string adapterDeviceName)
    {
        identities.TryGetValue(adapterDeviceName, out MonitorIdentity? identity);

        string hardwareId = identity?.DevicePath ?? string.Empty;
        string model = identity?.ModelName ?? string.Empty;

        // EnumDisplayDevices knows less, but it answers on machines and
        // remote sessions where the display config API reports nothing.
        if (hardwareId.Length == 0 || model.Length == 0)
        {
            MonitorIdentity fallback = ReadMonitorDevice(adapterDeviceName);

            if (hardwareId.Length == 0)
                hardwareId = fallback.DevicePath;

            if (model.Length == 0)
                model = fallback.ModelName;
        }

        return new MonitorIdentity(hardwareId, model);
    }

    /// <summary>
    /// The monitors to blank.
    ///
    /// A selection that matches nothing blanks nothing. The monitor a user ticked
    /// is regularly missing from this list — one asleep on DisplayPort leaves the
    /// topology altogether, and so does one switched off at the panel — and the
    /// only other monitors to fall back on are the ones they deliberately left
    /// unticked. Blanking those is the complaint, not the safety net: a hotkey
    /// that does nothing is an obvious, correctable failure, while a screen that
    /// goes black despite never being ticked reads as the app ignoring its own
    /// settings. <see cref="BlackoutController"/> puts the reason in the log.
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
                // Nothing ticked at all is a different case from a ticked monitor
                // that is missing: AppSettings.Normalize and MainViewModel move the
                // mode back to AllDisplays when the last box is unticked, and this
                // agrees with them rather than blanking nothing until they do.
                if (selectedIds.Count == 0)
                    return displays;

                return displays
                    .Where(display => selectedIds.Contains(display.StableId, StringComparer.OrdinalIgnoreCase))
                    .ToList();

            default:
                return displays;
        }
    }

    /// <summary>
    /// Rewrites a selection saved before schema 2, which named monitors by their
    /// GDI slot (<c>\\.\DISPLAY2</c>).
    ///
    /// Windows hands those slots back out — after a monitor sleeps, is switched
    /// off at the panel, or the driver restarts, the monitor that held
    /// <c>\\.\DISPLAY2</c> can come back as <c>\\.\DISPLAY1</c> and something
    /// else takes the slot. A tick saved against the slot then blanks whichever
    /// monitor happens to be sitting in it, which is the wrong screen.
    ///
    /// The slot is all an old file says, so the best that can be done is to read
    /// it once and pin it to the monitor occupying it now. A slot with nothing
    /// behind it — that monitor is switched off at this moment — cannot be tied
    /// to a panel at all, and is dropped rather than left to match some other
    /// monitor later.
    /// </summary>
    public static IReadOnlyList<string> MigrateLegacySelection(
        IReadOnlyList<DisplayInfo> displays,
        IEnumerable<string> selectedIds)
    {
        var migrated = new List<string>();

        foreach (string id in selectedIds)
        {
            if (!IsLegacyGdiDeviceName(id))
            {
                migrated.Add(id);
                continue;
            }

            DisplayInfo? match = displays.FirstOrDefault(
                display => string.Equals(display.Id, id, StringComparison.OrdinalIgnoreCase));

            if (match != null)
                migrated.Add(match.StableId);
        }

        return migrated.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Tells a slot name apart from a hardware id. Device paths start
    /// <c>\\?\DISPLAY#</c>, GDI names <c>\\.\DISPLAY</c>.
    /// </summary>
    private static bool IsLegacyGdiDeviceName(string id) =>
        id.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// "Display 2: XG27AQDMGR", or just "Display 2" when nothing reports a model.
    /// The number is Windows' own, so the list lines up with the arrangement in
    /// the Settings app.
    /// </summary>
    private static string BuildName(int number, string model)
    {
        string label = $"Display {number}";
        return model.Length > 0 ? $"{label}: {model}" : label;
    }

    /// <summary>
    /// The number out of <c>\\.\DISPLAY2</c>. Enumeration order will not do:
    /// <see cref="Screen.AllScreens"/> does not return the monitors in slot
    /// order, and a list that numbers them differently from the Settings app is
    /// worse than one that does not number them at all.
    /// </summary>
    private static int ResolveDisplayNumber(string adapterDeviceName, int index)
    {
        int start = adapterDeviceName.Length;
        while (start > 0 && char.IsAsciiDigit(adapterDeviceName[start - 1]))
            start--;

        return int.TryParse(adapterDeviceName.AsSpan(start), out int number) && number > 0
            ? number
            : index;
    }

    /// <summary>
    /// The monitor's device path and model name through EnumDisplayDevices, for
    /// the machines <see cref="DisplayConfigInterop"/> gets nothing out of. The
    /// interface flag is what makes the device path come back in DeviceID;
    /// without it the field holds a hardware id shared by identical monitors.
    /// </summary>
    private static MonitorIdentity ReadMonitorDevice(string adapterDeviceName)
    {
        try
        {
            var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(adapterDeviceName, 0, ref monitor, EDD_GET_DEVICE_INTERFACE_NAME))
                return new MonitorIdentity(string.Empty, string.Empty);

            string model = monitor.DeviceString.Trim();

            // A lot of monitors only ever report this, which tells the user
            // nothing and would make two rows in the list read identically.
            if (model.Equals("Generic PnP Monitor", StringComparison.OrdinalIgnoreCase))
                model = string.Empty;

            return new MonitorIdentity(monitor.DeviceID.Trim(), model);
        }
        catch (Exception)
        {
            return new MonitorIdentity(string.Empty, string.Empty);
        }
    }

    private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;

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
