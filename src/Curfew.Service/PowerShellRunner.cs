using System.Diagnostics;
using System.Text;

namespace Curfew.Service;

/// <summary>Run PowerShell script hidden, pipe over stdin to dodge temp files and quoting. Return exit code, or <c>-1</c> when script no launch, timed out and killed, or failed to complete.</summary>
/// <remarks>Drain output streams continuously; else script writing past OS pipe buffer (few KB) blocks on write while this blocks on <c>WaitForExit</c> — deadlock. stderr logged on failure for on-device diagnosis (hosted logger hard to see as Windows service).</remarks>
internal static class PowerShellRunner
{
    /// <summary>Sentinel returned when script no run to completion.</summary>
    public const int FailureExitCode = -1;

    /// <summary>Hard wall-clock cap on one run. Hung script (e.g. blocked on prompt <c>-NonInteractive</c> failed to suppress) must never stall service poll loop.</summary>
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Max stderr length logged on failure, to keep log bounded.</summary>
    private const int MaxLoggedStdErr = 2000;

    /// <summary>Execute <paramref name="script"/> via <c>powershell.exe -Command -</c>.</summary>
    /// <param name="script">PowerShell source to pipe over stdin. null/blank is no-op, reported as failure.</param>
    /// <returns>Process exit code, or <see cref="FailureExitCode"/> on any failure.</returns>
    public static int Run(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            ServiceLog.Write("PowerShellRunner: refused to run an empty script");
            return FailureExitCode;
        }

        // -EncodedCommand (base64 UTF-16LE) NOT "-Command -" over stdin: a multi-line script piped to
        // "-Command -" was observed to run and exit 0 yet silently skip CIM cmdlet side effects
        // (Set-DnsClientServerAddress / firewall / w32tm did nothing), so the DNS content filter never
        // actually applied. EncodedCommand runs the script in a normal full context exactly like a .ps1,
        // and also sidesteps all quoting. -NonInteractive: never prompt; -ExecutionPolicy Bypass: ignore
        // machine policy for this invocation.
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NonInteractive -NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            RedirectStandardInput = false,
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

            // drain both streams concurrently so chatty script no deadlock on full pipe buffer. keep stderr for diagnostics
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

            // script is in -EncodedCommand; nothing to feed over stdin

            if (!process.WaitForExit((int)RunTimeout.TotalMilliseconds))
            {
                // kill whole tree: script may have spawned schtasks/w32tm etc
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                ServiceLog.Write(
                    $"PowerShellRunner: timed out after {RunTimeout.TotalSeconds:0}s; killed");
                return FailureExitCode;
            }

            // let async readers flush buffered tail before reading stderr
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
