namespace Curfew.Core;

/// <summary>
/// Pure countdown state machine for the daily screen-time budget. Each tick
/// represents one elapsed second: it decrements the remaining time and answers
/// the host's policy questions — when to persist, when a warning fires, and
/// when the limit is reached.
/// </summary>
/// <remarks>
/// Every member is a deterministic, side-effect-free function of its inputs, so
/// the App, Overlay and Service can share the exact same rules while each owns
/// its own timer and storage. All times are measured in whole seconds; daily
/// limits and warning thresholds are supplied in minutes. Arithmetic is
/// performed with a 64-bit intermediate and clamped to the
/// <see cref="int"/> range so that absurdly large limits or extensions can
/// never silently overflow into a negative budget.
/// </remarks>
public static class TimeKeeper
{
    /// <summary>The cadence, in seconds, at which the budget is persisted.</summary>
    private const int PersistIntervalSeconds = 30;

    /// <summary>
    /// Seconds remaining at startup: today's previously saved value when one
    /// exists, otherwise a fresh allowance derived from the daily limit.
    /// </summary>
    /// <param name="savedSeconds">
    /// The value persisted earlier today, or <c>null</c> when this is the first
    /// run of the day.
    /// </param>
    /// <param name="dailyLimitMinutes">
    /// The configured limit for today, in minutes. Non-positive values yield a
    /// zero allowance (the budget is immediately exhausted).
    /// </param>
    /// <returns>The seconds the host should start counting down from.</returns>
    public static int InitialRemaining(int? savedSeconds, int dailyLimitMinutes) =>
        savedSeconds ?? MinutesToSeconds(dailyLimitMinutes);

    /// <summary>Records one elapsed second; the result never drops below zero.</summary>
    /// <param name="remaining">The current seconds remaining.</param>
    /// <returns><paramref name="remaining"/> decremented by one, floored at zero.</returns>
    public static int Tick(int remaining) => Math.Max(0, remaining - 1);

    /// <summary>
    /// Whether the current value should be written to storage, throttled to one
    /// write every <see cref="PersistIntervalSeconds"/> seconds to spare the
    /// database. A depleted budget (zero) is never persisted on this cadence;
    /// the host persists that transition explicitly.
    /// </summary>
    /// <param name="remaining">The current seconds remaining.</param>
    /// <returns><c>true</c> on a persistence boundary; otherwise <c>false</c>.</returns>
    public static bool ShouldPersist(int remaining) =>
        remaining > 0 && remaining % PersistIntervalSeconds == 0;

    /// <summary>
    /// Whether a warning should fire on this exact second. The warning is edge
    /// triggered: it is <c>true</c> only on the single tick where the remaining
    /// time equals the threshold, so the host fires it once rather than for the
    /// whole final stretch.
    /// </summary>
    /// <param name="remaining">The current seconds remaining.</param>
    /// <param name="warningMinutes">
    /// How long before exhaustion to warn, in minutes. Non-positive values
    /// disable the warning entirely.
    /// </param>
    /// <returns><c>true</c> on the threshold second; otherwise <c>false</c>.</returns>
    public static bool WarningFires(int remaining, int warningMinutes) =>
        warningMinutes > 0 && remaining == MinutesToSeconds(warningMinutes);

    /// <summary>Whether the budget is spent (no time remains).</summary>
    /// <param name="remaining">The current seconds remaining.</param>
    /// <returns><c>true</c> when <paramref name="remaining"/> is zero or below.</returns>
    public static bool IsExhausted(int remaining) => remaining <= 0;

    /// <summary>
    /// Grants additional minutes of screen time. A negative running value is
    /// treated as "no budget", so the extension starts a fresh allowance from
    /// zero rather than compounding the debt.
    /// </summary>
    /// <param name="remaining">The current seconds remaining (may be negative).</param>
    /// <param name="minutes">
    /// The minutes to add. Non-positive values are ignored, leaving a
    /// non-negative budget unchanged.
    /// </param>
    /// <returns>The new seconds remaining, clamped to the <see cref="int"/> range.</returns>
    public static int Extend(int remaining, int minutes)
    {
        var added = MinutesToSeconds(minutes);
        var baseline = remaining < 0 ? 0L : remaining;
        return Clamp(baseline + added);
    }

    /// <summary>
    /// Converts minutes to seconds without overflowing, treating negative input
    /// as zero and clamping the result to the <see cref="int"/> range.
    /// </summary>
    private static int MinutesToSeconds(int minutes) =>
        minutes <= 0 ? 0 : Clamp(minutes * 60L);

    /// <summary>Clamps a 64-bit second count into the non-negative <see cref="int"/> range.</summary>
    private static int Clamp(long seconds) =>
        (int)Math.Clamp(seconds, 0L, int.MaxValue);
}
