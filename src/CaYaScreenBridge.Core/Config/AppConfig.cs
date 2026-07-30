namespace CaYaScreenBridge.Core.Config;

public enum WrapMode
{
    None = 0,
    Horizontal = 1,
    Vertical = 2,
    Both = 3,
}

/// <summary>How the window drag correction behaves when a window crosses a DPI boundary.</summary>
public enum DragScalingMode
{
    /// <summary>Never touch dragged windows.</summary>
    Off = 0,

    /// <summary>Correct once, when the mouse button is released. No flicker, slightly less seamless.</summary>
    OnDrop = 1,

    /// <summary>Rescale live as the window crosses the boundary, keeping the grab point under the cursor.</summary>
    Live = 2,
}

/// <summary>What the engine should do while a given application is in the foreground.</summary>
public enum RuleAction
{
    /// <summary>Normal cursor correction.</summary>
    Correct = 0,

    /// <summary>Leave the cursor completely alone.</summary>
    PassThrough = 1,

    /// <summary>Correct the cursor but never rescale that application's windows.</summary>
    NoWindowScaling = 2,
}

public sealed class AppConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public bool Enabled { get; set; } = true;

    public GeneralSettings General { get; set; } = new();

    public TransitionSettings Transition { get; set; } = new();

    public DragSettings Drag { get; set; } = new();

    public GameSettings Games { get; set; } = new();

    public List<AppRule> Rules { get; set; } = new();

    public List<LayoutProfile> Profiles { get; set; } = new();

    public LayoutProfile GetOrCreateProfile(string id, string name)
    {
        LayoutProfile? existing = Profiles.FirstOrDefault(p =>
            string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.LastUsedUtc = DateTimeOffset.UtcNow;
            return existing;
        }

        var created = new LayoutProfile { Id = id, Name = name };
        Profiles.Add(created);
        return created;
    }
}

public sealed class GeneralSettings
{
    public bool StartWithWindows { get; set; } = true;

    /// <summary>Register the startup entry as an elevated scheduled task instead of the Run key.</summary>
    public bool StartElevated { get; set; } = true;

    public bool StartMinimised { get; set; } = true;

    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>UI language: "auto", "tr" or "en".</summary>
    public string Language { get; set; } = "auto";

    /// <summary>
    /// Raise <c>HKCU\Control Panel\Desktop\LowLevelHooksTimeout</c> so Windows stops silently
    /// evicting the mouse hook when the machine stalls. Takes effect after sign out.
    /// </summary>
    public bool RaiseHookTimeout { get; set; } = true;

    public bool VerboseLogging { get; set; }
}

public sealed class TransitionSettings
{
    /// <summary>Master switch for the physical-space cursor mapping.</summary>
    public bool AlignCursor { get; set; } = true;

    /// <summary>
    /// Scale pointer sensitivity per display so a given hand movement covers the same physical
    /// distance on every screen.
    /// </summary>
    public bool NormalisePointerSpeed { get; set; }

    /// <summary>Millimetres the pointer must be pushed into a border before it crosses.</summary>
    public double BorderResistanceMm { get; set; }

    /// <summary>Resistance build up is discarded after this long without an outward push.</summary>
    public int ResistanceResetMs { get; set; } = 400;

    public WrapMode Wrap { get; set; } = WrapMode.None;

    /// <summary>
    /// Reconstruct the intended movement from raw HID deltas when Windows pins the cursor against
    /// the edge of the desktop. This is what makes crossings into a physically offset neighbour
    /// reliable instead of requiring the user to find the exact overlapping pixel band.
    /// </summary>
    public bool UseRawInputAssist { get; set; } = true;

    /// <summary>
    /// Keep the cursor inside the union of all displays instead of letting Windows decide. Prevents
    /// the pointer from being stranded in the dead space of an L shaped arrangement.
    /// </summary>
    public bool PreventCursorLoss { get; set; } = true;
}

public sealed class DragSettings
{
    public DragScalingMode Mode { get; set; } = DragScalingMode.Live;

    /// <summary>Keep the point the user grabbed under the cursor after the window is rescaled.</summary>
    public bool PreserveGrabPoint { get; set; } = true;

    /// <summary>Minimum milliseconds between two live rescale operations on the same window.</summary>
    public int LiveThrottleMs { get; set; } = 40;

    /// <summary>
    /// Skip windows that declare themselves DPI unaware. Windows bitmap stretches those, and
    /// resizing them from outside produces blurry, mispositioned results.
    /// </summary>
    public bool SkipDpiUnawareWindows { get; set; } = true;

    /// <summary>Never resize a maximised window; only reposition it.</summary>
    public bool SkipMaximisedWindows { get; set; } = true;
}

public sealed class GameSettings
{
    /// <summary>Stop correcting while an exclusive full screen application is running.</summary>
    public bool PauseInExclusiveFullScreen { get; set; } = true;

    /// <summary>Keep correcting in borderless full screen, but never rescale those windows.</summary>
    public bool CorrectInBorderlessFullScreen { get; set; } = true;

    /// <summary>
    /// Suspend everything while a known anti-cheat service is running. Injected cursor movement can
    /// be read as automation by kernel anti-cheat, so the safe default is to stay out of the way.
    /// </summary>
    public bool PauseForAntiCheat { get; set; } = true;

    public List<string> AntiCheatProcesses { get; set; } = new()
    {
        "EasyAntiCheat",
        "EasyAntiCheat_EOS",
        "BEService",
        "BEDaisy",
        "vgc",
        "vgtray",
        "FACEIT",
        "mcc-service",
    };
}

public sealed class AppRule
{
    /// <summary>Process name without extension, case insensitive. Supports a trailing '*'.</summary>
    public string Process { get; set; } = string.Empty;

    public RuleAction Action { get; set; } = RuleAction.PassThrough;

    public bool Enabled { get; set; } = true;

    public string? Note { get; set; }

    public bool Matches(string processName)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(Process) || string.IsNullOrEmpty(processName))
        {
            return false;
        }

        if (Process.EndsWith('*'))
        {
            return processName.StartsWith(Process[..^1], StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(Process, processName, StringComparison.OrdinalIgnoreCase);
    }
}
