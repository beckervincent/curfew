using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

public class TimeGuardTests
{
    private static readonly DateTimeOffset Trusted =
        new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void In_sync_clock_is_ok()
    {
        var local = Trusted.AddSeconds(30);
        Assert.Equal(TimeGuard.Verdict.Ok, TimeGuard.Evaluate(local, Trusted));
        Assert.False(TimeGuard.ShouldCorrect(TimeGuard.Evaluate(local, Trusted)));
    }

    [Fact]
    public void Clock_set_a_day_ahead_is_tampered()
    {
        var local = Trusted.AddDays(1);
        var verdict = TimeGuard.Evaluate(local, Trusted);
        Assert.Equal(TimeGuard.Verdict.AheadTampered, verdict);
        Assert.True(TimeGuard.ShouldCorrect(verdict));
    }

    [Fact]
    public void Clock_set_behind_is_tampered()
    {
        var local = Trusted.AddHours(-3);
        Assert.Equal(TimeGuard.Verdict.BehindTampered, TimeGuard.Evaluate(local, Trusted));
    }

    [Fact]
    public void Effective_date_ignores_forward_jump()
    {
        // Local clock jumped to tomorrow; effective date must stay on trusted day.
        var local = Trusted.AddDays(1);
        Assert.Equal(
            DateOnly.FromDateTime(Trusted.LocalDateTime),
            TimeGuard.EffectiveDate(local, Trusted));
    }

    [Fact]
    public void Effective_date_trusts_local_when_in_sync()
    {
        var local = Trusted.AddSeconds(10);
        Assert.Equal(
            DateOnly.FromDateTime(local.LocalDateTime),
            TimeGuard.EffectiveDate(local, Trusted));
    }
}
