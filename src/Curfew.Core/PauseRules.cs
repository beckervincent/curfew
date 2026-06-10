namespace Curfew.Core;

/// <summary>Why a pause cannot start right now.</summary>
public enum PauseBlock
{
    None,
    Disabled,
    BudgetExhausted,
    Cooldown,
    MinActiveTimeNotMet,
    TimeTooLow,
}

/// <summary>Inputs needed to decide whether pausing is allowed.</summary>
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
/// layer supplies the current numbers; this decides the verdict.
/// </summary>
public static class PauseRules
{
    public static PauseBlock CanPause(PauseState s)
    {
        if (!s.Enabled) return PauseBlock.Disabled;
        if (s.RemainingSeconds < 60) return PauseBlock.TimeTooLow;
        if (s.PauseUsedSeconds >= s.DailyBudgetSeconds) return PauseBlock.BudgetExhausted;

        if (s.LastPauseEndUnix > 0)
        {
            var sinceLast = s.NowUnix - s.LastPauseEndUnix;
            if (sinceLast < s.CooldownSeconds) return PauseBlock.Cooldown;
        }

        if (s.SessionActiveSeconds < s.MinActiveSeconds) return PauseBlock.MinActiveTimeNotMet;

        return PauseBlock.None;
    }

    /// <summary>Unused pause budget in seconds (never negative).</summary>
    public static int RemainingBudget(int dailyBudgetSeconds, int usedSeconds) =>
        Math.Max(0, dailyBudgetSeconds - usedSeconds);

    /// <summary>Longest allowed duration for the current pause.</summary>
    public static int MaxPauseDuration(int maxSingleSeconds, int dailyBudgetSeconds, int usedSeconds) =>
        Math.Min(maxSingleSeconds, RemainingBudget(dailyBudgetSeconds, usedSeconds));
}
