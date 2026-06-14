using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

/// <summary>Tests for <see cref="PauseRules"/>: pure pause-eligibility logic shared by App, Overlay, Service. Cover happy path, every <see cref="PauseBlock"/> reason, priority order, threshold boundaries, budget arithmetic.</summary>
public class PauseRulesTests
{
    /// <summary><see cref="PauseState"/> where every condition met, so <see cref="PauseRules.CanPause"/> returns <see cref="PauseBlock.None"/>; tests use <c>with</c> to violate exactly one.</summary>
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

    // ----- blocking reasons --------------------------------------

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

    // ----- Boundary conditions ----------------------------------------------

    [Fact]
    public void Allows_when_remaining_time_exactly_at_threshold() =>
        Assert.Equal(PauseBlock.None,
            PauseRules.CanPause(Ready() with { RemainingSeconds = PauseRules.MinPausableRemainingSeconds }));

    [Fact]
    public void Blocks_when_remaining_time_one_below_threshold() =>
        Assert.Equal(PauseBlock.TimeTooLow,
            PauseRules.CanPause(Ready() with { RemainingSeconds = PauseRules.MinPausableRemainingSeconds - 1 }));

    [Fact]
    public void Blocks_when_budget_used_exceeds_total() =>
        Assert.Equal(PauseBlock.BudgetExhausted,
            PauseRules.CanPause(Ready() with { DailyBudgetSeconds = 2700, PauseUsedSeconds = 5000 }));

    [Fact]
    public void Allows_when_only_one_second_of_budget_remains() =>
        Assert.Equal(PauseBlock.None,
            PauseRules.CanPause(Ready() with { DailyBudgetSeconds = 2700, PauseUsedSeconds = 2699 }));

    [Fact]
    public void Allows_when_cooldown_has_exactly_elapsed()
    {
        // Gap == CooldownSeconds no longer inside cooldown window
        var s = Ready() with { LastPauseEndUnix = 9_100, NowUnix = 10_000, CooldownSeconds = 900 };
        Assert.Equal(PauseBlock.None, PauseRules.CanPause(s));
    }

    [Fact]
    public void Blocks_one_second_before_cooldown_elapses()
    {
        // Gap == CooldownSeconds - 1 still inside cooldown window
        var s = Ready() with { LastPauseEndUnix = 9_101, NowUnix = 10_000, CooldownSeconds = 900 };
        Assert.Equal(PauseBlock.Cooldown, PauseRules.CanPause(s));
    }

    [Fact]
    public void Allows_when_session_active_exactly_meets_minimum() =>
        Assert.Equal(PauseBlock.None,
            PauseRules.CanPause(Ready() with { SessionActiveSeconds = 600, MinActiveSeconds = 600 }));

    [Fact]
    public void Blocks_one_second_before_min_active_time() =>
        Assert.Equal(PauseBlock.MinActiveTimeNotMet,
            PauseRules.CanPause(Ready() with { SessionActiveSeconds = 599, MinActiveSeconds = 600 }));

    // ----- Cooldown "no previous pause" + clock-skew -------------

    [Fact]
    public void No_cooldown_when_no_previous_pause() =>
        // LastPauseEndUnix == 0 means "no pause yet": skip cooldown
        Assert.Equal(PauseBlock.None,
            PauseRules.CanPause(Ready() with { LastPauseEndUnix = 0, NowUnix = 10, CooldownSeconds = 900 }));

    [Fact]
    public void No_cooldown_when_last_pause_timestamp_is_negative() =>
        // non-positive timestamp treated as "no pause yet"
        Assert.Equal(PauseBlock.None,
            PauseRules.CanPause(Ready() with { LastPauseEndUnix = -5, NowUnix = 10_000, CooldownSeconds = 900 }));

    [Fact]
    public void Backwards_clock_keeps_blocking_with_cooldown()
    {
        // recorded pause + wall clock moved backwards (negative gap) = child rolling clock back to escape cooldown; fail closed, keep blocking until enough real time elapses
        var s = Ready() with { LastPauseEndUnix = 10_000, NowUnix = 9_000, CooldownSeconds = 900 };
        Assert.Equal(PauseBlock.Cooldown, PauseRules.CanPause(s));
    }

    // ----- Priority order -----------------------------------------

    [Fact]
    public void Disabled_takes_priority_over_every_other_reason()
    {
        // violate every condition at once; Disabled checked first
        var s = Ready() with
        {
            Enabled = false,
            RemainingSeconds = 0,
            PauseUsedSeconds = 9999,
            LastPauseEndUnix = 9_999,
            SessionActiveSeconds = 0,
        };
        Assert.Equal(PauseBlock.Disabled, PauseRules.CanPause(s));
    }

    [Fact]
    public void TimeTooLow_takes_priority_over_budget_and_cooldown()
    {
        var s = Ready() with
        {
            RemainingSeconds = 0,
            PauseUsedSeconds = 9999,
            LastPauseEndUnix = 9_999,
            SessionActiveSeconds = 0,
        };
        Assert.Equal(PauseBlock.TimeTooLow, PauseRules.CanPause(s));
    }

    [Fact]
    public void BudgetExhausted_takes_priority_over_cooldown_and_min_active()
    {
        var s = Ready() with
        {
            PauseUsedSeconds = 9999,
            LastPauseEndUnix = 9_999,
            SessionActiveSeconds = 0,
        };
        Assert.Equal(PauseBlock.BudgetExhausted, PauseRules.CanPause(s));
    }

    [Fact]
    public void Cooldown_takes_priority_over_min_active_time()
    {
        var s = Ready() with { LastPauseEndUnix = 9_999, NowUnix = 10_000, SessionActiveSeconds = 0 };
        Assert.Equal(PauseBlock.Cooldown, PauseRules.CanPause(s));
    }

    // ----- RemainingBudget ----------------------------------------

    [Theory]
    [InlineData(2700, 0, 2700)]    // nothing used yet
    [InlineData(2700, 600, 2100)]  // partially used
    [InlineData(2700, 2700, 0)]    // exactly exhausted
    [InlineData(2700, 3000, 0)]    // over-spent, clamp to zero
    [InlineData(0, 0, 0)]          // no budget configured
    public void RemainingBudget_never_negative(int budget, int used, int expected) =>
        Assert.Equal(expected, PauseRules.RemainingBudget(budget, used));

    // ----- MaxPauseDuration ---------------------------------------

    [Fact]
    public void MaxPauseDuration_is_capped_by_budget() =>
        // remaining budget (300) smaller than single-pause cap (1200)
        Assert.Equal(300, PauseRules.MaxPauseDuration(1200, 2700, 2400));

    [Fact]
    public void MaxPauseDuration_is_capped_by_single_pause_limit() =>
        // single-pause cap (600) smaller than remaining budget (2700)
        Assert.Equal(600, PauseRules.MaxPauseDuration(600, 2700, 0));

    [Fact]
    public void MaxPauseDuration_is_zero_when_budget_exhausted() =>
        Assert.Equal(0, PauseRules.MaxPauseDuration(1200, 2700, 2700));

    [Fact]
    public void MaxPauseDuration_is_zero_when_over_budget() =>
        // over-spent budget must not produce negative duration
        Assert.Equal(0, PauseRules.MaxPauseDuration(1200, 2700, 5000));

    [Theory]
    [InlineData(0, 2700, 0, 0)]      // single-pause cap of zero
    [InlineData(900, 1800, 900, 900)] // cap and remaining budget equal
    public void MaxPauseDuration_handles_edge_inputs(int maxSingle, int budget, int used, int expected) =>
        Assert.Equal(expected, PauseRules.MaxPauseDuration(maxSingle, budget, used));
}
