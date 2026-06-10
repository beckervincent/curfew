using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

public class PauseRulesTests
{
    private static PauseState Ready() => new(
        Enabled: true,
        RemainingSeconds: 3600,
        PauseUsedSeconds: 0,
        DailyBudgetSeconds: 2700,
        LastPauseEndUnix: 0,
        NowUnix: 10_000,
        CooldownSeconds: 900,
        SessionActiveSeconds: 1200,
        MinActiveSeconds: 600);

    [Fact]
    public void Allows_when_all_conditions_met() =>
        Assert.Equal(PauseBlock.None, PauseRules.CanPause(Ready()));

    [Fact]
    public void Blocks_when_disabled() =>
        Assert.Equal(PauseBlock.Disabled, PauseRules.CanPause(Ready() with { Enabled = false }));

    [Fact]
    public void Blocks_when_time_too_low() =>
        Assert.Equal(PauseBlock.TimeTooLow, PauseRules.CanPause(Ready() with { RemainingSeconds = 30 }));

    [Fact]
    public void Blocks_when_budget_exhausted() =>
        Assert.Equal(PauseBlock.BudgetExhausted,
            PauseRules.CanPause(Ready() with { PauseUsedSeconds = 2700 }));

    [Fact]
    public void Blocks_during_cooldown() =>
        Assert.Equal(PauseBlock.Cooldown,
            PauseRules.CanPause(Ready() with { LastPauseEndUnix = 9_500, NowUnix = 10_000 }));

    [Fact]
    public void Blocks_before_min_active_time() =>
        Assert.Equal(PauseBlock.MinActiveTimeNotMet,
            PauseRules.CanPause(Ready() with { SessionActiveSeconds = 100 }));

    [Theory]
    [InlineData(2700, 0, 2700)]
    [InlineData(2700, 600, 2100)]
    [InlineData(2700, 3000, 0)]
    public void RemainingBudget_never_negative(int budget, int used, int expected) =>
        Assert.Equal(expected, PauseRules.RemainingBudget(budget, used));

    [Fact]
    public void MaxPauseDuration_is_capped_by_budget() =>
        Assert.Equal(300, PauseRules.MaxPauseDuration(1200, 2700, 2400));
}
