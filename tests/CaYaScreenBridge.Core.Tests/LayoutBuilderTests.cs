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

        var profile = new LayoutProfile { Id = "test" };
        DisplayOverride ovr = profile.GetOrCreate("B");
        ovr.PhysicalLeftMm = 700;
        ovr.PhysicalTopMm = 40;
        ovr.PhysicalWidthMm = 600;
        ovr.PhysicalHeightMm = 340;

        ZoneLayout layout = LayoutBuilder.Build(displays, profile);
        DisplayZone b = layout.FindByStableId("B")!;

        Assert.Equal(600, b.PhysicalBounds.Width, 3);
        Assert.Equal(340, b.PhysicalBounds.Height, 3);

        // The layout is normalised so its top left corner sits at the origin; A is at y = 0 and B
        // keeps its 40 mm offset relative to it.
        Assert.Equal(40, b.PhysicalBounds.Top - layout.FindByStableId("A")!.PhysicalBounds.Top, 3);
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
