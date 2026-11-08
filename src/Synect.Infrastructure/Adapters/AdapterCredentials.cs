using System.Text.Json;
using Synect.Application.Adapters;
using Synect.Application.Secrets;

namespace Synect.Infrastructure.Adapters;

/// <summary>Login and password stored as the "credentials" secret: {"login": "...", "password": "..."}.</summary>
public sealed class LoginPasswordCredentials
{
    public required string Login { get; init; }
    public required SecretValue Password { get; init; }

    public static LoginPasswordCredentials Parse(SecretValue secret)
    {
        try
        {
            using var doc = JsonDocument.Parse(secret.Reveal());
            var login = doc.RootElement.GetProperty("login").GetString();
            var password = doc.RootElement.GetProperty("password").GetString();
            if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(password)) throw new FormatException();
            return new LoginPasswordCredentials { Login = login, Password = new SecretValue(password) };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            // Never include the secret (or the parser message, which may quote it).
            throw new AdapterAuthenticationException("Stored credentials are malformed: expected login and password.");
        }
    }

    public static SecretValue Create(string login, string password) =>
        new(JsonSerializer.Serialize(new Dictionary<string, string> { ["login"] = login, ["password"] = password }));
}
