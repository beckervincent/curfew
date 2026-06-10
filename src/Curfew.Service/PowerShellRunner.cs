using System.Diagnostics;

namespace Curfew.Service;

/// <summary>Runs a PowerShell script hidden, piping it over stdin to avoid
/// temp files and quoting issues. Returns the process exit code (-1 on failure
/// to launch).</summary>
internal static class PowerShellRunner
{
    public static int Run(string script)
    {
        var psi = new ProcessStartInfo("powershell.exe",
            "-NonInteractive -ExecutionPolicy Bypass -Command -")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return -1;
            process.StandardInput.Write(script);
            process.StandardInput.Close();

            // Never block forever — a hung script must not stall the service.
            if (!process.WaitForExit(60_000))
            {
                try { process.Kill(true); } catch { /* ignore */ }
                return -1;
            }
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
