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
public sealed class RouterOptions
{
    public static readonly RouterOptions Default = FromSettings(new TransitionSettings());

    public bool AlignCursor { get; init; } = true;

    public double BorderResistanceMm { get; init; }

    public int ResistanceResetMs { get; init; } = 400;

    public WrapMode Wrap { get; init; } = WrapMode.None;

    public bool UseRawInputAssist { get; init; } = true;

    public bool PreventCursorLoss { get; init; } = true;

    /// <summary>
    /// A single event moving further than this is not a hand movement; it is another application
    /// teleporting the cursor. The router adopts such a position instead of trying to correct it.
    /// </summary>
    public double TeleportThresholdPx { get; init; } = 600;

    public static RouterOptions FromSettings(TransitionSettings settings) => new()
    {
        AlignCursor = settings.AlignCursor,
        BorderResistanceMm = settings.BorderResistanceMm,
        ResistanceResetMs = settings.ResistanceResetMs,
        Wrap = settings.Wrap,
        UseRawInputAssist = settings.UseRawInputAssist,
        PreventCursorLoss = settings.PreventCursorLoss,
    };
}
