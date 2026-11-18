using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Synect.Api.Hosting;
using Synect.Tests.Postgres;

namespace Synect.Tests.Deployment;

/// <summary>/ready (database + schema compatibility, 503 while stopping) and /version of the real Api host.</summary>
public class ReadinessTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private readonly string _keysPath = Path.Combine(Path.GetTempPath(), "synect-test-keys-" + Guid.NewGuid().ToString("N"));
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    private WebApplicationFactory<Program> Api(string connectionString)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:Synect", connectionString);
            b.UseSetting("Secrets:KeysPath", _keysPath);
            b.UseSetting("Hosting:Readiness:CacheDuration", "00:00:00");
        });
        _factories.Add(factory);
        return factory;
    }

    [Fact]
    public async Task Ready_and_version_report_a_compatible_schema()
    {
        Assert.SkipUnless(fixture.Available, PostgresFixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        using var http = Api(fixture.AppConnectionString).CreateClient();

        var ready = await http.GetAsync("/ready", ct);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("ready", (await Json(ready)).GetProperty("status").GetString());

        var version = await Json(await http.GetAsync("/version", ct));
        Assert.False(string.IsNullOrEmpty(version.GetProperty("version").GetString()));
        var schema = version.GetProperty("schema");
        Assert.True(schema.GetProperty("compatible").GetBoolean());
        Assert.Equal(schema.GetProperty("expected").GetString(), schema.GetProperty("applied").GetString());
        Assert.Equal(0, schema.GetProperty("pending").GetArrayLength());
    }

    [Fact]
    public async Task Ready_fails_while_a_migration_of_this_build_is_missing()
    {
        Assert.SkipUnless(fixture.Available, PostgresFixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        using var http = Api(fixture.AppConnectionString).CreateClient();
        var latest = await fixture.ScalarAsync<string>("SELECT max(migration_id) FROM __ef_migrations_history");
        var product = await fixture.ScalarAsync<string>($"SELECT product_version FROM __ef_migrations_history WHERE migration_id = '{latest}'");

        await PostgresFixture.ExecAsync(fixture.AdminDbConnectionString, $"DELETE FROM __ef_migrations_history WHERE migration_id = '{latest}'");
        try
        {
            var ready = await http.GetAsync("/ready", ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            var body = await Json(ready);
            Assert.Equal("schema_incompatible", body.GetProperty("reason").GetString());
            // Nothing about migrations leaks through the public endpoint.
            Assert.DoesNotContain(latest, body.GetRawText());

            var schema = (await Json(await http.GetAsync("/version", ct))).GetProperty("schema");
            Assert.False(schema.GetProperty("compatible").GetBoolean());
            Assert.Equal(latest, schema.GetProperty("pending")[0].GetString());
        }
        finally
        {
            await PostgresFixture.ExecAsync(fixture.AdminDbConnectionString,
                $"INSERT INTO __ef_migrations_history (migration_id, product_version) VALUES ('{latest}', '{product}')");
        }
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/ready", ct)).StatusCode);
    }

    [Fact]
    public async Task Ready_answers_503_once_the_stop_has_begun()
    {
        Assert.SkipUnless(fixture.Available, PostgresFixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var api = Api(fixture.AppConnectionString);
        using var http = api.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/ready", ct)).StatusCode);

        // What IHostApplicationLifetime.ApplicationStopping triggers (SIGTERM from systemd).
        api.Services.GetRequiredService<ReadinessProbe>().MarkStopping();

        var ready = await http.GetAsync("/ready", ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("stopping", (await Json(ready)).GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health", ct)).StatusCode);
    }

    [Fact]
    public async Task Ready_answers_503_when_the_database_is_unreachable_while_health_stays_up()
    {
        var ct = TestContext.Current.CancellationToken;
        // Nothing listens on port 1: the connection is refused at once.
        using var http = Api("Host=127.0.0.1;Port=1;Database=synect;Username=synect_app;Timeout=2").CreateClient();

        var ready = await http.GetAsync("/ready", ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("database_unavailable", (await Json(ready)).GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health", ct)).StatusCode);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    public async ValueTask DisposeAsync()
    {
        foreach (var f in _factories) await f.DisposeAsync();
        try { if (Directory.Exists(_keysPath)) Directory.Delete(_keysPath, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
