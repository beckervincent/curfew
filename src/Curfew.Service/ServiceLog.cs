namespace Curfew.Service;

/// <summary>
/// Minimal, dependency-free file logger that writes to a SYSTEM-writable file
/// under <c>%ProgramData%\Curfew\service.log</c>.
/// </summary>
/// <remarks>
/// The standard hosted <see cref="Microsoft.Extensions.Logging.ILogger"/> output
/// is not easily visible when the worker runs under <c>nssm</c> as a Windows
/// service, so this provides simple on-device diagnostics that survive restarts.
/// <para>
/// Design constraints:
/// <list type="bullet">
/// <item>Logging must <b>never</b> throw — diagnostics failing must not take down
/// the service, so every operation is wrapped and best-effort.</item>
/// <item>Writes are serialized with a process-wide lock. Cross-process contention
/// (multiple service instances) is not expected; the OS append still keeps lines
/// intact even if it occurred.</item>
/// <item>The file is size-capped so it cannot grow without bound on a long-running
/// machine.</item>
/// </list>
/// </para>
/// </remarks>
internal static class ServiceLog
{
    /// <summary>Serializes writes (and rotation) within this process.</summary>
    private static readonly object Gate = new();

    /// <summary>Log file name within the Curfew data directory.</summary>
    private const string LogFileName = "service.log";

    /// <summary>
    /// Maximum size of the active log file before it is rotated. Keeping this
    /// small bounds disk usage while retaining enough history to diagnose the
    /// most recent incidents.
    /// </summary>
    private const long MaxLogBytes = 1 * 1024 * 1024; // 1 MiB

    /// <summary>
    /// Resolved full path to the log file, computed once. <see langword="null"/>
    /// only if path resolution itself failed (extremely unlikely).
    /// </summary>
    private static readonly string? LogFilePath = ResolveLogFilePath();

    /// <summary>
    /// Appends a timestamped line to the service log. Never throws; failures are
    /// silently ignored so diagnostics can never disrupt the service.
    /// </summary>
    /// <param name="message">The message to record. <see langword="null"/> is treated as empty.</param>
    public static void Write(string message)
    {
        var path = LogFilePath;
        if (path is null)
        {
            return; // Path resolution failed earlier; nothing we can safely do.
        }

        // Build the line outside the lock to keep the critical section short.
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message ?? string.Empty}{Environment.NewLine}";

        try
        {
            lock (Gate)
            {
                RotateIfTooLarge(path);
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Diagnostics must never throw. Swallow IO, ACL and disk-full errors.
        }
    }

    /// <summary>
    /// Convenience overload that records an exception with its type and message,
    /// giving more actionable detail than the message alone.
    /// </summary>
    /// <param name="context">A short description of what was being attempted.</param>
    /// <param name="ex">The exception to record.</param>
    public static void Write(string context, Exception ex)
    {
        // Defensive: never let a malformed argument escape from a logging call.
        var detail = ex is null
            ? "(null exception)"
            : $"{ex.GetType().Name}: {ex.Message}";
        Write($"{context}: {detail}");
    }

    /// <summary>
    /// Computes the log file path under <c>%ProgramData%\Curfew</c>, creating the
    /// directory if necessary. Returns <see langword="null"/> if the path cannot
    /// be resolved at all (in which case logging becomes a no-op).
    /// </summary>
    private static string? ResolveLogFilePath()
    {
        try
        {
            // Prefer the well-known folder API; fall back to the environment
            // variable and finally a hard-coded default for resilience on
            // unusual or locked-down installs.
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(programData))
            {
                programData = Environment.GetEnvironmentVariable("ProgramData");
            }
            if (string.IsNullOrWhiteSpace(programData))
            {
                programData = @"C:\ProgramData";
            }

            var dir = Path.Combine(programData, "Curfew");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, LogFileName);
        }
        catch
        {
            // If even resolving/creating the directory fails, disable logging
            // rather than risk throwing from every Write call.
            return null;
        }
    }

    /// <summary>
    /// Rotates the log when it exceeds <see cref="MaxLogBytes"/> by moving the
    /// current file to <c>service.log.1</c> (replacing any previous backup), so
    /// the live file always starts fresh. Best-effort: any failure leaves the
    /// existing file in place and is ignored.
    /// </summary>
    /// <remarks>Callers must hold <see cref="Gate"/>.</remarks>
    private static void RotateIfTooLarge(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxLogBytes)
            {
                return;
            }

            var backup = path + ".1";
            // File.Move with overwrite is atomic enough for our needs and avoids
            // a delete/copy race window.
            File.Move(path, backup, overwrite: true);
        }
        catch
        {
            // Rotation is non-critical; on failure we simply keep appending.
        }
    }
}
