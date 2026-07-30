using CaYaScreenBridge.Core.Config;

namespace CaYaScreenBridge.Core.Algorithm;

public enum ForegroundKind
{
    Normal = 0,

    /// <summary>A window covering exactly one display with no frame: borderless full screen.</summary>
    BorderlessFullScreen = 1,

    /// <summary>Direct3D exclusive full screen, or Windows reporting presentation mode.</summary>
    ExclusiveFullScreen = 2,
}

/// <summary>
/// What the platform layer observed about the foreground application.
///
/// A reference type rather than a struct so the engine can publish it through a volatile field: the
/// hook callback has to read the current state without taking a lock, and a struct cannot be torn
/// safely across threads.
/// </summary>
public sealed record ForegroundState(
    string ProcessName,
    ForegroundKind Kind,
    bool IsElevated,
    bool AntiCheatRunning)
{
    public static readonly ForegroundState Unknown = new(string.Empty, ForegroundKind.Normal, false, false);
}

/// <summary>
/// The decision the policy reached for the current foreground state. Reference type for the same
/// reason as <see cref="ForegroundState"/>.
/// </summary>
public sealed record PolicyDecision(
    bool CorrectCursor,
    bool ScaleWindows,
    string Reason)
{
    public static readonly PolicyDecision Full = new(true, true, "normal");
}

/// <summary>
/// Decides how much the engine may interfere, given what is in the foreground.
///
/// The order matters. Anti-cheat comes first because injected cursor movement can be read as
/// automation and get a player banned, so that check overrides everything including an explicit
/// per application rule. Exclusive full screen comes next: the pointer is already confined to one
/// display, so correcting it has no benefit and some risk.
/// </summary>
public static class EnginePolicy
{
    public static PolicyDecision Evaluate(AppConfig config, ForegroundState foreground)
    {
        if (!config.Enabled)
        {
            return new PolicyDecision(false, false, "disabled");
        }

        if (config.Games.PauseForAntiCheat && foreground.AntiCheatRunning)
        {
            return new PolicyDecision(false, false, "anti-cheat");
        }

        if (foreground.Kind == ForegroundKind.ExclusiveFullScreen && config.Games.PauseInExclusiveFullScreen)
        {
            return new PolicyDecision(false, false, "exclusive-fullscreen");
        }

        AppRule? rule = FindRule(config, foreground.ProcessName);
        if (rule is not null)
        {
            return rule.Action switch
            {
                RuleAction.PassThrough => new PolicyDecision(false, false, $"rule:{rule.Process}"),
                RuleAction.NoWindowScaling => new PolicyDecision(
                    config.Transition.AlignCursor, false, $"rule:{rule.Process}"),
                _ => Normal(config, foreground),
            };
        }

        return Normal(config, foreground);
    }

    private static PolicyDecision Normal(AppConfig config, ForegroundState foreground)
    {
        bool correct = config.Transition.AlignCursor;
        bool scale = config.Drag.Mode != DragScalingMode.Off;

        if (foreground.Kind == ForegroundKind.BorderlessFullScreen)
        {
            correct = correct && config.Games.CorrectInBorderlessFullScreen;

            // Resizing a borderless full screen window would take the game out of full screen.
            scale = false;
            return new PolicyDecision(correct, scale, "borderless-fullscreen");
        }

        return new PolicyDecision(correct, scale, "normal");
    }

    public static AppRule? FindRule(AppConfig config, string processName)
    {
        if (string.IsNullOrEmpty(processName))
        {
            return null;
        }

        List<AppRule> rules = config.Rules;
        for (int i = 0; i < rules.Count; i++)
        {
            if (rules[i].Matches(processName))
            {
                return rules[i];
            }
        }

        return null;
    }
}
