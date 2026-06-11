using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

/// <summary>
/// Unit tests for <see cref="TimeGuard"/>, the pure clock-tampering classifier.
/// <para>
/// All assertions are built relative to <see cref="Tolerance"/> rather than
/// hard-coded magnitudes, so the suite keeps pinning the documented contract even
/// if the tolerance is later retuned. The trusted reference is anchored at a fixed
/// UTC instant; expected effective dates are derived by projecting the chosen
/// instant through <see cref="DateTimeOffset.LocalDateTime"/> exactly as the
/// production code does, which keeps the tests deterministic regardless of the
/// machine's local time zone.
/// </para>
/// </summary>
public class TimeGuardTests
{
    /// <summary>A fixed, trusted reference instant (noon UTC) used as NTP time.</summary>
    private static readonly DateTimeOffset Trusted =
        new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Convenience alias for the guard's configured tolerance window.</summary>
    private static readonly TimeSpan Tolerance = TimeGuard.Tolerance;

    /// <summary>
    /// Projects an instant onto the local wall-clock date the same way
    /// <see cref="TimeGuard.EffectiveDate(DateTimeOffset, DateTimeOffset)"/> does,
    /// so expectations stay correct on any machine time zone.
    /// </summary>
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
    [InlineData(-30)]   // small backward drift
    public void Evaluate_returns_ok_for_drift_within_tolerance(int driftSeconds)
    {
        var local = Trusted.AddSeconds(driftSeconds);
        Assert.Equal(TimeGuard.Verdict.Ok, TimeGuard.Evaluate(local, Trusted));
    }

    [Fact]
    public void Evaluate_treats_drift_exactly_at_tolerance_as_ok()
    {
        // The contract documents the boundary as inclusive in both directions.
        Assert.Equal(TimeGuard.Verdict.Ok, TimeGuard.Evaluate(Trusted + Tolerance, Trusted));
        Assert.Equal(TimeGuard.Verdict.Ok, TimeGuard.Evaluate(Trusted - Tolerance, Trusted));
    }

    [Fact]
    public void Evaluate_is_independent_of_time_zone_offset()
    {
        // Same absolute instant, expressed in a different offset, is still in sync.
        var sameInstantOtherZone = Trusted.ToOffset(TimeSpan.FromHours(5));
        Assert.Equal(TimeGuard.Verdict.Ok, TimeGuard.Evaluate(sameInstantOtherZone, Trusted));
    }

    // ---- Evaluate: ahead (the time-farming case) --------------------------

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

    // ---- Evaluate: behind (curfew-dodging case) ---------------------------

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
        // A forward jump must not unlock a fresh day's allowance: the trusted day wins.
        var local = Trusted.AddDays(1);
        Assert.Equal(LocalDateOf(Trusted), TimeGuard.EffectiveDate(local, Trusted));
    }

    [Fact]
    public void EffectiveDate_ignores_backward_jump_into_yesterday()
    {
        // A backward jump must not replay an already-spent day: the trusted day wins.
        var local = Trusted.AddDays(-1);
        Assert.Equal(LocalDateOf(Trusted), TimeGuard.EffectiveDate(local, Trusted));
    }

    [Fact]
    public void EffectiveDate_uses_local_day_even_when_local_is_a_different_date_but_in_sync()
    {
        // Across local midnight the two instants can carry different calendar dates
        // while still agreeing within tolerance; the local date is then authoritative.
        var nearMidnightTrusted = new DateTimeOffset(2026, 6, 10, 23, 59, 30, TimeSpan.Zero);
        var local = nearMidnightTrusted + TimeSpan.FromSeconds(45); // rolls past midnight

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
        // Even if the local clock is far ahead, an explicit Ok verdict trusts local.
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
        // The supplied verdict, not the actual drift, drives the choice of source date.
        var local = Trusted.AddSeconds(10); // genuinely in sync...
        Assert.Equal(
            LocalDateOf(Trusted),
            TimeGuard.EffectiveDate(local, Trusted, verdict)); // ...but told it is tampered.
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
        // Median of the two-element cluster is the earlier (lower-median) sample.
        Assert.Equal(Trusted, result);
    }

    [Fact]
    public void Corroborate_ignores_a_single_spoofed_outlier()
    {
        // Two honest sources agree; one forged source is hours off and must not win.
        var samples = new[] { Trusted, Trusted.AddSeconds(8), Trusted.AddHours(6) };
        var result = TimeGuard.Corroborate(samples);
        Assert.NotNull(result);
        Assert.True((result!.Value - Trusted).Duration() <= TimeGuard.AgreementWindow);
    }

    [Fact]
    public void Corroborate_returns_null_when_no_cluster_agrees()
    {
        // Three sources, all far apart: nothing corroborates, so fail closed.
        var samples = new[] { Trusted, Trusted.AddMinutes(10), Trusted.AddMinutes(20) };
        Assert.Null(TimeGuard.Corroborate(samples));
    }

    [Fact]
    public void Corroborate_picks_the_largest_agreeing_cluster()
    {
        // A lone early sample, then a tight cluster of three: the cluster wins.
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
