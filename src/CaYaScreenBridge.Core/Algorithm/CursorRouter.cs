using CaYaScreenBridge.Core.Config;
using CaYaScreenBridge.Core.Geometry;
using CaYaScreenBridge.Core.Model;

namespace CaYaScreenBridge.Core.Algorithm;

/// <summary>
/// The cursor transition solver.
///
/// Windows moves the pointer in pixels, which means a movement that crosses from a 96 DPI panel to a
/// 192 DPI panel keeps its pixel row and therefore lands at a completely different height on the
/// desk. This class maintains a second, continuous position in millimetres, routes every movement
/// through that physical space, and converts back to pixels only at the very end.
///
/// Three properties matter, and each one is a deliberate part of the design:
///
/// 1. <b>No rounding drift.</b> The authoritative position is the millimetre one, and it is never
///    re-derived from the rounded pixel position. Crossing a border back and forth a thousand times
///    therefore returns the cursor to where it started instead of creeping.
///
/// 2. <b>Trajectory aware.</b> The target display is chosen by intersecting the movement segment
///    with the physical layout, not by testing where the movement happened to end. A fast diagonal
///    flick that passes over a corner lands on the display it actually crossed.
///
/// 3. <b>Never loses the cursor.</b> If a movement ends in a gap (bezels, an L shaped arrangement,
///    a monitor that was just unplugged), the position is projected onto the nearest display in the
///    direction of travel rather than left in dead space.
///
/// The public entry point runs inside a WH_MOUSE_LL callback, so it allocates nothing and takes no
/// locks on the hot path.
/// </summary>
public sealed class CursorRouter
{
    /// <summary>Keeps a rounded pixel from landing on the exclusive right/bottom edge of a display.</summary>
    private const double PixelClampMargin = 1.0;

    private readonly RawDeltaCalibrator _calibrator = new();

    private ZoneLayout _layout = ZoneLayout.Empty;
    private RouterOptions _options = RouterOptions.Default;

    private DisplayZone? _zone;
    private Vec2 _physical;
    private Vec2 _lastPixel;
    private bool _synced;

    private double _resistance;
    private long _resistanceStampMs;
    private long _lastSampleStampMs;

    private DisplayZone? _lastCrossingFrom;
    private DisplayZone? _lastCrossingTo;
    private long _lastCrossingStampMs;

    private const int ReverseCrossingLockMs = 120;
    private const double ReverseCrossingReleaseMm = 2.0;

    // Set when we move the cursor ourselves; the resulting event comes straight back through the
    // hook and must not be mistaken for user movement.
    private bool _selfMovePending;
    private Vec2 _selfMoveTarget;

    public ZoneLayout Layout => _layout;

    public RouterOptions Options => _options;

    public DisplayZone? CurrentZone => _zone;

    /// <summary>Continuous cursor position in millimetres. Exposed for the diagnostics view.</summary>
    public Vec2 PhysicalPosition => _physical;

    public long CrossingCount { get; private set; }

    public long RecoveryCount { get; private set; }

    public void SetLayout(ZoneLayout layout)
    {
        _layout = layout;
        Invalidate();
    }

    public void SetOptions(RouterOptions options) => _options = options;

    /// <summary>
    /// Drops the cached position so the next sample re-derives everything from the live cursor.
    /// Called after a display change, a session switch, or any time the engine was paused.
    /// </summary>
    public void Invalidate()
    {
        _synced = false;
        _zone = null;
        _resistance = 0;
        _selfMovePending = false;
        _lastCrossingFrom = null;
        _lastCrossingTo = null;
        _lastCrossingStampMs = 0;
        _lastSampleStampMs = 0;
        _calibrator.Reset();
    }

    public RouterDecision Process(in MouseSample sample)
    {
        if (!_options.AlignCursor || _layout.Count == 0)
        {
            return RouterDecision.PassThrough;
        }

        Vec2 position = sample.Position;

        // Our own correction bouncing back through the hook. Absorb it silently: the state was
        // already updated when the correction was issued.
        if (_selfMovePending)
        {
            _selfMovePending = false;
            if (Math.Abs(position.X - _selfMoveTarget.X) <= 1 && Math.Abs(position.Y - _selfMoveTarget.Y) <= 1)
            {
                return RouterDecision.PassThrough;
            }
        }

        if (!_synced)
        {
            return Resync(position);
        }

        Vec2 pixelDelta = position - _lastPixel;

        // Another application (or a tablet, or a remote desktop client) placed the cursor. Adopt the
        // new position rather than fighting it; correcting a teleport would produce visible fights
        // between the two pieces of software.
        if (sample.Injected)
        {
            return Resync(position);
        }

        // Large physical movement must still go through the strict edge solver. Treating it as a
        // teleport allowed a very fast sample to appear directly inside a blocked display.

        // A normal crossing arrives here with the reported position already inside the neighbouring
        // display, because that is exactly how Windows moves the cursor over a shared edge. So the
        // fact that the position is no longer in the previous zone is not a reason to resynchronise:
        // it is the case this whole class exists to correct. Genuine desynchronisation is caught by
        // the injected flag and the teleport threshold above, and by Invalidate() whenever the
        // display configuration changes.
        DisplayZone source = _zone!;

        Vec2 intendedPixelDelta = ApplyRawInputAssist(sample, pixelDelta);
        bool assisted = intendedPixelDelta != pixelDelta;

        if (intendedPixelDelta.X == 0 && intendedPixelDelta.Y == 0)
        {
            _lastPixel = position;
            return RouterDecision.PassThrough;
        }

        Vec2 fromMm = _physical;
        Vec2 toMm = fromMm + source.PixelDeltaToMm(intendedPixelDelta);

        // Fast path: still on the same display and nothing was reconstructed. This is the
        // overwhelming majority of events and costs one rectangle test.
        if (!assisted && source.PhysicalBounds.Contains(toMm))
        {
            _physical = toMm;
            _lastPixel = position;
            _resistance = 0;
            return RouterDecision.PassThrough;
        }

        DisplayZone target = Solve(source, fromMm, toMm, out Vec2 landingMm);
        long elapsedMs = _lastSampleStampMs <= 0 ? 0 : Math.Max(1, sample.TimestampMs - _lastSampleStampMs);
        _lastSampleStampMs = sample.TimestampMs;
        double speedMmPerSecond = elapsedMs <= 0
            ? 0
            : Vec2.Distance(fromMm, toMm) * 1000.0 / elapsedMs;
        DisplayEdge exitEdge = FindExitEdge(source.PhysicalBounds, toMm);
        double resistanceThresholdMm = _options.ResolveResistanceMm(
            source.StableId,
            exitEdge,
            speedMmPerSecond);

        if (!ReferenceEquals(target, source) &&
            IsReverseCrossingLocked(source, target, fromMm, sample.TimestampMs))
        {
            landingMm = source.PhysicalBounds.ClampInside(fromMm, MmMarginFor(source));
            target = source;
        }

        if (!ReferenceEquals(target, source) &&
            resistanceThresholdMm > 0 &&
            !ResistanceSatisfied(source, landingMm, sample.TimestampMs, resistanceThresholdMm))
        {
            landingMm = source.PhysicalBounds.ClampInside(landingMm, MmMarginFor(source));
            target = source;

            Vec2 heldPixel = ToPixel(target, landingMm);
            _physical = landingMm;

            if (SamePixel(heldPixel, position))
            {
                _lastPixel = position;
                return RouterDecision.PassThrough;
            }

            return Commit(source, target, landingMm, heldPixel, RouterOutcome.Held);
        }

        Vec2 targetPixel = ToPixel(target, landingMm);
        bool crossed = !ReferenceEquals(target, source);

        if (crossed)
        {
            _lastCrossingFrom = source;
            _lastCrossingTo = target;
            _lastCrossingStampMs = sample.TimestampMs;
            CrossingCount++;

            // Pointer sensitivity is per display; the learned raw-to-pixel gain no longer applies.
            _calibrator.Reset();
            _resistance = 0;
        }

        // Where the physical layout is continuous, the corrected position is the one Windows was
        // going to use anyway. Letting the event through is not just an optimisation: an
        // unnecessary SetCursorPos on every crossing would add a round trip through the input stack
        // for no visible benefit.
        if (SamePixel(targetPixel, position))
        {
            _zone = target;
            _physical = landingMm;
            _lastPixel = position;
            return RouterDecision.PassThrough;
        }

        return Commit(source, target, landingMm, targetPixel, crossed ? RouterOutcome.Crossed : RouterOutcome.Adjusted);
    }

    /// <summary>
    /// Chooses the display the movement should land on and the exact millimetre position on it.
    /// </summary>
    private DisplayZone Solve(DisplayZone source, Vec2 fromMm, Vec2 toMm, out Vec2 landingMm)
    {
        // a) The straightforward case: the destination is on a display.
        Vec2 delta = toMm - fromMm;

        DisplayZone? direct = FindPhysical(toMm);
        if (direct is not null &&
            (ReferenceEquals(direct, source) || IsContinuousCrossing(source, direct, fromMm, delta)))
        {
            landingMm = toMm;
            return direct;
        }



        // b) The movement passed over a display but overshot it. Entering that display is what the
        //    user meant, so keep the travel and clamp onto the display that was crossed. This is
        //    what makes fast flicks across a narrow monitor behave.
        DisplayZone? crossed = FirstZoneAlong(source, fromMm, delta);
        if (crossed is not null)
        {
            landingMm = crossed.PhysicalBounds.ClampInside(toMm, MmMarginFor(crossed));
            return crossed;
        }

        // c) Wrap around the desktop when enabled.
        if (_options.Wrap != WrapMode.None)
        {
            Vec2 wrapped = ApplyWrap(toMm);
            if (wrapped != toMm)
            {
                DisplayZone? wrapZone = FindPhysical(wrapped) ?? _layout.NearestByPhysical(wrapped);
                if (wrapZone is not null)
                {
                    landingMm = wrapZone.PhysicalBounds.ClampInside(wrapped, MmMarginFor(wrapZone));
                    return wrapZone;
                }
            }
        }

        // d) The movement ended in dead space: a bezel gap, or the notch of an L shaped layout.
        //    Project onto the most plausible display instead of stranding the cursor.
        // No physically continuous edge was crossed. Keep the pointer on the source display instead
        // of allowing a fast sample to jump across a gap or a forbidden section of the border.
        DisplayZone best = source;
        landingMm = best.PhysicalBounds.ClampInside(toMm, MmMarginFor(best));
        return best;
    }

    private DisplayZone? FindPhysical(Vec2 mm)
    {
        IReadOnlyList<DisplayZone> zones = _layout.Zones;
        for (int i = 0; i < zones.Count; i++)
        {
            if (zones[i].PhysicalBounds.Contains(mm))
            {
                return zones[i];
            }
        }

        return null;
    }

    /// <summary>First display the movement segment enters, ignoring the one it started on.</summary>
    private DisplayZone? FirstZoneAlong(DisplayZone source, Vec2 fromMm, Vec2 delta)
    {
        DisplayZone? best = null;
        double bestEntry = double.MaxValue;

        IReadOnlyList<DisplayZone> zones = _layout.Zones;
        for (int i = 0; i < zones.Count; i++)
        {
            DisplayZone zone = zones[i];
            if (ReferenceEquals(zone, source))
            {
                continue;
            }

            if (!RayCast.SegmentIntersectsRect(fromMm, delta, zone.PhysicalBounds, out double entry, out double exit))
            {
                continue;
            }

            if (exit < 0 || entry > 1 || !IsContinuousCrossing(source, zone, fromMm, delta))
            {
                continue;
            }

            double t = Math.Max(entry, 0);
            if (t < bestEntry)
            {
                bestEntry = t;
                best = zone;
            }
        }

        return best;
    }

    /// <summary>
    /// Picks a display for a movement that ended in dead space. Distance decides, but displays that
    /// sit behind the direction of travel are penalised so the cursor does not snap backwards.
    /// </summary>
    private bool IsReverseCrossingLocked(DisplayZone source, DisplayZone target, Vec2 fromMm, long timestampMs)
    {
        if (_lastCrossingFrom is null || _lastCrossingTo is null ||
            !ReferenceEquals(source, _lastCrossingTo) ||
            !ReferenceEquals(target, _lastCrossingFrom) ||
            timestampMs - _lastCrossingStampMs > ReverseCrossingLockMs)
        {
            return false;
        }

        RectD bounds = source.PhysicalBounds;
        double depth = Math.Min(
            Math.Min(fromMm.X - bounds.Left, bounds.Right - fromMm.X),
            Math.Min(fromMm.Y - bounds.Top, bounds.Bottom - fromMm.Y));
        return depth < ReverseCrossingReleaseMm;
    }

    private static bool IsContinuousCrossing(DisplayZone source, DisplayZone target, Vec2 fromMm, Vec2 delta)
    {
        if (!RayCast.SegmentIntersectsRect(fromMm, delta, source.PhysicalBounds, out _, out double sourceExit) ||
            !RayCast.SegmentIntersectsRect(fromMm, delta, target.PhysicalBounds, out double targetEntry, out _))
        {
            return false;
        }

        const double epsilonMm = 0.001;
        return Math.Abs(targetEntry - sourceExit) <= epsilonMm;
    }

    private DisplayZone ChooseFallback(DisplayZone source, Vec2 fromMm, Vec2 toMm)
    {
        Vec2 direction = (toMm - fromMm).Normalized();

        DisplayZone best = source;
        double bestScore = double.MaxValue;

        IReadOnlyList<DisplayZone> zones = _layout.Zones;
        for (int i = 0; i < zones.Count; i++)
        {
            DisplayZone zone = zones[i];
            double score = zone.PhysicalBounds.DistanceSquaredTo(toMm);

            Vec2 toCentre = zone.PhysicalBounds.Center - fromMm;
            double alignment = (toCentre.X * direction.X) + (toCentre.Y * direction.Y);
            if (alignment < 0)
            {
                score *= 4.0;
            }

            // A small bias towards staying put keeps the cursor from hopping across a wide gap when
            // the source display is an equally good match.
            if (ReferenceEquals(zone, source))
            {
                score *= 0.9;
            }

            if (score < bestScore)
            {
                bestScore = score;
                best = zone;
            }
        }

        return best;
    }

    private Vec2 ApplyWrap(Vec2 mm)
    {
        RectD bounds = _layout.PhysicalBounds;
        double x = mm.X;
        double y = mm.Y;

        if (_options.Wrap is WrapMode.Horizontal or WrapMode.Both && bounds.Width > 0)
        {
            if (x < bounds.Left)
            {
                x += bounds.Width;
            }
            else if (x >= bounds.Right)
            {
                x -= bounds.Width;
            }
        }

        if (_options.Wrap is WrapMode.Vertical or WrapMode.Both && bounds.Height > 0)
        {
            if (y < bounds.Top)
            {
                y += bounds.Height;
            }
            else if (y >= bounds.Bottom)
            {
                y -= bounds.Height;
            }
        }

        return new Vec2(x, y);
    }

    private static DisplayEdge FindExitEdge(RectD bounds, Vec2 point)
    {
        double left = Math.Max(0, bounds.Left - point.X);
        double top = Math.Max(0, bounds.Top - point.Y);
        double right = Math.Max(0, point.X - bounds.Right);
        double bottom = Math.Max(0, point.Y - bounds.Bottom);

        double maximum = Math.Max(Math.Max(left, right), Math.Max(top, bottom));
        if (maximum == left) return DisplayEdge.Left;
        if (maximum == right) return DisplayEdge.Right;
        if (maximum == top) return DisplayEdge.Top;
        return DisplayEdge.Bottom;
    }

    /// <summary>
    /// Accumulates how far the pointer has been pushed past a border and reports whether the
    /// configured threshold has been reached. The accumulator decays after a pause so that brushing
    /// the edge repeatedly over a minute does not eventually trigger a crossing on its own.
    /// </summary>
    private bool ResistanceSatisfied(DisplayZone source, Vec2 toMm, long timestampMs, double thresholdMm)
    {
        if (timestampMs - _resistanceStampMs > _options.ResistanceResetMs)
        {
            _resistance = 0;
        }

        _resistanceStampMs = timestampMs;

        RectD bounds = source.PhysicalBounds;
        Vec2 clamped = bounds.ClosestPoint(toMm);
        double outward = Vec2.Distance(toMm, clamped);

        // Movement parallel to the border does not build resistance.
        if (outward <= 0)
        {
            return true;
        }

        _resistance += outward;

        if (_resistance >= thresholdMm)
        {
            _resistance = 0;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reconstructs the movement Windows swallowed. When the pointer is pinned against the outer
    /// edge of the desktop the hook keeps firing with an unchanged position; without this, crossing
    /// into a physically adjacent but pixel-offset display would be impossible.
    /// </summary>
    private Vec2 ApplyRawInputAssist(in MouseSample sample, Vec2 pixelDelta)
    {
        if (!_options.UseRawInputAssist || !sample.HasRawDelta)
        {
            return pixelDelta;
        }

        Vec2 raw = sample.RawDelta;
        if (raw.X == 0 && raw.Y == 0)
        {
            return pixelDelta;
        }

        bool blockedX = IsAxisBlocked(raw.X, horizontal: true) && Math.Abs(pixelDelta.X) < 1;
        bool blockedY = IsAxisBlocked(raw.Y, horizontal: false) && Math.Abs(pixelDelta.Y) < 1;

        if (!blockedX && !blockedY)
        {
            _calibrator.Observe(raw, pixelDelta);
            return pixelDelta;
        }

        Vec2 predicted = _calibrator.Predict(raw);
        return new Vec2(
            blockedX ? predicted.X : pixelDelta.X,
            blockedY ? predicted.Y : pixelDelta.Y);
    }

    /// <summary>
    /// True when the pixel immediately beyond the cursor in the given direction belongs to no
    /// display, which is exactly the condition under which Windows stops advancing the pointer.
    /// </summary>
    private bool IsAxisBlocked(double rawComponent, bool horizontal)
    {
        if (Math.Abs(rawComponent) < 1)
        {
            return false;
        }

        double step = rawComponent > 0 ? 2 : -2;
        Vec2 probe = horizontal
            ? new Vec2(_lastPixel.X + step, _lastPixel.Y)
            : new Vec2(_lastPixel.X, _lastPixel.Y + step);

        return _layout.FindByPixel(probe) is null;
    }

    /// <summary>
    /// Re-derives the whole state from the live cursor position. Also the recovery path when the
    /// cursor is found outside every display.
    /// </summary>
    private RouterDecision Resync(Vec2 position)
    {
        DisplayZone? zone = _layout.FindByPixel(position);

        if (zone is null)
        {
            zone = _layout.NearestByPixel(position);
            if (zone is null)
            {
                return RouterDecision.PassThrough;
            }

            if (_options.PreventCursorLoss)
            {
                Vec2 rescued = zone.PixelBounds.ClampInside(position, PixelClampMargin);
                rescued = new Vec2(Math.Round(rescued.X), Math.Round(rescued.Y));

                _zone = zone;
                _physical = zone.PixelToMm(rescued);
                _lastPixel = rescued;
                _synced = true;
                _resistance = 0;
                _calibrator.Reset();
                RecoveryCount++;

                _selfMovePending = true;
                _selfMoveTarget = rescued;

                return new RouterDecision
                {
                    Handled = true,
                    TargetPixel = rescued,
                    Outcome = RouterOutcome.Recovered,
                    FromZone = null,
                    ToZone = zone,
                };
            }
        }

        _zone = zone;
        _physical = zone.PixelToMm(position);
        _lastPixel = position;
        _synced = true;
        _resistance = 0;
        _calibrator.Reset();

        return RouterDecision.PassThrough;
    }

    private RouterDecision Commit(
        DisplayZone source,
        DisplayZone target,
        Vec2 landingMm,
        Vec2 targetPixel,
        RouterOutcome outcome)
    {
        _zone = target;

        // The millimetre position is stored unrounded on purpose. Keeping the sub-pixel remainder is
        // what stops repeated crossings from walking the cursor away from where it started.
        _physical = landingMm;
        _lastPixel = targetPixel;

        _selfMovePending = true;
        _selfMoveTarget = targetPixel;

        return new RouterDecision
        {
            Handled = true,
            TargetPixel = targetPixel,
            Outcome = outcome,
            FromZone = source,
            ToZone = target,
        };
    }

    private static Vec2 ToPixel(DisplayZone zone, Vec2 mm)
    {
        Vec2 exact = zone.MmToPixel(mm);
        var rounded = new Vec2(Math.Round(exact.X), Math.Round(exact.Y));
        return zone.PixelBounds.ClampInside(rounded, PixelClampMargin);
    }

    private static double MmMarginFor(DisplayZone zone) =>
        Math.Max(zone.MmPerPixel.X, zone.MmPerPixel.Y) * PixelClampMargin;

    private static bool SamePixel(Vec2 a, Vec2 b) =>
        Math.Abs(a.X - b.X) < 0.5 && Math.Abs(a.Y - b.Y) < 0.5;
}
