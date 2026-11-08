using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Synect.Application.Abstractions;

namespace Synect.Infrastructure.Persistence;

/// <summary>
/// Used by `dotnet ef`. Generating migrations needs no database. For `database update` the owner
/// connection string is taken from the SYNECT_DB environment variable, else from the shared
/// user-secrets store (ConnectionStrings:SynectOwner, id "synect-local-dev"), else `--connection`.
/// No password is stored in the repository.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SynectDbContext>
{
    public const string UserSecretsId = "synect-local-dev";

    public SynectDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("SYNECT_DB")
            ?? new ConfigurationBuilder().AddUserSecrets(UserSecretsId).Build().GetConnectionString("SynectOwner")
            ?? "Host=localhost;Port=5432;Database=synect;Username=synect_owner";
        var options = new DbContextOptionsBuilder<SynectDbContext>()
            .UseSynectNpgsql(connectionString)
            .Options;
        return new SynectDbContext(options, new TenantContext());
    }
}
