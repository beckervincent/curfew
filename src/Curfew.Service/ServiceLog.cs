namespace Curfew.Service;

/// <summary>Minimal dependency-free file logger writing to SYSTEM-writable <c>%ProgramData%\Curfew\service.log</c>.</summary>
/// <remarks>
/// Hosted <see cref="Microsoft.Extensions.Logging.ILogger"/> output hard to see as Windows service, so this is on-device diagnostics surviving restarts.
/// <para>Constraints:
/// <list type="bullet">
/// <item>Logging <b>never</b> throws — diagnostics failing no take down service; every op wrapped best-effort.</item>
/// <item>Writes serialized with process-wide lock. Cross-process contention not expected; OS append keeps lines intact anyway.</item>
/// <item>File size-capped so no unbounded growth on long-running machine.</item>
/// </list>
/// </para>
/// </remarks>
internal static class ServiceLog
{
    /// <summary>Serialize writes (and rotation) within this process.</summary>
    private static readonly object Gate = new();

    /// <summary>Log file name within Curfew data dir.</summary>
    private const string LogFileName = "service.log";

    /// <summary>Max size of active log file before rotation. Small bounds disk while keeping enough history for recent incidents.</summary>
    private const long MaxLogBytes = 1 * 1024 * 1024; // 1 MiB

    /// <summary>Full path to log file, computed once. <see langword="null"/> only if path resolution failed (extremely unlikely).</summary>
    private static readonly string? LogFilePath = ResolveLogFilePath();

    /// <summary>Append timestamped line to service log. Never throws; failures silently ignored so diagnostics no disrupt service.</summary>
    /// <param name="message">Message to record. <see langword="null"/> treated as empty.</param>
    public static void Write(string message)
    {
        var path = LogFilePath;
        if (path is null)
        {
            return; // path resolution failed earlier; nothing safe to do
        }

        // build line outside lock to keep critical section short
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
            // diagnostics never throw. swallow IO, ACL, disk-full errors
        }
    }

    /// <summary>Overload recording exception with type and message, more actionable than message alone.</summary>
    /// <param name="context">Short description of what was attempted.</param>
    /// <param name="ex">Exception to record.</param>
    public static void Write(string context, Exception ex)
    {
        // defensive: never let malformed arg escape a logging call
        var detail = ex is null
            ? "(null exception)"
            : $"{ex.GetType().Name}: {ex.Message}";
        Write($"{context}: {detail}");
    }

    /// <summary>Compute log file path under <c>%ProgramData%\Curfew</c>, creating dir if needed. Return <see langword="null"/> if path no resolve (logging becomes no-op).</summary>
    private static string? ResolveLogFilePath()
    {
        try
        {
            // prefer well-known folder API; fall back to env var, then hard-coded default for unusual/locked-down installs
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
            // if resolving/creating dir fails, disable logging not risk throwing from every Write
            return null;
        }
    }

    /// <summary>Rotate log when over <see cref="MaxLogBytes"/> by moving current file to <c>service.log.1</c> (replacing prior backup) so live file starts fresh. Best-effort: failure leaves file in place, ignored.</summary>
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
            // File.Move overwrite atomic enough, avoids delete/copy race window
            File.Move(path, backup, overwrite: true);
        }
        catch
        {
            // rotation non-critical; on failure keep appending
        }
    }
}
