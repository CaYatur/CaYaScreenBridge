using System.Runtime.InteropServices;
using CaYaScreenBridge.Core.Diagnostics;
using CaYaScreenBridge.Core.Geometry;
using CaYaScreenBridge.Core.Model;
using CaYaScreenBridge.Windows.Native;

namespace CaYaScreenBridge.Windows.Platform;

/// <summary>
/// Builds <see cref="DisplaySnapshot"/> objects from the live Windows display configuration.
///
/// The process is declared per-monitor DPI aware v2, so every rectangle Windows hands back here is
/// already in real physical pixels of the virtual desktop. That matters: under system DPI awareness
/// the same call would return virtualised coordinates and every calculation downstream would be
/// silently wrong on any secondary display with a different scale factor.
/// </summary>
public sealed class MonitorEnumerator
{
    private readonly ILogSink _log;

    public MonitorEnumerator(ILogSink log) => _log = log;

    public IReadOnlyList<DisplaySnapshot> Enumerate()
    {
        var handles = new List<nint>();
        var results = new List<DisplaySnapshot>();

        bool Callback(nint monitor, nint hdc, ref RECT rect, nint data)
        {
            handles.Add(monitor);
            return true;
        }

        // The delegate must outlive the native call; keeping it in a local and referencing it after
        // EnumDisplayMonitors returns is enough to stop the GC from collecting it mid-enumeration.
        MonitorEnumProc callback = Callback;

        if (!Win32.EnumDisplayMonitors(0, 0, callback, 0))
        {
            _log.Error("Displays", $"EnumDisplayMonitors failed ({Marshal.GetLastWin32Error()}).");
        }

        GC.KeepAlive(callback);

        Dictionary<string, MonitorDeviceInfo> devices = ReadMonitorDevices();

        foreach (nint handle in handles)
        {
            DisplaySnapshot? snapshot = Describe(handle, devices);
            if (snapshot is not null)
            {
                results.Add(snapshot);
            }
        }

        if (results.Count == 0)
        {
            _log.Warn("Displays", "No displays were enumerated.");
        }

        return results;
    }

    private DisplaySnapshot? Describe(nint handle, Dictionary<string, MonitorDeviceInfo> devices)
    {
        var info = new MONITORINFOEXW
        {
            cbSize = Marshal.SizeOf<MONITORINFOEXW>(),
            szDevice = string.Empty,
        };

        if (!Win32.GetMonitorInfoW(handle, ref info))
        {
            _log.Warn("Displays", $"GetMonitorInfo failed for handle 0x{handle:X}.");
            return null;
        }

        string deviceName = info.szDevice.TrimEnd('\0');
        devices.TryGetValue(deviceName, out MonitorDeviceInfo? device);

        (double effectiveX, double effectiveY) = ReadDpi(handle, Win32.MDT_EFFECTIVE_DPI, 96);
        (double rawX, double rawY) = ReadDpi(handle, Win32.MDT_RAW_DPI, 0);

        EdidInfo edid = device?.Edid ?? EdidInfo.Empty;

        return new DisplaySnapshot
        {
            DeviceId = device?.DeviceId ?? deviceName,
            StableId = BuildStableId(deviceName, device, edid),
            DeviceName = deviceName,
            FriendlyName = BuildFriendlyName(deviceName, device, edid),
            PixelBounds = ToRect(info.rcMonitor),
            WorkArea = ToRect(info.rcWork),
            IsPrimary = (info.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0,
            EffectiveDpiX = effectiveX,
            EffectiveDpiY = effectiveY,
            RawDpiX = rawX,
            RawDpiY = rawY,
            EdidWidthMm = edid.WidthMm,
            EdidHeightMm = edid.HeightMm,
        };
    }

    private (double X, double Y) ReadDpi(nint handle, int type, double fallback)
    {
        try
        {
            if (Win32.GetDpiForMonitor(handle, type, out uint x, out uint y) == 0 && x > 0 && y > 0)
            {
                return (x, y);
            }
        }
        catch (DllNotFoundException)
        {
            // shcore.dll is present on every supported Windows version; guard anyway so a stripped
            // installation degrades to the fallback rather than crashing at startup.
        }
        catch (EntryPointNotFoundException)
        {
        }

        return (fallback, fallback);
    }

    /// <summary>
    /// A per display identity that survives reboots, cable swaps and resolution changes. The EDID
    /// serial is used when the panel provides one, because that is the only field that distinguishes
    /// two identical monitors from each other.
    /// </summary>
    private static string BuildStableId(string deviceName, MonitorDeviceInfo? device, EdidInfo edid)
    {
        if (edid.ManufacturerCode.Length > 0)
        {
            string suffix = edid.SerialNumber.Length > 0 ? edid.SerialNumber : device?.InstanceId ?? deviceName;
            return $"{edid.ManufacturerCode}{edid.ProductCode:X4}-{suffix}";
        }

        if (device is not null && device.DeviceId.Length > 0)
        {
            return device.DeviceId;
        }

        return deviceName;
    }

    private static string BuildFriendlyName(string deviceName, MonitorDeviceInfo? device, EdidInfo edid)
    {
        if (edid.ModelName.Length > 0)
        {
            return edid.ModelName;
        }

        if (!string.IsNullOrWhiteSpace(device?.Description))
        {
            return device.Description;
        }

        return deviceName;
    }

    private static RectD ToRect(RECT rect) =>
        RectD.FromEdges(rect.Left, rect.Top, rect.Right, rect.Bottom);

    /// <summary>
    /// Walks the display adapters and their attached monitors, pulling the device interface path
    /// that the EDID lookup needs.
    /// </summary>
    private Dictionary<string, MonitorDeviceInfo> ReadMonitorDevices()
    {
        var map = new Dictionary<string, MonitorDeviceInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                var adapter = new DISPLAY_DEVICEW
                {
                    cb = Marshal.SizeOf<DISPLAY_DEVICEW>(),
                    DeviceName = string.Empty,
                    DeviceString = string.Empty,
                    DeviceID = string.Empty,
                    DeviceKey = string.Empty,
                };

                if (!Win32.EnumDisplayDevicesW(null, adapterIndex, ref adapter, 0))
                {
                    break;
                }

                if ((adapter.StateFlags & Win32.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0)
                {
                    continue;
                }

                string adapterName = adapter.DeviceName.TrimEnd('\0');

                var monitor = new DISPLAY_DEVICEW
                {
                    cb = Marshal.SizeOf<DISPLAY_DEVICEW>(),
                    DeviceName = string.Empty,
                    DeviceString = string.Empty,
                    DeviceID = string.Empty,
                    DeviceKey = string.Empty,
                };

                if (!Win32.EnumDisplayDevicesW(adapterName, 0, ref monitor, Win32.EDD_GET_DEVICE_INTERFACE_NAME))
                {
                    continue;
                }

                string interfacePath = monitor.DeviceID.TrimEnd('\0');
                EdidInfo edid = EdidReader.ReadForInterfacePath(interfacePath);
                EdidReader.TryParseInterfacePath(interfacePath, out _, out string instanceId);

                map[adapterName] = new MonitorDeviceInfo(
                    interfacePath,
                    instanceId,
                    monitor.DeviceString.TrimEnd('\0'),
                    edid);
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Displays", $"Could not read monitor device information: {ex.Message}");
        }

        return map;
    }

    private sealed record MonitorDeviceInfo(string DeviceId, string InstanceId, string Description, EdidInfo Edid);
}
