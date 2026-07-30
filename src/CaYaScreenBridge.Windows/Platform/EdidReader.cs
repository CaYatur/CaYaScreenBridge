using CaYaScreenBridge.Core.Model;
using Microsoft.Win32;

namespace CaYaScreenBridge.Windows.Platform;

/// <summary>
/// Finds the EDID block Windows caches in the device registry for each attached monitor. Parsing
/// itself lives in <see cref="EdidBlock"/> so it can be covered by tests without a display attached.
/// </summary>
public static class EdidReader
{
    private const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum\DISPLAY";

    /// <summary>
    /// Looks up the EDID for a monitor interface path as returned by
    /// <c>EnumDisplayDevices(..., EDD_GET_DEVICE_INTERFACE_NAME)</c>, for example
    /// <c>\\?\DISPLAY#GSM5B09#5&amp;1a2b3c4d&amp;0&amp;UID4353#{e6f07b5f-...}</c>.
    /// </summary>
    public static EdidInfo ReadForInterfacePath(string interfacePath)
    {
        if (!TryParseInterfacePath(interfacePath, out string hardwareId, out string instanceId))
        {
            return EdidInfo.Empty;
        }

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                $@"{EnumRoot}\{hardwareId}\{instanceId}\Device Parameters");

            if (key?.GetValue("EDID") is byte[] edid)
            {
                return EdidBlock.Parse(edid);
            }
        }
        catch (Exception)
        {
            // A locked or missing registry key just means we fall back to a DPI derived estimate.
        }

        return EdidInfo.Empty;
    }

    /// <summary>Splits <c>\\?\DISPLAY#HARDWAREID#INSTANCE#{guid}</c> into its registry path segments.</summary>
    public static bool TryParseInterfacePath(string interfacePath, out string hardwareId, out string instanceId)
    {
        hardwareId = string.Empty;
        instanceId = string.Empty;

        if (string.IsNullOrWhiteSpace(interfacePath))
        {
            return false;
        }

        string[] parts = interfacePath.Split('#');
        if (parts.Length < 3)
        {
            return false;
        }

        hardwareId = parts[1];
        instanceId = parts[2];
        return hardwareId.Length > 0 && instanceId.Length > 0;
    }
}
