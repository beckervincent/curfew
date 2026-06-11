namespace Curfew.Core;

/// <summary>
/// Why a pause cannot start right now. <see cref="None"/> means a pause is
/// permitted; every other value is a distinct reason it is blocked.
/// </summary>
/// <remarks>
/// The integer values of these members are part of the public contract — they
/// must stay stable, so new reasons should be appended rather than inserted.
/// </remarks>
public enum PauseBlock
{
    /// <summary>Pausing is allowed; nothing is blocking it.</summary>
    None,

    /// <summary>The pause feature is turned off in settings.</summary>
    Disabled,

    /// <summary>The daily pause budget has been fully consumed.</summary>
    BudgetExhausted,

    /// <summary>A previous pause ended too recently; the cooldown has not elapsed.</summary>
    Cooldown,

    /// <summary>The current session has not been active long enough to earn a pause.</summary>
    MinActiveTimeNotMet,

    /// <summary>Too little screen time remains today to be worth pausing.</summary>
    TimeTooLow,
}

/// <summary>
/// Inputs needed to decide whether pausing is allowed. All durations are in
/// seconds and all timestamps are Unix seconds (UTC).
/// </summary>
/// <param name="Enabled">Whether the pause feature is enabled in settings.</param>
/// <param name="RemainingSeconds">Screen time left today, in seconds.</param>
/// <param name="PauseUsedSeconds">Pause budget already consumed today, in seconds.</param>
/// <param name="DailyBudgetSeconds">Total pause budget for the day, in seconds.</param>
/// <param name="LastPauseEndUnix">When the last pause ended (Unix seconds), or 0 if none yet.</param>
/// <param name="NowUnix">The current time (Unix seconds).</param>
/// <param name="CooldownSeconds">Minimum gap required between pauses, in seconds.</param>
/// <param name="SessionActiveSeconds">How long the current session has been active, in seconds.</param>
/// <param name="MinActiveSeconds">Active time required before a pause is offered, in seconds.</param>
public readonly record struct PauseState(
    bool Enabled,
    int RemainingSeconds,
    int PauseUsedSeconds,
    int DailyBudgetSeconds,
    long LastPauseEndUnix,
    long NowUnix,
    int CooldownSeconds,
    int SessionActiveSeconds,
    int MinActiveSeconds);

/// <summary>
/// Pure pause-eligibility rules, ported from the Rust <c>can_pause</c>. The UI
/// layer supplies the current numbers; this decides the verdict. Every method is
/// deterministic and side-effect free, so it is safe to call from any thread.
/// </summary>
public static class PauseRules
{
    /// <summary>
    /// Below this much remaining screen time a pause is not offered: pausing the
    /// last few seconds of the day is pointless and just adds friction.
    /// </summary>
    public const int MinPausableRemainingSeconds = 60;

    /// <summary>
    /// Decides whether a pause may start, returning <see cref="PauseBlock.None"/>
    /// when it may or the first reason it may not. Reasons are evaluated in a fixed
    /// priority order: the feature must be enabled, there must be meaningful screen
    /// time left and remaining pause budget, any cooldown from the previous pause
    /// must have elapsed, and the session must have been active long enough.
    /// </summary>
    /// <param name="s">The current pause inputs.</param>
    /// <returns>
    /// <see cref="PauseBlock.None"/> if pausing is allowed; otherwise the highest-priority
    /// blocking reason.
    /// </returns>
    public static PauseBlock CanPause(PauseState s)
    {
        if (!s.Enabled) return PauseBlock.Disabled;
        if (s.RemainingSeconds < MinPausableRemainingSeconds) return PauseBlock.TimeTooLow;
        if (RemainingBudget(s.DailyBudgetSeconds, s.PauseUsedSeconds) <= 0) return PauseBlock.BudgetExhausted;

        // A non-positive LastPauseEndUnix means "no pause yet", so no cooldown applies.
        // Otherwise the cooldown holds until enough wall-clock has elapsed. A
        // negative gap means the clock moved backwards (e.g. a child rolled it back
        // to escape the cooldown): treat that as "cooldown not yet elapsed" and keep
        // blocking, rather than failing open.
        if (s.LastPauseEndUnix > 0)
        {
            var secondsSinceLastPause = s.NowUnix - s.LastPauseEndUnix;
            if (secondsSinceLastPause < s.CooldownSeconds)
                return PauseBlock.Cooldown;
        }

        if (s.SessionActiveSeconds < s.MinActiveSeconds) return PauseBlock.MinActiveTimeNotMet;

        return PauseBlock.None;
    }

    /// <summary>Unused pause budget in seconds (never negative).</summary>
    /// <param name="dailyBudgetSeconds">Total pause budget for the day, in seconds.</param>
    /// <param name="usedSeconds">Pause budget already consumed today, in seconds.</param>
    /// <returns>The remaining budget, clamped to zero.</returns>
    public static int RemainingBudget(int dailyBudgetSeconds, int usedSeconds) =>
        Math.Max(0, dailyBudgetSeconds - usedSeconds);

    /// <summary>Longest allowed duration for the current pause, in seconds (never negative).</summary>
    /// <param name="maxSingleSeconds">Cap on a single pause, in seconds.</param>
    /// <param name="dailyBudgetSeconds">Total pause budget for the day, in seconds.</param>
    /// <param name="usedSeconds">Pause budget already consumed today, in seconds.</param>
    /// <returns>
    /// The smaller of the single-pause cap and the remaining daily budget, clamped to zero.
    /// </returns>
    public static int MaxPauseDuration(int maxSingleSeconds, int dailyBudgetSeconds, int usedSeconds) =>
        Math.Max(0, Math.Min(maxSingleSeconds, RemainingBudget(dailyBudgetSeconds, usedSeconds)));
}
