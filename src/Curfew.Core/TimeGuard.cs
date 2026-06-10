namespace Curfew.Core;

/// <summary>
/// Time Manipulation Guarding. The daily allowance resets at midnight, so a
/// forward clock change is the easiest way to farm extra time. The service
/// fetches trusted time over NTP and compares it to the local clock; if they
/// disagree beyond a tolerance, the local clock is being tampered with.
/// </summary>
public static class TimeGuard
{
    /// <summary>Allowed clock drift before we treat it as tampering.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(2);

    public enum Verdict
    {
        /// <summary>Local clock agrees with trusted time.</summary>
        Ok,
        /// <summary>Local clock set ahead — the time-farming case.</summary>
        AheadTampered,
        /// <summary>Local clock set behind trusted time.</summary>
        BehindTampered,
    }

    /// <summary>Classifies the local clock against trusted NTP time.</summary>
    public static Verdict Evaluate(DateTimeOffset local, DateTimeOffset trusted)
    {
        var drift = local - trusted;
        if (drift > Tolerance) return Verdict.AheadTampered;
        if (drift < -Tolerance) return Verdict.BehindTampered;
        return Verdict.Ok;
    }

    /// <summary>True when the clock should be force-corrected to trusted time.</summary>
    public static bool ShouldCorrect(Verdict verdict) => verdict != Verdict.Ok;

    /// <summary>
    /// The date whose allowance should apply, using trusted time when the local
    /// clock is tampered so a forward jump cannot unlock a fresh day.
    /// </summary>
    public static DateOnly EffectiveDate(DateTimeOffset local, DateTimeOffset trusted)
    {
        var source = Evaluate(local, trusted) == Verdict.Ok ? local : trusted;
        return DateOnly.FromDateTime(source.LocalDateTime);
    }
}
