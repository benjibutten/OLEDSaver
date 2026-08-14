using System.Runtime.InteropServices;

namespace OLEDSaver.Helpers;

/// <summary>
/// What Windows knows about one physical monitor: the device path that stays
/// with the panel, and the model name off its EDID.
/// </summary>
public sealed record MonitorIdentity(string DevicePath, string ModelName);

/// <summary>
/// Reads monitor identities through the Connecting and Configuring Displays
/// (CCD) API, keyed by the GDI device name (<c>\\.\DISPLAY1</c>) that
/// <see cref="System.Windows.Forms.Screen"/> reports.
///
/// Worth the interop for the two things EnumDisplayDevices cannot give:
///
/// the monitor's device path, which belongs to the physical panel and survives
/// sleep, a power cycle and a driver restart — the GDI name does not, Windows
/// hands those slots back out and a monitor that was <c>\\.\DISPLAY2</c> before
/// it slept can be <c>\\.\DISPLAY1</c> after;
///
/// and the model name, because most monitors report nothing but
/// "Generic PnP Monitor" through the older API, which leaves a two-monitor
/// settings list showing two identical rows told apart only by a number that
/// itself moves.
/// </summary>
public static class DisplayConfigInterop
{
    /// <summary>
    /// Monitor identities by GDI device name. Empty when the API is unavailable
    /// or fails; every caller has to cope with a monitor that is missing from it.
    /// </summary>
    public static IReadOnlyDictionary<string, MonitorIdentity> GetMonitorIdentities()
    {
        var identities = new Dictionary<string, MonitorIdentity>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!TryQueryActivePaths(out DISPLAYCONFIG_PATH_INFO[] paths))
                return identities;

            foreach (DISPLAYCONFIG_PATH_INFO path in paths)
            {
                string gdiName = GetSourceGdiName(path);

                // A cloned source drives several monitors from one GDI name, and
                // there is only one desktop rectangle to blank either way — the
                // first target is the one the blackout will land on.
                if (gdiName.Length == 0 || identities.ContainsKey(gdiName))
                    continue;

                if (TryGetTargetIdentity(path, out MonitorIdentity identity))
                    identities[gdiName] = identity;
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warning("Failed to read monitor identities through the display config API.", ex);
        }

        return identities;
    }

    private static bool TryQueryActivePaths(out DISPLAYCONFIG_PATH_INFO[] paths)
    {
        paths = Array.Empty<DISPLAYCONFIG_PATH_INFO>();

        // The layout can change between sizing the buffers and filling them —
        // a monitor waking up is exactly when this runs — and Windows answers
        // that with ERROR_INSUFFICIENT_BUFFER. Asking again picks up the new size.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != ERROR_SUCCESS)
                return false;

            if (pathCount == 0)
                return false;

            var candidates = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

            int result = QueryDisplayConfig(
                QDC_ONLY_ACTIVE_PATHS, ref pathCount, candidates, ref modeCount, modes, IntPtr.Zero);

            if (result == ERROR_SUCCESS)
            {
                // Windows can fill in fewer paths than it sized the buffer for.
                Array.Resize(ref candidates, (int)pathCount);
                paths = candidates;
                return true;
            }

            if (result != ERROR_INSUFFICIENT_BUFFER)
                return false;
        }

        return false;
    }

    private static string GetSourceGdiName(DISPLAYCONFIG_PATH_INFO path)
    {
        var request = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                adapterId = path.sourceInfo.adapterId,
                id = path.sourceInfo.id
            }
        };

        return DisplayConfigGetDeviceInfo(ref request) == ERROR_SUCCESS
            ? request.viewGdiDeviceName.Trim()
            : string.Empty;
    }

    private static bool TryGetTargetIdentity(DISPLAYCONFIG_PATH_INFO path, out MonitorIdentity identity)
    {
        identity = new MonitorIdentity(string.Empty, string.Empty);

        var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = path.targetInfo.adapterId,
                id = path.targetInfo.id
            }
        };

        if (DisplayConfigGetDeviceInfo(ref request) != ERROR_SUCCESS)
            return false;

        string devicePath = request.monitorDevicePath.Trim();

        // Without a path there is nothing stable to remember the monitor by, and
        // a name on its own would be no better than the slot number.
        if (devicePath.Length == 0)
            return false;

        identity = new MonitorIdentity(devicePath, request.monitorFriendlyDeviceName.Trim());
        return true;
    }

    private const int ERROR_SUCCESS = 0;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshRateNumerator;
        public uint refreshRateDenominator;
        public uint scanLineOrdering;

        [MarshalAs(UnmanagedType.Bool)]
        public bool targetAvailable;

        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    /// <summary>
    /// Opaque on purpose: nothing here reads a mode, the array only exists
    /// because QueryDisplayConfig insists on filling one. 64 bytes is the
    /// documented size of DISPLAYCONFIG_MODE_INFO.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);
}
