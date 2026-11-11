using Microsoft.EntityFrameworkCore;
using Synect.Application.Abstractions;
using Synect.Domain.Access;
using Synect.Domain.Accounts;
using Synect.Domain.Analytics;
using Synect.Domain.Attention;
using Synect.Domain.Banking;
using Synect.Domain.Common;
using Synect.Domain.Counterparties;
using Synect.Domain.Edo;
using Synect.Domain.Raw;
using Synect.Domain.Reconciliation;
using Synect.Domain.Sync;
using Synect.Domain.Tenants;

namespace Synect.Infrastructure.Persistence;

/// <summary>
/// Synect database. Tenant isolation is enforced twice:
/// 1) EF global query filters and SaveChanges checks (this class);
/// 2) PostgreSQL row-level security driven by app.tenant_id, set with SET LOCAL at transaction start
///    (<see cref="TenantSessionInterceptor"/>).
/// </summary>
public sealed class SynectDbContext(DbContextOptions<SynectDbContext> options, ITenantContext tenantContext)
    : DbContext(options), ISynectDbContext
{
    /// <summary>Current tenant id, or Guid.Empty (matches nothing) when no tenant is set.</summary>
    public Guid CurrentTenantId => tenantContext.TenantId ?? Guid.Empty;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Connection> Connections => Set<Connection>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();
    public DbSet<RawObject> RawObjects => Set<RawObject>();
    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();
    public DbSet<BankTransaction> BankTransactions => Set<BankTransaction>();
    public DbSet<BankDailyBalance> BankDailyBalances => Set<BankDailyBalance>();
    public DbSet<EdoDocument> EdoDocuments => Set<EdoDocument>();
    public DbSet<EdoDocumentStatusLog> EdoDocumentStatusLogs => Set<EdoDocumentStatusLog>();
    public DbSet<Counterparty> Counterparties => Set<Counterparty>();
    public DbSet<SyncJob> SyncJobs => Set<SyncJob>();
    public DbSet<ConnectionSecret> ConnectionSecrets => Set<ConnectionSecret>();
    public DbSet<CounterpartyMonthly> CounterpartyMonthly => Set<CounterpartyMonthly>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<CounterpartyAlias> CounterpartyAliases => Set<CounterpartyAlias>();
    public DbSet<ToolAuditLog> ToolAuditLogs => Set<ToolAuditLog>();
    public DbSet<ReconciliationRun> ReconciliationRuns => Set<ReconciliationRun>();
    public DbSet<ReconciliationMatch> ReconciliationMatches => Set<ReconciliationMatch>();
    public DbSet<ReconciliationMatchItem> ReconciliationMatchItems => Set<ReconciliationMatchItem>();
    public DbSet<ReconciliationFinding> ReconciliationFindings => Set<ReconciliationFinding>();
    public DbSet<ReconciliationMatchDecision> ReconciliationMatchDecisions => Set<ReconciliationMatchDecision>();
    public DbSet<AttentionItem> AttentionItems => Set<AttentionItem>();
    public DbSet<AttentionItemEvent> AttentionItemEvents => Set<AttentionItemEvent>();
    public DbSet<StatementImport> StatementImports => Set<StatementImport>();

    // Account tables (stage 6.5). The application role has no access to them (no grants, row-level security without a
    // policy); only the owner (DevTools administration) and the SECURITY DEFINER functions read them.
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<AccountTenant> AccountTenants => Set<AccountTenant>();
    public DbSet<ApiKeyTenant> ApiKeyTenants => Set<ApiKeyTenant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SynectDbContext).Assembly);

        // The filter references a context member, so EF evaluates it per query (per tenant).
        modelBuilder.Entity<Connection>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<SyncRun>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<RawObject>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<BankAccount>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<BankTransaction>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<BankDailyBalance>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<EdoDocument>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<EdoDocumentStatusLog>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<Counterparty>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<SyncJob>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<ConnectionSecret>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<CounterpartyMonthly>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<ApiKey>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<CounterpartyAlias>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<ToolAuditLog>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<ReconciliationRun>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<ReconciliationMatch>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<ReconciliationMatchItem>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<ReconciliationFinding>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<AttentionItem>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<AttentionItemEvent>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<StatementImport>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceTenantRules();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceTenantRules();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnforceTenantRules()
    {
        var current = tenantContext.TenantId;
        foreach (var entry in ChangeTracker.Entries<ITenantOwned>())
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached) continue;

            if (entry.Entity is EdoDocumentStatusLog && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("edo_document_status_logs is append-only.");
            if (entry.Entity is AttentionItemEvent && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("attention_item_events is append-only.");

            if (current is null)
                throw new InvalidOperationException("Tenant context is not set; tenant-scoped data cannot be written.");

            if (entry.State == EntityState.Added && entry.Entity is TenantEntity { TenantId: var id } added && id == Guid.Empty)
                added.TenantId = current.Value;

            if (entry.Entity.TenantId != current.Value)
                throw new InvalidOperationException("Attempt to write data of another tenant.");
        }

        // api_keys is not ITenantOwned since stage 6.5 (account keys have no tenant). A single-organization key is written
        // only in its own tenant context; an account key (no tenant) only by the owner, outside a tenant context (the
        // application role could not insert it anyway: row-level security rejects a NULL tenant_id).
        foreach (var entry in ChangeTracker.Entries<ApiKey>())
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached) continue;
            var key = entry.Entity;
            if ((key.TenantId is null) == (key.AccountId is null))
                throw new InvalidOperationException("An API key belongs to exactly one of: a tenant or an account.");
            if (key.TenantId is { } keyTenant && current is not null && keyTenant != current.Value)
                throw new InvalidOperationException("Attempt to write data of another tenant.");
            if (key.TenantId is not null && current is null)
                throw new InvalidOperationException("Tenant context is not set; tenant-scoped data cannot be written.");
            if (key.AccountId is not null && current is not null)
                throw new InvalidOperationException("Account keys are written outside a tenant context (administration).");
        }
    }
}
