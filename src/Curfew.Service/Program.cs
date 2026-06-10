using Curfew.Service;

// Curfew background service. Runs as SYSTEM (via nssm), spawns the per-session
// tray app and drives automatic update checks. Session/spawn logic lands in a
// later milestone; this is the hosting skeleton.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "Curfew");
builder.Services.AddHostedService<CurfewWorker>();

var host = builder.Build();
host.Run();
