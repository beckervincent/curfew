namespace Curfew.Service;

/// <summary>Boot-time self-heal: re-register overlay logon scheduled task if removed, or repair wrong multiple-instances policy from old install. Child deleting logon task no permanently stop overlay spawning.</summary>
internal static class SelfHeal
{
    public static void EnsureOverlayTask()
    {
        try
        {
            // present AND already Parallel — nothing to do. old installs registered IgnoreNew, leaving every session after first without overlay: first overlay never exits message loop, suppressing later logon triggers and on-demand runs. re-register such (and missing) so each interactive session covered
            if (PowerShellRunner.Run(
                    $"$t = Get-ScheduledTask -TaskName '{SessionManager.TaskName}' -ErrorAction SilentlyContinue; " +
                    "if ($t -and \"$($t.Settings.MultipleInstances)\" -eq 'Parallel') { exit 0 }; exit 1") == 0)
                return;

            var servicePath = Environment.ProcessPath; // ...\service\Curfew.Service.exe
            if (servicePath is null) return;
            var installRoot = Path.GetDirectoryName(Path.GetDirectoryName(servicePath)!);
            if (installRoot is null) return;

            var overlay = Path.Combine(installRoot, "overlay", "Curfew.Overlay.exe");
            if (!File.Exists(overlay)) return;

            // mirror installer registration (at-logon, limited Users principal, auto-restart). path from own process path, so trusted; single-quote for spaces (Program Files)
            var script =
                $"$act = New-ScheduledTaskAction -Execute '{overlay}'\n" +
                "$trg = New-ScheduledTaskTrigger -AtLogOn\n" +
                "$prn = New-ScheduledTaskPrincipal -GroupId 'S-1-5-32-545' -RunLevel Limited\n" +
                "$set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries " +
                "-MultipleInstances Parallel -RestartCount 99 -RestartInterval (New-TimeSpan -Minutes 1)\n" +
                "$set.ExecutionTimeLimit = 'PT0S'\n" +
                $"Register-ScheduledTask -TaskName '{SessionManager.TaskName}' -Action $act -Trigger $trg " +
                "-Principal $prn -Settings $set -Force | Out-Null";

            var rc = PowerShellRunner.Run(script);
            ServiceLog.Write(rc == 0
                ? "self-heal: re-registered the overlay logon task"
                : $"self-heal: re-registration failed (exit {rc})");
        }
        catch (Exception ex)
        {
            ServiceLog.Write($"self-heal: {ex.Message}");
        }
    }
}
