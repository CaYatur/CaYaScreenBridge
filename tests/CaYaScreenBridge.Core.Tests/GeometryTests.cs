using CaYaScreenBridge.Core.Geometry;
using CaYaScreenBridge.Core.Model;
using Xunit;

namespace CaYaScreenBridge.Core.Tests;

public class GeometryTests
{
    [Fact]
    public void ContainmentIsHalfOpenSoSharedEdgesBelongToOneRectangleOnly()
    {
        var left = new RectD(0, 0, 100, 100);
        var right = new RectD(100, 0, 100, 100);

        var onTheSeam = new Vec2(100, 50);

        Assert.False(left.Contains(onTheSeam));
        Assert.True(right.Contains(onTheSeam));
    }

    [Fact]
    public void ClampInsideKeepsThePointStrictlyWithin()
    {
        var rect = new RectD(10, 20, 100, 50);

        Vec2 clamped = rect.ClampInside(new Vec2(500, 500), margin: 1);

        Assert.True(rect.Contains(clamped));
        Assert.Equal(109, clamped.X, 6);
        Assert.Equal(69, clamped.Y, 6);
    }

    [Fact]
    public void DistanceIsZeroInsideAndGrowsOutside()
    {
        var rect = new RectD(0, 0, 100, 100);

        Assert.Equal(0, rect.DistanceSquaredTo(new Vec2(50, 50)), 6);
        Assert.Equal(100, rect.DistanceSquaredTo(new Vec2(110, 50)), 6);
    }

    [Theory]
    [InlineData(0, 0, 200, 0, true)]   // straight through
    [InlineData(0, 0, 40, 0, false)]   // stops short
    [InlineData(0, 200, 200, 0, false)] // passes below
    [InlineData(0, 0, 200, 200, true)] // diagonal clips the corner
    public void SegmentIntersectionMatchesExpectation(
        double originX,
        double originY,
        double deltaX,
        double deltaY,
        bool expected)
    {
        var rect = new RectD(50, 0, 100, 100);

        bool hit = RayCast.SegmentIntersectsRect(
            new Vec2(originX, originY),
            new Vec2(deltaX, deltaY),
            rect,
            out double entry,
            out double exit);

        bool overlaps = hit && exit >= 0 && entry <= 1;
        Assert.Equal(expected, overlaps);
    }

    [Fact]
    public void SegmentParallelToAnEdgeIsHandled()
    {
        var rect = new RectD(0, 0, 100, 100);

        Assert.True(RayCast.SegmentIntersectsRect(new Vec2(10, 50), new Vec2(50, 0), rect, out _, out _));
        Assert.False(RayCast.SegmentIntersectsRect(new Vec2(10, 500), new Vec2(50, 0), rect, out _, out _));
    }

    [Fact]
    public void PixelAndMillimetreTransformsRoundTrip()
    {
        var zone = new DisplayZone(
            "A",
            "A",
            new RectD(1920, 100, 3840, 2160),
            new RectD(500, 40, 597, 336),
            144,
            isPrimary: false);

        var pixel = new Vec2(2500, 900);
        Vec2 mm = zone.PixelToMm(pixel);
        Vec2 back = zone.MmToPixel(mm);

        Assert.Equal(pixel.X, back.X, 6);
        Assert.Equal(pixel.Y, back.Y, 6);
    }

    [Fact]
    public void ADegeneratePhysicalSizeDoesNotProduceInfiniteScale()
    {
        var zone = new DisplayZone(
            "A",
            "A",
            new RectD(0, 0, 1920, 1080),
            new RectD(0, 0, 0, 0),
            96,
            isPrimary: true);

        Assert.True(double.IsFinite(zone.PixelsPerMm.X));
        Assert.True(double.IsFinite(zone.MmPerPixel.Y));
        Assert.True(zone.PixelToMm(new Vec2(10, 10)).IsFinite);
    }

    [Fact]
    public void UnionCoversBothRectangles()
    {
        RectD union = RectD.Union(new RectD(0, 0, 10, 10), new RectD(-5, 20, 10, 10));

        Assert.Equal(-5, union.Left, 6);
        Assert.Equal(0, union.Top, 6);
        Assert.Equal(10, union.Right, 6);
        Assert.Equal(30, union.Bottom, 6);
    }
}
