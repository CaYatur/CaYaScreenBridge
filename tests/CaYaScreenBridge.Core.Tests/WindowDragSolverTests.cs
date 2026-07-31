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
    [Fact]
    public void StraddlingWindowKeepsSourcePixelSizeUntilFullyInsideTarget()
    {
        ZoneLayout layout = TestLayouts.HighDpiLeftOfLowDpi();
        DisplayZone source = layout.FindByStableId("PANEL-4K")!;
        DisplayZone targetZone = layout.FindByStableId("PANEL-FHD")!;
        var start = new RectD(2600, 500, 1200, 800);
        DragState drag = CreateDrag(start, source, new Vec2(3000, 520));

        RectD transition = WindowDragSolver.SolveTransitionRect(
            drag,
            new Vec2(3900, 700),
            preserveGrabPoint: true);

        Assert.Equal(start.Width, transition.Width);
        Assert.Equal(start.Height, transition.Height);
        Assert.False(WindowDragSolver.IsFullyInside(transition, targetZone.PixelBounds));

        double heldX = transition.Left + (drag.GrabFraction.X * transition.Width);
        double heldY = transition.Top + (drag.GrabFraction.Y * transition.Height);
        Assert.Equal(3900, heldX, 0);
        Assert.Equal(700, heldY, 0);
    }

    [Fact]
    public void FullyTransferredWindowUsesExactPhysicalTargetSize()
    {
        ZoneLayout layout = TestLayouts.HighDpiLeftOfLowDpi();
        DisplayZone source = layout.FindByStableId("PANEL-4K")!;
        DisplayZone targetZone = layout.FindByStableId("PANEL-FHD")!;
        var start = new RectD(2600, 500, 1200, 600);
        DragState drag = CreateDrag(start, source, new Vec2(3000, 520));
        var cursor = new Vec2(4700, 900);

        RectD transition = WindowDragSolver.SolveTransitionRect(drag, cursor, preserveGrabPoint: true);
        Assert.True(WindowDragSolver.IsFullyInside(transition, targetZone.PixelBounds));

        RectD settled = WindowDragSolver.SolveTargetRect(drag, targetZone, cursor, preserveGrabPoint: true);
        Assert.Equal(drag.PhysicalSizeMm.X, settled.Width * targetZone.MmPerPixel.X, 0);
        Assert.Equal(drag.PhysicalSizeMm.Y, settled.Height * targetZone.MmPerPixel.Y, 0);
    }

    [Fact]
    public void ContinuousSolverBlendsAcrossThreeDifferentDisplays()
    {
        var a = new DisplayZone("A", "A", new RectD(0, 0, 2560, 1440), new RectD(0, 0, 600, 340), 120, true);
        var b = new DisplayZone("B", "B", new RectD(2560, 200, 1920, 1080), new RectD(600, 60, 530, 300), 96, false);
        var c = new DisplayZone("C", "C", new RectD(4480, -120, 3840, 2160), new RectD(1130, -35, 700, 390), 168, false);
        var layout = new ZoneLayout(new[] { a, b, c });
        var start = new RectD(1800, 400, 1200, 700);
        DragState drag = CreateDrag(start, a, new Vec2(2100, 430));

        RectD onA = WindowDragSolver.SolveContinuousRect(drag, new Vec2(2100, 430), layout, true);
        drag.LastAppliedRect = onA;
        RectD betweenAB = WindowDragSolver.SolveContinuousRect(drag, new Vec2(3000, 500), layout, true);
        drag.LastAppliedRect = betweenAB;
        RectD onB = WindowDragSolver.SolveContinuousRect(drag, new Vec2(3500, 500), layout, true);
        drag.LastAppliedRect = onB;
        RectD betweenBC = WindowDragSolver.SolveContinuousRect(drag, new Vec2(4700, 450), layout, true);

        Assert.InRange(betweenAB.Width, Math.Min(onA.Width, onB.Width), Math.Max(onA.Width, onB.Width));
        Assert.True(betweenBC.Width > 0);

        foreach ((RectD rect, Vec2 cursor) in new[]
                 {
                     (onA, new Vec2(2100, 430)),
                     (betweenAB, new Vec2(3000, 500)),
                     (onB, new Vec2(3500, 500)),
                     (betweenBC, new Vec2(4700, 450)),
                 })
        {
            double heldX = rect.Left + (drag.GrabFraction.X * rect.Width);
            double heldY = rect.Top + (drag.GrabFraction.Y * rect.Height);
            Assert.Equal(cursor.X, heldX, 0);
            Assert.Equal(cursor.Y, heldY, 0);
        }
    }

    [Fact]
    public void ContinuousSolverReachesExactPhysicalSizeWhenFullyOnOneDisplay()
    {
        ZoneLayout layout = TestLayouts.HighDpiLeftOfLowDpi();
        DisplayZone source = layout.FindByStableId("PANEL-4K")!;
        DisplayZone target = layout.FindByStableId("PANEL-FHD")!;
        var start = new RectD(1000, 400, 1200, 800);
        DragState drag = CreateDrag(start, source, new Vec2(1300, 420));

        RectD solved = WindowDragSolver.SolveContinuousRect(drag, new Vec2(5000, 700), layout, true);

        Assert.True(WindowDragSolver.IsFullyInside(solved, target.PixelBounds));
        Assert.Equal(drag.PhysicalSizeMm.X, solved.Width * target.MmPerPixel.X, 0);
        Assert.Equal(drag.PhysicalSizeMm.Y, solved.Height * target.MmPerPixel.Y, 0);
    }

}
