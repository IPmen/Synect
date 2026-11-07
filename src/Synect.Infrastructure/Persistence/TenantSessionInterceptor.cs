using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Synect.Infrastructure.Persistence;

/// <summary>
/// At the start of every transaction runs the equivalent of
/// <c>SET LOCAL app.tenant_id = '&lt;tenant&gt;'</c> (via set_config(..., is_local =&gt; true), which accepts
/// a bind parameter). The value lives only until COMMIT/ROLLBACK, so pooled connections never leak
/// a tenant. RLS policies read it with current_setting('app.tenant_id', true).
/// </summary>
public sealed class TenantSessionInterceptor : DbTransactionInterceptor
{
    public const string SettingName = "app.tenant_id";
    private const string Sql = "SELECT set_config('" + SettingName + "', @tenant_id, true)";

    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        using var command = CreateCommand(connection, result, eventData);
        command.ExecuteNonQuery();
        return result;
    }

    public override async ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        await using var command = CreateCommand(connection, result, eventData);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return result;
    }

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction transaction, TransactionEndEventData eventData)
    {
        var tenantId = (eventData.Context as SynectDbContext)?.CurrentTenantId ?? Guid.Empty;
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "tenant_id";
        // Empty string means "no tenant": policies use NULLIF(..., '') and match nothing.
        parameter.Value = tenantId == Guid.Empty ? string.Empty : tenantId.ToString();
        command.Parameters.Add(parameter);
        return command;
    }
}
