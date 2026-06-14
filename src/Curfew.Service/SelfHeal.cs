namespace Curfew.Service;

/// <summary>Boot-time self-heal: re-register overlay logon scheduled task if removed, or repair wrong multiple-instances policy from old install. Child deleting logon task no permanently stop overlay spawning.</summary>
internal static class SelfHeal
{
    public static void EnsureOverlayTask()
    {
        try
        {
            // present AND already Parallel AND carries the session-connect triggers -- nothing to do.
            // old installs registered IgnoreNew, leaving every session after first without overlay: first
            // overlay never exits message loop, suppressing later logon triggers and on-demand runs. installs
            // before the session-connect triggers had no relaunch on switching back to an already-logged-on
            // session. re-register any such (and missing) so each interactive session stays covered
            if (PowerShellRunner.Run(
                    $"$t = Get-ScheduledTask -TaskName '{SessionManager.TaskName}' -ErrorAction SilentlyContinue; " +
                    "if ($t -and \"$($t.Settings.MultipleInstances)\" -eq 'Parallel' -and " +
                    "@($t.Triggers | ? { $_.CimClass.CimClassName -eq 'MSFT_TaskSessionStateChangeTrigger' }).Count -ge 1) " +
                    "{ exit 0 }; exit 1") == 0)
                return;

            var servicePath = Environment.ProcessPath; // ...\service\Curfew.Service.exe
            if (servicePath is null) return;
            var installRoot = Path.GetDirectoryName(Path.GetDirectoryName(servicePath)!);
            if (installRoot is null) return;

            var overlay = Path.Combine(installRoot, "overlay", "Curfew.Overlay.exe");
            if (!File.Exists(overlay)) return;

            // mirror installer registration (at-logon + console/remote connect, limited Users principal,
            // auto-restart). at-logon covers each user's sign-in (incl. fast-user-switch); connect triggers
            // relaunch on switching back to / reconnecting an already-logged-on session, which fires no logon
            // event. empty UserId = any user; the per-session mutex blocks duplicates. path from own process
            // path, so trusted; single-quote for spaces (Program Files)
            var script =
                $"$act = New-ScheduledTaskAction -Execute '{overlay}'\n" +
                "$logon = New-ScheduledTaskTrigger -AtLogOn\n" +
                "$cls = Get-CimClass -ClassName MSFT_TaskSessionStateChangeTrigger -Namespace Root/Microsoft/Windows/TaskScheduler\n" +
                "$conn = New-CimInstance -CimClass $cls -ClientOnly; $conn.Enabled = $true; $conn.StateChange = 1\n" +
                "$rconn = New-CimInstance -CimClass $cls -ClientOnly; $rconn.Enabled = $true; $rconn.StateChange = 3\n" +
                "$trg = @($logon, $conn, $rconn)\n" +
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
