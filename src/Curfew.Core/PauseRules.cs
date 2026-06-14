namespace Curfew.Core;

/// <summary>why pause can't start now. <see cref="None"/> = allowed; every other value a distinct block reason</summary>
/// <remarks>integer values part of public contract — keep stable, append new reasons not insert</remarks>
public enum PauseBlock
{
    /// <summary>pausing allowed; nothing blocking</summary>
    None,

    /// <summary>pause feature off in settings</summary>
    Disabled,

    /// <summary>daily pause budget fully consumed</summary>
    BudgetExhausted,

    /// <summary>previous pause ended too recently; cooldown not elapsed</summary>
    Cooldown,

    /// <summary>session not active long enough to earn pause</summary>
    MinActiveTimeNotMet,

    /// <summary>too little screen time left today to bother pausing</summary>
    TimeTooLow,
}

/// <summary>inputs to decide if pausing allowed. all durations seconds, all timestamps Unix seconds (UTC)</summary>
/// <param name="Enabled">pause feature enabled in settings</param>
/// <param name="RemainingSeconds">screen time left today, seconds</param>
/// <param name="PauseUsedSeconds">pause budget used today, seconds</param>
/// <param name="DailyBudgetSeconds">total pause budget for day, seconds</param>
/// <param name="LastPauseEndUnix">when last pause ended (Unix seconds), or 0 if none yet</param>
/// <param name="NowUnix">current time (Unix seconds)</param>
/// <param name="CooldownSeconds">min gap between pauses, seconds</param>
/// <param name="SessionActiveSeconds">how long session active, seconds</param>
/// <param name="MinActiveSeconds">active time required before pause offered, seconds</param>
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

/// <summary>pure pause-eligibility rules, ported from Rust <c>can_pause</c>. UI supplies numbers, this decides verdict. deterministic, side-effect free, thread-safe</summary>
public static class PauseRules
{
    /// <summary>below this remaining screen time no pause offered: pausing last few seconds pointless, just friction</summary>
    public const int MinPausableRemainingSeconds = 60;

    /// <summary>decide if pause may start; <see cref="PauseBlock.None"/> or first block reason. fixed priority: enabled, meaningful time left + budget, cooldown elapsed, session active long enough</summary>
    /// <param name="s">current pause inputs</param>
    /// <returns><see cref="PauseBlock.None"/> if allowed; else highest-priority blocking reason</returns>
    public static PauseBlock CanPause(PauseState s)
    {
        if (!s.Enabled) return PauseBlock.Disabled;
        if (s.RemainingSeconds < MinPausableRemainingSeconds) return PauseBlock.TimeTooLow;
        if (RemainingBudget(s.DailyBudgetSeconds, s.PauseUsedSeconds) <= 0) return PauseBlock.BudgetExhausted;

        // non-positive LastPauseEndUnix = "no pause yet", no cooldown. else cooldown
        // holds until enough wall-clock elapsed. negative gap = clock moved backwards
        // (child rolled it back to escape cooldown): treat as not elapsed, keep
        // blocking, not fail open.
        if (s.LastPauseEndUnix > 0)
        {
            var secondsSinceLastPause = s.NowUnix - s.LastPauseEndUnix;
            if (secondsSinceLastPause < s.CooldownSeconds)
                return PauseBlock.Cooldown;
        }

        if (s.SessionActiveSeconds < s.MinActiveSeconds) return PauseBlock.MinActiveTimeNotMet;

        return PauseBlock.None;
    }

    /// <summary>unused pause budget, seconds (never negative)</summary>
    /// <param name="dailyBudgetSeconds">total pause budget for day, seconds</param>
    /// <param name="usedSeconds">pause budget used today, seconds</param>
    /// <returns>remaining budget, clamped to zero</returns>
    public static int RemainingBudget(int dailyBudgetSeconds, int usedSeconds) =>
        Math.Max(0, dailyBudgetSeconds - usedSeconds);

    /// <summary>longest allowed duration for current pause, seconds (never negative)</summary>
    /// <param name="maxSingleSeconds">cap on single pause, seconds</param>
    /// <param name="dailyBudgetSeconds">total pause budget for day, seconds</param>
    /// <param name="usedSeconds">pause budget used today, seconds</param>
    /// <returns>smaller of single-pause cap and remaining daily budget, clamped to zero</returns>
    public static int MaxPauseDuration(int maxSingleSeconds, int dailyBudgetSeconds, int usedSeconds) =>
        Math.Max(0, Math.Min(maxSingleSeconds, RemainingBudget(dailyBudgetSeconds, usedSeconds)));
}
