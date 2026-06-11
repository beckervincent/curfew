using Curfew.Core.Security;
using Curfew.Service;
using Microsoft.Extensions.Logging.EventLog;

// Curfew background service entry point.
//
// Runs as the SYSTEM account as a native Windows service (registered with
// sc.exe / New-Service by installer/setup.iss; the SCM integration comes from
// AddWindowsService() below). Its job is to keep the per-session overlay alive,
// apply the DNS content filter, run the NTP-based time-manipulation guard and
// check for updates. All of that lives in CurfewWorker; this file is only the
// host wiring.
//
// Operational logging goes to the Windows Event Log (a tamper-resistant trail
// for operators). Any failure that escapes Build()/Run() is also appended to
// the on-device diagnostics file (ServiceLog), which is the most reliable place
// to look when the process refuses to start.

// The service name must match the name the installer/uninstaller scripts
// register ("Curfew"). Do not change this literal without updating
// installer/setup.iss in lockstep.
const string ServiceName = "Curfew";

// Harden against DLL injection / hijacking before anything else loads.
ProcessHardening.Apply();

try
{
    ServiceLog.Write("service host starting");

    var builder = Host.CreateApplicationBuilder(args);

    // Integrate with the Windows Service Control Manager. Harmless when the
    // process is launched directly (e.g. for debugging) — it simply no-ops.
    builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);

    // Surface lifecycle and warning events in the Windows Event Log so a
    // locked-down machine still has an audit trail even if the redirected
    // stdout/stderr files are unavailable. Use the service name as the source.
    builder.Logging.AddEventLog(new EventLogSettings { SourceName = ServiceName });

    builder.Services.AddHostedService<CurfewWorker>();

    var host = builder.Build();
    host.Run();

    ServiceLog.Write("service host stopped");
    return 0;
}
catch (Exception ex)
{
    // A throw here means the host never reached its run loop (bad config,
    // missing dependency, etc.). The hosted logger may not be initialised yet,
    // so record it where we can always read it back and let the SCM observe a
    // non-zero exit code.
    ServiceLog.Write($"service host failed to start: {ex}");
    return 1;
}
