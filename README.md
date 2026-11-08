# Synect

Loads data from Didox, banks and 1C into PostgreSQL on a schedule (or on demand); REST and MCP tools read only this
database and never call external systems.

## Layout

| Project | Purpose |
|---|---|
| `Synect.Domain` | Entities: tenants, connections (states), sync runs, sync jobs, connection secrets, raw objects, bank, EDO, counterparties |
| `Synect.Application` | Adapter contracts and registry, sync services, queue (planner, dispatcher, requests), rate limiting and retries, secrets and credentials contracts, freshness query |
| `Synect.Infrastructure` | EF Core + Npgsql, migrations, RLS interceptor, Data Protection secret store, Kapital Bank / Didox mappers, session and token caches, fake adapters |
| `Synect.Api` | HTTP host (health only for now) |
| `Synect.Worker` | Hosts the sync queue (`SyncWorker`) |
| `Synect.Tests` | xUnit v3 tests (SQLite temporary file + local PostgreSQL) |

## Local database (no Docker)

1. Create roles and the database (as a superuser): `psql -U postgres -f db/create-roles.sql`, then set passwords with `\password`.
2. Put the connection strings into user-secrets (never commit them). Api, Worker, Tests and `dotnet ef` share the id `synect-local-dev`:
   ```
   dotnet user-secrets set "ConnectionStrings:Synect" "Host=localhost;Database=synect;Username=synect_app;Password=..." --project src/Synect.Api
   dotnet user-secrets set "ConnectionStrings:SynectOwner" "Host=localhost;Database=synect;Username=synect_owner;Password=..." --project src/Synect.Api
   ```
3. Apply migrations as the owner (the design-time factory reads `ConnectionStrings:SynectOwner` from user-secrets, or `SYNECT_DB`):
   ```
   dotnet tool restore
   dotnet ef database update -p src/Synect.Infrastructure -s src/Synect.Infrastructure
   ```
   `db/schema.sql` is the same schema as an idempotent SQL script, for review.

The app must connect as `synect_app` (not owner, not superuser): row-level security is forced on every tenant table and
reads `app.tenant_id`, which `TenantSessionInterceptor` sets with `set_config(..., true)` (= `SET LOCAL`) at the start
of every transaction. Application code therefore reads and writes inside transactions (`InTransactionAsync`).

## Sync queue

Own queue on PostgreSQL (`sync_jobs`) run by `SyncDispatcher` in the Worker:

- Every `Sync:Queue:PollInterval` the worker walks the `tenants` table (no RLS) and, per tenant in its own scope and
  transaction: marks runs/jobs Running for longer than `StaleRunTimeout` as Failed, plans due jobs, collects queued jobs.
- Jobs start by priority (manual, then scheduled/reactivation, then initial loads), limited globally (`MaxConcurrent`)
  and per provider (`MaxConcurrentPerSource`). A partial unique index allows only one Queued/Running job per
  connection, so loads never overlap. Each job runs in its own task with `JobTimeout`.
- Schedule: nightly load inside `Sync:Schedule:NightlyWindowStart..End` (local time, UTC+5); the start is shifted by a
  deterministic hash of the connection id. EDO connections also refresh every `EdoDaytimeRefreshHours` during the day.
- "Refresh now": `SyncRequestService.RequestSyncAsync(connectionId)` queues a job and returns at once:
  `Queued`, `AlreadyQueued`, `AlreadyRunning`, `TooSoon` (`ManualRequestMinInterval`) or `ConnectionNotActive`.
- Connection states: Active, NeedsAttention, Disabled (with reason and time of change). After `Sync:CircuitBreaker:FailureThreshold`
  failed loads in a row, or at once on an authentication error, a connection goes to NeedsAttention and is no longer
  scheduled. `ReactivateAsync(connectionId)` tests the connection and, on success, makes it Active and queues a load.
- `Sync:RunOnStartup=true` (development) queues a load of every Active connection at startup.

Bank days are re-read from the earliest day with `is_day_closed = false`, otherwise only yesterday and today; days
missed while the worker was down are always included. The first load covers `Sync:Bank:InitialDays` (capped by the
bank history depth).

## Secrets

`connection_secrets` holds Data Protection ciphertext only (bound to tenant, connection and kind); `connections.credentials_ref`
points to the row. Keys live in `Secrets:KeysPath` (default `%APPDATA%/Synect/DataProtection-Keys`, DPAPI-protected on
Windows), never in the repository. `ISecretStore` (Get/Set/Rotate) writes secrets; adapters read them through
`ICredentialProvider`. `SecretValue.ToString()` is redacted. Didox tokens (360 min) and Kapital Bank sessions (`sid`) are
cached in memory per connection.

## Tests

```
dotnet test --solution Synect.slnx
```

PostgreSQL tests (migrations, RLS, queue, secrets) create and drop a temporary database and role. They need a
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
| `Adapters:Didox:Token:TokenLifetime` / `RefreshMargin` | 6 h / 10 min | Didox user token lifetime and early refresh. |
| `Adapters:{KapitalBank,Didox}:RateLimit:PerConnection` | 5 tokens / 1 s | Token bucket per connection. |
| `Adapters:{KapitalBank,Didox}:RateLimit:PerSource` | 20 tokens / 1 s | Token bucket shared by the provider. |
| `Sync:Bank:InitialDays` | 30 | First load depth. |
| `Sync:Bank:OperationalDayUtcOffset` | `05:00:00` | Bank day boundary. |
| `Sync:Edo:UpdatedSinceOverlap` | 1 day | Incremental window overlap. |
| `Sync:Queue:PollInterval` | 10 s | Planner/dispatcher tick. |
| `Sync:Queue:MaxConcurrent` | 4 | Loads at the same time, overall. |
| `Sync:Queue:DefaultMaxConcurrentPerSource` / `MaxConcurrentPerSource` | 2 / `{KapitalBank: 2, Didox: 2}` | Loads at the same time per provider. |
| `Sync:Queue:JobTimeout` | 1 h | A load is cancelled after this. |
| `Sync:Queue:StaleRunTimeout` | 2 h | Running runs/jobs older than this are marked Failed. |
| `Sync:Queue:ManualRequestMinInterval` | 5 min | Between two "refresh now" requests per connection. |
| `Sync:Schedule:NightlyWindowStart` / `End` | 01:00 / 05:00 | Local time (UTC+5). |
| `Sync:Schedule:EdoDaytimeRefreshHours` | 4 | 0 disables daytime refreshes. |
| `Sync:Schedule:DaytimeStart` / `End` | 08:00 / 20:00 | Daytime refresh window. |
| `Sync:Retry:MaxAttempts` / `BaseDelay` / `MaxDelay` | 4 / 1 s / 30 s | Exponential backoff with jitter for network, 5xx, 429. |
| `Sync:CircuitBreaker:FailureThreshold` | 3 | Failed loads in a row before NeedsAttention. |
| `Secrets:KeysPath` | `%APPDATA%/Synect/DataProtection-Keys` | Data Protection key ring, outside the repository. |

All money is stored as `long` in minor units.

Bank operations with `err != 0` are stored with `has_error = true`, `error_code`, `error_message`; balances and
reconciliation must use `BankTransactionQueries.WithoutErrors()`.
