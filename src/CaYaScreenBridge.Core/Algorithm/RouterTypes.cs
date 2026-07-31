using CaYaScreenBridge.Core.Config;
using CaYaScreenBridge.Core.Geometry;
using CaYaScreenBridge.Core.Model;

namespace CaYaScreenBridge.Core.Algorithm;

/// <summary>One mouse movement as observed by the platform layer.</summary>
public readonly struct MouseSample
{
    /// <summary>Cursor position in physical pixels, as Windows reports it.</summary>
    public required Vec2 Position { get; init; }

    /// <summary>Raw HID movement accumulated since the previous sample, in device units.</summary>
    public Vec2 RawDelta { get; init; }

    public bool HasRawDelta { get; init; }

    /// <summary>Monotonic timestamp in milliseconds.</summary>
    public required long TimestampMs { get; init; }

    /// <summary>Set when Windows flagged the event as synthetic (SendInput, remote desktop, ...).</summary>
    public bool Injected { get; init; }

    /// <summary>Set while a mouse button is held, which is what makes a move a drag.</summary>
    public bool ButtonDown { get; init; }
}

public enum RouterOutcome
{
    /// <summary>Nothing to do; let the event through untouched.</summary>
    PassThrough = 0,

    /// <summary>The cursor moved within one display but needed a correction.</summary>
    Adjusted = 1,

    /// <summary>The cursor moved from one display to another.</summary>
    Crossed = 2,

    /// <summary>The cursor was held at a border by the resistance setting.</summary>
    Held = 3,

    /// <summary>The cursor was recovered from a position that maps to no display.</summary>
    Recovered = 4,
}

public readonly struct RouterDecision
{
    public static readonly RouterDecision PassThrough = new()
    {
        Outcome = RouterOutcome.PassThrough,
        Handled = false,
    };

    /// <summary>
    /// True when the caller must move the cursor to <see cref="TargetPixel"/> and swallow the
    /// original event so Windows does not also apply it.
    /// </summary>
    public bool Handled { get; init; }

    public Vec2 TargetPixel { get; init; }

    public RouterOutcome Outcome { get; init; }

    public DisplayZone? FromZone { get; init; }

    public DisplayZone? ToZone { get; init; }
}

/// <summary>
/// An immutable snapshot of the transition settings. The router is called from the low level hook
/// callback, so it reads a frozen options object rather than live mutable settings.
/// </summary>
public sealed class EdgeResistanceOption
{
    public bool UseCustomResistance { get; init; }
    public double ResistanceMm { get; init; }
    public bool? SpeedAdaptive { get; init; }
}

public sealed class DisplayResistanceOption
{
    public required EdgeResistanceOption Left { get; init; }
    public required EdgeResistanceOption Top { get; init; }
    public required EdgeResistanceOption Right { get; init; }
    public required EdgeResistanceOption Bottom { get; init; }

    public EdgeResistanceOption For(DisplayEdge edge) => edge switch
    {
        DisplayEdge.Left => Left,
        DisplayEdge.Top => Top,
        DisplayEdge.Right => Right,
        _ => Bottom,
    };
}

public sealed class RouterOptions
{
    public static readonly RouterOptions Default = FromSettings(new TransitionSettings());

    public bool AlignCursor { get; init; } = true;

    public double BorderResistanceMm { get; init; }

    public int ResistanceResetMs { get; init; } = 400;

    public bool SpeedAdaptiveResistance { get; init; }

    public double ResistanceSpeedReferenceMmPerSecond { get; init; } = 500;

    public double MinimumResistanceFactor { get; init; } = 0.25;

    public IReadOnlyDictionary<string, DisplayResistanceOption> DisplayResistance { get; init; } =
        new Dictionary<string, DisplayResistanceOption>(StringComparer.OrdinalIgnoreCase);

    public WrapMode Wrap { get; init; } = WrapMode.None;

    public bool UseRawInputAssist { get; init; } = true;

    public bool PreventCursorLoss { get; init; } = true;

    /// <summary>
    /// Retained for configuration compatibility. Fast non-injected movement is always routed
    /// through physical-edge validation; only explicitly injected events are treated as teleports.
    /// </summary>
    public double TeleportThresholdPx { get; init; } = 600;

    public static RouterOptions FromSettings(TransitionSettings settings) => new()
    {
        AlignCursor = settings.AlignCursor,
        BorderResistanceMm = settings.BorderResistanceMm,
        ResistanceResetMs = settings.ResistanceResetMs,
        SpeedAdaptiveResistance = settings.SpeedAdaptiveResistance,
        ResistanceSpeedReferenceMmPerSecond = settings.ResistanceSpeedReferenceMmPerSecond,
        MinimumResistanceFactor = settings.MinimumResistanceFactor,
        DisplayResistance = settings.DisplayResistance
            .Where(d => !string.IsNullOrWhiteSpace(d.StableId))
            .GroupBy(d => d.StableId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => CloneDisplayResistance(g.Last()),
                StringComparer.OrdinalIgnoreCase),
        Wrap = settings.Wrap,
        UseRawInputAssist = settings.UseRawInputAssist,
        PreventCursorLoss = settings.PreventCursorLoss,
    };

    public double ResolveResistanceMm(string stableId, DisplayEdge edge, double speedMmPerSecond)
    {
        double resistance = BorderResistanceMm;
        bool adaptive = SpeedAdaptiveResistance;

        if (DisplayResistance.TryGetValue(stableId, out DisplayResistanceOption? display))
        {
            EdgeResistanceOption option = display.For(edge);
            if (option.UseCustomResistance)
            {
                resistance = option.ResistanceMm;
            }

            if (option.SpeedAdaptive.HasValue)
            {
                adaptive = option.SpeedAdaptive.Value;
            }
        }

        resistance = Math.Clamp(resistance, 0, 200);
        if (!adaptive || resistance <= 0 || speedMmPerSecond <= ResistanceSpeedReferenceMmPerSecond)
        {
            return resistance;
        }

        double factor = ResistanceSpeedReferenceMmPerSecond / Math.Max(1, speedMmPerSecond);
        factor = Math.Clamp(factor, MinimumResistanceFactor, 1.0);
        return resistance * factor;
    }

    private static DisplayResistanceOption CloneDisplayResistance(DisplayResistanceSettings source) => new()
    {
        Left = CloneEdge(source.Left),
        Top = CloneEdge(source.Top),
        Right = CloneEdge(source.Right),
        Bottom = CloneEdge(source.Bottom),
    };

    private static EdgeResistanceOption CloneEdge(EdgeResistanceSettings source) => new()
    {
        UseCustomResistance = source.UseCustomResistance,
        ResistanceMm = source.ResistanceMm,
        SpeedAdaptive = source.SpeedAdaptive,
    };
}
