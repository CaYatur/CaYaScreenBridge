using CaYaScreenBridge.Core.Geometry;
using CaYaScreenBridge.Core.Model;

namespace CaYaScreenBridge.Core.Tests;

/// <summary>
/// Layout fixtures modelled on real desks, so the tests exercise the ratios that actually cause
/// trouble rather than convenient round numbers.
/// </summary>
internal static class TestLayouts
{
    /// <summary>
    /// The classic mismatch: a 27 inch 4K panel at 150% next to a 24 inch 1080p panel at 100%.
    /// The 4K panel has almost exactly twice the pixel density, which is what makes an uncorrected
    /// crossing land at half the expected height.
    /// </summary>
    public static ZoneLayout HighDpiLeftOfLowDpi()
    {
        // 27" 16:9 -> 597 x 336 mm, 3840 x 2160 px.
        var left = new DisplayZone(
            "PANEL-4K",
            "27\" 4K",
            new RectD(0, 0, 3840, 2160),
            new RectD(0, 0, 597, 336),
            144,
            isPrimary: true);

        // 24" 16:9 -> 531 x 299 mm, 1920 x 1080 px, placed to the right and vertically centred.
        var right = new DisplayZone(
            "PANEL-FHD",
            "24\" FHD",
            new RectD(3840, 540, 1920, 1080),
            new RectD(597, 18.5, 531, 299),
            96,
            isPrimary: false);

        return new ZoneLayout(new[] { left, right });
    }

    /// <summary>Two identical displays side by side: every crossing must be a pure pass through.</summary>
    public static ZoneLayout IdenticalPair()
    {
        var left = new DisplayZone(
            "A",
            "A",
            new RectD(0, 0, 1920, 1080),
            new RectD(0, 0, 531, 299),
            96,
            isPrimary: true);

        var right = new DisplayZone(
            "B",
            "B",
            new RectD(1920, 0, 1920, 1080),
            new RectD(531, 0, 531, 299),
            96,
            isPrimary: false);

        return new ZoneLayout(new[] { left, right });
    }

    /// <summary>
    /// An L shaped arrangement: a wide display with a portrait one above and to the right, leaving
    /// physical dead space that a naive solver would strand the cursor in.
    /// </summary>
    public static ZoneLayout LShaped()
    {
        var main = new DisplayZone(
            "MAIN",
            "Main",
            new RectD(0, 400, 2560, 1440),
            new RectD(0, 300, 597, 336),
            96,
            isPrimary: true);

        var portrait = new DisplayZone(
            "PORTRAIT",
            "Portrait",
            new RectD(2560, 0, 1080, 1920),
            new RectD(700, 0, 299, 531),
            96,
            isPrimary: false);

        return new ZoneLayout(new[] { main, portrait });
    }
}
