using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

/// <summary>tests for <see cref="TimeGuard"/> pure clock-tamper classifier; asserts relative to <see cref="Tolerance"/>, dates via <see cref="DateTimeOffset.LocalDateTime"/> like prod, time-zone independent</summary>
public class TimeGuardTests
{
    /// <summary>fixed trusted reference instant (noon UTC) as NTP time</summary>
    private static readonly DateTimeOffset Trusted =
        new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    /// <summary>alias for guard's tolerance window</summary>
    private static readonly TimeSpan Tolerance = TimeGuard.Tolerance;

    /// <summary>project instant to local wall-clock date like <see cref="TimeGuard.EffectiveDate(DateTimeOffset, DateTimeOffset)"/>; correct on any time zone</summary>
    private static DateOnly LocalDateOf(DateTimeOffset instant) =>
        DateOnly.FromDateTime(instant.LocalDateTime);

    // ---- Evaluate: in-sync ------------------------------------------------

    [Fact]
    public void Evaluate_returns_ok_when_clock_matches_trusted_exactly()
    {
        Assert.Equal(TimeGuard.Verdict.Ok, TimeGuard.Evaluate(Trusted, Trusted));
    }

    [Theory]
    [InlineData(30)]    // small forward drift
    [InlineData(-30)]   // small back drift
    public void Evaluate_returns_ok_for_drift_within_tolerance(int driftSeconds)
    {
        var local = Trusted.AddSeconds(driftSeconds);
        Assert.Equal(TimeGuard.Verdict.Ok, TimeGuard.Evaluate(local, Trusted));
    }

    [Fact]
    public void Evaluate_treats_drift_exactly_at_tolerance_as_ok()
    {
        // boundary inclusive both directions
        Assert.Equal(TimeGuard.Verdict.Ok, TimeGuard.Evaluate(Trusted + Tolerance, Trusted));
        Assert.Equal(TimeGuard.Verdict.Ok, TimeGuard.Evaluate(Trusted - Tolerance, Trusted));
    }

    [Fact]
    public void Evaluate_is_independent_of_time_zone_offset()
    {
        // same instant, different offset, still in sync
        var sameInstantOtherZone = Trusted.ToOffset(TimeSpan.FromHours(5));
        Assert.Equal(TimeGuard.Verdict.Ok, TimeGuard.Evaluate(sameInstantOtherZone, Trusted));
    }

    // ---- Evaluate: ahead (time-farming) --------------------------

    [Fact]
    public void Evaluate_flags_ahead_just_past_tolerance()
    {
        var local = Trusted + Tolerance + TimeSpan.FromSeconds(1);
        Assert.Equal(TimeGuard.Verdict.AheadTampered, TimeGuard.Evaluate(local, Trusted));
    }

    [Fact]
    public void Evaluate_flags_clock_set_a_day_ahead_as_ahead_tampered()
    {
        var local = Trusted.AddDays(1);
        Assert.Equal(TimeGuard.Verdict.AheadTampered, TimeGuard.Evaluate(local, Trusted));
    }

    // ---- Evaluate: behind (curfew-dodging) ---------------------------

    [Fact]
    public void Evaluate_flags_behind_just_past_tolerance()
    {
        var local = Trusted - Tolerance - TimeSpan.FromSeconds(1);
        Assert.Equal(TimeGuard.Verdict.BehindTampered, TimeGuard.Evaluate(local, Trusted));
    }

    [Fact]
    public void Evaluate_flags_clock_set_behind_as_behind_tampered()
    {
        var local = Trusted.AddHours(-3);
        Assert.Equal(TimeGuard.Verdict.BehindTampered, TimeGuard.Evaluate(local, Trusted));
    }

    // ---- ShouldCorrect ----------------------------------------------------

    [Theory]
    [InlineData(TimeGuard.Verdict.Ok, false)]
    [InlineData(TimeGuard.Verdict.AheadTampered, true)]
    [InlineData(TimeGuard.Verdict.BehindTampered, true)]
    public void ShouldCorrect_only_for_tampered_verdicts(TimeGuard.Verdict verdict, bool expected)
    {
        Assert.Equal(expected, TimeGuard.ShouldCorrect(verdict));
    }

    [Fact]
    public void ShouldCorrect_agrees_with_evaluate_for_in_sync_clock()
    {
        var local = Trusted.AddSeconds(30);
        Assert.False(TimeGuard.ShouldCorrect(TimeGuard.Evaluate(local, Trusted)));
    }

    [Fact]
    public void ShouldCorrect_agrees_with_evaluate_for_forward_jump()
    {
        var local = Trusted.AddDays(1);
        Assert.True(TimeGuard.ShouldCorrect(TimeGuard.Evaluate(local, Trusted)));
    }

    // ---- EffectiveDate ----------------------------------------------------

    [Fact]
    public void EffectiveDate_trusts_local_when_in_sync()
    {
        var local = Trusted.AddSeconds(10);
        Assert.Equal(LocalDateOf(local), TimeGuard.EffectiveDate(local, Trusted));
    }

    [Fact]
    public void EffectiveDate_ignores_forward_jump_into_tomorrow()
    {
        // forward jump must not unlock fresh day; trusted day wins
        var local = Trusted.AddDays(1);
        Assert.Equal(LocalDateOf(Trusted), TimeGuard.EffectiveDate(local, Trusted));
    }

    [Fact]
    public void EffectiveDate_ignores_backward_jump_into_yesterday()
    {
        // back jump must not replay spent day; trusted day wins
        var local = Trusted.AddDays(-1);
        Assert.Equal(LocalDateOf(Trusted), TimeGuard.EffectiveDate(local, Trusted));
    }

    [Fact]
    public void EffectiveDate_uses_local_day_even_when_local_is_a_different_date_but_in_sync()
    {
        // across local midnight instants carry different dates yet agree within tolerance; local date wins
        var nearMidnightTrusted = new DateTimeOffset(2026, 6, 10, 23, 59, 30, TimeSpan.Zero);
        var local = nearMidnightTrusted + TimeSpan.FromSeconds(45); // past midnight

        Assert.Equal(TimeGuard.Verdict.Ok, TimeGuard.Evaluate(local, nearMidnightTrusted));
        Assert.Equal(LocalDateOf(local), TimeGuard.EffectiveDate(local, nearMidnightTrusted));
    }

    // ---- EffectiveDate(verdict) overload ----------------------------------

    [Fact]
    public void EffectiveDate_overload_matches_single_arg_overload_when_in_sync()
    {
        var local = Trusted.AddSeconds(10);
        var verdict = TimeGuard.Evaluate(local, Trusted);
        Assert.Equal(
            TimeGuard.EffectiveDate(local, Trusted),
            TimeGuard.EffectiveDate(local, Trusted, verdict));
    }

    [Fact]
    public void EffectiveDate_overload_matches_single_arg_overload_when_tampered()
    {
        var local = Trusted.AddDays(1);
        var verdict = TimeGuard.Evaluate(local, Trusted);
        Assert.Equal(
            TimeGuard.EffectiveDate(local, Trusted),
            TimeGuard.EffectiveDate(local, Trusted, verdict));
    }

    [Fact]
    public void EffectiveDate_overload_honors_an_ok_verdict_by_trusting_local()
    {
        // even if local far ahead, explicit Ok verdict trusts local
        var local = Trusted.AddDays(1);
        Assert.Equal(
            LocalDateOf(local),
            TimeGuard.EffectiveDate(local, Trusted, TimeGuard.Verdict.Ok));
    }

    [Theory]
    [InlineData(TimeGuard.Verdict.AheadTampered)]
    [InlineData(TimeGuard.Verdict.BehindTampered)]
    public void EffectiveDate_overload_uses_trusted_date_for_any_tampered_verdict(TimeGuard.Verdict verdict)
    {
        // supplied verdict, not actual drift, picks source date
        var local = Trusted.AddSeconds(10); // genuinely in sync...
        Assert.Equal(
            LocalDateOf(Trusted),
            TimeGuard.EffectiveDate(local, Trusted, verdict)); // ...but told tampered
    }

    // ----- Multi-source corroboration ---------------------------------------

    [Fact]
    public void Corroborate_returns_null_with_fewer_than_two_sources()
    {
        Assert.Null(TimeGuard.Corroborate(Array.Empty<DateTimeOffset>()));
        Assert.Null(TimeGuard.Corroborate(new[] { Trusted }));
    }

    [Fact]
    public void Corroborate_trusts_two_agreeing_sources()
    {
        var samples = new[] { Trusted, Trusted.AddSeconds(5) };
        var result = TimeGuard.Corroborate(samples);
        Assert.NotNull(result);
        // median of two-element cluster is earlier (lower-median) sample
        Assert.Equal(Trusted, result);
    }

    [Fact]
    public void Corroborate_ignores_a_single_spoofed_outlier()
    {
        // two honest sources agree; one forged hours off must not win
        var samples = new[] { Trusted, Trusted.AddSeconds(8), Trusted.AddHours(6) };
        var result = TimeGuard.Corroborate(samples);
        Assert.NotNull(result);
        Assert.True((result!.Value - Trusted).Duration() <= TimeGuard.AgreementWindow);
    }

    [Fact]
    public void Corroborate_returns_null_when_no_cluster_agrees()
    {
        // three sources far apart: nothing corroborates, fail closed
        var samples = new[] { Trusted, Trusted.AddMinutes(10), Trusted.AddMinutes(20) };
        Assert.Null(TimeGuard.Corroborate(samples));
    }

    [Fact]
    public void Corroborate_picks_the_largest_agreeing_cluster()
    {
        // lone early sample, then tight cluster of three: cluster wins
        var samples = new[]
        {
            Trusted.AddHours(-3),
            Trusted, Trusted.AddSeconds(4), Trusted.AddSeconds(9),
        };
        var result = TimeGuard.Corroborate(samples);
        Assert.NotNull(result);
        Assert.True((result!.Value - Trusted).Duration() <= TimeGuard.AgreementWindow);
    }
}
