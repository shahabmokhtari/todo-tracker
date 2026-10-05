using System.Runtime.InteropServices;

namespace TodoTracker.Windows;

/// <summary>
/// Maps GDI display names (<c>\\.\DISPLAYn</c>, renumbered whenever monitors reconnect) to each monitor's stable
/// device path and EDID friendly name, via the Connecting and Configuring Displays (CCD) API.
/// </summary>
internal static unsafe partial class DisplayConfig
{
    private const uint QdcOnlyActivePaths = 0x2;
    private const int GetSourceName = 1;
    private const int GetTargetName = 2;
    private const int ErrorInsufficientBuffer = 122;

    /// <param name="ClonePaths">Device paths of other monitors showing the same desktop (clone mode).</param>
    public sealed record Names(string DevicePath, string FriendlyName, IReadOnlyList<string> ClonePaths);

    /// <summary>GDI device name → stable names. Empty if the CCD API is unavailable (callers fall back to GDI names).</summary>
    public static IReadOnlyDictionary<string, Names> Query()
    {
        var result = new Dictionary<string, Names>(StringComparer.OrdinalIgnoreCase);
        try
        {
            PathInfo[] paths;
            uint pathCount;
            int status;
            var attempts = 0;
            do
            {
                // Retry: the topology can change between sizing and querying (e.g. a monitor being plugged in).
                if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out pathCount, out var modeCount) != 0)
                {
                    return result;
                }

                paths = new PathInfo[pathCount];
                var modes = new ModeInfo[modeCount];
                fixed (PathInfo* p = paths)
                fixed (ModeInfo* m = modes)
                {
                    status = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, p, ref modeCount, m, IntPtr.Zero);
                }
            }
            while (status == ErrorInsufficientBuffer && ++attempts < 3);

            if (status != 0)
            {
                return result;
            }

            for (var i = 0; i < pathCount; i++)
            {
                var source = new SourceDeviceName { Header = new Header { Type = GetSourceName, Size = (uint)sizeof(SourceDeviceName), AdapterId = paths[i].SourceAdapterId, Id = paths[i].SourceId } };
                var target = new TargetDeviceName { Header = new Header { Type = GetTargetName, Size = (uint)sizeof(TargetDeviceName), AdapterId = paths[i].TargetAdapterId, Id = paths[i].TargetId } };
                if (DisplayConfigGetDeviceInfo(&source.Header) != 0 || DisplayConfigGetDeviceInfo(&target.Header) != 0)
                {
                    continue;
                }

                var gdiName = new string(source.ViewGdiDeviceName, 0, 32).TrimEnd('\0');
                var devicePath = new string(target.MonitorDevicePath, 0, 128).TrimEnd('\0');
                var friendly = new string(target.MonitorFriendlyDeviceName, 0, 64).TrimEnd('\0');
                if (gdiName.Length > 0 && devicePath.Length > 0)
                {
                    // Cloned displays share a source: the first target names it, the others become aliases.
                    result[gdiName] = result.TryGetValue(gdiName, out var existing)
                        ? existing with { ClonePaths = [.. existing.ClonePaths, devicePath] }
                        : new Names(devicePath, friendly, []);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Pre-Windows 7 or a stripped-down host: GDI names only.
        }

        return result;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [LibraryImport("user32.dll")]
    private static partial int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, PathInfo* pathArray, ref uint numModeInfoArrayElements, ModeInfo* modeInfoArray, IntPtr currentTopologyId);

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigGetDeviceInfo(Header* requestPacket);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    /// <summary>DISPLAYCONFIG_PATH_INFO (72 bytes): source info (20), target info (48), flags (4).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo
    {
        public Luid SourceAdapterId;
        public uint SourceId;
        public uint SourceModeInfoIdx;
        public uint SourceStatusFlags;
        public Luid TargetAdapterId;
        public uint TargetId;
        public uint TargetModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public uint RefreshNumerator;
        public uint RefreshDenominator;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint TargetStatusFlags;
        public uint Flags;
    }

    /// <summary>DISPLAYCONFIG_MODE_INFO (64 bytes); only its size matters here.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct ModeInfo
    {
        public uint InfoType;
    }

    /// <summary>DISPLAYCONFIG_DEVICE_INFO_HEADER (20 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public int Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    /// <summary>DISPLAYCONFIG_SOURCE_DEVICE_NAME (84 bytes).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public Header Header;
        public fixed char ViewGdiDeviceName[32];
    }

    /// <summary>DISPLAYCONFIG_TARGET_DEVICE_NAME (420 bytes).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetDeviceName
    {
        public Header Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        public fixed char MonitorFriendlyDeviceName[64];
        public fixed char MonitorDevicePath[128];
    }
}
