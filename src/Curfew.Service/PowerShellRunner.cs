using System.Diagnostics;
using System.Text;

namespace Curfew.Service;

/// <summary>
/// Runs a PowerShell script hidden, piping it over stdin to avoid temp files and
/// quoting issues. Returns the process exit code, or <c>-1</c> when the script
/// could not be launched, timed out and was killed, or otherwise failed to run to
/// completion.
/// </summary>
/// <remarks>
/// Output streams are drained continuously while the script runs. Without that,
/// a script that writes more than the OS pipe buffer (a few KB) to stdout/stderr
/// would block on the write while this side blocked on <c>WaitForExit</c> — a
/// classic deadlock that the timeout would only paper over. Captured stderr is
/// logged on failure so problems are diagnosable on-device (the hosted logger is
/// not easily visible when the process runs as a Windows service).
/// </remarks>
internal static class PowerShellRunner
{
    /// <summary>Sentinel returned when the script could not be run to completion.</summary>
    public const int FailureExitCode = -1;

    /// <summary>
    /// Hard wall-clock cap on a single run. A hung script (e.g. one blocked on an
    /// interactive prompt that <c>-NonInteractive</c> failed to suppress) must
    /// never be allowed to stall the service's poll loop.
    /// </summary>
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Maximum captured stderr length logged on failure, to keep the log bounded.</summary>
    private const int MaxLoggedStdErr = 2000;

    /// <summary>
    /// Executes <paramref name="script"/> via <c>powershell.exe -Command -</c>.
    /// </summary>
    /// <param name="script">The PowerShell source to pipe over stdin. A null or
    /// blank script is treated as a no-op and reported as a failure.</param>
    /// <returns>The process exit code, or <see cref="FailureExitCode"/> on any failure.</returns>
    public static int Run(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            ServiceLog.Write("PowerShellRunner: refused to run an empty script");
            return FailureExitCode;
        }

        // -NonInteractive: never prompt; -ExecutionPolicy Bypass: ignore machine
        // policy for this invocation only; -Command -: read the script from stdin.
        var psi = new ProcessStartInfo("powershell.exe",
            "-NonInteractive -NoProfile -ExecutionPolicy Bypass -Command -")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process is null)
            {
                ServiceLog.Write("PowerShellRunner: failed to start powershell.exe");
                return FailureExitCode;
            }

            // Drain both output streams concurrently so a chatty script can never
            // deadlock against a full pipe buffer. We keep stderr for diagnostics.
            var stdErr = new StringBuilder();
            process.OutputDataReceived += static (_, _) => { /* discard stdout */ };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (stdErr) { stdErr.AppendLine(e.Data); }
                }
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Feed the script and signal EOF so PowerShell begins executing. A
            // process that has already died (e.g. it crashed on startup) closes the
            // pipe, surfacing here as an IOException — swallow it and let the exit
            // code / timeout handling below report the outcome.
            try
            {
                process.StandardInput.Write(script);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // Broken pipe: the child is gone. WaitForExit will return promptly.
            }

            if (!process.WaitForExit((int)RunTimeout.TotalMilliseconds))
            {
                // Kill the whole tree: the script may have spawned schtasks/w32tm etc.
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                ServiceLog.Write(
                    $"PowerShellRunner: timed out after {RunTimeout.TotalSeconds:0}s; killed");
                return FailureExitCode;
            }

            // Let the async readers flush any buffered tail before we read stderr.
            process.WaitForExit();

            var exitCode = process.ExitCode;
            if (exitCode != 0)
            {
                string err;
                lock (stdErr) { err = stdErr.ToString().Trim(); }
                if (err.Length > MaxLoggedStdErr)
                {
                    err = err[..MaxLoggedStdErr] + " …(truncated)";
                }
                ServiceLog.Write(err.Length == 0
                    ? $"PowerShellRunner: exited with code {exitCode}"
                    : $"PowerShellRunner: exited with code {exitCode}: {err}");
            }

            return exitCode;
        }
        catch (Exception ex)
        {
            ServiceLog.Write($"PowerShellRunner: launch/run error: {ex.Message}");
            return FailureExitCode;
        }
        finally
        {
            process?.Dispose();
        }
    }
}
