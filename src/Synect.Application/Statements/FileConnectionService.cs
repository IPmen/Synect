using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Synect.Application.Abstractions;
using Synect.Domain.Sync;

namespace Synect.Application.Statements;

public sealed record FileConnectionInfo(Guid Id, string Name, string BankName, string? DefaultProfile, bool Created);

/// <summary>
/// File import connections (<see cref="AdapterKind.File"/>): one per bank whose statements are imported from files. No
/// credentials; settings hold the bank name and the default profile. Never queued: data arrives with each import.
/// </summary>
public sealed class FileConnectionService(ISynectDbContext db, TimeProvider time)
{
    public const string BankNameSetting = "bank_name";
    public const string ProfileSetting = "default_profile";

    /// <summary>Creates the connection "{bankName} (file import)" or returns the existing one with that name.</summary>
    public Task<FileConnectionInfo> CreateAsync(string bankName, string? defaultProfile, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            if (string.IsNullOrWhiteSpace(bankName)) throw new ArgumentException("A bank name is required.");
            var name = bankName.Trim() + " (file import)";
            if (name.Length > 256) throw new ArgumentException("The bank name is too long.");
            var existing = await db.Connections.AsNoTracking().SingleOrDefaultAsync(c => c.Name == name, token);
            if (existing is not null)
            {
                if (existing.AdapterKind != AdapterKind.File)
                    throw new InvalidOperationException($"A connection named '{name}' exists and is not a file import connection.");
                return Describe(existing, created: false);
            }

            var settings = new Dictionary<string, string?> { [BankNameSetting] = bankName.Trim() };
            if (!string.IsNullOrWhiteSpace(defaultProfile)) settings[ProfileSetting] = defaultProfile.Trim();
            var connection = new Connection
            {
                Name = name,
                SourceCode = Connection.FileImportSourceCode,
                AdapterKind = AdapterKind.File,
                Settings = JsonSerializer.Serialize(settings),
                CreatedAt = time.GetUtcNow(),
            };
            db.Connections.Add(connection);
            await db.SaveChangesAsync(token);
            return Describe(connection, created: true);
        }, ct);

    /// <summary>A file import connection by id or name.</summary>
    public Task<FileConnectionInfo> FindAsync(string idOrName, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var key = idOrName.Trim();
            var connection = Guid.TryParse(key, out var id)
                ? await db.Connections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, token)
                : await db.Connections.AsNoTracking().SingleOrDefaultAsync(c => c.Name == key, token)
                  ?? await db.Connections.AsNoTracking().SingleOrDefaultAsync(c => c.Name == key + " (file import)", token);
            if (connection is null) throw new KeyNotFoundException($"Connection '{key}' not found for this client.");
            if (connection.AdapterKind != AdapterKind.File)
                throw new InvalidOperationException($"Connection '{connection.Name}' is not a file import connection.");
            return Describe(connection, created: false);
        }, ct);

    private static FileConnectionInfo Describe(Connection c, bool created)
    {
        string? bank = null, profile = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(c.Settings) ? "{}" : c.Settings);
            if (doc.RootElement.TryGetProperty(BankNameSetting, out var b) && b.ValueKind == JsonValueKind.String) bank = b.GetString();
            if (doc.RootElement.TryGetProperty(ProfileSetting, out var p) && p.ValueKind == JsonValueKind.String) profile = p.GetString();
        }
        catch (JsonException)
        {
        }
        return new FileConnectionInfo(c.Id, c.Name, bank ?? c.Name, profile, created);
    }
}
