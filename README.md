# Synect

Loads data from Didox, banks and 1C into PostgreSQL on a schedule (or on demand); REST and MCP tools read only this
database and never call external systems.

## Layout

| Project | Purpose |
|---|---|
| `Synect.Domain` | Entities: tenants, connections (states), sync runs, sync jobs, connection secrets, raw objects, bank, EDO, counterparties |
| `Synect.Application` | Adapter contracts and registry, sync services, queue (planner, dispatcher, requests), rate limiting and retries, secrets and credentials contracts, freshness query, read-only analytics (`Analytics/`) |
| `Synect.Infrastructure` | EF Core + Npgsql, migrations, RLS interceptor, Data Protection secret store, Kapital Bank / Didox mappers, session and token caches, fake adapters |
| `Synect.Api` | HTTP host (health only for now) |
| `Synect.Worker` | Hosts the sync queue (`SyncWorker`) |
| `Synect.DevTools` | Development only: demo/load data, `query`, `bench`, `explain` (not referenced by Api or Worker) |
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

- At startup the worker (single instance) marks every Running job and run as Failed. Then every `Sync:Queue:PollInterval`
  it walks the `tenants` table (no RLS) and, per tenant in its own scope and transaction: fails Running jobs whose
  `heartbeat_at` is older than `HeartbeatTimeout` (with their runs), plans due jobs, collects queued jobs. Running jobs
  write `heartbeat_at` every `HeartbeatInterval`.
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

## Analytics

`Synect.Application/Analytics` answers the question catalogue (blocks A, B, V) from the database only; it has no
dependency on adapters, sync, queue or secrets (enforced by a test). Rules shared by every method:

- the tenant comes from the current tenant context, never from arguments; reads run in a tenant transaction (RLS);
- amounts are never added across currencies (results are grouped by currency, no exchange rates);
- bank operations with `has_error = true` are always excluded;
- periods are local dates of UTC+5; Didox periods select by `DateBasis` (DocumentDate, SourceCreated, SourceUpdated);
- every result carries `DataFreshness` (last successful load, connection state, completeness per source);
- lists use keyset cursors with a stable order: 100 rows by default, at most 1000; each call times out after
  `Analytics:QueryTimeout` (5 s, also applied as `statement_timeout`).

Counterparty blocks B2, B5, B6 and B9 read `analytics_counterparty_monthly`, which each successful load refreshes for
exactly the (INN, month) pairs it touched (old and new values of every changed row). `dotnet run --project src/Synect.DevTools -- rebuild-totals --tenant <name>` rebuilds it fully.

**Assumption to confirm:** Didox statuses counted in sums (VAT, tops, turnover) are `Analytics:Edo:CountedStatuses`
= 1, 2, 3, 6, 8, 60 (draft 0, rejected 4 and invalid 40 excluded). Didox amounts are treated as UZS.

## Developer tools

```
dotnet run --project src/Synect.DevTools -- seed-demo --tenants 3
dotnet run --project src/Synect.DevTools -- seed-load --operations 200000 --documents 30000
dotnet run --project src/Synect.DevTools -- list-queries
dotnet run --project src/Synect.DevTools -- query b2-top-counterparties --tenant "Demo client 1" --top 5
dotnet run --project src/Synect.DevTools -- bench --tenant "Load test" --runs 20
dotnet run --project src/Synect.DevTools -- explain --tenant "Load test"
```

Data is loaded through the real sync services and the fake adapters. Timings are measured by `bench`, never asserted in tests.

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
| `Sync:Queue:HeartbeatInterval` | 1 min | How often a running job writes `heartbeat_at`. |
| `Sync:Queue:HeartbeatTimeout` | 10 min | Running jobs with an older heartbeat are marked Failed (with their runs). |
| `Sync:Queue:ManualRequestMinInterval` | 5 min | Between two "refresh now" requests per connection. |
| `Sync:Schedule:NightlyWindowStart` / `End` | 01:00 / 05:00 | Local time (UTC+5). |
| `Sync:Schedule:EdoDaytimeRefreshHours` | 4 | 0 disables daytime refreshes. |
| `Sync:Schedule:DaytimeStart` / `End` | 08:00 / 20:00 | Daytime refresh window. |
| `Sync:Retry:MaxAttempts` / `BaseDelay` / `MaxDelay` | 4 / 1 s / 30 s | Exponential backoff with jitter for network, 5xx, 429. |
| `Sync:CircuitBreaker:FailureThreshold` | 3 | Failed loads in a row before NeedsAttention. |
| `Analytics:QueryTimeout` | 5 s | Per analytics call. |
| `Analytics:DefaultPageSize` / `MaxPageSize` | 100 / 1000 | List paging. |
| `Analytics:MaxParallelWorkersPerGather` | 0 | Parallel workers cost about 150 ms per query on Windows; analytics queries are short. |
| `Analytics:UtcOffset` | `05:00:00` | Period dates are local dates. |
| `Analytics:Edo:CountedStatuses` | 1, 2, 3, 6, 8, 60 | **Assumption to confirm.** |
| `Secrets:KeysPath` | `%APPDATA%/Synect/DataProtection-Keys` | Data Protection key ring, outside the repository. |

All money is stored as `long` in minor units.

Bank operations with `err != 0` are stored with `has_error = true`, `error_code`, `error_message`; balances and
reconciliation must use `BankTransactionQueries.WithoutErrors()`.
