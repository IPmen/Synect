using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Abstractions;
using Synect.Application.Queue;
using Synect.Application.Secrets;
using Synect.Infrastructure.Adapters;

namespace Synect.DevTools;

/// <summary>
/// Access of a client organization to a bank or Didox: create-connection, set-credentials, test-connection,
/// list-connections, list-sources. The password is never a command-line argument (it would stay in the shell history and
/// the process list): it is typed without echo, or read from standard input with --password-stdin. It is never printed.
/// </summary>
public sealed class ConnectionCommands(IServiceProvider services)
{
    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<ConnectionCredentialsService, Task<T>> work)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenantId);
        return await work(scope.ServiceProvider.GetRequiredService<ConnectionCredentialsService>());
    }

    public int ListSources()
    {
        using var scope = services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ConnectionCredentialsService>();
        foreach (var (code, provider, kind) in service.AvailableSources()) Console.WriteLine($"{code,-24} {kind,-6} {provider}");
        Console.WriteLine("Only these source codes can be used with create-connection (adapters registered in this build).");
        return 0;
    }

    /// <summary>create-connection --tenant X --source &lt;code&gt; --login &lt;login&gt; [--name ...] [--test] [--password-stdin]</summary>
    public async Task<int> CreateAsync(Guid tenantId, CliArgs options)
    {
        var source = options.Get("source") ?? throw new ArgumentException("--source <source code> is required (list-sources shows them)");
        var login = RequireLogin(options);
        var password = ReadPassword(options, confirm: true);
        var test = options.Get("test") == "true";
        var r = await InTenantAsync(tenantId, s => s.CreateAsync(source, options.Get("name"), login, password, test, CancellationToken.None));
        Console.WriteLine($"Connection created: {r.Name} ({r.SourceCode}, {r.Kind}), id {r.ConnectionId}");
        Console.WriteLine($"  credentials stored encrypted (version {r.SecretVersion}), login {ConnectionCredentialsService.MaskLogin(login)}");
        return Report(r.Test, test, created: true);
    }

    /// <summary>set-credentials --tenant X --connection &lt;id|name&gt; --login &lt;login&gt; [--test] [--password-stdin]</summary>
    public async Task<int> SetAsync(Guid tenantId, CliArgs options)
    {
        var connection = options.Get("connection") ?? throw new ArgumentException("--connection <id|name> is required (list-connections shows them)");
        var login = RequireLogin(options);
        var password = ReadPassword(options, confirm: true);
        var test = options.Get("test") == "true";
        var r = await InTenantAsync(tenantId, s => s.SetCredentialsAsync(connection, login, password, test, CancellationToken.None));
        Console.WriteLine($"Credentials of {r.Name} ({r.SourceCode}) replaced: version {r.SecretVersion}, login {ConnectionCredentialsService.MaskLogin(login)}");
        return Report(r.Test, test, created: false);
    }

    public async Task<int> TestAsync(Guid tenantId, CliArgs options)
    {
        var connection = options.Get("connection") ?? throw new ArgumentException("--connection <id|name> is required (list-connections shows them)");
        var r = await InTenantAsync(tenantId, s => s.TestAsync(connection, CancellationToken.None));
        return Report(r, true, created: false);
    }

    public async Task<int> ListAsync(Guid tenantId)
    {
        var rows = await InTenantAsync(tenantId, s => s.ListAsync(CancellationToken.None));
        foreach (var c in rows)
        {
            Console.WriteLine($"{c.Id}  {c.Name}  [{c.SourceCode}, {c.Kind}]  {c.Status}{(c.StatusReason is { } reason ? $" ({reason})" : "")}");
            Console.WriteLine(c.HasCredentials
                ? $"    credentials: login {c.LoginMasked}, version {c.SecretVersion}, entered {c.SecretCreatedAt:u}{(c.SecretRotatedAt is { } rot ? $", replaced {rot:u}" : "")}"
                : "    credentials: none (set-credentials)");
            Console.WriteLine($"    last successful load: {(c.LastSuccessfulSyncAt is { } at ? at.ToString("u") : "never")}");
        }
        if (rows.Count == 0) Console.WriteLine("No bank or Didox connections.");
        return 0;
    }

    private static int Report(ReactivationResult? test, bool requested, bool created)
    {
        if (!requested)
        {
            Console.WriteLine(created
                ? "  Not tested: the connection is Active and will be loaded on the schedule (test-connection checks the access now)."
                : "  Not tested (test-connection checks the access now).");
            return 0;
        }
        switch (test!.Status)
        {
            case ReactivationStatus.Reactivated:
                Console.WriteLine($"  Test passed: the connection is Active, first load queued (job {test.JobId}).");
                return 0;
            case ReactivationStatus.AlreadyActive:
                Console.WriteLine("  The connection is already Active.");
                return 0;
            case ReactivationStatus.Disabled:
                Console.WriteLine($"  The connection is Disabled and was not tested{(test.Error is { } d ? $": {d}" : "")}.");
                return 1;
            default:
                // The adapter's message never contains the password (adapters do not echo credentials).
                Console.WriteLine($"  Test FAILED: {test.Error}. The connection stays in NeedsAttention and is not loaded; check the login and password and run set-credentials --test.");
                return 2;
        }
    }

    private static string RequireLogin(CliArgs options)
    {
        var login = options.Get("login");
        if (string.IsNullOrWhiteSpace(login) || login == "true") throw new ArgumentException("--login <login> is required");
        if (options.Get("password") is not null)
            throw new ArgumentException("Do not pass the password as an argument (it stays in the shell history); omit --password to type it, or use --password-stdin.");
        return login;
    }

    /// <summary>The password typed without echo (twice), or one line of standard input with --password-stdin.</summary>
    private static SecretValue ReadPassword(CliArgs options, bool confirm)
    {
        if (options.Get("password-stdin") == "true")
        {
            var line = Console.In.ReadLine() ?? "";
            return new SecretValue(line.TrimEnd('\r', '\n'));
        }
        if (Console.IsInputRedirected)
            throw new ArgumentException("Standard input is redirected: use --password-stdin to pass the password through it.");
        var first = ReadHidden("Password: ");
        if (confirm && ReadHidden("Repeat the password: ") != first) throw new ArgumentException("The passwords differ; nothing was stored.");
        return new SecretValue(first);
    }

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0) text.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
        }
        Console.WriteLine();
        return text.ToString();
    }
}
