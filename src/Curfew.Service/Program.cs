using Curfew.Core.Security;
using Curfew.Service;
using Microsoft.Extensions.Logging.EventLog;

// Curfew background service entry point
//
// Runs as SYSTEM, native Windows service (registered sc.exe / New-Service by installer/setup.iss; SCM via AddWindowsService() below). Keeps per-session overlay alive, applies DNS content filter, runs NTP time-manipulation guard, checks updates. Logic in CurfewWorker; this is just host wiring.
//
// Operational logging to Windows Event Log (tamper-resistant trail). Failures escaping Build()/Run() also appended to on-device ServiceLog, most reliable place when process refuses to start.

// service name must match installer/uninstaller scripts ("Curfew"). no change without updating installer/setup.iss in lockstep
const string ServiceName = "Curfew";

// harden against DLL injection / hijacking before anything else loads
ProcessHardening.Apply();

try
{
    ServiceLog.Write("service host starting");

    var builder = Host.CreateApplicationBuilder(args);

    // integrate with Windows Service Control Manager. harmless when launched directly (debugging) — no-ops
    builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);

    // surface lifecycle/warning events in Windows Event Log so locked-down machine has audit trail even if redirected stdout/stderr unavailable. service name as source
    builder.Logging.AddEventLog(new EventLogSettings { SourceName = ServiceName });

    builder.Services.AddHostedService<CurfewWorker>();

    var host = builder.Build();
    host.Run();

    ServiceLog.Write("service host stopped");
    return 0;
}
catch (Exception ex)
{
    // throw here = host never reached run loop (bad config, missing dependency). hosted logger maybe not initialised, so record where we can always read back and let SCM see non-zero exit
    ServiceLog.Write($"service host failed to start: {ex}");
    return 1;
}
