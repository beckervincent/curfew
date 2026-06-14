namespace Curfew.Service;

/// <summary>
/// Boot-time self-healing: re-registers the overlay's logon scheduled task if it
/// has been removed, or repairs it if an older install left it with the wrong
/// multiple-instances policy. The SYSTEM service itself survives a missing task
/// (it keeps running), and full removal still needs admin — but a child deleting
/// the logon task should not permanently stop the overlay from spawning.
/// </summary>
internal static class SelfHeal
{
    public static void EnsureOverlayTask()
    {
        try
        {
            // Present AND already using the Parallel multiple-instances policy —
            // nothing to do. Older installs registered the task IgnoreNew, which left
            // every session after the first without an overlay: the first session's
            // overlay never exits its message loop, so that single running instance
            // suppressed every later logon trigger and on-demand run. Re-register such
            // tasks (and missing ones) so each interactive session is covered.
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

            // Mirror the installer's registration (at-logon, limited Users principal,
            // auto-restart). The path comes from our own process path, so it is
            // trusted; single-quote it to tolerate spaces (Program Files).
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
