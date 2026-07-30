using CaYaScreenBridge.Core.Geometry;

namespace CaYaScreenBridge.Core.Model;

/// <summary>
/// What the platform layer knows about one physical display at a point in time. Immutable, so the
/// hook thread can read a snapshot without locking while the UI thread builds the next one.
/// </summary>
public sealed class DisplaySnapshot
{
    public required string DeviceId { get; init; }

    /// <summary>Stable identity across reboots and port changes: EDID serial when available.</summary>
    public required string StableId { get; init; }

    public string DeviceName { get; init; } = string.Empty;

    public string FriendlyName { get; init; } = string.Empty;

    /// <summary>Monitor bounds in physical pixels on the virtual desktop (per-monitor v2 aware).</summary>
    public required RectD PixelBounds { get; init; }

    /// <summary>Working area (bounds minus taskbar), physical pixels.</summary>
    public RectD WorkArea { get; init; }

    public bool IsPrimary { get; init; }

    /// <summary>Effective DPI reported by <c>GetDpiForMonitor(MDT_EFFECTIVE_DPI)</c>.</summary>
    public double EffectiveDpiX { get; init; } = 96;

    public double EffectiveDpiY { get; init; } = 96;

    /// <summary>Raw panel DPI reported by <c>GetDpiForMonitor(MDT_RAW_DPI)</c>. Zero when unknown.</summary>
    public double RawDpiX { get; init; }

    public double RawDpiY { get; init; }

    /// <summary>Panel size in millimetres as read from EDID. Zero when EDID was unavailable.</summary>
    public double EdidWidthMm { get; init; }

    public double EdidHeightMm { get; init; }

    /// <summary>Windows scale factor, e.g. 1.5 for 150%.</summary>
    public double ScaleFactor => EffectiveDpiX / 96.0;

    public bool HasEdidSize => EdidWidthMm > 1 && EdidHeightMm > 1;

    public double DiagonalInches
    {
        get
        {
            if (!HasEdidSize)
            {
                return 0;
            }

            double diagonalMm = Math.Sqrt((EdidWidthMm * EdidWidthMm) + (EdidHeightMm * EdidHeightMm));
            return diagonalMm / 25.4;
        }
    }

    public override string ToString() =>
        $"{FriendlyName} ({DeviceName}) {PixelBounds} @{EffectiveDpiX}dpi";
}
