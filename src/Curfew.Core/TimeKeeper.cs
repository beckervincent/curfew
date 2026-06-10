namespace Curfew.Core;

/// <summary>
/// Pure countdown state machine: one tick per second decrements the remaining
/// time, decides when to persist, when warnings fire, and when the limit is
/// reached. The host (the app) owns the timer and the stored value.
/// </summary>
public static class TimeKeeper
{
    /// <summary>Seconds remaining at startup: today's saved value, or a fresh
    /// day's limit in minutes.</summary>
    public static int InitialRemaining(int? savedSeconds, int dailyLimitMinutes) =>
        savedSeconds ?? dailyLimitMinutes * 60;

    /// <summary>One second elapsed; never goes below zero.</summary>
    public static int Tick(int remaining) => Math.Max(0, remaining - 1);

    /// <summary>Persist periodically (every 30 s) to limit database writes.</summary>
    public static bool ShouldPersist(int remaining) => remaining > 0 && remaining % 30 == 0;

    /// <summary>True on the exact second a warning threshold is crossed.</summary>
    public static bool WarningFires(int remaining, int warningMinutes) =>
        warningMinutes > 0 && remaining == warningMinutes * 60;

    /// <summary>The limit is reached when no time remains.</summary>
    public static bool IsExhausted(int remaining) => remaining <= 0;

    /// <summary>Add minutes of extension; a negative running value starts fresh.</summary>
    public static int Extend(int remaining, int minutes)
    {
        var added = minutes * 60;
        return remaining < 0 ? added : remaining + added;
    }
}
