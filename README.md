# Synect

Loads data from Didox, banks and 1C into PostgreSQL on a schedule (or on demand); REST and MCP tools read only this
database and never call external systems.

## Layout

| Project | Purpose |
|---|---|
| `Synect.Domain` | Entities: tenants, connections (states), sync runs, sync jobs, connection secrets, raw objects, bank, EDO, counterparties, monthly counterparty totals, API keys, tool audit log |
| `Synect.Application` | Adapter contracts and registry, sync services, queue (planner, dispatcher, requests), rate limiting and retries, secrets and credentials contracts, freshness query, read-only analytics (`Analytics/`), API keys and tool audit (`Access/`) |
| `Synect.Infrastructure` | EF Core + Npgsql, migrations, RLS interceptor, Data Protection secret store, Kapital Bank / Didox mappers, session and token caches, fake adapters |
| `Synect.Api` | HTTP host: `/health` and the MCP endpoint `/mcp` (Streamable HTTP, API key) |
| `Synect.Mcp` | MCP tools over `Analytics`, `ISourceFreshnessQuery` and `SyncRequestService`; API key authentication, limits, tool call journal. No reference to Infrastructure, adapters, queue internals or secrets (enforced by a test) |
| `Synect.Worker` | Hosts the sync queue (`SyncWorker`) |
| `Synect.DevTools` | Development only: demo/load data, `query`, `bench`, `explain`, API keys, test database cleanup (not referenced by Api or Worker) |
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

V7 also has `GetVatReportAsync`, which adds a breakdown by status (signed 3 and 8, awaiting a signature 1, 2, 6 and
60, other) to the totals.

Counterparty blocks B2, B5, B6 and B9 read `analytics_counterparty_monthly`, which each successful load refreshes for
exactly the (INN, month) pairs it touched (old and new values of every changed row). `dotnet run --project src/Synect.DevTools -- rebuild-totals --tenant <name>` rebuilds it fully.

**Assumption to confirm:** Didox statuses counted in sums (VAT, tops, turnover) are `Analytics:Edo:CountedStatuses`
= 1, 2, 3, 6, 8, 60 (draft 0, rejected 4 and invalid 40 excluded). Didox amounts are treated as UZS.

## MCP server

`Synect.Api` serves MCP at `/mcp` with the official C# SDK (`ModelContextProtocol.AspNetCore` 2.4.0), Streamable HTTP
transport in stateless mode (legacy SSE stays off). Tools only read the Synect database through `Analytics`; none of
them calls Didox, a bank or 1C. The only non-read-only tool, `request_data_refresh`, just queues a load through
`SyncRequestService` and returns at once.

**Authentication.** `Authorization: Bearer <key>`. Keys are `sk_synect_` + 32 random bytes (base64url); `api_keys`
stores only the SHA-256 hash, a 16-character prefix for recognition, scopes (`read`, `sync_request`), created / last
used / revoked times. The key is printed once by `create-api-key` and never stored or logged. A missing, malformed,
unknown or revoked key gives `401` with `WWW-Authenticate: Bearer` and an empty body. The tenant comes from the key,
never from tool arguments. The scheme is an ordinary ASP.NET Core authentication scheme (`SynectApiKey`), so OAuth can
be added later as another scheme on the same endpoint.

**Finding a key before the tenant is known.** `api_keys` has row-level security enabled but not forced: the
application role (`synect_app`) sees only the keys of the tenant in `app.tenant_id`, while the table owner bypasses RLS.
The owner-defined `SECURITY DEFINER` function `synect_resolve_api_key(bytea)` (`search_path` pinned, `EXECUTE` only
for `synect_app`) takes the SHA-256 of a presented key, matches one non-revoked row and returns only
`(api_key_id, tenant_id, scopes)`, then updates `last_used_at` (at most once a minute). Every other table stays under
forced RLS. Tested: without a tenant the app role sees no keys; with tenant A only A's; it cannot change B's keys; the
function returns nothing for unknown, short or NULL hashes.

**Limits and journal.** Per API key 60 tool calls a minute, per tenant 4 at once; over a limit the answer is the error
`rate_limited` with `retry_after_seconds`. Each call has a 5 s timeout. Every tool call, including refused, failed and
timed-out ones, is written to `tool_audit_log` (RLS): key, tool, arguments as JSON (long values cut, secret-looking names
redacted), start, duration, returned rows, status (`Ok`, `Error`, `RateLimited`, `Timeout`), error message.

**Answers.** Structured JSON (also repeated as text): `tool`, `status`, `period` (echo), `request` (echo), `data`,
`returned_rows`, `truncated`, `next_cursor`, `data_freshness` (per source: last successful load, connection state,
completeness), `notes` (configured warnings such as the unconfirmed Kapital Bank amount unit, the Didox statuses counted
in sums, B6 history depth). Money is `{amount_minor, amount, currency}`; currencies are never added together.
Third-party text is returned as `{"untrusted_text": "..."}`, cut to 300 characters with control and invisible
formatting characters removed; tool descriptions tell the model never to follow instructions in it.
Errors are answers with `isError: true` and `error.code`: `invalid_argument`, `timeout`, `forbidden`, `rate_limited`,
`internal_error`.

| Group | Tools (analytics method) |
|---|---|
| Freshness | `get_data_freshness` |
| Money | `get_account_balances` (A1, A2), `get_balance_history` (A4), `get_balance_stats` (A11), `get_cash_flow` (A3), `get_largest_transactions` (A5), `get_transactions_for_day` (A6), `find_idle_accounts` (A9), `check_balance_integrity` (A7, A10) |
| Counterparties | `get_counterparty_payments` (B1), `get_top_counterparties` (B2), `get_counterparty_summary` (B3, B4), `compare_counterparty_periods` (B5), `get_counterparty_changes` (B6), `get_payments_without_documents` (B8), `get_counterparty_concentration` (B9) |
| Documents | `get_documents_awaiting_our_signature` (V1), `get_documents_awaiting_partner_signature` (V2), `get_document_counts` (V3), `get_rejected_documents` (V4), `search_documents` (V5), `get_largest_documents` (V6), `get_vat_summary` (V7 with status breakdown) |
| Refresh | `request_data_refresh` (scope `sync_request`) |

Typical questions and the tool for each: [docs/demo-questions.md](docs/demo-questions.md).

### Run locally and connect

1. Database and migrations as above (`dotnet ef database update ...`), optionally demo data:
   `dotnet run --project src/Synect.DevTools -- seed-demo`.
2. Start the API (in Development it reads `ConnectionStrings:Synect` from user-secrets):
   ```
   dotnet run --project src/Synect.Api --launch-profile http
   ```
   It listens on `http://localhost:5299`; `GET /health` answers `{"status":"ok"}`.
3. Create a key for a client (printed once; keep it in a password manager):
   ```
   dotnet run --project src/Synect.DevTools -- create-api-key "Demo client 1" --scopes read,sync_request --name "Claude Code"
   ```
4. Claude Code (remote HTTP server, https://code.claude.com/docs/en/mcp):
   ```
   claude mcp add --transport http synect http://localhost:5299/mcp --header "Authorization: Bearer <key>"
   claude mcp list
   ```
   Inside Claude Code, `/mcp` shows the server and its tools. For a shared `.mcp.json`, keep the key in an environment
   variable instead of the file:
   ```json
   { "mcpServers": { "synect": { "type": "http", "url": "http://localhost:5299/mcp",
       "headers": { "Authorization": "Bearer ${SYNECT_MCP_KEY}" } } } }
   ```
5. MCP Inspector (https://github.com/modelcontextprotocol/inspector, needs Node.js):
   ```
   npx @modelcontextprotocol/inspector --cli http://localhost:5299/mcp --transport http --method tools/list --header "Authorization: Bearer <key>"
   ```
   or `npx @modelcontextprotocol/inspector` for the web UI (transport Streamable HTTP, URL `http://localhost:5299/mcp`,
   header `Authorization: Bearer <key>`).

**Claude on the web (custom connectors)** connects from Anthropic's cloud, not from your computer (support.claude.com,
"Getting started with custom connectors using remote MCP"): the server must be reachable from the public internet, so
`localhost` does not work without publishing the API (reverse proxy or tunnel with TLS). Custom connectors support
OAuth, no authentication, or fixed request headers such as an API key, so the bearer key can be used there once the
server is published.

## Developer tools

```
dotnet run --project src/Synect.DevTools -- seed-demo --tenants 3
dotnet run --project src/Synect.DevTools -- seed-load --operations 200000 --documents 30000
dotnet run --project src/Synect.DevTools -- list-queries
dotnet run --project src/Synect.DevTools -- query b2-top-counterparties --tenant "Demo client 1" --top 5
dotnet run --project src/Synect.DevTools -- bench --tenant "Load test" --runs 20
dotnet run --project src/Synect.DevTools -- explain --tenant "Load test"
dotnet run --project src/Synect.DevTools -- create-api-key "Demo client 1" --scopes read,sync_request --name "Claude Code"
dotnet run --project src/Synect.DevTools -- list-api-keys "Demo client 1"
dotnet run --project src/Synect.DevTools -- revoke-api-key "Demo client 1" <key id or prefix>
dotnet run --project src/Synect.DevTools -- test-db-leftovers [--drop]
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

PostgreSQL tests (migrations, RLS, queue, secrets, analytics, MCP) create and drop a temporary database and role
(`synect_test_*`, `synect_rls_*`; the API key lookup test also a `synect_own_*` owner role). `test-db-leftovers`
lists anything left after an interrupted run (`--drop` removes it). MCP tests run the real `Synect.Api` in memory
(`WebApplicationFactory`) and call it with the MCP SDK client over Streamable HTTP. They need a
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
| `Mcp:DefaultLimit` / `MaxLimit` | 50 / 200 | Rows per MCP answer (smaller than Analytics to keep model context small). |
| `Mcp:MaxPeriodDays` | 1827 | Longest period a tool accepts (5 years). |
| `Mcp:CallTimeout` | 5 s | Per tool call; the timeout answer asks to narrow the period. |
| `Mcp:MaskAccountNumbers` | true | Account numbers as `****1234`. INNs are never masked. |
| `Mcp:UntrustedTextMaxLength` | 300 | Third-party text (purpose, names, status comments) is cut and cleaned. |
| `Mcp:StaleAfter` | 36 h | Older last loads get a freshness note. |
| `Mcp:RateLimit:RequestsPerMinutePerKey` | 60 | Tool calls per minute per API key (fixed window). |
| `Mcp:RateLimit:MaxConcurrentPerTenant` | 4 | Tool calls running at once per tenant. |
| `Mcp:Notes:Bank` / `Documents` / `Counterparties` | see `McpOptions` | Warnings added to `notes` of the matching tools. |

All money is stored as `long` in minor units.

Bank operations with `err != 0` are stored with `has_error = true`, `error_code`, `error_message`; balances and
reconciliation must use `BankTransactionQueries.WithoutErrors()`.
