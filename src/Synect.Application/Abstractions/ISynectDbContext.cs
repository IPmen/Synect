using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Synect.Domain.Analytics;
using Synect.Domain.Banking;
using Synect.Domain.Counterparties;
using Synect.Domain.Edo;
using Synect.Domain.Raw;
using Synect.Domain.Sync;
using Synect.Domain.Tenants;

namespace Synect.Application.Abstractions;

/// <summary>
/// Persistence surface used by application services. Tenant-scoped sets are filtered
/// to the current tenant; PostgreSQL row-level security enforces the same rule in the database.
/// </summary>
public interface ISynectDbContext
{
    DbSet<Tenant> Tenants { get; }
    DbSet<Connection> Connections { get; }
    DbSet<SyncRun> SyncRuns { get; }
    DbSet<RawObject> RawObjects { get; }
    DbSet<BankAccount> BankAccounts { get; }
    DbSet<BankTransaction> BankTransactions { get; }
    DbSet<BankDailyBalance> BankDailyBalances { get; }
    DbSet<EdoDocument> EdoDocuments { get; }
    DbSet<EdoDocumentStatusLog> EdoDocumentStatusLogs { get; }
    DbSet<Counterparty> Counterparties { get; }
    DbSet<SyncJob> SyncJobs { get; }
    DbSet<ConnectionSecret> ConnectionSecrets { get; }
    DbSet<CounterpartyMonthly> CounterpartyMonthly { get; }

    DatabaseFacade Database { get; }
    ChangeTracker ChangeTracker { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
