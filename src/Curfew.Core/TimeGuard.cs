namespace Curfew.Core;

/// <summary>
/// Time-Manipulation Guarding.
/// <para>
/// Daily allowance resets at midnight, so pushing the clock forward farms extra screen time (and rolling back dodges a curfew window). Service fetches trusted time over NTP, compares to local clock; disagreement past <see cref="Tolerance"/> = tampered.
/// </para>
/// <para>
/// Pure: tampering decision from two timestamps, no side effects. Acquiring trusted time + correcting the system clock live in the privileged service layer.
/// </para>
/// </summary>
public static class TimeGuard
{
    /// <summary>Max clock disagreement, either direction, tolerated before local clock = tampered. Sized to absorb ordinary drift + NTP round-trip jitter without flagging an honest machine.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(2);

    /// <summary>How close two independent time sources must be to agree. Sized to absorb NTP jitter + small delay of querying servers in sequence.</summary>
    public static readonly TimeSpan AgreementWindow = TimeSpan.FromSeconds(30);

    /// <summary>Min independent sources that must agree before trust. Two = a single spoofed/redirected server can't move the clock.</summary>
    public const int MinAgreeingSources = 2;

    /// <summary>Reduce several independent time samples to one trusted instant, or <see langword="null"/> when too few agree. Trusted only if at least <paramref name="minAgree"/> samples fall within <paramref name="window"/> of each other; returns median of largest such cluster. Fail-closed core of the multi-source guard: one forged source among honest ones never forms a cluster; one reachable source trusts nothing.</summary>
    /// <param name="samples">Times from queried sources (nulls already removed).</param>
    /// <param name="window">Max spread within an agreeing cluster; defaults to <see cref="AgreementWindow"/>.</param>
    /// <param name="minAgree">Min cluster size to trust; defaults to <see cref="MinAgreeingSources"/>.</param>
    /// <returns>Corroborated time, or <see langword="null"/> if no cluster large enough.</returns>
    public static DateTimeOffset? Corroborate(
        IReadOnlyList<DateTimeOffset> samples, TimeSpan? window = null, int minAgree = MinAgreeingSources)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var w = window ?? AgreementWindow;
        if (samples.Count < minAgree) return null;

        var ordered = samples.OrderBy(s => s).ToList();
        DateTimeOffset? best = null;
        var bestCount = 0;

        // slide window over sorted samples; widest cluster of size >= minAgree
        // wins, its median is the trusted instant.
        for (var i = 0; i < ordered.Count; i++)
        {
            var j = i;
            while (j < ordered.Count && ordered[j] - ordered[i] <= w) j++;
            var count = j - i;
            if (count >= minAgree && count > bestCount)
            {
                bestCount = count;
                best = ordered[i + (count - 1) / 2];
            }
        }

        return best;
    }

    /// <summary>Outcome of comparing local clock against trusted time.</summary>
    public enum Verdict
    {
        /// <summary>Local clock agrees with trusted time within <see cref="Tolerance"/>.</summary>
        Ok,

        /// <summary>Local clock set ahead of trusted time — the time-farming case.</summary>
        AheadTampered,

        /// <summary>Local clock set behind trusted time.</summary>
        BehindTampered,
    }

    /// <summary>Classify local clock against trusted (NTP) time.</summary>
    /// <param name="local">Current local-clock reading.</param>
    /// <param name="trusted">Reference time from a trusted source.</param>
    /// <returns><see cref="Verdict.Ok"/> when the two agree within <see cref="Tolerance"/>; else the direction local clock moved.</returns>
    /// <remarks>
    /// Compared on absolute instants (<see cref="DateTimeOffset"/> is offset-aware), so result is time-zone-independent. Drift exactly equal to <see cref="Tolerance"/> is acceptable.
    /// </remarks>
    public static Verdict Evaluate(DateTimeOffset local, DateTimeOffset trusted)
    {
        var drift = local - trusted;
        if (drift > Tolerance) return Verdict.AheadTampered;
        if (drift < -Tolerance) return Verdict.BehindTampered;
        return Verdict.Ok;
    }

    /// <summary>Whether verdict warrants force-correcting system clock to trusted time. True for any tampered verdict, false for <see cref="Verdict.Ok"/>.</summary>
    public static bool ShouldCorrect(Verdict verdict) => verdict != Verdict.Ok;

    /// <summary>
    /// Date whose daily allowance applies.
    /// <para>
    /// Trustworthy local clock = local date; tampered = trusted date, so a forward jump can't unlock a fresh day's allowance and a backward jump can't replay an already-spent one.
    /// </para>
    /// </summary>
    /// <param name="local">Current local-clock reading.</param>
    /// <param name="trusted">Reference time from a trusted source.</param>
    /// <returns>Calendar date the allowance is charged against.</returns>
    public static DateOnly EffectiveDate(DateTimeOffset local, DateTimeOffset trusted) =>
        EffectiveDate(local, trusted, Evaluate(local, trusted));

    /// <summary>Effective allowance date from an already-determined verdict, skipping a redundant <see cref="Evaluate"/> call.</summary>
    /// <param name="local">Current local-clock reading.</param>
    /// <param name="trusted">Reference time from a trusted source.</param>
    /// <param name="verdict">Verdict for <paramref name="local"/> against <paramref name="trusted"/>, typically from <see cref="Evaluate"/>.</param>
    /// <returns>Calendar date the allowance is charged against.</returns>
    /// <remarks>
    /// Chosen timestamp projected onto machine's local wall-clock date (<see cref="DateTimeOffset.LocalDateTime"/>) so the day boundary matches user's local midnight even when the trusted reading is UTC.
    /// </remarks>
    public static DateOnly EffectiveDate(DateTimeOffset local, DateTimeOffset trusted, Verdict verdict)
    {
        var source = verdict == Verdict.Ok ? local : trusted;
        return DateOnly.FromDateTime(source.LocalDateTime);
    }
}
