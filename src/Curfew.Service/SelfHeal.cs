namespace Curfew.Service;

/// <summary>
/// Boot-time self-healing: re-registers the overlay's logon scheduled task if it
/// has been removed. The SYSTEM service itself survives a missing task (it keeps
/// running), and full removal still needs admin — but a child deleting the logon
/// task should not permanently stop the overlay from spawning.
/// </summary>
internal static class SelfHeal
{
    public static void EnsureOverlayTask()
    {
        try
        {
            // Already present — nothing to do.
            if (PowerShellRunner.Run($"schtasks /query /tn \"{SessionManager.TaskName}\" 2>$null") == 0)
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
                "-MultipleInstances IgnoreNew -RestartCount 99 -RestartInterval (New-TimeSpan -Minutes 1)\n" +
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
