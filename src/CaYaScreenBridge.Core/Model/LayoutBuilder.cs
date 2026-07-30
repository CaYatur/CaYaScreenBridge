using CaYaScreenBridge.Core.Config;
using CaYaScreenBridge.Core.Geometry;

namespace CaYaScreenBridge.Core.Model;

/// <summary>
/// Turns the raw display snapshots reported by Windows into a physical (millimetre) layout.
///
/// Windows only knows about pixel rectangles, so the physical arrangement has to be reconstructed:
/// each panel's real size comes from EDID (or a user override), and the panels are then laid out
/// edge to edge in millimetre space following the same neighbour relationships the user set up in
/// the Windows display settings. Any position the user has adjusted by hand wins over the
/// reconstruction.
/// </summary>
public static class LayoutBuilder
{
    /// <summary>Pixel tolerance when deciding whether two monitors touch along an edge.</summary>
    private const double AdjacencyTolerancePx = 2.0;

    public static ZoneLayout Build(IReadOnlyList<DisplaySnapshot> displays, LayoutProfile? profile)
    {
        if (displays.Count == 0)
        {
            return ZoneLayout.Empty;
        }

        // 1. Physical size for every display: user override, then EDID, then a DPI derived estimate.
        var sizes = new Vec2[displays.Count];
        for (int i = 0; i < displays.Count; i++)
        {
            sizes[i] = ResolvePhysicalSize(displays[i], profile);
        }

        // 2. Physical placement.
        var locations = new Vec2?[displays.Count];
        for (int i = 0; i < displays.Count; i++)
        {
            DisplayOverride? ovr = profile?.Find(displays[i].StableId);
            if (ovr is { HasLocation: true })
            {
                locations[i] = new Vec2(ovr.PhysicalLeftMm!.Value, ovr.PhysicalTopMm!.Value);
            }
        }

        ReconstructMissingLocations(displays, sizes, locations);

        // 3. Normalise so the top left of the whole layout sits at the origin. Keeps the numbers in
        //    the editor readable and makes saved layouts comparable.
        double minX = double.MaxValue;
        double minY = double.MaxValue;
        for (int i = 0; i < displays.Count; i++)
        {
            Vec2 loc = locations[i]!.Value;
            minX = Math.Min(minX, loc.X);
            minY = Math.Min(minY, loc.Y);
        }

        var zones = new DisplayZone[displays.Count];
        for (int i = 0; i < displays.Count; i++)
        {
            Vec2 loc = locations[i]!.Value - new Vec2(minX, minY);
            var physical = new RectD(loc.X, loc.Y, sizes[i].X, sizes[i].Y);

            zones[i] = new DisplayZone(
                displays[i].StableId,
                string.IsNullOrWhiteSpace(displays[i].FriendlyName) ? displays[i].DeviceName : displays[i].FriendlyName,
                displays[i].PixelBounds,
                physical,
                displays[i].EffectiveDpiX,
                displays[i].IsPrimary);
        }

        return new ZoneLayout(zones);
    }

    private static Vec2 ResolvePhysicalSize(DisplaySnapshot display, LayoutProfile? profile)
    {
        DisplayOverride? ovr = profile?.Find(display.StableId);
        if (ovr is { HasSize: true })
        {
            return new Vec2(ovr.PhysicalWidthMm!.Value, ovr.PhysicalHeightMm!.Value);
        }

        if (display.HasEdidSize)
        {
            return new Vec2(display.EdidWidthMm, display.EdidHeightMm);
        }

        // No EDID: fall back to the raw panel DPI when the driver reports one, otherwise assume the
        // panel runs at its effective DPI. The result is only a starting point; the user can correct
        // it in the layout editor and the correction is what gets saved.
        double dpiX = display.RawDpiX > 1 ? display.RawDpiX : display.EffectiveDpiX;
        double dpiY = display.RawDpiY > 1 ? display.RawDpiY : display.EffectiveDpiY;
        return new Vec2(
            display.PixelBounds.Width / dpiX * 25.4,
            display.PixelBounds.Height / dpiY * 25.4);
    }

    /// <summary>
    /// Places every display that has no saved position by walking outward from an already placed
    /// neighbour, so that panels which touch in Windows also touch in millimetre space and keep
    /// their relative offset along the shared edge.
    /// </summary>
    private static void ReconstructMissingLocations(
        IReadOnlyList<DisplaySnapshot> displays,
        Vec2[] sizes,
        Vec2?[] locations)
    {
        int seed = FindSeed(displays, locations);
        locations[seed] ??= Vec2.Zero;

        var pending = new Queue<int>();
        pending.Enqueue(seed);
        var visited = new bool[displays.Count];
        visited[seed] = true;

        while (pending.Count > 0)
        {
            int from = pending.Dequeue();

            foreach (int to in OrderNeighbours(displays, from))
            {
                if (visited[to])
                {
                    continue;
                }

                if (!AreAdjacent(displays[from].PixelBounds, displays[to].PixelBounds))
                {
                    continue;
                }

                visited[to] = true;
                locations[to] ??= PlaceRelative(
                    displays[from].PixelBounds,
                    locations[from]!.Value,
                    sizes[from],
                    displays[to].PixelBounds,
                    sizes[to]);

                pending.Enqueue(to);
            }
        }

        // Anything the adjacency walk could not reach (a monitor separated by a gap in the Windows
        // arrangement) is placed against whichever placed display it is nearest to in pixel space.
        for (int i = 0; i < displays.Count; i++)
        {
            if (locations[i] is not null)
            {
                continue;
            }

            int anchor = NearestPlaced(displays, locations, i);
            locations[i] = PlaceRelative(
                displays[anchor].PixelBounds,
                locations[anchor]!.Value,
                sizes[anchor],
                displays[i].PixelBounds,
                sizes[i]);
        }
    }

    private static int FindSeed(IReadOnlyList<DisplaySnapshot> displays, Vec2?[] locations)
    {
        // Prefer a display the user already positioned, so hand placed panels anchor the rest.
        for (int i = 0; i < displays.Count; i++)
        {
            if (locations[i] is not null)
            {
                return i;
            }
        }

        for (int i = 0; i < displays.Count; i++)
        {
            if (displays[i].IsPrimary)
            {
                return i;
            }
        }

        return 0;
    }

    private static IEnumerable<int> OrderNeighbours(IReadOnlyList<DisplaySnapshot> displays, int from)
    {
        Vec2 origin = displays[from].PixelBounds.Center;
        return Enumerable.Range(0, displays.Count)
            .Where(i => i != from)
            .OrderBy(i => (displays[i].PixelBounds.Center - origin).LengthSquared);
    }

    private static int NearestPlaced(IReadOnlyList<DisplaySnapshot> displays, Vec2?[] locations, int index)
    {
        int best = 0;
        double bestDistance = double.MaxValue;
        Vec2 origin = displays[index].PixelBounds.Center;

        for (int i = 0; i < displays.Count; i++)
        {
            if (i == index || locations[i] is null)
            {
                continue;
            }

            double distance = (displays[i].PixelBounds.Center - origin).LengthSquared;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    private static bool AreAdjacent(RectD a, RectD b)
    {
        bool horizontallyTouching =
            (Math.Abs(a.Right - b.Left) <= AdjacencyTolerancePx || Math.Abs(b.Right - a.Left) <= AdjacencyTolerancePx) &&
            a.Top < b.Bottom && b.Top < a.Bottom;

        bool verticallyTouching =
            (Math.Abs(a.Bottom - b.Top) <= AdjacencyTolerancePx || Math.Abs(b.Bottom - a.Top) <= AdjacencyTolerancePx) &&
            a.Left < b.Right && b.Left < a.Right;

        return horizontallyTouching || verticallyTouching;
    }

    /// <summary>
    /// Places <paramref name="targetPx"/> against the anchor in millimetre space. The dominant
    /// direction is taken from the pixel arrangement, and the offset along the shared edge is kept
    /// proportional so "the top of the side monitor lines up a third of the way down the main one"
    /// survives the conversion.
    /// </summary>
    private static Vec2 PlaceRelative(
        RectD anchorPx,
        Vec2 anchorMm,
        Vec2 anchorSizeMm,
        RectD targetPx,
        Vec2 targetSizeMm)
    {
        double dx = targetPx.Center.X - anchorPx.Center.X;
        double dy = targetPx.Center.Y - anchorPx.Center.Y;

        // Compare overlap rather than raw centre distance: two monitors stacked vertically can have
        // a larger horizontal centre offset than vertical if their widths differ a lot.
        double horizontalGap = Math.Max(targetPx.Left - anchorPx.Right, anchorPx.Left - targetPx.Right);
        double verticalGap = Math.Max(targetPx.Top - anchorPx.Bottom, anchorPx.Top - targetPx.Bottom);
        bool horizontal = horizontalGap >= verticalGap;

        double scaleX = anchorPx.Width > 0 ? anchorSizeMm.X / anchorPx.Width : 1.0;
        double scaleY = anchorPx.Height > 0 ? anchorSizeMm.Y / anchorPx.Height : 1.0;

        if (horizontal)
        {
            double x = dx >= 0
                ? anchorMm.X + anchorSizeMm.X
                : anchorMm.X - targetSizeMm.X;
            double y = anchorMm.Y + ((targetPx.Top - anchorPx.Top) * scaleY);
            return new Vec2(x, y);
        }
        else
        {
            double y = dy >= 0
                ? anchorMm.Y + anchorSizeMm.Y
                : anchorMm.Y - targetSizeMm.Y;
            double x = anchorMm.X + ((targetPx.Left - anchorPx.Left) * scaleX);
            return new Vec2(x, y);
        }
    }
}
