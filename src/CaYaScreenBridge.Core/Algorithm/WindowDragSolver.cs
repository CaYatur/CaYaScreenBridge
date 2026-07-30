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
