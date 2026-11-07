using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Synect.Application.Abstractions;

namespace Synect.Infrastructure.Persistence;

/// <summary>
/// Used by `dotnet ef`. Generating migrations needs no database; for `database update`
/// pass the connection string explicitly: `dotnet ef database update --connection "..."`
/// or set the SYNECT_DB environment variable. No password is stored in the repository.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SynectDbContext>
{
    public SynectDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("SYNECT_DB")
            ?? "Host=localhost;Port=5432;Database=synect;Username=postgres";
        var options = new DbContextOptionsBuilder<SynectDbContext>()
            .UseSynectNpgsql(connectionString)
            .Options;
        return new SynectDbContext(options, new TenantContext());
    }
}
