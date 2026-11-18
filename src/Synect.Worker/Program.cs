using Synect.Infrastructure;
using Synect.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Under systemd: journald log format and Type=notify; does nothing elsewhere.
builder.Services.AddSystemd();
// Graceful stop: running loads get Sync:Queue:ShutdownDrainTimeout to finish, the rest go back to the queue,
// all within Hosting:ShutdownTimeout (60 s; systemd TimeoutStopSec is longer).
builder.Services.Configure<HostOptions>(o =>
    o.ShutdownTimeout = builder.Configuration.GetValue("Hosting:ShutdownTimeout", TimeSpan.FromSeconds(60)));

// The connection string comes from user-secrets (Development) or ConnectionStrings__Synect (environment / systemd
// EnvironmentFile in Production). Migrations are never applied here: see `Synect.DevTools migrate`.
builder.Services.AddSynectInfrastructure(builder.Configuration);
builder.Services.AddHostedService<SyncWorker>();

var host = builder.Build();
host.Run();
