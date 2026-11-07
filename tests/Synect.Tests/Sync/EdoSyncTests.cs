using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Abstractions;
using Synect.Domain.Sync;
using Synect.Infrastructure.Adapters.Didox;
using Synect.Tests.Infrastructure;

namespace Synect.Tests.Sync;

public class EdoSyncTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Guid _tenant;
    private Guid _connection;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateSqliteAsync();
        _tenant = await _host.CreateTenantAsync("Tenant A");
        _connection = await _host.CreateConnectionAsync(_tenant, AdapterKind.Edo);
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task First_sync_loads_all_pages_and_logs_initial_status_of_each_document()
    {
        var outcome = await _host.SyncEdoAsync(_tenant, _connection);

        var expected = _host.FakeEdo.Documents(_connection).Count;
        Assert.Equal(SyncRunResult.Succeeded, outcome.Result);
        Assert.True(outcome.IsComplete);
        Assert.Equal(expected, outcome.RecordCount);
        Assert.Equal(expected, await _host.CountAsync(_tenant, db => db.EdoDocuments));
        Assert.Equal(expected, await _host.CountAsync(_tenant, db => db.EdoDocumentStatusLogs));
        Assert.True(await _host.CountAsync(_tenant, db => db.Counterparties) > 0);
    }

    [Fact]
    public async Task Status_change_between_runs_appends_exactly_one_log_entry()
    {
        await _host.SyncEdoAsync(_tenant, _connection);
        var doc = _host.FakeEdo.Documents(_connection)[3];
        var oldStatus = doc.Status;
        var newStatus = oldStatus == 3 ? 4 : 3;

        _host.Time.Advance(TimeSpan.FromDays(1));
        _host.FakeEdo.SetStatus(_connection, doc.DocId, newStatus, _host.Time.Now);
        var outcome = await _host.SyncEdoAsync(_tenant, _connection);

        // Incremental run: only the changed document came back.
        Assert.Equal(1, outcome.RecordCount);
        var stored = await _host.QueryAsync(_tenant, db => db.EdoDocuments.SingleAsync(d => d.ExternalId == doc.DocId));
        Assert.Equal(newStatus.ToString(), stored.Status);

        var history = await _host.QueryAsync(_tenant, db => db.EdoDocumentStatusLogs
            .Where(l => l.EdoDocumentId == stored.Id).ToListAsync());
        Assert.Equal([oldStatus.ToString(), newStatus.ToString()], history.OrderBy(h => h.RecordedAt).Select(h => h.Status));
        Assert.Equal(_host.Time.Now, history.Max(h => h.RecordedAt));
        Assert.Equal(_host.FakeEdo.Documents(_connection).Count + 1, await _host.CountAsync(_tenant, db => db.EdoDocumentStatusLogs));
    }

    [Fact]
    public async Task Repeated_sync_without_changes_adds_no_log_entries()
    {
        await _host.SyncEdoAsync(_tenant, _connection);
        var logs = await _host.CountAsync(_tenant, db => db.EdoDocumentStatusLogs);

        _host.Time.Advance(TimeSpan.FromHours(2));
        await _host.SyncEdoAsync(_tenant, _connection);

        Assert.Equal(logs, await _host.CountAsync(_tenant, db => db.EdoDocumentStatusLogs));
    }

    [Fact]
    public async Task Status_log_is_append_only()
    {
        await _host.SyncEdoAsync(_tenant, _connection);

        await using var scope = _host.Scope(_tenant);
        var db = scope.ServiceProvider.GetRequiredService<ISynectDbContext>();
        var log = await db.EdoDocumentStatusLogs.FirstAsync(TestContext.Current.CancellationToken);
        db.EdoDocumentStatusLogs.Remove(log);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Contains("append-only", ex.Message);
    }

    [Fact]
    public async Task Authentication_error_marks_connection_as_needing_attention()
    {
        _host.FakeEdo.For(_connection).AuthFailure = true;

        var outcome = await _host.SyncEdoAsync(_tenant, _connection);

        Assert.Equal(SyncRunResult.Failed, outcome.Result);
        var connection = await _host.QueryAsync(_tenant, db => db.Connections.SingleAsync());
        Assert.Equal(ConnectionStatus.NeedsAttention, connection.Status);
        Assert.DoesNotContain("secret-store", outcome.Error);
    }
}

public class RawObjectTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Guid _tenant;
    private Guid _connection;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateSqliteAsync();
        _tenant = await _host.CreateTenantAsync("Tenant A");
        _connection = await _host.CreateConnectionAsync(_tenant, AdapterKind.Edo);
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task Only_the_latest_version_of_a_raw_object_is_kept()
    {
        await _host.SyncEdoAsync(_tenant, _connection);
        var doc = _host.FakeEdo.Documents(_connection)[0];
        var before = await _host.QueryAsync(_tenant, db => db.RawObjects.AsNoTracking()
            .SingleAsync(r => r.ObjectType == DidoxMapper.DocumentObjectType && r.ExternalId == doc.DocId));
        var rawCount = await _host.CountAsync(_tenant, db => db.RawObjects);

        _host.Time.Advance(TimeSpan.FromDays(1));
        _host.FakeEdo.SetStatus(_connection, doc.DocId, doc.Status == 8 ? 6 : 8, _host.Time.Now);
        await _host.SyncEdoAsync(_tenant, _connection);

        var after = await _host.QueryAsync(_tenant, db => db.RawObjects.AsNoTracking()
            .SingleAsync(r => r.ObjectType == DidoxMapper.DocumentObjectType && r.ExternalId == doc.DocId));
        Assert.Equal(rawCount, await _host.CountAsync(_tenant, db => db.RawObjects));
        Assert.Equal(before.Id, after.Id);
        Assert.NotEqual(before.Hash, after.Hash);
        Assert.Contains($"\"doc_status\":{doc.Status}", after.Payload.Replace(" ", ""));
        Assert.Equal(before.FirstSeenAt, after.FirstSeenAt);
        Assert.Equal(_host.Time.Now, after.UpdatedAt);
    }

    [Fact]
    public async Task Re_delivered_unchanged_payload_keeps_the_version_and_only_bumps_last_seen()
    {
        // The bank re-reads the same days, so every raw object is delivered again unchanged.
        var bank = await _host.CreateConnectionAsync(_tenant, AdapterKind.Bank);
        await _host.SyncBankAsync(_tenant, bank);
        var before = await _host.QueryAsync(_tenant, db => db.RawObjects.AsNoTracking().OrderBy(r => r.ExternalId).ToListAsync());

        _host.Time.Advance(TimeSpan.FromHours(1));
        await _host.SyncBankAsync(_tenant, bank);

        var after = await _host.QueryAsync(_tenant, db => db.RawObjects.AsNoTracking().OrderBy(r => r.ExternalId).ToListAsync());
        Assert.NotEmpty(before);
        Assert.Equal(before.Select(r => (r.Id, r.Hash, r.UpdatedAt)), after.Select(r => (r.Id, r.Hash, r.UpdatedAt)));
        Assert.All(after, r => Assert.Equal(_host.Time.Now, r.LastSeenAt));
    }
}
