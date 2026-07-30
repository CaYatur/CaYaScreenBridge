using CaYaScreenBridge.Core.Algorithm;
using CaYaScreenBridge.Core.Config;
using CaYaScreenBridge.Core.Geometry;
using CaYaScreenBridge.Core.Model;
using Xunit;

namespace CaYaScreenBridge.Core.Tests;

public class CursorRouterTests
{
    private static CursorRouter CreateRouter(ZoneLayout layout, RouterOptions? options = null)
    {
        var router = new CursorRouter();
        router.SetLayout(layout);
        router.SetOptions(options ?? RouterOptions.Default);
        return router;
    }

    private static RouterDecision Move(CursorRouter router, double x, double y, long time = 0) =>
        router.Process(new MouseSample { Position = new Vec2(x, y), TimestampMs = time });

    /// <summary>
    /// The core promise: after crossing from a dense panel to a sparse one, the pointer sits at the
    /// same height above the desk, not at the same pixel row.
    /// </summary>
    [Fact]
    public void CrossingPreservesPhysicalHeight()
    {
        ZoneLayout layout = TestLayouts.HighDpiLeftOfLowDpi();
        CursorRouter router = CreateRouter(layout);

        // Start two thirds of the way down the 4K panel, which is 224 mm from its top edge.
        Move(router, 3800, 1440);

        RouterDecision decision = Move(router, 3860, 1440, 10);

        Assert.True(decision.Handled);
        Assert.Equal(RouterOutcome.Crossed, decision.Outcome);
        Assert.Equal("PANEL-FHD", decision.ToZone!.StableId);

        // 1440 px on the 4K panel is 224 mm down the desk. The FHD panel starts 18.5 mm lower and
        // is 1080 px over 299 mm, so the same height lands on row 540 + 742 = 1282.
        Vec2 physical = layout.Zones[1].PixelToMm(decision.TargetPixel);
        Assert.InRange(physical.Y, 223.5, 224.5);
        Assert.InRange(decision.TargetPixel.Y, 1280, 1285);

        // The uncorrected behaviour would have kept row 1440, which is 60 mm too low.
        Assert.NotEqual(1440, decision.TargetPixel.Y);
    }

    /// <summary>
    /// Crossing back and forth hundreds of times must leave the cursor at exactly the same height.
    ///
    /// This is what the unrounded millimetre position buys. Each crossing rounds the pixel position
    /// it hands to Windows, and if that rounded value were fed back into the model the error would
    /// accumulate and walk the pointer steadily up or down the screen.
    /// </summary>
    [Fact]
    public void RepeatedCrossingsDoNotDrift()
    {
        CursorRouter router = CreateRouter(TestLayouts.HighDpiLeftOfLowDpi());

        // A deliberately awkward row: 1001 px maps to 155.71 mm, which is not a whole pixel on the
        // other panel, so every crossing has a remainder to lose.
        Move(router, 3800, 1001);
        double startY = router.PhysicalPosition.Y;
        double startX = router.PhysicalPosition.X;

        var position = new Vec2(3800, 1001);
        int crossings = 0;

        for (int i = 0; i < 500; i++)
        {
            RouterDecision right = Move(router, position.X + 60, position.Y, i * 12);
            position = right.Handled ? right.TargetPixel : new Vec2(position.X + 60, position.Y);
            if (right.Outcome == RouterOutcome.Crossed)
            {
                crossings++;
            }

            RouterDecision left = Move(router, position.X - 120, position.Y, (i * 12) + 4);
            position = left.Handled ? left.TargetPixel : new Vec2(position.X - 120, position.Y);
            if (left.Outcome == RouterOutcome.Crossed)
            {
                crossings++;
            }

            // Return to the starting column with an ordinary in-screen movement.
            Move(router, 3800, position.Y, (i * 12) + 8);
            position = new Vec2(3800, position.Y);
        }

        Assert.True(crossings > 900, $"expected the loop to keep crossing, saw {crossings}");
        Assert.Equal("PANEL-4K", router.CurrentZone!.StableId);

        // The perpendicular axis is never rounded into the model, so it is exact after 1000 crossings.
        Assert.Equal(startY, router.PhysicalPosition.Y, 9);

        // The axis of travel wobbles by at most the width of one pixel and does not accumulate.
        Assert.InRange(router.PhysicalPosition.X, startX - 0.5, startX + 0.5);
    }

    [Fact]
    public void MovementInsideOneDisplayIsNotTouched()
    {
        CursorRouter router = CreateRouter(TestLayouts.HighDpiLeftOfLowDpi());

        Move(router, 1000, 500);

        RouterDecision decision = Move(router, 1040, 520, 8);

        Assert.False(decision.Handled);
        Assert.Equal(RouterOutcome.PassThrough, decision.Outcome);
    }

    [Fact]
    public void IdenticalDisplaysCrossWithoutRepositioning()
    {
        ZoneLayout layout = TestLayouts.IdenticalPair();
        CursorRouter router = CreateRouter(layout);

        Move(router, 1900, 600);
        RouterDecision decision = Move(router, 1930, 600, 8);

        // The physical layout is continuous here, so the destination is where Windows would have
        // put it anyway and there is nothing to correct.
        Assert.False(decision.Handled);
    }

    /// <summary>
    /// A movement that ends in the dead space of an L shaped arrangement must not leave the cursor
    /// there. It is projected onto the nearest display in the direction of travel.
    /// </summary>
    [Fact]
    public void MovementIntoDeadSpaceIsRecovered()
    {
        ZoneLayout layout = TestLayouts.LShaped();
        CursorRouter router = CreateRouter(layout);

        // Push right from low down the main display. In pixel space that lands inside the portrait
        // panel's rectangle, but physically it is below the portrait panel entirely: a place no
        // display occupies.
        Move(router, 2500, 1700);
        RouterDecision decision = Move(router, 2600, 1700, 8);

        Assert.True(decision.Handled);
        Assert.Equal("MAIN", decision.ToZone!.StableId);
        Assert.NotNull(layout.FindByPixel(decision.TargetPixel));
    }

    /// <summary>
    /// A movement long enough to overshoot a narrow display should still land on it, because the
    /// trajectory passed through it.
    /// </summary>
    [Fact]
    public void FastMovementLandsOnTheDisplayItCrossed()
    {
        // A small, very dense auxiliary panel: 800 x 1280 px across only 106 mm.
        var narrow = new DisplayZone(
            "NARROW",
            "Narrow",
            new RectD(1920, 0, 800, 1280),
            new RectD(531, 0, 106, 170),
            96,
            isPrimary: false);

        var main = new DisplayZone(
            "MAIN",
            "Main",
            new RectD(0, 0, 1920, 1080),
            new RectD(0, 0, 531, 299),
            96,
            isPrimary: true);

        var layout = new ZoneLayout(new[] { main, narrow });
        CursorRouter router = CreateRouter(layout);

        Move(router, 1900, 500);

        // 550 px of travel is 152 mm, which overshoots the 106 mm wide panel completely. The
        // movement still passed straight through it, so that is where the cursor belongs.
        RouterDecision decision = Move(router, 2450, 500, 8);

        Assert.True(decision.Handled);
        Assert.Equal("NARROW", decision.ToZone!.StableId);
        Assert.NotNull(layout.FindByPixel(decision.TargetPixel));
    }

    [Fact]
    public void BorderResistanceHoldsThenReleases()
    {
        ZoneLayout layout = TestLayouts.HighDpiLeftOfLowDpi();
        var options = new RouterOptions
        {
            AlignCursor = true,
            BorderResistanceMm = 10,
            ResistanceResetMs = 400,
        };

        CursorRouter router = CreateRouter(layout, options);

        Move(router, 3800, 1000);

        // Each 10 px step on the 4K panel is about 1.55 mm, so the first few pushes must be held.
        RouterDecision first = Move(router, 3850, 1000, 10);
        Assert.Equal(RouterOutcome.Held, first.Outcome);
        Assert.Equal("PANEL-4K", first.ToZone!.StableId);

        RouterDecision? crossing = null;
        for (int i = 0; i < 20 && crossing is null; i++)
        {
            RouterDecision decision = Move(router, 3900 + (i * 20), 1000, 20 + (i * 10));
            if (decision.Outcome == RouterOutcome.Crossed)
            {
                crossing = decision;
            }
        }

        Assert.NotNull(crossing);
        Assert.Equal("PANEL-FHD", crossing!.Value.ToZone!.StableId);
    }

    [Fact]
    public void ResistanceDecaysAfterAPause()
    {
        var options = new RouterOptions
        {
            AlignCursor = true,
            BorderResistanceMm = 20,
            ResistanceResetMs = 300,
        };

        CursorRouter router = CreateRouter(TestLayouts.HighDpiLeftOfLowDpi(), options);
        Move(router, 3800, 1000);

        // Brush the border repeatedly, but with a long pause between each attempt.
        for (int i = 0; i < 10; i++)
        {
            RouterDecision decision = Move(router, 3850, 1000, i * 5000);
            Assert.NotEqual(RouterOutcome.Crossed, decision.Outcome);
            Move(router, 3800, 1000, (i * 5000) + 100);
        }
    }

    [Fact]
    public void InjectedMovementIsAdoptedNotCorrected()
    {
        CursorRouter router = CreateRouter(TestLayouts.HighDpiLeftOfLowDpi());

        Move(router, 1000, 500);

        RouterDecision decision = router.Process(new MouseSample
        {
            Position = new Vec2(4200, 800),
            TimestampMs = 20,
            Injected = true,
        });

        Assert.False(decision.Handled);
        Assert.Equal("PANEL-FHD", router.CurrentZone!.StableId);
    }

    [Fact]
    public void CursorFoundOutsideEveryDisplayIsRescued()
    {
        ZoneLayout layout = TestLayouts.LShaped();
        CursorRouter router = CreateRouter(layout);

        // Above the main display and to the left of the portrait one: no display covers this point.
        RouterDecision decision = Move(router, 200, 100);

        Assert.True(decision.Handled);
        Assert.Equal(RouterOutcome.Recovered, decision.Outcome);
        Assert.NotNull(layout.FindByPixel(decision.TargetPixel));
        Assert.Equal(1, router.RecoveryCount);
    }

    [Fact]
    public void WrapCarriesTheCursorAcrossTheDesktop()
    {
        ZoneLayout layout = TestLayouts.IdenticalPair();
        var options = new RouterOptions { AlignCursor = true, Wrap = WrapMode.Horizontal };
        CursorRouter router = CreateRouter(layout, options);

        Move(router, 3830, 500);
        RouterDecision decision = Move(router, 3900, 500, 8);

        Assert.True(decision.Handled);
        Assert.Equal("A", decision.ToZone!.StableId);
        Assert.InRange(decision.TargetPixel.X, 0, 90);
    }

    [Fact]
    public void DisabledAlignmentPassesEverythingThrough()
    {
        var options = new RouterOptions { AlignCursor = false };
        CursorRouter router = CreateRouter(TestLayouts.HighDpiLeftOfLowDpi(), options);

        Move(router, 3800, 1000);
        RouterDecision decision = Move(router, 3900, 1000, 8);

        Assert.False(decision.Handled);
    }

    [Fact]
    public void EmptyLayoutIsHarmless()
    {
        var router = new CursorRouter();
        RouterDecision decision = Move(router, 100, 100);
        Assert.False(decision.Handled);
    }

    /// <summary>
    /// With the cursor pinned at the outer edge of the desktop the reported position stops changing.
    /// The raw delta is what lets the router see the movement and complete the crossing.
    /// </summary>
    [Fact]
    public void RawInputCompletesACrossingWhileTheCursorIsPinned()
    {
        ZoneLayout layout = TestLayouts.HighDpiLeftOfLowDpi();
        CursorRouter router = CreateRouter(layout);

        // Row 1929 on the 4K panel is 300 mm down the desk, which is well inside the FHD panel
        // physically. In pixel terms it is below the FHD panel's rectangle, so Windows has nowhere
        // to move the cursor and pins it against the edge instead.
        Move(router, 3839, 1929);

        // Teach the calibrator what one raw unit is worth while movement is still unobstructed.
        for (int i = 0; i < 30; i++)
        {
            router.Process(new MouseSample
            {
                Position = new Vec2(3839 - (i % 2 == 0 ? 10 : 0), 1929),
                RawDelta = new Vec2(i % 2 == 0 ? -10 : 10, 0),
                HasRawDelta = true,
                TimestampMs = 100 + i,
            });
        }

        // Now push right. The reported position never changes; only the raw delta shows the intent.
        RouterDecision decision = default;
        for (int i = 0; i < 5 && decision.Outcome != RouterOutcome.Crossed; i++)
        {
            decision = router.Process(new MouseSample
            {
                Position = new Vec2(3839, 1929),
                RawDelta = new Vec2(10, 0),
                HasRawDelta = true,
                TimestampMs = 200 + i,
            });
        }

        Assert.Equal(RouterOutcome.Crossed, decision.Outcome);
        Assert.Equal("PANEL-FHD", decision.ToZone!.StableId);

        // Without the raw assist the same sequence must produce nothing at all.
        var plain = new CursorRouter();
        plain.SetLayout(layout);
        plain.SetOptions(new RouterOptions { AlignCursor = true, UseRawInputAssist = false });
        Move(plain, 3839, 1929);

        for (int i = 0; i < 5; i++)
        {
            RouterDecision blocked = plain.Process(new MouseSample
            {
                Position = new Vec2(3839, 1929),
                RawDelta = new Vec2(10, 0),
                HasRawDelta = true,
                TimestampMs = 200 + i,
            });

            Assert.False(blocked.Handled);
        }
    }
}
