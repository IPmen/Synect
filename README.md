# Synect

Loads data from Didox, banks and 1C into PostgreSQL once a day (or on demand); REST and MCP tools read only this database.

## Layout

| Project | Purpose |
|---|---|
| `Synect.Domain` | Entities: tenants, connections, sync runs, raw objects, bank, EDO, counterparties |
| `Synect.Application` | Adapter contracts (`IBankAdapter`, `IEdoAdapter`, registry), sync services, `ISynectDbContext` |
| `Synect.Infrastructure` | EF Core + Npgsql, migrations, RLS interceptor, Kapital Bank / Didox mappers, fake adapters |
| `Synect.Api` | HTTP host (health only for now) |
| `Synect.Worker` | Runs a one-off sync of all active connections when `Sync:RunOnStartup=true` |
| `Synect.Tests` | xUnit v3 tests (SQLite in-memory + optional local PostgreSQL) |

## Local database (no Docker)

1. Create roles and the database (as a superuser): `psql -U postgres -f db/create-roles.sql`, then set passwords with `\password`.
2. Apply migrations as the owner role:
   ```
   dotnet tool restore
   dotnet ef database update -p src/Synect.Infrastructure -s src/Synect.Infrastructure --connection "Host=localhost;Database=synect;Username=synect_owner;Password=..."
   ```
   `db/schema.sql` is the same schema as an idempotent SQL script, for review.
3. Give the apps their connection string (never commit it). Api, Worker and Tests share one user-secrets id:
   ```
   dotnet user-secrets set "ConnectionStrings:Synect" "Host=localhost;Database=synect;Username=synect_app;Password=..." --project src/Synect.Api
   ```

The app must connect as `synect_app` (not owner, not superuser): row-level security is forced on every tenant table and
reads `app.tenant_id`, which `TenantSessionInterceptor` sets with `set_config(..., true)` (= `SET LOCAL`) at the start
of every transaction. Application code therefore reads and writes inside transactions (`InTransactionAsync`).

## Tests

```
dotnet test --solution Synect.slnx
```

PostgreSQL tests (migrations, RLS, append-only trigger) create and drop a temporary database and role. They need a
superuser connection string and are skipped otherwise:

```
dotnet user-secrets set "ConnectionStrings:SynectTestAdmin" "Host=localhost;Username=postgres;Password=..." --project tests/Synect.Tests
```

or the `SYNECT_TEST_PG` environment variable.

## Configuration

| Key | Default | Notes |
|---|---|---|
| `Adapters:KapitalBank:AmountUnit` | `Minor` | **Unconfirmed** whether Kapital Bank sends sum or tiyin. `Major` multiplies by 100. |
| `Adapters:Didox:SumUnit` | `Major` | Didox totals in sum with fraction, stored in tiyin. |
| `Adapters:Didox:SourceUtcOffset` | `05:00:00` | Zone for Didox timestamps without offset. |
| `Sync:Bank:InitialDays` / `RefetchDays` | 30 / 3 | First load depth / re-read window. |
| `Sync:Edo:UpdatedSinceOverlap` | 1 day | Incremental window overlap. |

All money is stored as `long` in minor units.

Bank operations with `err != 0` are stored with `has_error = true`, `error_code`, `error_message`; balances and
reconciliation must use `BankTransactionQueries.WithoutErrors()`.
