using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

/// <summary>
/// Unit tests for <see cref="PauseRules"/>: the pure pause-eligibility logic shared
/// by the App, Overlay and Service. The tests cover the happy path, every distinct
/// <see cref="PauseBlock"/> reason, the fixed priority order in which reasons are
/// reported, boundary conditions around each threshold, and the budget arithmetic
/// helpers.
/// </summary>
public class PauseRulesTests
{
    /// <summary>
    /// A <see cref="PauseState"/> in which every condition is satisfied, so
    /// <see cref="PauseRules.CanPause"/> returns <see cref="PauseBlock.None"/>.
    /// Individual tests use <c>with</c> expressions to violate exactly one condition.
    /// </summary>
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

    // ----- Individual blocking reasons --------------------------------------

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
        // Gap == CooldownSeconds is no longer inside the cooldown window.
        var s = Ready() with { LastPauseEndUnix = 9_100, NowUnix = 10_000, CooldownSeconds = 900 };
        Assert.Equal(PauseBlock.None, PauseRules.CanPause(s));
    }

    [Fact]
    public void Blocks_one_second_before_cooldown_elapses()
    {
        // Gap == CooldownSeconds - 1 is still inside the cooldown window.
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

    // ----- Cooldown "no previous pause" and clock-skew handling -------------

    [Fact]
    public void No_cooldown_when_no_previous_pause() =>
        // LastPauseEndUnix == 0 means "no pause yet": cooldown is skipped entirely.
        Assert.Equal(PauseBlock.None,
            PauseRules.CanPause(Ready() with { LastPauseEndUnix = 0, NowUnix = 10, CooldownSeconds = 900 }));

    [Fact]
    public void No_cooldown_when_last_pause_timestamp_is_negative() =>
        // A non-positive timestamp is treated as "no pause yet".
        Assert.Equal(PauseBlock.None,
            PauseRules.CanPause(Ready() with { LastPauseEndUnix = -5, NowUnix = 10_000, CooldownSeconds = 900 }));

    [Fact]
    public void Backwards_clock_does_not_block_with_cooldown()
    {
        // If the wall clock moved backwards the gap is negative; a negative gap must
        // never block, otherwise a clock change could lock the user out indefinitely.
        var s = Ready() with { LastPauseEndUnix = 10_000, NowUnix = 9_000, CooldownSeconds = 900 };
        Assert.Equal(PauseBlock.None, PauseRules.CanPause(s));
    }

    // ----- Priority order ----------------------------------------------------

    [Fact]
    public void Disabled_takes_priority_over_every_other_reason()
    {
        // Violate every condition at once; Disabled is checked first.
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

    // ----- RemainingBudget ---------------------------------------------------

    [Theory]
    [InlineData(2700, 0, 2700)]    // nothing used yet
    [InlineData(2700, 600, 2100)]  // partially used
    [InlineData(2700, 2700, 0)]    // exactly exhausted
    [InlineData(2700, 3000, 0)]    // over-spent, clamped to zero
    [InlineData(0, 0, 0)]          // no budget configured
    public void RemainingBudget_never_negative(int budget, int used, int expected) =>
        Assert.Equal(expected, PauseRules.RemainingBudget(budget, used));

    // ----- MaxPauseDuration --------------------------------------------------

    [Fact]
    public void MaxPauseDuration_is_capped_by_budget() =>
        // Remaining budget (300) is smaller than the single-pause cap (1200).
        Assert.Equal(300, PauseRules.MaxPauseDuration(1200, 2700, 2400));

    [Fact]
    public void MaxPauseDuration_is_capped_by_single_pause_limit() =>
        // Single-pause cap (600) is smaller than the remaining budget (2700).
        Assert.Equal(600, PauseRules.MaxPauseDuration(600, 2700, 0));

    [Fact]
    public void MaxPauseDuration_is_zero_when_budget_exhausted() =>
        Assert.Equal(0, PauseRules.MaxPauseDuration(1200, 2700, 2700));

    [Fact]
    public void MaxPauseDuration_is_zero_when_over_budget() =>
        // Over-spent budget must not produce a negative duration.
        Assert.Equal(0, PauseRules.MaxPauseDuration(1200, 2700, 5000));

    [Theory]
    [InlineData(0, 2700, 0, 0)]      // single-pause cap of zero
    [InlineData(900, 1800, 900, 900)] // cap and remaining budget are equal
    public void MaxPauseDuration_handles_edge_inputs(int maxSingle, int budget, int used, int expected) =>
        Assert.Equal(expected, PauseRules.MaxPauseDuration(maxSingle, budget, used));
}
