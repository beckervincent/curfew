namespace Curfew.Core;

/// <summary>persisted failed-attempt state for lock brute-force guard</summary>
/// <param name="FailedAttempts">consecutive wrong passcode/code entries</param>
/// <param name="LastAttemptUnix">when last wrong attempt happened (Unix seconds, UTC)</param>
public readonly record struct LockoutState(int FailedAttempts, long LastAttemptUnix);

/// <summary>pure brute-force backoff for lock screen; after free tries each wrong attempt waits exponentially longer, capped. counter owned by SYSTEM service (<c>config.db</c>), child can't reset</summary>
public static class LockoutPolicy
{
    /// <summary>wrong attempts allowed before backoff</summary>
    public const int FreeAttempts = 3;

    /// <summary>backoff for first throttled attempt, seconds</summary>
    public const int BaseBackoffSeconds = 5;

    /// <summary>upper bound on backoff, seconds</summary>
    public const int MaxBackoffSeconds = 300;

    /// <summary>required wait (seconds) after <paramref name="failedAttempts"/> failures: zero for first <see cref="FreeAttempts"/>, then <see cref="BaseBackoffSeconds"/> doubling per extra failure up to <see cref="MaxBackoffSeconds"/></summary>
    public static int BackoffSeconds(int failedAttempts)
    {
        if (failedAttempts <= FreeAttempts) return 0;

        var over = Math.Min(failedAttempts - FreeAttempts, 16); // bound shift
        long secs = (long)BaseBackoffSeconds << (over - 1);
        return (int)Math.Min(secs, MaxBackoffSeconds);
    }

    /// <summary>is new attempt blocked, and for how long. clock moved backwards only increases remaining wait (fail closed)</summary>
    public static bool IsLockedOut(LockoutState state, long nowUnix, out int retryAfterSeconds)
    {
        var backoff = BackoffSeconds(state.FailedAttempts);
        var remaining = (int)(state.LastAttemptUnix + backoff - nowUnix);

        if (backoff > 0 && remaining > 0)
        {
            retryAfterSeconds = remaining;
            return true;
        }

        retryAfterSeconds = 0;
        return false;
    }
}
