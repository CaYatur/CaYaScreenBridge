using CaYaScreenBridge.Core.Geometry;
using CaYaScreenBridge.Core.Model;

namespace CaYaScreenBridge.Core.Algorithm;

/// <summary>
/// Everything the router knows about a window that is currently being dragged. Captured once, when
/// the drag starts, so the physical size is taken from the display the window actually came from.
/// </summary>
public sealed class DragState
{
    public required nint WindowHandle { get; init; }

    public required string ProcessName { get; init; }

    /// <summary>Window rectangle in pixels at the moment the drag started.</summary>
    public required RectD StartRect { get; init; }

    /// <summary>Physical size of the window in millimetres on the display it started on.</summary>
    public required Vec2 PhysicalSizeMm { get; init; }

    /// <summary>Where inside the window the user grabbed it, as a fraction of width and height.</summary>
    public required Vec2 GrabFraction { get; init; }

    public required string StartZoneId { get; init; }

    public bool Resizable { get; init; } = true;

    public string CurrentZoneId { get; set; } = string.Empty;

    /// <summary>The display on which the complete window last settled.</summary>
    public string SettledZoneId { get; set; } = string.Empty;

    /// <summary>Pixel size to preserve while the window straddles the next display boundary.</summary>
    public Vec2 SettledSizePx { get; set; }

    public long LastAppliedMs { get; set; }

    public RectD LastAppliedRect { get; set; }
}

/// <summary>
/// Computes where a dragged window must land so that it keeps the same real world size while the
/// cursor stays on the same point of the window.
///
/// Windows already rescales windows on a DPI change, but it does so after the fact and it anchors on
/// its own suggested rectangle, which is why a window dragged across a scaling boundary appears to
/// jump out from under the pointer. Solving from the physical size and the grab fraction removes
/// both problems.
/// </summary>
public static class WindowDragSolver
{
    /// <summary>A window is never shrunk below this, whatever the DPI ratio says.</summary>
    private const double MinimumWidthPx = 120;

    private const double MinimumHeightPx = 60;

    public static Vec2 ComputeGrabFraction(RectD windowRect, Vec2 cursorPixel)
    {
        double fx = windowRect.Width > 0 ? (cursorPixel.X - windowRect.Left) / windowRect.Width : 0.5;
        double fy = windowRect.Height > 0 ? (cursorPixel.Y - windowRect.Top) / windowRect.Height : 0.5;

        // The grab point can sit slightly outside the window (a resize border, a shadow); clamping
        // keeps the reconstructed position sane without discarding the drag.
        return new Vec2(Math.Clamp(fx, -0.25, 1.25), Math.Clamp(fy, -0.25, 1.25));
    }

    public static Vec2 ComputePhysicalSize(RectD windowRect, DisplayZone zone) =>
        new(windowRect.Width * zone.MmPerPixel.X, windowRect.Height * zone.MmPerPixel.Y);

    /// <summary>
    /// The rectangle the window should occupy on <paramref name="target"/>.
    /// </summary>
    /// <param name="drag">State captured when the drag started.</param>
    /// <param name="target">Display the window is moving onto.</param>
    /// <param name="cursorPixel">Current cursor position, in pixels.</param>
    /// <param name="preserveGrabPoint">
    /// When true the window is scaled about the cursor, so the pixel the user grabbed stays under
    /// the pointer. When false it is scaled about its top left corner.
    /// </param>
    public static RectD SolveTargetRect(DragState drag, DisplayZone target, Vec2 cursorPixel, bool preserveGrabPoint)
    {
        double width = drag.PhysicalSizeMm.X * target.PixelsPerMm.X;
        double height = drag.PhysicalSizeMm.Y * target.PixelsPerMm.Y;

        // A window can never be usefully larger than the display it sits on.
        width = Math.Clamp(width, MinimumWidthPx, Math.Max(MinimumWidthPx, target.PixelBounds.Width));
        height = Math.Clamp(height, MinimumHeightPx, Math.Max(MinimumHeightPx, target.PixelBounds.Height));

        double left;
        double top;

        if (preserveGrabPoint)
        {
            left = cursorPixel.X - (drag.GrabFraction.X * width);
            top = cursorPixel.Y - (drag.GrabFraction.Y * height);
        }
        else
        {
            left = drag.StartRect.Left;
            top = drag.StartRect.Top;
        }

        return new RectD(Math.Round(left), Math.Round(top), Math.Round(width), Math.Round(height));
    }

    /// <summary>
    /// During the straddling phase keep the source-screen pixel size. This prevents Windows from
    /// applying the destination DPI suggestion while half of the window is still visible on the
    /// source display. The grab point remains fixed under the cursor.
    /// </summary>
    public static RectD SolveTransitionRect(DragState drag, Vec2 cursorPixel, bool preserveGrabPoint)
    {
        double width = drag.SettledSizePx.X > 0 ? drag.SettledSizePx.X : drag.StartRect.Width;
        double height = drag.SettledSizePx.Y > 0 ? drag.SettledSizePx.Y : drag.StartRect.Height;
        double left = preserveGrabPoint
            ? cursorPixel.X - (drag.GrabFraction.X * width)
            : drag.StartRect.Left;
        double top = preserveGrabPoint
            ? cursorPixel.Y - (drag.GrabFraction.Y * height)
            : drag.StartRect.Top;

        return new RectD(Math.Round(left), Math.Round(top), Math.Round(width), Math.Round(height));
    }

    /// <summary>
    /// Solves one continuous window rectangle across any number of displays. The source window's
    /// physical size remains the invariant. Each display contributes according to the physical
    /// area of the window currently visible on that display, so high-density and low-density
    /// monitors blend smoothly instead of switching at an arbitrary cursor boundary.
    /// </summary>
    public static RectD SolveContinuousRect(
        DragState drag,
        Vec2 cursorPixel,
        ZoneLayout layout,
        bool preserveGrabPoint,
        int iterations = 6,
        bool snapToSingleDisplay = true)
    {
        if (layout.Count == 0)
        {
            return SolveTransitionRect(drag, cursorPixel, preserveGrabPoint);
        }

        double width = drag.LastAppliedRect.Width > 0
            ? drag.LastAppliedRect.Width
            : drag.StartRect.Width;
        double height = drag.LastAppliedRect.Height > 0
            ? drag.LastAppliedRect.Height
            : drag.StartRect.Height;

        width = Math.Max(MinimumWidthPx, width);
        height = Math.Max(MinimumHeightPx, height);

        for (int iteration = 0; iteration < Math.Max(1, iterations); iteration++)
        {
            RectD candidate = PositionFromGrab(drag, cursorPixel, preserveGrabPoint, width, height);
            if (!TryComputeBlendedPixelsPerMm(candidate, layout, out Vec2 pixelsPerMm))
            {
                DisplayZone? fallback = layout.FindByPixel(cursorPixel) ?? layout.NearestByPixel(cursorPixel);
                if (fallback is null)
                {
                    break;
                }

                pixelsPerMm = fallback.PixelsPerMm;
            }

            double desiredWidth = Math.Max(MinimumWidthPx, drag.PhysicalSizeMm.X * pixelsPerMm.X);
            double desiredHeight = Math.Max(MinimumHeightPx, drag.PhysicalSizeMm.Y * pixelsPerMm.Y);

            // Damped fixed-point iteration prevents a wide window from oscillating when changing
            // its own size also changes the overlap weights at a monitor boundary.
            const double response = 0.72;
            width += (desiredWidth - width) * response;
            height += (desiredHeight - height) * response;
        }

        RectD solved = PositionFromGrab(drag, cursorPixel, preserveGrabPoint, width, height, round: true);
        DisplayZone? dominant = FindDominantZone(solved, layout);
        if (snapToSingleDisplay && dominant is not null && IsFullyInside(solved, dominant.PixelBounds, 1.5))
        {
            // Once the complete rectangle is on one display there is no ambiguity left: snap to the
            // exact physical target size so the endpoint is deterministic and does not retain a
            // fraction of the previous monitor's density.
            return SolveTargetRect(drag, dominant, cursorPixel, preserveGrabPoint);
        }

        return solved;
    }

    /// <summary>Counts displays containing a meaningful portion of the rectangle.</summary>
    public static int CountIntersectingDisplays(RectD rect, ZoneLayout layout, double minimumAreaPx = 1)
    {
        int count = 0;
        foreach (DisplayZone zone in layout.Zones)
        {
            if (IntersectionArea(rect, zone.PixelBounds) >= minimumAreaPx)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Returns the display containing the largest physical part of a window.</summary>
    public static DisplayZone? FindDominantZone(RectD rect, ZoneLayout layout)
    {
        DisplayZone? best = null;
        double bestPhysicalArea = 0;

        foreach (DisplayZone zone in layout.Zones)
        {
            double pixelArea = IntersectionArea(rect, zone.PixelBounds);
            double physicalArea = pixelArea * zone.MmPerPixel.X * zone.MmPerPixel.Y;
            if (physicalArea > bestPhysicalArea)
            {
                bestPhysicalArea = physicalArea;
                best = zone;
            }
        }

        return best;
    }

    private static bool TryComputeBlendedPixelsPerMm(RectD rect, ZoneLayout layout, out Vec2 result)
    {
        double totalPhysicalArea = 0;
        double weightedPixelsPerMmX = 0;
        double weightedPixelsPerMmY = 0;

        foreach (DisplayZone zone in layout.Zones)
        {
            double pixelArea = IntersectionArea(rect, zone.PixelBounds);
            if (pixelArea <= 0)
            {
                continue;
            }

            double physicalArea = pixelArea * zone.MmPerPixel.X * zone.MmPerPixel.Y;
            totalPhysicalArea += physicalArea;
            weightedPixelsPerMmX += physicalArea * zone.PixelsPerMm.X;
            weightedPixelsPerMmY += physicalArea * zone.PixelsPerMm.Y;
        }

        if (totalPhysicalArea <= 0)
        {
            result = default;
            return false;
        }

        result = new Vec2(
            weightedPixelsPerMmX / totalPhysicalArea,
            weightedPixelsPerMmY / totalPhysicalArea);
        return true;
    }

    private static RectD PositionFromGrab(
        DragState drag,
        Vec2 cursorPixel,
        bool preserveGrabPoint,
        double width,
        double height,
        bool round = false)
    {
        double left = preserveGrabPoint
            ? cursorPixel.X - (drag.GrabFraction.X * width)
            : drag.StartRect.Left;
        double top = preserveGrabPoint
            ? cursorPixel.Y - (drag.GrabFraction.Y * height)
            : drag.StartRect.Top;

        return round
            ? new RectD(Math.Round(left), Math.Round(top), Math.Round(width), Math.Round(height))
            : new RectD(left, top, width, height);
    }

    private static double IntersectionArea(RectD a, RectD b)
    {
        double width = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        double height = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        return width * height;
    }

    public static bool IsFullyInside(RectD rect, RectD bounds, double tolerancePx = 1) =>
        rect.Left >= bounds.Left - tolerancePx &&
        rect.Top >= bounds.Top - tolerancePx &&
        rect.Right <= bounds.Right + tolerancePx &&
        rect.Bottom <= bounds.Bottom + tolerancePx;

    /// <summary>
    /// Keeps enough of the title bar reachable that the window can still be moved after the drag
    /// ends, without pinning the window fully on screen (dragging a window half off a display is a
    /// legitimate thing to do).
    /// </summary>
    public static RectD KeepReachable(RectD rect, ZoneLayout layout, double minimumVisiblePx = 80)
    {
        if (layout.Count == 0)
        {
            return rect;
        }

        RectD bounds = layout.PixelBounds;

        double x = Math.Clamp(
            rect.X,
            bounds.Left - rect.Width + minimumVisiblePx,
            bounds.Right - minimumVisiblePx);

        double y = Math.Clamp(rect.Y, bounds.Top, bounds.Bottom - minimumVisiblePx);

        return new RectD(x, y, rect.Width, rect.Height);
    }

    /// <summary>
    /// Whether a live rescale is worth issuing. Suppresses sub pixel churn and rate limits the
    /// SetWindowPos calls so a slow application being dragged does not stutter.
    /// </summary>
    public static bool ShouldApply(DragState drag, RectD candidate, long nowMs, int throttleMs)
    {
        if (nowMs - drag.LastAppliedMs < throttleMs)
        {
            return false;
        }

        RectD last = drag.LastAppliedRect;
        return Math.Abs(last.X - candidate.X) >= 1 ||
               Math.Abs(last.Y - candidate.Y) >= 1 ||
               Math.Abs(last.Width - candidate.Width) >= 1 ||
               Math.Abs(last.Height - candidate.Height) >= 1;
    }
}
