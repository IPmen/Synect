using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synect.Api.Ingest;
using Synect.Application.Ingest;
using Synect.Tests.Infrastructure;

namespace Synect.Tests.Ingest;

/// <summary>
/// Ingest:AllowAnyTinLengthInDevelopment: 10 and 12 digits in organization_tin and counterparty_tin in Development only;
/// Production ignores the setting and logs a warning.
/// </summary>
public class IngestDevTinTests
{
    private static readonly DateTimeOffset Exported = new(2026, 11, 1, 5, 0, 0, TimeSpan.Zero);

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>The options as the API host builds them: Ingest section, then <see cref="IngestEndpoint.AddSynectIngest"/>.</summary>
    private static (IngestOptions Options, CapturingLoggerProvider Logs) Build(string environment, bool allow)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ingest:AllowAnyTinLengthInDevelopment"] = allow ? "true" : "false" })
            .Build();
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new Env(environment));
        services.AddLogging(l => l.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        services.Configure<IngestOptions>(configuration.GetSection("Ingest"));
        services.AddSynectIngest();
        using var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IOptions<IngestOptions>>().Value, logs);
    }

    private static IngestEnvelope Envelope(IngestOptions options, string tin) =>
        IngestEnvelopeParser.Parse(J($$"""
            {"schema_version":1,"organization_tin":"{{tin}}","batch_id":"b-1","sequence":1,"exported_at":"2026-11-01T10:00:00",
             "mode":"delta","entity":"documents","items":[]}
            """), options, tin).Envelope;

    private static ParsedItem Document(IngestOptions options, string counterpartyTin) =>
        new IngestItemParser(IngestSchema.Find("documents")!, options).Parse(0, J($$"""
            {"ref":"d-1","doc_kind":"sales","number":"1","date":"2026-11-01","posted":true,"amount_total":"10.00",
             "counterparty_tin":"{{counterpartyTin}}"}
            """), Exported);

    [Theory]
    [InlineData("3010000011")]
    [InlineData("301000001122")]
    public void Development_with_the_setting_accepts_10_and_12_digits(string tin)
    {
        var (options, logs) = Build(Environments.Development, allow: true);
        Assert.True(options.ExtraTinLengthsAccepted);

        Assert.Equal(tin, Envelope(options, tin).OrganizationTin);
        var item = Document(options, tin);
        Assert.True(item.IsValid, item.ErrorMessage);
        Assert.Equal(tin, item.Values["counterparty_tin"]);
        Assert.Contains(logs.Lines, l => l.Contains("Warning: Ingest:AllowAnyTinLengthInDevelopment is on", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("301000001")]
    [InlineData("12345678901234")]
    public void Development_with_the_setting_still_accepts_9_and_14_digits(string tin)
    {
        var (options, _) = Build(Environments.Development, allow: true);
        Assert.Equal(tin, Envelope(options, tin).OrganizationTin);
        Assert.True(Document(options, tin).IsValid);
    }

    [Theory]
    [InlineData("30100000")]
    [InlineData("30100000112")]
    [InlineData("3010000011223")]
    [InlineData("301000001A")]
    public void Development_with_the_setting_refuses_other_lengths_and_non_digits(string tin)
    {
        var (options, _) = Build(Environments.Development, allow: true);
        Assert.Equal(IngestEnvelopeCodes.InvalidEnvelope, Assert.Throws<IngestEnvelopeException>(() => Envelope(options, tin)).Code);
        Assert.Equal(IngestCodes.InvalidTin, Document(options, tin).ErrorCode);
    }

    [Theory]
    [InlineData("3010000011")]
    [InlineData("301000001122")]
    public void Production_refuses_10_and_12_digits_even_with_the_setting_and_warns(string tin)
    {
        var (options, logs) = Build(Environments.Production, allow: true);
        Assert.False(options.ExtraTinLengthsAccepted);

        var ex = Assert.Throws<IngestEnvelopeException>(() => Envelope(options, tin));
        Assert.Equal(IngestEnvelopeCodes.InvalidEnvelope, ex.Code);
        Assert.Equal(400, ex.HttpStatus);
        Assert.Equal(IngestCodes.InvalidTin, Document(options, tin).ErrorCode);
        Assert.Contains(logs.Lines, l => l.Contains("Warning: Ingest:AllowAnyTinLengthInDevelopment is set but ignored in the Production environment", StringComparison.Ordinal));
    }

    [Fact]
    public void Development_without_the_setting_refuses_10_digits_and_logs_nothing()
    {
        var (options, logs) = Build(Environments.Development, allow: false);
        Assert.False(options.ExtraTinLengthsAccepted);
        Assert.Throws<IngestEnvelopeException>(() => Envelope(options, "3010000011"));
        Assert.Equal(IngestCodes.InvalidTin, Document(options, "3010000011").ErrorCode);
        Assert.DoesNotContain(logs.Lines, l => l.Contains("AllowAnyTinLengthInDevelopment", StringComparison.Ordinal));
    }

    private static ParsedItem Counterparty(IngestOptions options, string tin) =>
        new IngestItemParser(IngestSchema.Find("counterparties")!, options)
            .Parse(0, J($$"""{"ref":"c-1","name":"A","tin":"{{tin}}"}"""), Exported);

    [Theory]
    [InlineData("Development", true, "3010000011", true)]
    [InlineData("Development", true, "301000001122", true)]
    [InlineData("Development", true, "301000001", true)]
    [InlineData("Development", true, "30100000112", false)]
    [InlineData("Development", false, "3010000011", false)]
    [InlineData("Development", false, "301000001122", false)]
    [InlineData("Production", true, "3010000011", false)]
    [InlineData("Production", true, "301000001122", false)]
    [InlineData("Production", true, "301000001", true)]
    public void The_counterparties_entity_tin_follows_the_same_rule(string environment, bool allow, string tin, bool valid)
    {
        var (options, _) = Build(environment, allow);
        var item = Counterparty(options, tin);
        if (valid)
        {
            Assert.True(item.IsValid, item.ErrorMessage);
            Assert.Equal(tin, item.Values["tin"]);
        }
        else Assert.Equal(IngestCodes.InvalidTin, item.ErrorCode);
    }

    [Fact]
    public void The_effective_flag_is_not_bound_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ingest:ExtraTinLengthsAccepted"] = "true" })
            .Build();
        Assert.False(configuration.GetSection("Ingest").Get<IngestOptions>()!.ExtraTinLengthsAccepted);
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Synect.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
