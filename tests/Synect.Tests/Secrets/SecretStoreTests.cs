using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Abstractions;
using Synect.Application.Secrets;
using Synect.Domain.Sync;
using Synect.Tests.Infrastructure;
using static Synect.Tests.Infrastructure.TestData;

namespace Synect.Tests.Secrets;

public class SecretStoreTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Guid _tenant;
    private Guid _bank;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateSqliteAsync();
        _tenant = await _host.CreateTenantAsync("T");
        _bank = await _host.CreateConnectionAsync(_tenant, AdapterKind.Bank);
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private Task<T> WithStoreAsync<T>(Func<ISecretStore, Task<T>> work) =>
        _host.InTenantAsync(_tenant, sp => work(sp.GetRequiredService<ISecretStore>()));

    [Fact]
    public async Task Secret_round_trips_and_is_stored_encrypted()
    {
        var value = TestHost.RandomSecret();
        var reference = await WithStoreAsync(s => s.SetAsync(_bank, "api-key", new SecretValue(value), CancellationToken.None));

        var read = await WithStoreAsync(s => s.GetAsync(_bank, "api-key", CancellationToken.None));
        Assert.Equal(value, read!.Reveal());
        Assert.Equal("***", read.ToString());

        var row = await _host.QueryAsync(_tenant, db => db.ConnectionSecrets.AsNoTracking().SingleAsync(s => s.Kind == "api-key"));
        Assert.Equal($"connection_secrets:{row.Id}", reference);
        Assert.Equal(1, row.Version);
        Assert.DoesNotContain(value, row.EncryptedValue);
        Assert.Null(await WithStoreAsync(s => s.GetAsync(_bank, "missing", CancellationToken.None)));
    }

    [Fact]
    public async Task Credentials_ref_of_the_connection_points_to_the_secret()
    {
        var connection = await _host.ConnectionAsync(_tenant, _bank);
        var secret = await _host.QueryAsync(_tenant, db => db.ConnectionSecrets.AsNoTracking()
            .SingleAsync(s => s.ConnectionId == _bank && s.Kind == ConnectionSecret.CredentialsKind));
        Assert.Equal(secret.Reference, connection.CredentialsRef);
    }

    [Fact]
    public async Task Rotation_replaces_the_value_and_bumps_the_version()
    {
        var oldValue = TestHost.RandomSecret();
        var newValue = TestHost.RandomSecret();
        await WithStoreAsync(s => s.SetAsync(_bank, "api-key", new SecretValue(oldValue), CancellationToken.None));
        var created = await _host.QueryAsync(_tenant, db => db.ConnectionSecrets.AsNoTracking().SingleAsync(s => s.Kind == "api-key"));

        _host.Time.Advance(TimeSpan.FromDays(30));
        await WithStoreAsync(async s => { await s.RotateAsync(_bank, "api-key", new SecretValue(newValue), CancellationToken.None); return true; });

        var rotated = await _host.QueryAsync(_tenant, db => db.ConnectionSecrets.AsNoTracking().SingleAsync(s => s.Kind == "api-key"));
        Assert.Equal(created.Id, rotated.Id);
        Assert.Equal(2, rotated.Version);
        Assert.Equal(_host.Time.Now, rotated.RotatedAt);
        Assert.NotEqual(created.EncryptedValue, rotated.EncryptedValue);
        Assert.Equal(newValue, (await WithStoreAsync(s => s.GetAsync(_bank, "api-key", CancellationToken.None)))!.Reveal());

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            WithStoreAsync(async s => { await s.RotateAsync(_bank, "nope", new SecretValue("x"), CancellationToken.None); return true; }));
    }

    [Fact]
    public async Task Ciphertext_is_bound_to_its_connection()
    {
        var other = await _host.CreateConnectionAsync(_tenant, AdapterKind.Bank);
        var foreignCipher = await _host.QueryAsync(_tenant, db => db.ConnectionSecrets.AsNoTracking()
            .Where(s => s.ConnectionId == other).Select(s => s.EncryptedValue).SingleAsync());

        // Copy the other connection's ciphertext into this connection's row.
        await _host.QueryAsync(_tenant, db => db.ConnectionSecrets
            .Where(s => s.ConnectionId == _bank && s.Kind == ConnectionSecret.CredentialsKind)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.EncryptedValue, foreignCipher)));

        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            WithStoreAsync(s => s.GetAsync(_bank, ConnectionSecret.CredentialsKind, CancellationToken.None)));
    }

    [Fact]
    public async Task Plain_secret_never_reaches_the_database_or_the_logs()
    {
        var failing = await _host.CreateConnectionAsync(_tenant, AdapterKind.Bank);
        var edo = await _host.CreateConnectionAsync(_tenant, AdapterKind.Edo);
        _host.FakeBank.For(failing).AuthFailure = true;

        Assert.Equal(SyncRunResult.Succeeded, (await _host.SyncBankAsync(_tenant, _bank, Day1, Day1)).Result);
        Assert.Equal(SyncRunResult.Failed, (await _host.SyncBankAsync(_tenant, failing, Day1, Day1)).Result);
        Assert.Equal(SyncRunResult.Succeeded, (await _host.SyncEdoAsync(_tenant, edo)).Result);
        await _host.InTenantAsync(_tenant, sp => sp.GetRequiredService<Synect.Application.Queue.SyncRequestService>()
            .ReactivateAsync(failing, CancellationToken.None));

        var secrets = _host.Passwords.Values.ToList();
        Assert.Equal(3, secrets.Count);

        var everyStoredValue = await DumpDatabaseAsync();
        var logs = _host.Logs.Lines.ToList();
        Assert.NotEmpty(logs);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(everyStoredValue, v => v.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(logs, l => l.Contains(secret, StringComparison.Ordinal));
        }
    }

    /// <summary>Every value of every column of every table in the SQLite test database, as text.</summary>
    private async Task<List<string>> DumpDatabaseAsync()
    {
        var values = new List<string>();
        await using var connection = new SqliteConnection(_host.SqliteConnectionStringValue);
        await connection.OpenAsync();
        var tables = new List<string>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        }
        foreach (var table in tables)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT * FROM \"{table}\"";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                for (var i = 0; i < reader.FieldCount; i++)
                    if (!reader.IsDBNull(i)) values.Add(Convert.ToString(reader.GetValue(i))!);
        }
        Assert.Contains("connection_secrets", tables);
        return values;
    }
}
