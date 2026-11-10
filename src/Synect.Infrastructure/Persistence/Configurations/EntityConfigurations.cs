using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Synect.Domain.Access;
using Synect.Domain.Analytics;
using Synect.Domain.Banking;
using Synect.Domain.Common;
using Synect.Domain.Counterparties;
using Synect.Domain.Edo;
using Synect.Domain.Raw;
using Synect.Domain.Reconciliation;
using Synect.Domain.Sync;
using Synect.Domain.Tenants;

namespace Synect.Infrastructure.Persistence.Configurations;

internal static class TenantEntityConfiguration
{
    /// <summary>
    /// Common mapping: PK (tenant_id, id) so tenant_id leads every index, and an FK to tenants.
    /// References between tenant tables use composite FKs (tenant_id, x_id), which makes
    /// cross-tenant references impossible at the database level.
    /// </summary>
    public static void ConfigureTenantEntity<T>(this EntityTypeBuilder<T> b) where T : TenantEntity
    {
        b.HasKey(e => new { e.TenantId, e.Id });
        b.Property(e => e.Id).ValueGeneratedNever();
        b.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);
    }

    public const int CodeLength = 64;
    public const string Jsonb = "jsonb";
}

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).ValueGeneratedNever();
        b.Property(e => e.Name).HasMaxLength(256);
        b.Property(e => e.Inn).HasMaxLength(16);
    }
}

internal sealed class ConnectionConfiguration : IEntityTypeConfiguration<Connection>
{
    public void Configure(EntityTypeBuilder<Connection> b)
    {
        b.ConfigureTenantEntity();
        b.Property(e => e.Name).HasMaxLength(256);
        b.Property(e => e.SourceCode).HasMaxLength(TenantEntityConfiguration.CodeLength);
        b.Property(e => e.AdapterKind).HasConversion<string>().HasMaxLength(32);
        b.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
        b.Property(e => e.StatusReason).HasMaxLength(2000);
        b.Property(e => e.ConsecutiveFailures).HasDefaultValue(0);
        b.Property(e => e.Settings).HasColumnType(TenantEntityConfiguration.Jsonb);
        b.Property(e => e.CredentialsRef).HasMaxLength(256);
        b.HasIndex(e => new { e.TenantId, e.SourceCode });
    }
}

internal sealed class SyncRunConfiguration : IEntityTypeConfiguration<SyncRun>
{
    public void Configure(EntityTypeBuilder<SyncRun> b)
    {
        b.ConfigureTenantEntity();
        b.Property(e => e.Result).HasConversion<string>().HasMaxLength(32);
        b.Property(e => e.Error).HasMaxLength(2000);
        b.HasOne<Connection>().WithMany().HasForeignKey(e => new { e.TenantId, e.ConnectionId }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(e => new { e.TenantId, e.ConnectionId, e.StartedAt });
    }
}

internal sealed class RawObjectConfiguration : IEntityTypeConfiguration<RawObject>
{
    public void Configure(EntityTypeBuilder<RawObject> b)
    {
        b.ConfigureTenantEntity();
        b.Property(e => e.SourceCode).HasMaxLength(TenantEntityConfiguration.CodeLength);
        b.Property(e => e.ObjectType).HasMaxLength(128);
        b.Property(e => e.ExternalId).HasMaxLength(256);
        b.Property(e => e.Payload).HasColumnType(TenantEntityConfiguration.Jsonb);
        b.Property(e => e.Hash).HasMaxLength(64).IsFixedLength();
        b.HasOne<Connection>().WithMany().HasForeignKey(e => new { e.TenantId, e.ConnectionId }).OnDelete(DeleteBehavior.Restrict);
        // One row per external object: this is what keeps only the latest version.
        b.HasIndex(e => new { e.TenantId, e.ConnectionId, e.ObjectType, e.ExternalId }).IsUnique();
    }
}

internal sealed class BankAccountConfiguration : IEntityTypeConfiguration<BankAccount>
{
    public void Configure(EntityTypeBuilder<BankAccount> b)
    {
        b.ConfigureTenantEntity();
        b.Property(e => e.AccountNumber).HasMaxLength(34);
        b.Property(e => e.Mfo).HasMaxLength(16);
        b.Property(e => e.Currency).HasMaxLength(3);
        b.Property(e => e.Name).HasMaxLength(512);
        b.Property(e => e.ExternalId).HasMaxLength(256);
        b.HasOne<Connection>().WithMany().HasForeignKey(e => new { e.TenantId, e.ConnectionId }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(e => new { e.TenantId, e.ConnectionId, e.AccountNumber }).IsUnique();
    }
}

internal sealed class BankTransactionConfiguration : IEntityTypeConfiguration<BankTransaction>
{
    public void Configure(EntityTypeBuilder<BankTransaction> b)
    {
        b.ConfigureTenantEntity();
        b.Ignore(e => e.SignedAmount);
        b.Property(e => e.OperationId).HasMaxLength(128);
        b.Property(e => e.Currency).HasMaxLength(3);
        b.Property(e => e.Direction).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.CounterpartyInn).HasMaxLength(16);
        b.Property(e => e.CounterpartyName).HasMaxLength(512);
        b.Property(e => e.CounterpartyAccount).HasMaxLength(34);
        b.Property(e => e.CounterpartyMfo).HasMaxLength(16);
        b.Property(e => e.PurposeCode).HasMaxLength(16);
        b.Property(e => e.DocumentNumber).HasMaxLength(64);
        b.Property(e => e.SourceState).HasMaxLength(32);
        b.Property(e => e.ErrorMessage).HasMaxLength(1000);
        b.Property(e => e.SourceHash).HasMaxLength(64).IsFixedLength();
        b.HasOne<Connection>().WithMany().HasForeignKey(e => new { e.TenantId, e.ConnectionId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<BankAccount>().WithMany().HasForeignKey(e => new { e.TenantId, e.BankAccountId }).OnDelete(DeleteBehavior.Restrict);
        // Idempotency key: the same bank operation is stored once per account.
        b.HasIndex(e => new { e.TenantId, e.BankAccountId, e.OperationId }).IsUnique();
        b.HasIndex(e => new { e.TenantId, e.BankAccountId, e.PostingDate });
        // B1/B3: one counterparty within a period.
        b.HasIndex(e => new { e.TenantId, e.CounterpartyInn, e.PostingDate });
        // A3, A7/A10, B8: period scans across accounts; covering and partial (errors are always excluded).
        b.HasIndex(e => new { e.TenantId, e.PostingDate })
            .IncludeProperties(e => new { e.BankAccountId, e.Currency, e.Direction, e.Amount, e.CounterpartyInn })
            .HasFilter("has_error = false")
            .HasDatabaseName("ix_bank_transactions_flows");
    }
}

internal sealed class BankDailyBalanceConfiguration : IEntityTypeConfiguration<BankDailyBalance>
{
    public void Configure(EntityTypeBuilder<BankDailyBalance> b)
    {
        b.ConfigureTenantEntity();
        b.HasOne<BankAccount>().WithMany().HasForeignKey(e => new { e.TenantId, e.BankAccountId }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(e => new { e.TenantId, e.BankAccountId, e.Date }).IsUnique();
        // Balance integrity and cross-account day scans.
        b.HasIndex(e => new { e.TenantId, e.Date });
    }
}

internal sealed class EdoDocumentConfiguration : IEntityTypeConfiguration<EdoDocument>
{
    public void Configure(EntityTypeBuilder<EdoDocument> b)
    {
        b.ConfigureTenantEntity();
        b.Property(e => e.ExternalId).HasMaxLength(128);
        b.Property(e => e.DocType).HasMaxLength(16);
        b.Property(e => e.Status).HasMaxLength(16);
        b.Property(e => e.Owner).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.StatusComment).HasMaxLength(2000);
        b.Property(e => e.Number).HasMaxLength(256);
        b.Property(e => e.CounterpartyInn).HasMaxLength(16);
        b.Property(e => e.CounterpartyName).HasMaxLength(512);
        b.Property(e => e.ContractNumber).HasMaxLength(256);
        b.Property(e => e.Currency).HasMaxLength(3).HasDefaultValue("UZS");
        b.Property(e => e.SellerAccount).HasMaxLength(34);
        b.Property(e => e.SourceHash).HasMaxLength(64).IsFixedLength();
        b.HasOne<Connection>().WithMany().HasForeignKey(e => new { e.TenantId, e.ConnectionId }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(e => new { e.TenantId, e.ConnectionId, e.ExternalId }).IsUnique();
        // B3/B8: documents of one counterparty; status included for the counted-status anti-join.
        b.HasIndex(e => new { e.TenantId, e.CounterpartyInn }).IncludeProperties(e => new { e.Status });
        b.HasIndex(e => new { e.TenantId, e.DocumentDate });
        // V1/V2: waiting for a signature, oldest first.
        b.HasIndex(e => new { e.TenantId, e.Owner, e.Status, e.DocumentDate });
        // DateBasis.SourceCreated / SourceUpdated.
        b.HasIndex(e => new { e.TenantId, e.SourceCreatedAt });
        b.HasIndex(e => new { e.TenantId, e.SourceUpdatedAt });
        // Prefix search by number and contract number uses text_pattern_ops indexes created in the migration.
    }
}

internal sealed class EdoDocumentStatusLogConfiguration : IEntityTypeConfiguration<EdoDocumentStatusLog>
{
    public void Configure(EntityTypeBuilder<EdoDocumentStatusLog> b)
    {
        b.ConfigureTenantEntity();
        b.Property(e => e.Status).HasMaxLength(16);
        b.HasOne<EdoDocument>().WithMany().HasForeignKey(e => new { e.TenantId, e.EdoDocumentId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SyncRun>().WithMany().HasForeignKey(e => new { e.TenantId, e.SyncRunId }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(e => new { e.TenantId, e.EdoDocumentId, e.RecordedAt });
    }
}

internal sealed class CounterpartyConfiguration : IEntityTypeConfiguration<Counterparty>
{
    public void Configure(EntityTypeBuilder<Counterparty> b)
    {
        b.ConfigureTenantEntity();
        b.Property(e => e.Inn).HasMaxLength(16);
        b.Property(e => e.Name).HasMaxLength(512);
        b.HasIndex(e => new { e.TenantId, e.Inn }).IsUnique();
    }
}

internal sealed class SyncJobConfiguration : IEntityTypeConfiguration<SyncJob>
{
    public void Configure(EntityTypeBuilder<SyncJob> b)
    {
        b.ConfigureTenantEntity();
        b.Ignore(e => e.IsActive);
        b.Property(e => e.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.Error).HasMaxLength(2000);
        b.HasOne<Connection>().WithMany().HasForeignKey(e => new { e.TenantId, e.ConnectionId }).OnDelete(DeleteBehavior.Restrict);
        // At most one Queued or Running job per connection: no overlapping loads, no duplicate requests.
        b.HasIndex(e => new { e.TenantId, e.ConnectionId })
            .IsUnique()
            .HasFilter("status IN ('Queued', 'Running')")
            .HasDatabaseName("ux_sync_jobs_tenant_id_connection_id_active");
        b.HasIndex(e => new { e.TenantId, e.Status, e.NotBefore });
        b.HasIndex(e => new { e.TenantId, e.ConnectionId, e.EnqueuedAt });
    }
}

internal sealed class ConnectionSecretConfiguration : IEntityTypeConfiguration<ConnectionSecret>
{
    public void Configure(EntityTypeBuilder<ConnectionSecret> b)
    {
        b.ConfigureTenantEntity();
        b.Ignore(e => e.Reference);
        b.Property(e => e.Kind).HasMaxLength(64);
        b.Property(e => e.EncryptedValue);
        b.HasOne<Connection>().WithMany().HasForeignKey(e => new { e.TenantId, e.ConnectionId }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(e => new { e.TenantId, e.ConnectionId, e.Kind }).IsUnique();
    }
}

internal sealed class CounterpartyMonthlyConfiguration : IEntityTypeConfiguration<CounterpartyMonthly>
{
    public void Configure(EntityTypeBuilder<CounterpartyMonthly> b)
    {
        b.ToTable("analytics_counterparty_monthly");
        b.ConfigureTenantEntity();
        b.Property(e => e.CounterpartyInn).HasMaxLength(16);
        b.Property(e => e.Currency).HasMaxLength(3);
        b.HasIndex(e => new { e.TenantId, e.CounterpartyInn, e.Month, e.Currency }).IsUnique();
        b.HasIndex(e => new { e.TenantId, e.Month, e.Currency });
    }
}

internal sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> b)
    {
        b.ToTable("api_keys");
        b.ConfigureTenantEntity();
        b.Property(e => e.Name).HasMaxLength(256);
        b.Property(e => e.KeyHash).HasMaxLength(32);
        b.Property(e => e.KeyPrefix).HasMaxLength(32);
        b.Ignore(e => e.IsRevoked);
        // Lookup by hash happens before the tenant is known (synect_resolve_api_key), so the hash is unique globally.
        b.HasIndex(e => e.KeyHash).IsUnique();
    }
}

internal sealed class ToolAuditLogConfiguration : IEntityTypeConfiguration<ToolAuditLog>
{
    public void Configure(EntityTypeBuilder<ToolAuditLog> b)
    {
        b.ToTable("tool_audit_log");
        b.ConfigureTenantEntity();
        b.Property(e => e.Tool).HasMaxLength(64);
        b.Property(e => e.Parameters).HasColumnType(TenantEntityConfiguration.Jsonb);
        b.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
        b.Property(e => e.ErrorMessage).HasMaxLength(2000);
        b.Property(e => e.Details).HasColumnType(TenantEntityConfiguration.Jsonb);
        b.HasOne<ApiKey>().WithMany().HasForeignKey(e => new { e.TenantId, e.ApiKeyId }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(e => new { e.TenantId, e.StartedAt });
    }
}

internal sealed class CounterpartyAliasConfiguration : IEntityTypeConfiguration<CounterpartyAlias>
{
    public void Configure(EntityTypeBuilder<CounterpartyAlias> b)
    {
        b.ToTable("counterparty_aliases");
        b.ConfigureTenantEntity();
        b.Property(e => e.CounterpartyInn).HasMaxLength(16);
        b.Property(e => e.Name).HasMaxLength(CounterpartyAlias.MaxNameLength);
        b.Property(e => e.NormalizedName).HasMaxLength(CounterpartyAlias.MaxNameLength);
        b.Property(e => e.Source).HasConversion<string>().HasMaxLength(16);
        b.HasIndex(e => new { e.TenantId, e.CounterpartyInn, e.Source, e.Name }).IsUnique();
        // The trigram GIN index (tenant_id, normalized_name gin_trgm_ops) is created in the migration (pg_trgm, btree_gin).
    }
}

internal sealed class ReconciliationRunConfiguration : IEntityTypeConfiguration<ReconciliationRun>
{
    public void Configure(EntityTypeBuilder<ReconciliationRun> b)
    {
        b.ToTable("reconciliation_runs");
        b.ConfigureTenantEntity();
        b.Property(e => e.Scope).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.RulesVersion).HasMaxLength(64);
        b.HasIndex(e => new { e.TenantId, e.StartedAt });
    }
}

internal sealed class ReconciliationMatchConfiguration : IEntityTypeConfiguration<ReconciliationMatch>
{
    public void Configure(EntityTypeBuilder<ReconciliationMatch> b)
    {
        b.ToTable("reconciliation_matches");
        b.ConfigureTenantEntity();
        b.Property(e => e.MatchType).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.Reasons).HasColumnType(TenantEntityConfiguration.Jsonb);
        b.Property(e => e.CounterpartyInn).HasMaxLength(16);
        b.Property(e => e.Currency).HasMaxLength(3);
        b.Property(e => e.DocumentOwner).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.Signature).HasMaxLength(64);
        b.Property(e => e.DecisionComment).HasMaxLength(ReconciliationMatch.MaxCommentLength);
        b.HasOne<ReconciliationRun>().WithMany().HasForeignKey(e => new { e.TenantId, e.RunId }).OnDelete(DeleteBehavior.Restrict);
        // Recalculation of one INN, and the per-INN queries.
        b.HasIndex(e => new { e.TenantId, e.CounterpartyInn, e.Status });
        b.HasIndex(e => new { e.TenantId, e.Status, e.MatchType });
    }
}

internal sealed class ReconciliationMatchDecisionConfiguration : IEntityTypeConfiguration<ReconciliationMatchDecision>
{
    public void Configure(EntityTypeBuilder<ReconciliationMatchDecision> b)
    {
        b.ToTable("reconciliation_match_decisions");
        b.ConfigureTenantEntity();
        b.Property(e => e.PreviousStatus).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.NewStatus).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.Comment).HasMaxLength(ReconciliationMatch.MaxCommentLength);
        // History lives as long as the match (a Proposed match is never decided, so it has no history to lose).
        b.HasOne<ReconciliationMatch>().WithMany().HasForeignKey(e => new { e.TenantId, e.MatchId }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(e => new { e.TenantId, e.MatchId, e.DecidedAt });
    }
}

internal sealed class ReconciliationMatchItemConfiguration : IEntityTypeConfiguration<ReconciliationMatchItem>
{
    public void Configure(EntityTypeBuilder<ReconciliationMatchItem> b)
    {
        b.ToTable("reconciliation_match_items");
        b.ConfigureTenantEntity();
        b.Property(e => e.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.MatchStatus).HasConversion<string>().HasMaxLength(16);
        b.HasOne<ReconciliationMatch>().WithMany().HasForeignKey(e => new { e.TenantId, e.MatchId }).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(e => new { e.TenantId, e.MatchId, e.Kind, e.RefId }).IsUnique();
        // Which matches a payment or a document is in.
        b.HasIndex(e => new { e.TenantId, e.Kind, e.RefId });
        // A payment or a document is in at most one Proposed match at a time.
        b.HasIndex(e => new { e.TenantId, e.Kind, e.RefId })
            .IsUnique()
            .HasFilter("match_status = 'Proposed'")
            .HasDatabaseName("ux_reconciliation_match_items_proposed");
    }
}

internal sealed class ReconciliationFindingConfiguration : IEntityTypeConfiguration<ReconciliationFinding>
{
    public void Configure(EntityTypeBuilder<ReconciliationFinding> b)
    {
        b.ToTable("reconciliation_findings");
        b.ConfigureTenantEntity();
        b.Property(e => e.Kind).HasConversion<string>().HasMaxLength(32);
        b.Property(e => e.CounterpartyInn).HasMaxLength(16);
        b.Property(e => e.SubjectKind).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.RelatedKind).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.Reasons).HasColumnType(TenantEntityConfiguration.Jsonb);
        b.HasOne<ReconciliationRun>().WithMany().HasForeignKey(e => new { e.TenantId, e.RunId }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(e => new { e.TenantId, e.CounterpartyInn });
        b.HasIndex(e => new { e.TenantId, e.Kind, e.SubjectId });
    }
}
