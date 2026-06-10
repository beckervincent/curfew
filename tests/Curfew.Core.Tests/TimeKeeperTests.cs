using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

/// <summary>
/// Behavioural specification for <see cref="TimeKeeper"/>, the pure countdown
/// state machine that drives the daily screen-time budget. Every member is a
/// deterministic function of its inputs, so these tests pin down the exact
/// contract the App, Overlay and Service rely on — including the documented
/// edge cases: non-positive limits, negative running budgets, the 30-second
/// persistence cadence, edge-triggered warnings and overflow clamping.
/// </summary>
public class TimeKeeperTests
{
    /// <summary>Seconds in one minute, mirroring <see cref="TimeKeeper"/>'s internal conversion.</summary>
    private const int SecondsPerMinute = 60;

    /// <summary>The cadence, in seconds, at which the budget is persisted.</summary>
    private const int PersistIntervalSeconds = 30;

    // ---- InitialRemaining --------------------------------------------------

    [Fact]
    public void InitialRemaining_uses_saved_when_present() =>
        Assert.Equal(500, TimeKeeper.InitialRemaining(500, 120));

    [Fact]
    public void InitialRemaining_prefers_saved_even_when_zero() =>
        // A saved value of zero means "today's budget is already spent" and must
        // win over the daily limit; otherwise the limit would be handed back out.
        Assert.Equal(0, TimeKeeper.InitialRemaining(0, 120));

    [Fact]
    public void InitialRemaining_falls_back_to_daily_limit() =>
        Assert.Equal(120 * SecondsPerMinute, TimeKeeper.InitialRemaining(null, 120));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void InitialRemaining_yields_zero_for_non_positive_limit(int dailyLimitMinutes) =>
        Assert.Equal(0, TimeKeeper.InitialRemaining(null, dailyLimitMinutes));

    [Fact]
    public void InitialRemaining_does_not_overflow_on_huge_limit() =>
        // int.MaxValue minutes * 60 overflows a 32-bit second count; the result
        // must clamp to int.MaxValue rather than wrap into a negative budget.
        Assert.Equal(int.MaxValue, TimeKeeper.InitialRemaining(null, int.MaxValue));

    // ---- Tick --------------------------------------------------------------

    [Fact]
    public void Tick_decrements_by_one_second()
    {
        Assert.Equal(99, TimeKeeper.Tick(100));
        Assert.Equal(1, TimeKeeper.Tick(2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-5)]
    public void Tick_floors_at_zero(int remaining) =>
        Assert.Equal(0, TimeKeeper.Tick(remaining));

    [Fact]
    public void Tick_repeated_converges_to_zero_and_stays_there()
    {
        var remaining = 3;
        for (var i = 0; i < 10; i++)
        {
            remaining = TimeKeeper.Tick(remaining);
        }

        Assert.Equal(0, remaining);
    }

    // ---- ShouldPersist -----------------------------------------------------

    [Theory]
    [InlineData(30, true)]
    [InlineData(60, true)]
    [InlineData(90, true)]
    [InlineData(45, false)]
    [InlineData(1, false)]
    [InlineData(29, false)]
    [InlineData(31, false)]
    public void ShouldPersist_every_30s(int remaining, bool expected) =>
        Assert.Equal(expected, TimeKeeper.ShouldPersist(remaining));

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(-60)]
    public void ShouldPersist_never_on_depleted_or_negative_budget(int remaining) =>
        // Zero and negatives are multiples of the interval, but the host owns the
        // depletion write; the cadence must stay silent for non-positive values.
        Assert.False(TimeKeeper.ShouldPersist(remaining));

    [Fact]
    public void ShouldPersist_fires_exactly_once_per_interval_window()
    {
        var hits = 0;
        for (var remaining = PersistIntervalSeconds * 3; remaining > 0; remaining--)
        {
            if (TimeKeeper.ShouldPersist(remaining))
            {
                hits++;
            }
        }

        // Three full intervals counted down to (but not including) zero.
        Assert.Equal(3, hits);
    }

    // ---- WarningFires ------------------------------------------------------

    [Fact]
    public void WarningFires_on_exact_threshold() =>
        Assert.True(TimeKeeper.WarningFires(600, 10));

    [Theory]
    [InlineData(601)]
    [InlineData(599)]
    [InlineData(1200)]
    [InlineData(0)]
    public void WarningFires_only_on_the_threshold_second(int remaining) =>
        // Edge triggered: anything other than exactly the threshold is silent,
        // so the host warns once instead of for the whole final stretch.
        Assert.False(TimeKeeper.WarningFires(remaining, 10));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void WarningFires_disabled_for_non_positive_threshold(int warningMinutes)
    {
        Assert.False(TimeKeeper.WarningFires(600, warningMinutes));
        // A zero threshold must not be interpreted as "warn at zero remaining".
        Assert.False(TimeKeeper.WarningFires(0, warningMinutes));
    }

    [Fact]
    public void WarningFires_does_not_overflow_on_huge_threshold() =>
        // The threshold in seconds saturates at int.MaxValue; a remaining value
        // below that must never accidentally equal the wrapped result.
        Assert.False(TimeKeeper.WarningFires(600, int.MaxValue));

    // ---- IsExhausted -------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(int.MinValue)]
    public void IsExhausted_at_zero_or_below(int remaining) =>
        Assert.True(TimeKeeper.IsExhausted(remaining));

    [Theory]
    [InlineData(1)]
    [InlineData(600)]
    [InlineData(int.MaxValue)]
    public void IsExhausted_false_while_time_remains(int remaining) =>
        Assert.False(TimeKeeper.IsExhausted(remaining));

    // ---- Extend ------------------------------------------------------------

    [Fact]
    public void Extend_starts_fresh_allowance_from_empty() =>
        Assert.Equal(900, TimeKeeper.Extend(0, 15));

    [Fact]
    public void Extend_adds_to_running_budget() =>
        Assert.Equal(1800, TimeKeeper.Extend(900, 15));

    [Theory]
    [InlineData(-1)]
    [InlineData(-600)]
    [InlineData(int.MinValue)]
    public void Extend_ignores_existing_debt(int remaining) =>
        // A negative running value is treated as "no budget", so the extension
        // starts from zero rather than compounding the debt.
        Assert.Equal(900, TimeKeeper.Extend(remaining, 15));

    [Theory]
    [InlineData(0)]
    [InlineData(-15)]
    [InlineData(int.MinValue)]
    public void Extend_with_non_positive_minutes_leaves_budget_unchanged(int minutes) =>
        Assert.Equal(900, TimeKeeper.Extend(900, minutes));

    [Fact]
    public void Extend_with_non_positive_minutes_normalises_debt_to_zero() =>
        // Non-positive minutes add nothing, but the negative baseline is still
        // clamped to a non-negative budget.
        Assert.Equal(0, TimeKeeper.Extend(-100, 0));

    [Fact]
    public void Extend_clamps_overflow_to_int_max()
    {
        Assert.Equal(int.MaxValue, TimeKeeper.Extend(int.MaxValue, 60));
        Assert.Equal(int.MaxValue, TimeKeeper.Extend(0, int.MaxValue));
    }

    // ---- Cross-method scenario ---------------------------------------------

    [Fact]
    public void FullSession_counts_down_warns_extends_and_exhausts()
    {
        // Drive the machine the way the host does: start from a daily limit,
        // tick down to the warning, grant an extension, then run dry.
        const int dailyLimitMinutes = 1; // 60 seconds
        const int warningMinutes = 0;    // warnings disabled in this scenario

        var remaining = TimeKeeper.InitialRemaining(null, dailyLimitMinutes);
        Assert.Equal(60, remaining);

        while (!TimeKeeper.IsExhausted(remaining))
        {
            Assert.False(TimeKeeper.WarningFires(remaining, warningMinutes));
            remaining = TimeKeeper.Tick(remaining);
        }

        Assert.True(TimeKeeper.IsExhausted(remaining));

        remaining = TimeKeeper.Extend(remaining, 2); // +120 seconds
        Assert.Equal(120, remaining);
        Assert.False(TimeKeeper.IsExhausted(remaining));
    }
}
