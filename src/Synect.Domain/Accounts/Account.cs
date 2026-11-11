using System.Text.RegularExpressions;

namespace Synect.Domain.Accounts;

public enum AccountStatus
{
    Active = 1,

    /// <summary>Keys of a suspended account do not work; its organizations and their data stay.</summary>
    Suspended = 2,
}

/// <summary>
/// An accounting company (accounts) that serves many client organizations (tenants). Holds no organization data.
/// The application role cannot read this table: it is reached only through SECURITY DEFINER functions.
/// </summary>
public class Account
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = string.Empty;
    public AccountStatus Status { get; set; } = AccountStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>An organization (tenant) served by an account (account_tenants). A tenant belongs to at most one account.</summary>
public class AccountTenant
{
    public const int MaxDisplayNameLength = 256;

    public Guid AccountId { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>Optional name the account uses for the organization; the tenant name otherwise.</summary>
    public string? DisplayName { get; set; }

    public DateTimeOffset AddedAt { get; set; }

    /// <summary>A disabled organization is invisible to account keys and its own keys stop working; its data stays.</summary>
    public DateTimeOffset? DisabledAt { get; set; }
}

/// <summary>
/// Restriction of an account key to some organizations (api_key_tenants). An account key without rows here sees every
/// non-disabled organization of its account.
/// </summary>
public class ApiKeyTenant
{
    public Guid KeyId { get; set; }
    public Guid TenantId { get; set; }
}

/// <summary>Taxpayer identification number of an organization: 9 digits (legal entity) or 14 digits (individual, PINFL).</summary>
public static partial class Tin
{
    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);

    /// <summary>Trimmed TIN, or null when the value is empty; throws for anything else than 9 or 14 digits.</summary>
    public static string? Normalize(string? value)
    {
        var tin = value?.Trim();
        if (string.IsNullOrEmpty(tin)) return null;
        if (!IsValid(tin)) throw new ArgumentException($"TIN must be exactly 9 or 14 digits, got '{tin}'.", nameof(value));
        return tin;
    }

    [GeneratedRegex("^([0-9]{9}|[0-9]{14})$")]
    private static partial Regex Pattern();
}
