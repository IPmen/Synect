namespace Synect.Application.Abstractions;

public static class DbContextExtensions
{
    /// <summary>
    /// Runs <paramref name="work"/> inside a database transaction. In PostgreSQL the tenant id is
    /// applied with SET LOCAL at transaction start, so row-level security covers reads and writes.
    /// Joins an already open transaction instead of nesting.
    /// </summary>
    public static async Task<T> InTransactionAsync<T>(
        this ISynectDbContext db, Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null)
            return await work(ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var result = await work(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public static Task InTransactionAsync(
        this ISynectDbContext db, Func<CancellationToken, Task> work, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            await work(token);
            return true;
        }, ct);
}
