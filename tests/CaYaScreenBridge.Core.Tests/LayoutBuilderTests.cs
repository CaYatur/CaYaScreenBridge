using CaYaScreenBridge.Core.Config;
using CaYaScreenBridge.Core.Geometry;
using CaYaScreenBridge.Core.Model;
using Xunit;

namespace CaYaScreenBridge.Core.Tests;

public class LayoutBuilderTests
{
    private static DisplaySnapshot Panel(
        string id,
        double x,
        double y,
        double width,
        double height,
        double dpi = 96,
        double edidWidthMm = 0,
        double edidHeightMm = 0,
        bool primary = false) => new()
        {
            DeviceId = id,
            StableId = id,
            DeviceName = id,
            FriendlyName = id,
            PixelBounds = new RectD(x, y, width, height),
            IsPrimary = primary,
            EffectiveDpiX = dpi,
            EffectiveDpiY = dpi,
            EdidWidthMm = edidWidthMm,
            EdidHeightMm = edidHeightMm,
        };

    [Fact]
    public void AdjacentPanelsAreLaidOutEdgeToEdgeInMillimetres()
    {
        var displays = new[]
        {
            Panel("A", 0, 0, 3840, 2160, 144, 597, 336, primary: true),
            Panel("B", 3840, 0, 1920, 1080, 96, 531, 299),
        };

        ZoneLayout layout = LayoutBuilder.Build(displays, null);

        DisplayZone a = layout.FindByStableId("A")!;
        DisplayZone b = layout.FindByStableId("B")!;

        Assert.Equal(597, a.PhysicalBounds.Width, 3);
        Assert.Equal(531, b.PhysicalBounds.Width, 3);

        // No gap and no overlap where the two panels touch.
        Assert.Equal(a.PhysicalBounds.Right, b.PhysicalBounds.Left, 3);
    }

    [Fact]
    public void PhysicalSizeIsDerivedFromDpiWhenEdidIsMissing()
    {
        var displays = new[] { Panel("A", 0, 0, 1920, 1080, 96, primary: true) };

        ZoneLayout layout = LayoutBuilder.Build(displays, null);
        DisplayZone a = layout.Zones[0];

        // 1920 px at 96 DPI is 20 inches, or 508 mm.
        Assert.Equal(508, a.PhysicalBounds.Width, 1);
    }

    [Fact]
    public void SavedOverridesWinOverTheReconstruction()
    {
        var displays = new[]
        {
            Panel("A", 0, 0, 1920, 1080, 96, 531, 299, primary: true),
            Panel("B", 1920, 0, 1920, 1080, 96, 531, 299),
        };

        // Both positions are pinned so the assertion is about the overrides alone. With only one of
        // them pinned, the other would be reconstructed against it and the whole layout normalised
        // back to the origin, which is correct behaviour but not what this test is about.
        var profile = new LayoutProfile { Id = "test" };

        DisplayOverride a = profile.GetOrCreate("A");
        a.PhysicalLeftMm = 0;
        a.PhysicalTopMm = 0;

        DisplayOverride b = profile.GetOrCreate("B");
        b.PhysicalLeftMm = 700;
        b.PhysicalTopMm = 40;
        b.PhysicalWidthMm = 600;
        b.PhysicalHeightMm = 340;

        ZoneLayout layout = LayoutBuilder.Build(displays, profile);
        DisplayZone zoneA = layout.FindByStableId("A")!;
        DisplayZone zoneB = layout.FindByStableId("B")!;

        // The hand entered size wins over EDID.
        Assert.Equal(600, zoneB.PhysicalBounds.Width, 3);
        Assert.Equal(340, zoneB.PhysicalBounds.Height, 3);

        // The EDID size is still used for the display that was not resized.
        Assert.Equal(531, zoneA.PhysicalBounds.Width, 3);

        // Both hand placed positions survive, including the deliberate 169 mm gap between them.
        Assert.Equal(0, zoneA.PhysicalBounds.Left, 3);
        Assert.Equal(0, zoneA.PhysicalBounds.Top, 3);
        Assert.Equal(700, zoneB.PhysicalBounds.Left, 3);
        Assert.Equal(40, zoneB.PhysicalBounds.Top, 3);
    }

    /// <summary>
    /// A display the user has not placed is reconstructed against one they have, so a partial
    /// calibration stays coherent instead of leaving the untouched panel at the origin.
    /// </summary>
    [Fact]
    public void UnplacedDisplaysAreReconstructedAgainstPlacedOnes()
    {
        var displays = new[]
        {
            Panel("A", 0, 0, 1920, 1080, 96, 531, 299, primary: true),
            Panel("B", 1920, 0, 1920, 1080, 96, 531, 299),
        };

        var profile = new LayoutProfile { Id = "test" };
        DisplayOverride b = profile.GetOrCreate("B");
        b.PhysicalLeftMm = 700;
        b.PhysicalTopMm = 40;

        ZoneLayout layout = LayoutBuilder.Build(displays, profile);
        DisplayZone zoneA = layout.FindByStableId("A")!;
        DisplayZone zoneB = layout.FindByStableId("B")!;

        // A is placed immediately to the left of B, and the layout is then normalised to the origin.
        Assert.Equal(zoneA.PhysicalBounds.Right, zoneB.PhysicalBounds.Left, 3);
        Assert.Equal(0, layout.PhysicalBounds.Left, 6);
        Assert.Equal(0, layout.PhysicalBounds.Top, 6);
    }

    [Fact]
    public void VerticalStacksArePlacedAboveAndBelow()
    {
        var displays = new[]
        {
            Panel("BOTTOM", 0, 1080, 1920, 1080, 96, 531, 299, primary: true),
            Panel("TOP", 0, 0, 1920, 1080, 96, 531, 299),
        };

        ZoneLayout layout = LayoutBuilder.Build(displays, null);

        DisplayZone bottom = layout.FindByStableId("BOTTOM")!;
        DisplayZone top = layout.FindByStableId("TOP")!;

        Assert.True(top.PhysicalBounds.Top < bottom.PhysicalBounds.Top);
        Assert.Equal(top.PhysicalBounds.Bottom, bottom.PhysicalBounds.Top, 3);
    }

    [Fact]
    public void LayoutIsNormalisedToTheOrigin()
    {
        var displays = new[]
        {
            Panel("A", -1920, -1080, 1920, 1080, 96, 531, 299),
            Panel("B", 0, 0, 1920, 1080, 96, 531, 299, primary: true),
        };

        ZoneLayout layout = LayoutBuilder.Build(displays, null);

        Assert.Equal(0, layout.PhysicalBounds.Left, 6);
        Assert.Equal(0, layout.PhysicalBounds.Top, 6);
    }

    [Fact]
    public void ConfigurationIdIsStableRegardlessOfEnumerationOrder()
    {
        var a = Panel("A", 0, 0, 1920, 1080, primary: true);
        var b = Panel("B", 1920, 0, 2560, 1440);

        string first = ZoneLayout.ComputeConfigurationId(new[] { a, b });
        string second = ZoneLayout.ComputeConfigurationId(new[] { b, a });

        Assert.Equal(first, second);
        Assert.NotEqual(first, ZoneLayout.ComputeConfigurationId(new[] { a }));
    }

    [Fact]
    public void UniformDensityIsDetected()
    {
        var uniform = new[]
        {
            Panel("A", 0, 0, 1920, 1080, 96, 531, 299, primary: true),
            Panel("B", 1920, 0, 1920, 1080, 96, 531, 299),
        };

        var mixed = new[]
        {
            Panel("A", 0, 0, 3840, 2160, 144, 597, 336, primary: true),
            Panel("B", 3840, 0, 1920, 1080, 96, 531, 299),
        };

        Assert.True(LayoutBuilder.Build(uniform, null).IsUniform);
        Assert.False(LayoutBuilder.Build(mixed, null).IsUniform);
    }

    [Fact]
    public void ZeroSizedOverrideDoesNotProduceInfiniteScale()
    {
        var displays = new[] { Panel("A", 0, 0, 1920, 1080, 96, 531, 299, primary: true) };

        var profile = new LayoutProfile { Id = "test" };
        DisplayOverride ovr = profile.GetOrCreate("A");
        ovr.PhysicalWidthMm = 0;
        ovr.PhysicalHeightMm = 0;

        ZoneLayout layout = LayoutBuilder.Build(displays, profile);

        Assert.True(double.IsFinite(layout.Zones[0].PixelsPerMm.X));
        Assert.True(double.IsFinite(layout.Zones[0].MmPerPixel.X));
    }
}
