using CaYaScreenBridge.Core.Algorithm;
using CaYaScreenBridge.Core.Config;
using Xunit;

namespace CaYaScreenBridge.Core.Tests;

public class EnginePolicyTests
{
    private static AppConfig Config() => new();

    [Fact]
    public void NormalForegroundCorrectsCursorButWindowScalingIsOffByDefault()
    {
        PolicyDecision decision = EnginePolicy.Evaluate(
            Config(),
            new ForegroundState("explorer", ForegroundKind.Normal, false, false));

        Assert.True(decision.CorrectCursor);
        Assert.False(decision.ScaleWindows);
    }

    /// <summary>
    /// Anti-cheat overrides everything, including an explicit rule asking for correction. Injected
    /// cursor movement can be read as automation, and the cost of getting that wrong falls on the
    /// user's account rather than on this application.
    /// </summary>
    [Fact]
    public void AntiCheatOverridesEvenAnExplicitRuleWhenEnabled()
    {
        AppConfig config = Config();
        config.Games.PauseForAntiCheat = true;
        config.Rules.Add(new AppRule { Process = "game", Action = RuleAction.Correct });

        PolicyDecision decision = EnginePolicy.Evaluate(
            config,
            new ForegroundState("game", ForegroundKind.Normal, false, AntiCheatRunning: true));

        Assert.False(decision.CorrectCursor);
        Assert.False(decision.ScaleWindows);
        Assert.Equal("anti-cheat", decision.Reason);
    }

    [Fact]
    public void ExclusiveFullScreenKeepsCorrectingByDefault()
    {
        PolicyDecision decision = EnginePolicy.Evaluate(
            Config(),
            new ForegroundState("game", ForegroundKind.ExclusiveFullScreen, false, false));

        Assert.True(decision.CorrectCursor);
        Assert.False(decision.ScaleWindows);
        Assert.Equal("normal", decision.Reason);
    }

    [Fact]
    public void BorderlessFullScreenKeepsTheCursorCorrectedButNeverResizesTheWindow()
    {
        PolicyDecision decision = EnginePolicy.Evaluate(
            Config(),
            new ForegroundState("game", ForegroundKind.BorderlessFullScreen, false, false));

        Assert.True(decision.CorrectCursor);
        Assert.False(decision.ScaleWindows);
    }

    [Fact]
    public void PassThroughRuleDisablesEverythingForThatProcess()
    {
        AppConfig config = Config();
        config.Rules.Add(new AppRule { Process = "photoshop", Action = RuleAction.PassThrough });

        PolicyDecision matched = EnginePolicy.Evaluate(
            config,
            new ForegroundState("photoshop", ForegroundKind.Normal, false, false));

        PolicyDecision other = EnginePolicy.Evaluate(
            config,
            new ForegroundState("notepad", ForegroundKind.Normal, false, false));

        Assert.False(matched.CorrectCursor);
        Assert.True(other.CorrectCursor);
    }

    [Fact]
    public void PrefixRulesMatchOnAWildcard()
    {
        AppConfig config = Config();
        config.Rules.Add(new AppRule { Process = "vmware*", Action = RuleAction.NoWindowScaling });

        PolicyDecision decision = EnginePolicy.Evaluate(
            config,
            new ForegroundState("vmware-vmx", ForegroundKind.Normal, false, false));

        Assert.True(decision.CorrectCursor);
        Assert.False(decision.ScaleWindows);
    }

    [Fact]
    public void DisabledRulesAreIgnored()
    {
        AppConfig config = Config();
        config.Rules.Add(new AppRule { Process = "notepad", Action = RuleAction.PassThrough, Enabled = false });

        PolicyDecision decision = EnginePolicy.Evaluate(
            config,
            new ForegroundState("notepad", ForegroundKind.Normal, false, false));

        Assert.True(decision.CorrectCursor);
    }

    [Fact]
    public void TheMasterSwitchWinsOverEverything()
    {
        AppConfig config = Config();
        config.Enabled = false;

        PolicyDecision decision = EnginePolicy.Evaluate(
            config,
            new ForegroundState("explorer", ForegroundKind.Normal, false, false));

        Assert.False(decision.CorrectCursor);
        Assert.False(decision.ScaleWindows);
    }
}
