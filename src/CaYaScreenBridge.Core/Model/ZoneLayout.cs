using CaYaScreenBridge.Core.Geometry;

namespace CaYaScreenBridge.Core.Model;

/// <summary>
/// The complete set of display zones, plus the precomputed bounds the router needs. Immutable and
/// swapped atomically whenever the display configuration changes, so the hook callback never sees a
/// half updated layout.
/// </summary>
public sealed class ZoneLayout
{
    public static readonly ZoneLayout Empty = new(Array.Empty<DisplayZone>());

    public ZoneLayout(IReadOnlyList<DisplayZone> zones)
    {
        Zones = zones;

        RectD px = RectD.Empty;
        RectD mm = RectD.Empty;
        for (int i = 0; i < zones.Count; i++)
        {
            px = RectD.Union(px, zones[i].PixelBounds);
            mm = RectD.Union(mm, zones[i].PhysicalBounds);
        }

        PixelBounds = px;
        PhysicalBounds = mm;
        Primary = zones.FirstOrDefault(z => z.IsPrimary) ?? zones.FirstOrDefault();
    }

    public IReadOnlyList<DisplayZone> Zones { get; }

    public DisplayZone? Primary { get; }

    /// <summary>Bounding box of every zone in pixel space.</summary>
    public RectD PixelBounds { get; }

    /// <summary>Bounding box of every zone in millimetre space.</summary>
    public RectD PhysicalBounds { get; }

    public int Count => Zones.Count;

    /// <summary>True when every display shares the same pixel density, i.e. nothing to correct.</summary>
    public bool IsUniform
    {
        get
        {
            if (Zones.Count < 2)
            {
                return true;
            }

            double reference = Zones[0].PixelsPerMm.X;
            for (int i = 1; i < Zones.Count; i++)
            {
                if (Math.Abs(Zones[i].PixelsPerMm.X - reference) > 0.01)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public DisplayZone? FindByPixel(Vec2 pixel)
    {
        for (int i = 0; i < Zones.Count; i++)
        {
            if (Zones[i].PixelBounds.Contains(pixel))
            {
                return Zones[i];
            }
        }

        return null;
    }

    public DisplayZone? FindByStableId(string stableId)
    {
        for (int i = 0; i < Zones.Count; i++)
        {
            if (string.Equals(Zones[i].StableId, stableId, StringComparison.OrdinalIgnoreCase))
            {
                return Zones[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Nearest zone in pixel space. The recovery path when the cursor is found somewhere that no
    /// longer maps to a display, e.g. right after a monitor was unplugged.
    /// </summary>
    public DisplayZone? NearestByPixel(Vec2 pixel)
    {
        DisplayZone? best = null;
        double bestDistance = double.MaxValue;

        for (int i = 0; i < Zones.Count; i++)
        {
            double distance = Zones[i].PixelBounds.DistanceSquaredTo(pixel);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = Zones[i];
            }
        }

        return best;
    }

    public DisplayZone? NearestByPhysical(Vec2 mm)
    {
        DisplayZone? best = null;
        double bestDistance = double.MaxValue;

        for (int i = 0; i < Zones.Count; i++)
        {
            double distance = Zones[i].PhysicalBounds.DistanceSquaredTo(mm);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = Zones[i];
            }
        }

        return best;
    }

    /// <summary>
    /// Identity of the current display arrangement. Used to store a separate saved layout per
    /// docking situation, so plugging into a different desk restores that desk's calibration.
    /// </summary>
    public static string ComputeConfigurationId(IEnumerable<DisplaySnapshot> displays)
    {
        IOrderedEnumerable<string> parts = displays
            .Select(d => $"{d.StableId}|{(int)d.PixelBounds.Width}x{(int)d.PixelBounds.Height}")
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);

        string joined = string.Join(";", parts);
        if (joined.Length == 0)
        {
            return "empty";
        }

        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
