using CaYaScreenBridge.Core.Algorithm;
using CaYaScreenBridge.Core.Geometry;
using CaYaScreenBridge.Core.Model;
using Xunit;

namespace CaYaScreenBridge.Core.Tests;

public class WindowDragSolverTests
{
    private static DragState CreateDrag(RectD windowRect, DisplayZone source, Vec2 grabPoint) => new()
    {
        WindowHandle = 1,
        ProcessName = "test",
        StartRect = windowRect,
        PhysicalSizeMm = WindowDragSolver.ComputePhysicalSize(windowRect, source),
        GrabFraction = WindowDragSolver.ComputeGrabFraction(windowRect, grabPoint),
        StartZoneId = source.StableId,
        CurrentZoneId = source.StableId,
        LastAppliedRect = windowRect,
    };

    /// <summary>
    /// The requirement in one test: a window dragged onto a panel with a different pixel density
    /// keeps the size it has on the desk, so it grows in pixels when it lands somewhere denser.
    /// </summary>
    [Fact]
    public void WindowKeepsItsPhysicalSizeAcrossADpiBoundary()
    {
        ZoneLayout layout = TestLayouts.HighDpiLeftOfLowDpi();
        DisplayZone dense = layout.FindByStableId("PANEL-4K")!;
        DisplayZone sparse = layout.FindByStableId("PANEL-FHD")!;

        // A 1200 x 800 px window on the 4K panel.
        var windowRect = new RectD(1000, 400, 1200, 800);
        DragState drag = CreateDrag(windowRect, dense, new Vec2(1600, 420));

        double physicalWidthMm = drag.PhysicalSizeMm.X;
        Assert.Equal(1200 * (597.0 / 3840.0), physicalWidthMm, 3);

        RectD target = WindowDragSolver.SolveTargetRect(drag, sparse, new Vec2(4200, 700), preserveGrabPoint: true);

        // The FHD panel is roughly half as dense, so the same real width is about half the pixels.
        double resultingMm = target.Width * sparse.MmPerPixel.X;
        Assert.Equal(physicalWidthMm, resultingMm, 0);
        Assert.InRange(target.Width, 660, 680);
    }

    [Fact]
    public void TheGrabbedPointStaysUnderTheCursor()
    {
        ZoneLayout layout = TestLayouts.HighDpiLeftOfLowDpi();
        DisplayZone dense = layout.FindByStableId("PANEL-4K")!;
        DisplayZone sparse = layout.FindByStableId("PANEL-FHD")!;

        var windowRect = new RectD(1000, 400, 1200, 800);

        // Grabbed a quarter of the way across the title bar.
        var grab = new Vec2(1300, 410);
        DragState drag = CreateDrag(windowRect, dense, grab);
        Assert.Equal(0.25, drag.GrabFraction.X, 3);

        var cursor = new Vec2(4500, 900);
        RectD target = WindowDragSolver.SolveTargetRect(drag, sparse, cursor, preserveGrabPoint: true);

        double grabX = target.Left + (drag.GrabFraction.X * target.Width);
        double grabY = target.Top + (drag.GrabFraction.Y * target.Height);

        Assert.Equal(cursor.X, grabX, 0);
        Assert.Equal(cursor.Y, grabY, 0);
    }

    [Fact]
    public void WithoutGrabPreservationTheTopLeftIsKept()
    {
        ZoneLayout layout = TestLayouts.HighDpiLeftOfLowDpi();
        DisplayZone dense = layout.FindByStableId("PANEL-4K")!;
        DisplayZone sparse = layout.FindByStableId("PANEL-FHD")!;

        var windowRect = new RectD(1000, 400, 1200, 800);
        DragState drag = CreateDrag(windowRect, dense, new Vec2(1600, 420));

        RectD target = WindowDragSolver.SolveTargetRect(drag, sparse, new Vec2(4500, 900), preserveGrabPoint: false);

        Assert.Equal(1000, target.Left, 0);
        Assert.Equal(400, target.Top, 0);
    }

    [Fact]
    public void AWindowIsNeverScaledLargerThanTheDisplayItLandsOn()
    {
        ZoneLayout layout = TestLayouts.HighDpiLeftOfLowDpi();
        DisplayZone dense = layout.FindByStableId("PANEL-4K")!;
        DisplayZone sparse = layout.FindByStableId("PANEL-FHD")!;

        // A window filling the 4K panel would need more pixels than the FHD panel has... in the
        // other direction. Here the reverse: dragging a full FHD window onto the dense panel.
        var windowRect = new RectD(3840, 540, 1920, 1080);
        DragState drag = CreateDrag(windowRect, sparse, new Vec2(4000, 560));

        RectD target = WindowDragSolver.SolveTargetRect(drag, dense, new Vec2(500, 300), preserveGrabPoint: true);

        Assert.InRange(target.Width, 0, dense.PixelBounds.Width);
        Assert.InRange(target.Height, 0, dense.PixelBounds.Height);
    }

    [Fact]
    public void ThrottleSuppressesRepeatedApplicationsOfTheSameRectangle()
    {
        ZoneLayout layout = TestLayouts.IdenticalPair();
        DisplayZone zone = layout.Zones[0];
        var windowRect = new RectD(100, 100, 800, 600);
        DragState drag = CreateDrag(windowRect, zone, new Vec2(400, 110));

        drag.LastAppliedMs = 1000;
        drag.LastAppliedRect = windowRect;

        Assert.False(WindowDragSolver.ShouldApply(drag, windowRect, 1010, 40));
        Assert.False(WindowDragSolver.ShouldApply(drag, windowRect, 2000, 40));
        Assert.True(WindowDragSolver.ShouldApply(drag, new RectD(200, 100, 800, 600), 2000, 40));
    }

    [Fact]
    public void ADraggedWindowStaysReachable()
    {
        ZoneLayout layout = TestLayouts.IdenticalPair();

        RectD offScreen = WindowDragSolver.KeepReachable(new RectD(-5000, -400, 800, 600), layout);

        Assert.True(offScreen.Right >= layout.PixelBounds.Left + 80);
        Assert.True(offScreen.Top >= layout.PixelBounds.Top);
    }

    [Fact]
    public void GrabPointOutsideTheWindowIsClamped()
    {
        var windowRect = new RectD(100, 100, 800, 600);

        Vec2 fraction = WindowDragSolver.ComputeGrabFraction(windowRect, new Vec2(-5000, -5000));

        Assert.InRange(fraction.X, -0.25, 1.25);
        Assert.InRange(fraction.Y, -0.25, 1.25);
    }
}
