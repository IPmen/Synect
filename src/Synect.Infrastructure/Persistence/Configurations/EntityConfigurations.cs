using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Synect.Domain.Banking;
using Synect.Domain.Common;
using Synect.Domain.Counterparties;
using Synect.Domain.Edo;
using Synect.Domain.Raw;
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
        b.HasIndex(e => new { e.TenantId, e.CounterpartyInn });
    }
}

internal sealed class BankDailyBalanceConfiguration : IEntityTypeConfiguration<BankDailyBalance>
{
    public void Configure(EntityTypeBuilder<BankDailyBalance> b)
    {
        b.ConfigureTenantEntity();
        b.HasOne<BankAccount>().WithMany().HasForeignKey(e => new { e.TenantId, e.BankAccountId }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(e => new { e.TenantId, e.BankAccountId, e.Date }).IsUnique();
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
        b.Property(e => e.Number).HasMaxLength(256);
        b.Property(e => e.CounterpartyInn).HasMaxLength(16);
        b.Property(e => e.CounterpartyName).HasMaxLength(512);
        b.Property(e => e.ContractNumber).HasMaxLength(256);
        b.Property(e => e.SourceHash).HasMaxLength(64).IsFixedLength();
        b.HasOne<Connection>().WithMany().HasForeignKey(e => new { e.TenantId, e.ConnectionId }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(e => new { e.TenantId, e.ConnectionId, e.ExternalId }).IsUnique();
        b.HasIndex(e => new { e.TenantId, e.CounterpartyInn });
        b.HasIndex(e => new { e.TenantId, e.DocumentDate });
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
