namespace Curfew.Core;

/// <summary>The persisted failed-attempt state for the lock's brute-force guard.</summary>
/// <param name="FailedAttempts">Consecutive wrong passcode/code entries.</param>
/// <param name="LastAttemptUnix">When the last wrong attempt happened (Unix seconds, UTC).</param>
public readonly record struct LockoutState(int FailedAttempts, long LastAttemptUnix);

/// <summary>
/// Pure brute-force backoff for the lock screen. After a few free tries each
/// further wrong attempt imposes an exponentially growing wait, capped, so a child
/// cannot grind the passcode/unlock code. The counter is owned by the SYSTEM
/// service (written to <c>config.db</c>), so it cannot be reset by the child.
/// </summary>
public static class LockoutPolicy
{
    /// <summary>Wrong attempts allowed before any backoff applies.</summary>
    public const int FreeAttempts = 3;

    /// <summary>Backoff for the first throttled attempt, in seconds.</summary>
    public const int BaseBackoffSeconds = 5;

    /// <summary>Upper bound on the backoff, in seconds.</summary>
    public const int MaxBackoffSeconds = 300;

    /// <summary>
    /// The required wait, in seconds, after <paramref name="failedAttempts"/>
    /// consecutive failures: zero for the first <see cref="FreeAttempts"/>, then
    /// <see cref="BaseBackoffSeconds"/> doubling per extra failure up to
    /// <see cref="MaxBackoffSeconds"/>.
    /// </summary>
    public static int BackoffSeconds(int failedAttempts)
    {
        if (failedAttempts <= FreeAttempts) return 0;

        var over = Math.Min(failedAttempts - FreeAttempts, 16); // bound the shift
        long secs = (long)BaseBackoffSeconds << (over - 1);
        return (int)Math.Min(secs, MaxBackoffSeconds);
    }

    /// <summary>
    /// Whether a new attempt is currently blocked, and if so for how long. A clock
    /// moved backwards only ever increases the remaining wait (fail closed).
    /// </summary>
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
