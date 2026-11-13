using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Abstractions;
using Synect.Application.Queue;
using Synect.Application.Secrets;
using Synect.Domain.Sync;
using Synect.Infrastructure.Adapters;
using Synect.Infrastructure.Adapters.Fake;
using Synect.Tests.Infrastructure;
using Synect.Tests.Postgres;

namespace Synect.Tests.Secrets;

/// <summary>Entering the bank and Didox access of a client organization (create-connection, set-credentials, test-connection).</summary>
public class ConnectionCredentialsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private TestHost Host => fixture.Host;

    private void RequirePostgres() => Assert.SkipUnless(fixture.Available, PostgresFixture.SkipReason);

    private Task<T> AsAsync<T>(Guid tenant, Func<ConnectionCredentialsService, Task<T>> work) =>
        Host.InTenantAsync(tenant, sp => work(sp.GetRequiredService<ConnectionCredentialsService>()));

    private Task<SecretValue?> StoredAsync(Guid tenant, Guid connection) =>
        Host.InTenantAsync(tenant, sp => sp.GetRequiredService<ISecretStore>().GetAsync(connection, ConnectionSecret.CredentialsKind, CancellationToken.None));

    [Fact]
    public async Task Creates_a_connection_with_encrypted_credentials_and_schedules_it()
    {
        RequirePostgres();
        var tenant = await Host.CreateTenantAsync("Credentials create");
        var password = TestHost.RandomSecret();
        var r = await AsAsync(tenant, s => s.CreateAsync(FakeBankAdapter.Code, "Kapital Bank", "client-login", new SecretValue(password), test: false, CancellationToken.None));

        Assert.True(r.Created);
        Assert.Equal(AdapterKind.Bank, r.Kind);
        Assert.Equal(1, r.SecretVersion);
        Assert.Null(r.Test);
        var connection = await Host.QueryAsync(tenant, db => db.Connections.AsNoTracking().SingleAsync(c => c.Id == r.ConnectionId));
        Assert.Equal(ConnectionStatus.Active, connection.Status);
        Assert.StartsWith("connection_secrets:", connection.CredentialsRef);

        // Only ciphertext in the database; the store gives back exactly what was entered.
        var cipher = await fixture.ScalarAsync<string>($"SELECT encrypted_value FROM connection_secrets WHERE connection_id = '{r.ConnectionId}'");
        Assert.DoesNotContain(password, cipher);
        Assert.DoesNotContain("client-login", cipher);
        var stored = LoginPasswordCredentials.Parse((await StoredAsync(tenant, r.ConnectionId))!);
        Assert.Equal("client-login", stored.Login);
        Assert.Equal(password, stored.Password.Reveal());

        // The same name twice is refused; an unknown source lists the available ones.
        await Assert.ThrowsAsync<InvalidOperationException>(() => AsAsync(tenant, s =>
            s.CreateAsync(FakeBankAdapter.Code, "Kapital Bank", "other", new SecretValue("x"), false, CancellationToken.None)));
        var unknown = await Assert.ThrowsAsync<ArgumentException>(() => AsAsync(tenant, s =>
            s.CreateAsync("kapitalbank_real", null, "l", new SecretValue("p"), false, CancellationToken.None)));
        Assert.Contains(FakeBankAdapter.Code, unknown.Message);
        Assert.Contains(FakeEdoAdapter.Code, unknown.Message);
    }

    [Fact]
    public async Task A_passing_test_activates_the_connection_and_queues_the_first_load()
    {
        RequirePostgres();
        var tenant = await Host.CreateTenantAsync("Credentials test ok");
        var r = await AsAsync(tenant, s => s.CreateAsync(FakeEdoAdapter.Code, null, "edo-login", new SecretValue(TestHost.RandomSecret()), test: true, CancellationToken.None));

        Assert.Equal("Didox", r.Name);
        Assert.Equal(ReactivationStatus.Reactivated, r.Test!.Status);
        Assert.NotNull(r.Test.JobId);
        var connection = await Host.QueryAsync(tenant, db => db.Connections.AsNoTracking().SingleAsync(c => c.Id == r.ConnectionId));
        Assert.Equal(ConnectionStatus.Active, connection.Status);
        var job = await Host.QueryAsync(tenant, db => db.SyncJobs.AsNoTracking().SingleAsync(j => j.ConnectionId == r.ConnectionId));
        Assert.Equal(SyncJobKind.Initial, job.Kind);
    }

    [Fact]
    public async Task A_failing_test_keeps_the_connection_out_of_the_schedule_until_the_access_is_fixed()
    {
        RequirePostgres();
        var tenant = await Host.CreateTenantAsync("Credentials test fails");
        var created = await AsAsync(tenant, s => s.CreateAsync(FakeBankAdapter.Code, "Bank", "login", new SecretValue("first"), test: false, CancellationToken.None));
        Host.FakeBank.For(created.ConnectionId).AuthFailure = true;

        var failed = await AsAsync(tenant, s => s.TestAsync("Bank", CancellationToken.None));
        Assert.Equal(ReactivationStatus.TestFailed, failed.Status);
        var connection = await Host.QueryAsync(tenant, db => db.Connections.AsNoTracking().SingleAsync(c => c.Id == created.ConnectionId));
        Assert.Equal(ConnectionStatus.NeedsAttention, connection.Status);
        Assert.Contains("Connection test failed", connection.StatusReason);
        Assert.DoesNotContain("first", connection.StatusReason);
        Assert.Equal(0, await Host.CountAsync(tenant, db => db.SyncJobs.Where(j => j.ConnectionId == created.ConnectionId)));

        // New access, tested: version 2, Active again, first load queued.
        Host.FakeBank.For(created.ConnectionId).AuthFailure = false;
        var second = TestHost.RandomSecret();
        var fixedAccess = await AsAsync(tenant, s => s.SetCredentialsAsync(created.ConnectionId.ToString(), "login2", new SecretValue(second), test: true, CancellationToken.None));
        Assert.False(fixedAccess.Created);
        Assert.Equal(2, fixedAccess.SecretVersion);
        Assert.Equal(ReactivationStatus.Reactivated, fixedAccess.Test!.Status);
        Assert.Equal(ConnectionStatus.Active, (await Host.QueryAsync(tenant, db => db.Connections.AsNoTracking().SingleAsync(c => c.Id == created.ConnectionId))).Status);
        Assert.Equal(second, LoginPasswordCredentials.Parse((await StoredAsync(tenant, created.ConnectionId))!).Password.Reveal());
        Assert.NotNull(await fixture.ScalarAsync<DateTime?>($"SELECT rotated_at FROM connection_secrets WHERE connection_id = '{created.ConnectionId}'"));
    }

    [Fact]
    public async Task Lists_connections_with_masked_login_and_never_the_password()
    {
        RequirePostgres();
        var tenant = await Host.CreateTenantAsync("Credentials list");
        var password = TestHost.RandomSecret();
        await AsAsync(tenant, s => s.CreateAsync(FakeBankAdapter.Code, "Bank", "accountant42", new SecretValue(password), false, CancellationToken.None));
        await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<Synect.Application.Statements.FileConnectionService>().CreateAsync("Other bank", null, CancellationToken.None));

        var rows = await AsAsync(tenant, s => s.ListAsync(CancellationToken.None));
        var bank = Assert.Single(rows); // the file import connection has no credentials and is not listed
        Assert.True(bank.HasCredentials);
        Assert.Equal("ac********", bank.LoginMasked);
        Assert.Equal(1, bank.SecretVersion);
        Assert.DoesNotContain(password, bank.ToString());
        Assert.DoesNotContain("accountant42", bank.ToString());
        Assert.Equal("**", ConnectionCredentialsService.MaskLogin("ab"));

        // Only bank and Didox connections take credentials.
        await Assert.ThrowsAsync<InvalidOperationException>(() => AsAsync(tenant, s =>
            s.SetCredentialsAsync("Other bank (file import)", "l", new SecretValue("p"), false, CancellationToken.None)));
    }

    [Fact]
    public async Task Organizations_never_see_or_change_each_others_connections()
    {
        RequirePostgres();
        var a = await Host.CreateTenantAsync("Credentials org A");
        var b = await Host.CreateTenantAsync("Credentials org B");
        var created = await AsAsync(a, s => s.CreateAsync(FakeBankAdapter.Code, "Bank", "a-login", new SecretValue("a-password"), false, CancellationToken.None));

        Assert.Empty(await AsAsync(b, s => s.ListAsync(CancellationToken.None)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => AsAsync(b, s =>
            s.SetCredentialsAsync(created.ConnectionId.ToString(), "evil", new SecretValue("evil"), false, CancellationToken.None)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => AsAsync(b, s => s.TestAsync(created.ConnectionId.ToString(), CancellationToken.None)));
        Assert.Equal("a-login", LoginPasswordCredentials.Parse((await StoredAsync(a, created.ConnectionId))!).Login);
        // Without an organization nothing can be managed.
        await using var scope = Host.Scope(null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<ConnectionCredentialsService>().ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Empty_or_oversized_access_is_refused_and_nothing_secret_reaches_the_logs()
    {
        RequirePostgres();
        var tenant = await Host.CreateTenantAsync("Credentials validation");
        foreach (var (login, password) in new[] { ("", "p"), ("  ", "p"), ("l", ""), (new string('l', 257), "p"), ("l", new string('p', 1025)), ("l\u0001", "p") })
            await Assert.ThrowsAsync<ArgumentException>(() => AsAsync(tenant, s =>
                s.CreateAsync(FakeBankAdapter.Code, Guid.NewGuid().ToString("N"), login, new SecretValue(password), false, CancellationToken.None)));
        Assert.Equal(0, await Host.CountAsync(tenant, db => db.Connections));

        var password2 = TestHost.RandomSecret();
        await AsAsync(tenant, s => s.CreateAsync(FakeBankAdapter.Code, "Bank", "logged-login", new SecretValue(password2), test: true, CancellationToken.None));
        Assert.DoesNotContain(Host.Logs.Lines, l => l.Contains(password2) || l.Contains("logged-login"));
    }
}
