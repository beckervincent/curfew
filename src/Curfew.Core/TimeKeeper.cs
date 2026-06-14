namespace Curfew.Core;

/// <summary>Pure countdown state machine for the daily screen-time budget. Each tick = one elapsed second: decrements remaining time, answers host policy questions — when to persist, when a warning fires, when the limit is reached.</summary>
/// <remarks>
/// Every member is a deterministic, side-effect-free function of its inputs, so App, Overlay, Service share the same rules while each owns its timer + storage. Times in whole seconds; daily limits + warning thresholds in minutes. Arithmetic via 64-bit intermediate, clamped to <see cref="int"/> range so absurd limits/extensions never silently overflow into a negative budget.
/// </remarks>
public static class TimeKeeper
{
    /// <summary>Cadence, seconds, at which the budget is persisted.</summary>
    private const int PersistIntervalSeconds = 30;

    /// <summary>Seconds remaining at startup: today's saved value if one exists, else a fresh allowance from the daily limit.</summary>
    /// <param name="savedSeconds">Value persisted earlier today, or <c>null</c> on the first run of the day.</param>
    /// <param name="dailyLimitMinutes">Configured limit for today, minutes. Non-positive = zero allowance (budget immediately exhausted).</param>
    /// <returns>Seconds the host starts counting down from.</returns>
    public static int InitialRemaining(int? savedSeconds, int dailyLimitMinutes) =>
        savedSeconds ?? MinutesToSeconds(dailyLimitMinutes);

    /// <summary>Record one elapsed second; result never drops below zero.</summary>
    /// <param name="remaining">Current seconds remaining.</param>
    /// <returns><paramref name="remaining"/> minus one, floored at zero.</returns>
    public static int Tick(int remaining) => Math.Max(0, remaining - 1);

    /// <summary>Whether current value should be written to storage, throttled to one write every <see cref="PersistIntervalSeconds"/> seconds to spare the db. Depleted (zero) never persisted on this cadence; host persists that transition explicitly.</summary>
    /// <param name="remaining">Current seconds remaining.</param>
    /// <returns><c>true</c> on a persistence boundary; else <c>false</c>.</returns>
    public static bool ShouldPersist(int remaining) =>
        remaining > 0 && remaining % PersistIntervalSeconds == 0;

    /// <summary>Whether a warning should fire on this exact second. Edge-triggered: <c>true</c> only on the single tick where remaining equals the threshold, so host fires it once not for the whole final stretch.</summary>
    /// <param name="remaining">Current seconds remaining.</param>
    /// <param name="warningMinutes">How long before exhaustion to warn, minutes. Non-positive disables the warning.</param>
    /// <returns><c>true</c> on the threshold second; else <c>false</c>.</returns>
    public static bool WarningFires(int remaining, int warningMinutes) =>
        warningMinutes > 0 && remaining == MinutesToSeconds(warningMinutes);

    /// <summary>Whether budget is spent (no time remains).</summary>
    /// <param name="remaining">Current seconds remaining.</param>
    /// <returns><c>true</c> when <paramref name="remaining"/> is zero or below.</returns>
    public static bool IsExhausted(int remaining) => remaining <= 0;

    /// <summary>Grant extra minutes of screen time. Negative running value = "no budget", so extension starts fresh from zero, not compounding the debt.</summary>
    /// <param name="remaining">Current seconds remaining (may be negative).</param>
    /// <param name="minutes">Minutes to add. Non-positive ignored, leaving a non-negative budget unchanged.</param>
    /// <returns>New seconds remaining, clamped to <see cref="int"/> range.</returns>
    public static int Extend(int remaining, int minutes)
    {
        var added = MinutesToSeconds(minutes);
        var baseline = remaining < 0 ? 0L : remaining;
        return Clamp(baseline + added);
    }

    /// <summary>Minutes to seconds without overflow; negative input = zero, result clamped to <see cref="int"/> range.</summary>
    private static int MinutesToSeconds(int minutes) =>
        minutes <= 0 ? 0 : Clamp(minutes * 60L);

    /// <summary>Clamp a 64-bit second count into the non-negative <see cref="int"/> range.</summary>
    private static int Clamp(long seconds) =>
        (int)Math.Clamp(seconds, 0L, int.MaxValue);
}
