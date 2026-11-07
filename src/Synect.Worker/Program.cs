using Synect.Infrastructure;
using Synect.Worker;

var builder = Host.CreateApplicationBuilder(args);

// The connection string comes from user-secrets (Development) or ConnectionStrings__Synect.
builder.Services.AddSynectInfrastructure(builder.Configuration);
builder.Services.AddHostedService<SyncOnceWorker>();

var host = builder.Build();
host.Run();
