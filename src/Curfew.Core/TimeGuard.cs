namespace Curfew.Core;

/// <summary>
/// Time-Manipulation Guarding.
/// <para>
/// The daily allowance resets at midnight, so pushing the clock forward is the
/// easiest way for a user to farm extra screen time (and rolling it backward can
/// be used to dodge a curfew window). The service fetches trusted time over NTP
/// and compares it to the local clock; if they disagree by more than
/// <see cref="Tolerance"/> the local clock is treated as tampered.
/// </para>
/// <para>
/// This type is pure: it makes the tampering decision from two timestamps and
/// has no side effects. Acquiring trusted time and correcting the system clock
/// live in the privileged service layer.
/// </para>
/// </summary>
public static class TimeGuard
{
    /// <summary>
    /// Maximum clock disagreement, in either direction, that is tolerated before
    /// the local clock is considered tampered. Sized to absorb ordinary drift and
    /// NTP round-trip jitter without flagging an honest machine.
    /// </summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(2);

    /// <summary>The outcome of comparing the local clock against trusted time.</summary>
    public enum Verdict
    {
        /// <summary>Local clock agrees with trusted time within <see cref="Tolerance"/>.</summary>
        Ok,

        /// <summary>Local clock is set ahead of trusted time — the time-farming case.</summary>
        AheadTampered,

        /// <summary>Local clock is set behind trusted time.</summary>
        BehindTampered,
    }

    /// <summary>
    /// Classifies the local clock against trusted (NTP) time.
    /// </summary>
    /// <param name="local">The current local-clock reading.</param>
    /// <param name="trusted">The reference time obtained from a trusted source.</param>
    /// <returns>
    /// <see cref="Verdict.Ok"/> when the two agree within <see cref="Tolerance"/>;
    /// otherwise the direction in which the local clock has been moved.
    /// </returns>
    /// <remarks>
    /// The comparison is made on the absolute instants (<see cref="DateTimeOffset"/>
    /// is offset-aware), so the result is independent of the time zones the two
    /// readings happen to carry. A drift exactly equal to <see cref="Tolerance"/>
    /// is treated as acceptable.
    /// </remarks>
    public static Verdict Evaluate(DateTimeOffset local, DateTimeOffset trusted)
    {
        var drift = local - trusted;
        if (drift > Tolerance) return Verdict.AheadTampered;
        if (drift < -Tolerance) return Verdict.BehindTampered;
        return Verdict.Ok;
    }

    /// <summary>
    /// Indicates whether the verdict warrants force-correcting the system clock to
    /// trusted time. True for any tampered verdict, false for <see cref="Verdict.Ok"/>.
    /// </summary>
    public static bool ShouldCorrect(Verdict verdict) => verdict != Verdict.Ok;

    /// <summary>
    /// Computes the date whose daily allowance should apply.
    /// <para>
    /// When the local clock is trustworthy the local date is used; when it is
    /// tampered the trusted date is used instead, so a forward jump cannot unlock a
    /// fresh day's allowance and a backward jump cannot replay an already-spent one.
    /// </para>
    /// </summary>
    /// <param name="local">The current local-clock reading.</param>
    /// <param name="trusted">The reference time obtained from a trusted source.</param>
    /// <returns>The calendar date the allowance should be charged against.</returns>
    public static DateOnly EffectiveDate(DateTimeOffset local, DateTimeOffset trusted) =>
        EffectiveDate(local, trusted, Evaluate(local, trusted));

    /// <summary>
    /// Computes the effective allowance date from a verdict that has already been
    /// determined, avoiding a redundant call to <see cref="Evaluate"/> when the
    /// caller has one in hand.
    /// </summary>
    /// <param name="local">The current local-clock reading.</param>
    /// <param name="trusted">The reference time obtained from a trusted source.</param>
    /// <param name="verdict">
    /// The verdict for <paramref name="local"/> against <paramref name="trusted"/>,
    /// typically from <see cref="Evaluate"/>.
    /// </param>
    /// <returns>The calendar date the allowance should be charged against.</returns>
    /// <remarks>
    /// The chosen timestamp is projected onto the machine's local wall-clock date
    /// (<see cref="DateTimeOffset.LocalDateTime"/>) so the allowance day boundary
    /// matches the user's local midnight even when the trusted reading is in UTC.
    /// </remarks>
    public static DateOnly EffectiveDate(DateTimeOffset local, DateTimeOffset trusted, Verdict verdict)
    {
        var source = verdict == Verdict.Ok ? local : trusted;
        return DateOnly.FromDateTime(source.LocalDateTime);
    }
}
