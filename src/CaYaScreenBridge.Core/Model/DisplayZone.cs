using CaYaScreenBridge.Core.Geometry;

namespace CaYaScreenBridge.Core.Model;

/// <summary>
/// One display expressed simultaneously in the two coordinate systems the router cares about:
/// the pixel rectangle Windows actually uses, and the physical rectangle in millimetres that
/// describes where the panel really sits on the user's desk.
/// </summary>
public sealed class DisplayZone
{
    public DisplayZone(
        string stableId,
        string displayName,
        RectD pixelBounds,
        RectD physicalBounds,
        double effectiveDpi,
        bool isPrimary)
    {
        StableId = stableId;
        DisplayName = displayName;
        PixelBounds = pixelBounds;
        PhysicalBounds = physicalBounds;
        EffectiveDpi = effectiveDpi;
        IsPrimary = isPrimary;

        // Guard against a degenerate physical size (bad EDID, or a user typing 0 into the editor).
        // Falling back to the pixel extent keeps every transform finite instead of producing NaN
        // coordinates that would strand the cursor.
        double pxPerMmX = physicalBounds.Width > 0.01 ? pixelBounds.Width / physicalBounds.Width : 1.0;
        double pxPerMmY = physicalBounds.Height > 0.01 ? pixelBounds.Height / physicalBounds.Height : 1.0;
        PixelsPerMm = new Vec2(pxPerMmX, pxPerMmY);
        MmPerPixel = new Vec2(1.0 / pxPerMmX, 1.0 / pxPerMmY);
    }

    public string StableId { get; }

    public string DisplayName { get; }

    public RectD PixelBounds { get; }

    public RectD PhysicalBounds { get; }

    public double EffectiveDpi { get; }

    public bool IsPrimary { get; }

    public Vec2 PixelsPerMm { get; }

    public Vec2 MmPerPixel { get; }

    /// <summary>Physical pixel density along X, in dots per inch, derived from the physical size.</summary>
    public double PhysicalDpiX => PixelsPerMm.X * 25.4;

    public Vec2 PixelToMm(Vec2 pixel) =>
        PhysicalBounds.TopLeft + (pixel - PixelBounds.TopLeft).Unscale(PixelsPerMm);

    public Vec2 MmToPixel(Vec2 mm) =>
        PixelBounds.TopLeft + (mm - PhysicalBounds.TopLeft).Scale(PixelsPerMm);

    /// <summary>Converts a movement vector (not a position) from pixels into millimetres.</summary>
    public Vec2 PixelDeltaToMm(Vec2 delta) => delta.Unscale(PixelsPerMm);

    /// <summary>Converts a movement vector (not a position) from millimetres into pixels.</summary>
    public Vec2 MmDeltaToPixel(Vec2 delta) => delta.Scale(PixelsPerMm);

    public bool ContainsPixel(Vec2 pixel) => PixelBounds.Contains(pixel);

    public override string ToString() => $"{DisplayName} px={PixelBounds} mm={PhysicalBounds}";
}
