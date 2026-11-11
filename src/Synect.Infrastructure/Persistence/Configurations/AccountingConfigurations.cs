using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Synect.Domain.Accounting;
using Synect.Domain.Sync;

namespace Synect.Infrastructure.Persistence.Configurations;

/// <summary>
/// 1C data (stage 8a). No foreign keys between the accounting records themselves (a document may arrive before its
/// counterparty; links are resolved later by ref); composite (tenant_id, ...) foreign keys only where a row points at a row
/// of the same organization that always exists first: lines and settlements to their document, records to the package that
/// last changed them, packages and keys to the 1C connection.
/// </summary>
internal static class AccountingRecordConfiguration
{
    public const int RefLength = AccountingRefRecord.MaxRefLength;
    public const int TinLength = 16;
    public const int CurrencyLength = 3;
    public const int NumberLength = 64;
    public const int ShortTextLength = 256;

    public static void ConfigureRecord<T>(this EntityTypeBuilder<T> b, string table) where T : AccountingRecordBase
    {
        b.ToTable(table);
        b.ConfigureTenantEntity();
        b.Property(e => e.ContentHash).HasMaxLength(64).IsFixedLength();
        b.Property(e => e.Extra).HasColumnType(TenantEntityConfiguration.Jsonb).HasDefaultValue("{}");
        b.Property(e => e.IsDeleted).HasDefaultValue(false);
        // (tenant_id, last_batch_id) references ingest_batches: the foreign key is created by the migration in SQL, not here,
        // because EF would add an index for it on every record table (a write per received record) that nothing reads:
        // packages are never deleted while their records exist.
    }

    public static void ConfigureRefRecord<T>(this EntityTypeBuilder<T> b, string table) where T : AccountingRefRecord
    {
        b.ConfigureRecord(table);
        b.Property(e => e.Ref).HasMaxLength(RefLength);
        // The identity of a record: (organization, entity, ref).
        b.HasAlternateKey(e => new { e.TenantId, e.Ref });
    }
}

internal sealed class AccountingCounterpartyConfiguration : IEntityTypeConfiguration<AccountingCounterparty>
{
    public void Configure(EntityTypeBuilder<AccountingCounterparty> b)
    {
        b.ConfigureRefRecord("accounting_counterparties");
        b.Property(e => e.Tin).HasMaxLength(AccountingRecordConfiguration.TinLength);
        b.Property(e => e.Kind).HasMaxLength(AccountingRecordConfiguration.ShortTextLength);
        b.HasIndex(e => new { e.TenantId, e.Tin });
    }
}

internal sealed class AccountingContractConfiguration : IEntityTypeConfiguration<AccountingContract>
{
    public void Configure(EntityTypeBuilder<AccountingContract> b)
    {
        b.ConfigureRefRecord("accounting_contracts");
        b.Property(e => e.CounterpartyRef).HasMaxLength(AccountingRecordConfiguration.RefLength);
        b.Property(e => e.Number).HasMaxLength(AccountingRecordConfiguration.NumberLength);
        b.Property(e => e.Kind).HasMaxLength(AccountingRecordConfiguration.ShortTextLength);
        b.Property(e => e.Currency).HasMaxLength(AccountingRecordConfiguration.CurrencyLength);
        b.HasIndex(e => new { e.TenantId, e.CounterpartyRef });
    }
}

internal sealed class AccountingBankAccountConfiguration : IEntityTypeConfiguration<AccountingBankAccount>
{
    public void Configure(EntityTypeBuilder<AccountingBankAccount> b)
    {
        b.ConfigureRefRecord("accounting_bank_accounts");
        b.Property(e => e.Number).HasMaxLength(AccountingRecordConfiguration.NumberLength);
        b.Property(e => e.BankCode).HasMaxLength(AccountingRecordConfiguration.NumberLength);
        b.Property(e => e.Currency).HasMaxLength(AccountingRecordConfiguration.CurrencyLength);
        b.Property(e => e.GlAccountCode).HasMaxLength(AccountingRecordConfiguration.NumberLength);
        b.HasIndex(e => new { e.TenantId, e.Number });
    }
}

internal sealed class AccountingDocumentConfiguration : IEntityTypeConfiguration<AccountingDocument>
{
    public void Configure(EntityTypeBuilder<AccountingDocument> b)
    {
        b.ConfigureRefRecord("accounting_documents");
        b.Property(e => e.DocKind).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.DocType).HasMaxLength(AccountingRecordConfiguration.ShortTextLength);
        b.Property(e => e.Number).HasMaxLength(AccountingRecordConfiguration.NumberLength);
        b.Property(e => e.CounterpartyRef).HasMaxLength(AccountingRecordConfiguration.RefLength);
        b.Property(e => e.CounterpartyTin).HasMaxLength(AccountingRecordConfiguration.TinLength);
        b.Property(e => e.ContractRef).HasMaxLength(AccountingRecordConfiguration.RefLength);
        b.Property(e => e.ContractNumber).HasMaxLength(AccountingRecordConfiguration.NumberLength);
        b.Property(e => e.Currency).HasMaxLength(AccountingRecordConfiguration.CurrencyLength);
        b.Property(e => e.EsfNumber).HasMaxLength(AccountingRecordConfiguration.NumberLength);
        b.Property(e => e.EsfId).HasMaxLength(AccountingRecordConfiguration.NumberLength);
        b.Property(e => e.EsfStatus).HasMaxLength(AccountingRecordConfiguration.NumberLength);
        // Only the index a reader will certainly need (documents of a counterparty by date); more are added with their readers.
        b.HasIndex(e => new { e.TenantId, e.CounterpartyTin, e.Date });
    }
}

internal sealed class AccountingDocumentLineConfiguration : IEntityTypeConfiguration<AccountingDocumentLine>
{
    public void Configure(EntityTypeBuilder<AccountingDocumentLine> b)
    {
        b.ToTable("accounting_document_lines");
        // Natural key, no separate id: one index per line instead of two. The organization is guaranteed by the document.
        b.HasKey(e => new { e.TenantId, e.DocumentRef, e.LineNo });
        b.Property(e => e.DocumentRef).HasMaxLength(AccountingRecordConfiguration.RefLength);
        b.Property(e => e.ProductCode).HasMaxLength(AccountingRecordConfiguration.ShortTextLength);
        b.Property(e => e.Quantity).HasColumnType("numeric(28,6)");
        b.Property(e => e.VatRate).HasColumnType("numeric(9,4)");
        b.Property(e => e.Extra).HasColumnType(TenantEntityConfiguration.Jsonb).HasDefaultValue("{}");
        b.HasOne<AccountingDocument>().WithMany()
            .HasForeignKey(e => new { e.TenantId, e.DocumentRef }).HasPrincipalKey(d => new { d.TenantId, d.Ref })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AccountingBankDocumentConfiguration : IEntityTypeConfiguration<AccountingBankDocument>
{
    public void Configure(EntityTypeBuilder<AccountingBankDocument> b)
    {
        b.ConfigureRefRecord("accounting_bank_documents");
        b.Property(e => e.Direction).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.Number).HasMaxLength(AccountingRecordConfiguration.NumberLength);
        b.Property(e => e.BankAccountRef).HasMaxLength(AccountingRecordConfiguration.RefLength);
        b.Property(e => e.BankAccountNumber).HasMaxLength(AccountingRecordConfiguration.NumberLength);
        b.Property(e => e.CounterpartyRef).HasMaxLength(AccountingRecordConfiguration.RefLength);
        b.Property(e => e.CounterpartyTin).HasMaxLength(AccountingRecordConfiguration.TinLength);
        b.Property(e => e.Currency).HasMaxLength(AccountingRecordConfiguration.CurrencyLength);
        b.Property(e => e.ContractRef).HasMaxLength(AccountingRecordConfiguration.RefLength);
        b.Property(e => e.BankDocumentNumber).HasMaxLength(AccountingRecordConfiguration.NumberLength);
        b.Property(e => e.CashFlowItem).HasMaxLength(AccountingRecordConfiguration.ShortTextLength);
        b.HasIndex(e => new { e.TenantId, e.CounterpartyTin, e.Date });
    }
}

internal sealed class AccountingBankDocumentSettlementConfiguration : IEntityTypeConfiguration<AccountingBankDocumentSettlement>
{
    public void Configure(EntityTypeBuilder<AccountingBankDocumentSettlement> b)
    {
        b.ToTable("accounting_bank_document_settlements");
        b.HasKey(e => new { e.TenantId, e.DocumentRef, e.LineNo });
        b.Property(e => e.DocumentRef).HasMaxLength(AccountingRecordConfiguration.RefLength);
        b.Property(e => e.SettledDocumentRef).HasMaxLength(AccountingRecordConfiguration.RefLength);
        b.Property(e => e.Extra).HasColumnType(TenantEntityConfiguration.Jsonb).HasDefaultValue("{}");
        b.HasOne<AccountingBankDocument>().WithMany()
            .HasForeignKey(e => new { e.TenantId, e.DocumentRef }).HasPrincipalKey(d => new { d.TenantId, d.Ref })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AccountingPaymentOrderConfiguration : IEntityTypeConfiguration<AccountingPaymentOrder>
{
    public void Configure(EntityTypeBuilder<AccountingPaymentOrder> b)
    {
        b.ConfigureRefRecord("accounting_payment_orders");
        b.Property(e => e.Number).HasMaxLength(AccountingRecordConfiguration.NumberLength);
        b.Property(e => e.BankAccountRef).HasMaxLength(AccountingRecordConfiguration.RefLength);
        b.Property(e => e.CounterpartyTin).HasMaxLength(AccountingRecordConfiguration.TinLength);
        b.Property(e => e.Currency).HasMaxLength(AccountingRecordConfiguration.CurrencyLength);
        b.Property(e => e.BankStatus).HasMaxLength(AccountingRecordConfiguration.ShortTextLength);
        b.HasIndex(e => new { e.TenantId, e.Date });
    }
}

internal sealed class AccountingSettlementBalanceConfiguration : IEntityTypeConfiguration<AccountingSettlementBalance>
{
    public void Configure(EntityTypeBuilder<AccountingSettlementBalance> b)
    {
        b.ConfigureRecord("accounting_settlement_balances");
        b.Property(e => e.CounterpartyRef).HasMaxLength(AccountingRecordConfiguration.RefLength);
        b.Property(e => e.CounterpartyTin).HasMaxLength(AccountingRecordConfiguration.TinLength);
        b.Property(e => e.ContractRef).HasMaxLength(AccountingRecordConfiguration.RefLength);
        b.Property(e => e.GlAccountCode).HasMaxLength(AccountingRecordConfiguration.NumberLength);
        b.Property(e => e.Currency).HasMaxLength(AccountingRecordConfiguration.CurrencyLength);
        // A missing counterparty or contract is NULL; NULLS NOT DISTINCT keeps the key unique all the same.
        b.HasIndex(e => new { e.TenantId, e.AsOfDate, e.CounterpartyRef, e.ContractRef, e.GlAccountCode, e.Currency })
            .IsUnique().AreNullsDistinct(false);
        b.HasIndex(e => new { e.TenantId, e.CounterpartyTin, e.AsOfDate });
    }
}

internal sealed class AccountingBankAccountBalanceConfiguration : IEntityTypeConfiguration<AccountingBankAccountBalance>
{
    public void Configure(EntityTypeBuilder<AccountingBankAccountBalance> b)
    {
        b.ConfigureRecord("accounting_bank_account_balances");
        b.Property(e => e.BankAccountRef).HasMaxLength(AccountingRecordConfiguration.RefLength);
        b.HasIndex(e => new { e.TenantId, e.AsOfDate, e.BankAccountRef }).IsUnique();
    }
}

internal sealed class IngestBatchConfiguration : IEntityTypeConfiguration<IngestBatch>
{
    public void Configure(EntityTypeBuilder<IngestBatch> b)
    {
        b.ToTable("ingest_batches");
        b.ConfigureTenantEntity();
        b.Property(e => e.BatchId).HasMaxLength(IngestBatch.MaxIdLength);
        b.Property(e => e.Entity).HasMaxLength(32);
        b.Property(e => e.Mode).HasMaxLength(16);
        b.Property(e => e.FullSyncId).HasMaxLength(IngestBatch.MaxIdLength);
        b.Property(e => e.ExtensionVersion).HasMaxLength(64);
        b.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
        b.Property(e => e.Error).HasMaxLength(2000);
        b.Property(e => e.PayloadSha256).HasMaxLength(64).IsFixedLength();
        b.Property(e => e.ItemErrors).HasColumnType(TenantEntityConfiguration.Jsonb);
        b.Property(e => e.EnvelopeExtra).HasColumnType(TenantEntityConfiguration.Jsonb);
        b.Property(e => e.Attempts).HasDefaultValue(1);
        b.HasOne<Connection>().WithMany().HasForeignKey(e => new { e.TenantId, e.ConnectionId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<IngestKey>().WithMany().HasForeignKey(e => new { e.TenantId, e.IngestKeyId }).OnDelete(DeleteBehavior.Restrict);
        // Idempotent repeated sending of a package.
        b.HasIndex(e => new { e.TenantId, e.BatchId, e.Sequence }).IsUnique();
        b.HasIndex(e => new { e.TenantId, e.FullSyncId, e.Entity, e.Sequence }).HasFilter("full_sync_id IS NOT NULL");
        b.HasIndex(e => new { e.TenantId, e.ReceivedAt });
    }
}

internal sealed class IngestKeyConfiguration : IEntityTypeConfiguration<IngestKey>
{
    public void Configure(EntityTypeBuilder<IngestKey> b)
    {
        b.ToTable("ingest_keys");
        b.ConfigureTenantEntity();
        b.Property(e => e.Name).HasMaxLength(256);
        b.Property(e => e.KeyHash).HasMaxLength(32);
        b.Property(e => e.KeyPrefix).HasMaxLength(32);
        b.HasOne<Connection>().WithMany().HasForeignKey(e => new { e.TenantId, e.ConnectionId }).OnDelete(DeleteBehavior.Restrict);
        // Deliberate exception to "tenant_id first", as for api_keys: a key is looked up by its hash before the
        // organization is known (only through synect_resolve_ingest_key).
        b.HasIndex(e => e.KeyHash).IsUnique();
        b.HasIndex(e => new { e.TenantId, e.ConnectionId });
    }
}

internal sealed class IngestFullSyncItemConfiguration : IEntityTypeConfiguration<IngestFullSyncItem>
{
    public void Configure(EntityTypeBuilder<IngestFullSyncItem> b)
    {
        b.ToTable("ingest_full_sync_items");
        b.HasKey(e => new { e.TenantId, e.FullSyncId, e.Entity, e.ItemKey });
        b.Property(e => e.FullSyncId).HasMaxLength(IngestBatch.MaxIdLength);
        b.Property(e => e.Entity).HasMaxLength(32);
        b.Property(e => e.ItemKey).HasMaxLength(400);
        b.HasOne<Synect.Domain.Tenants.Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(e => new { e.TenantId, e.CreatedAt });
    }
}
