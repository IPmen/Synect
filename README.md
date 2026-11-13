# Synect

Loads data from Didox, banks and 1C into PostgreSQL on a schedule (or on demand), and bank statement files (MT940, CSV)
on import; REST and MCP tools read only this database and never call external systems.

## Layout

| Project | Purpose |
|---|---|
| `Synect.Domain` | Entities: accounts and their organizations, tenants, connections (states; kinds Bank, Edo, File, Accounting1C), sync runs, sync jobs, connection secrets, raw objects, bank, EDO, counterparties, monthly counterparty totals, counterparty name spellings (aliases), API keys, tool audit log, reconciliation runs / matches / match items / findings, attention items and their events, statement imports, 1C data and the ingest journal (`Accounting/`); shared transliteration (`Text/`) and currency codes (`Common/IsoCurrency`) |
| `Synect.Application` | Adapter contracts and registry, sync services and the shared bank writer (`Sync/BankLoadWriter`), queue (planner, dispatcher, requests), rate limiting and retries, secrets and credentials contracts, freshness query, read-only analytics (`Analytics/`), reconciliation of payments with documents (`Reconciliation/`), attention rules and inbox (`Attention/`), API keys and tool audit (`Access/`), accounts: organization resolution, multi-organization fan-out, portfolio figures (`Accounts/`), bank statement files: parsers, profiles, import, anonymizer (`Statements/`), receiver of 1C packages: contract schema, validation, set-based writer, ingest keys (`Ingest/`) |
| `Synect.Infrastructure` | EF Core + Npgsql, migrations, RLS interceptor, Data Protection secret store, Kapital Bank / Didox mappers, session and token caches, fake adapters, synthetic statement generator |
| `Synect.Api` | HTTP host: `/health`, the MCP endpoint `/mcp` (Streamable HTTP, API key) and the 1C receiver `POST /ingest/1c/v1/batches` (ingest key; `Ingest/`) |
| `Synect.Mcp` | MCP tools (including the portfolio tools over all organizations of an account key) over `Analytics`, `Reconciliation`, `Attention`, `ISourceFreshnessQuery` and `SyncRequestService`; API key authentication, HTTP and tool limits, tool call journal. No reference to Infrastructure, adapters, queue internals or secrets (enforced by a test) |
| `Synect.Worker` | Hosts the sync queue (`SyncWorker`) |
| `Synect.DevTools` | Development only: demo/load data, `query`, `bench`, `explain`, API keys, reconciliation (`reconcile`, `rebuild-reconciliation`, `reconcile-eval`, `reconcile-bench`), attention (`attention`, `attention-eval`, `attention-bench`), statement files (`create-file-connection`, `import-statement`, `list-imports`, `anonymize-statement`, `statement-bench`), `normalize-currency`, test database cleanup (not referenced by Api or Worker) |
| `Synect.Tests` | xUnit v3 tests (SQLite temporary file + local PostgreSQL); synthetic statement files in `Fixtures/Statements` |

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
  `Queued`, `AlreadyQueued`, `AlreadyRunning`, `TooSoon` (`ManualRequestMinInterval`), `ConnectionNotActive` or
  `NotLoadable` (a file import connection: its data comes only from imported statements).
- File import connections (`adapter_kind = 'File'`) are never planned, queued or refreshed by `request_data_refresh`.
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

**Counterparty search.** `CounterpartySearch.FindAsync(query, page, period)` finds counterparties by a name fragment in
any spelling or by INN (9 or 14 digits). Every spelling seen per INN is kept in `counterparty_aliases` (source bank or
edo, occurrences, first and last seen); loads add them where they update `counterparties`, and
`rebuild-counterparty-aliases` refills them from loaded data. Names are compared after `CounterpartyNameNormalizer`:
lower case, quotes and signs removed, legal forms removed (ООО, ИП, ЧП, АО, МЧЖ / MChJ, ЯТТ / YaTT, АЖ / AJ, ХК, LLC
and full spellings), Russian and Uzbek Cyrillic transliterated to one Latin form (ў→o, қ→k, ғ→g, ҳ/х→h, ш→sh, ч→ch,
ж→j, ё→yo; Latin q→k, x→h, kh→h, zh→j, ph→f; apostrophes of o' g' dropped). Matching uses `pg_trgm` (substring or
`similarity` / `word_similarity` ≥ `Search:MinSimilarity`); candidates are ordered by similarity, then activity (bank
operations plus counted Didox documents in the period), then INN. Each candidate has the main (most frequent) name, up to
3 other spellings, last payment and document dates, bank turnover per currency and Didox totals for the period (default
the last 12 months). Payments without an INN are never matched; their number in the period is returned separately.
`pg_trgm` and `btree_gin` are trusted extensions, created by the migration as the database owner. Note: under row-level
security PostgreSQL does not pass the non-leakproof trigram operators to the index, so the GIN index
`(tenant_id, normalized_name gin_trgm_ops)` narrows by tenant only and the names of one tenant are compared one by one
(about 20 ms for 4 000 spellings, 80 ms for 20 000, 250 ms for 50 000).

Counterparty blocks B2, B5, B6 and B9 read `analytics_counterparty_monthly`, which each successful load refreshes for
exactly the (INN, month) pairs it touched (old and new values of every changed row). `dotnet run --project src/Synect.DevTools -- rebuild-totals --tenant <name>` rebuilds it fully.

**Assumption to confirm:** Didox statuses counted in sums (VAT, tops, turnover) are `Analytics:Edo:CountedStatuses`
= 1, 2, 3, 6, 8, 60 (draft 0, rejected 4 and invalid 40 excluded). Didox amounts are treated as UZS by the analytics
(blocks A, B, V); `edo_documents.currency` (UZS unless the optional `currency` / `currency_code` field is present; that field is not in
the Didox document list description, so it is not confirmed and must be verified on a real response)
is used by the reconciliation only.

A warning is logged (level Warning, at most once a day per tenant and process) after a load when a tenant has more than
`Search:AliasWarningThreshold` (30 000) name spellings in `counterparty_aliases`, because name search compares them one by
one (see above).

**Currency codes.** The Kapital Bank and Didox mappers normalize numeric ISO 4217 codes at load time: 860 and the
account-number code 000 become UZS, 840 USD, 978 EUR, 643 RUB; letter codes are upper-cased; an unknown numeric code is
stored as is. The source value always stays in `raw_objects`. Data loaded before stage 5 is fixed with
`dotnet run --project src/Synect.DevTools -- normalize-currency --all` (accounts, operations, documents; it also fills the
new `edo_documents.currency` and `seller_account` of documents loaded before stage 5 from their raw Didox JSON, because
unchanged documents are not rewritten by a load; monthly totals and the reconciliation are refreshed). Run it once after
applying the stage 5 migration.

## MCP server

`Synect.Api` serves MCP at `/mcp` with the official C# SDK (`ModelContextProtocol.AspNetCore` 2.4.0), Streamable HTTP
transport in stateless mode (legacy SSE stays off). Tools only read the Synect database through `Analytics`; none of
them calls Didox, a bank or 1C. The only non-read-only tool, `request_data_refresh`, just queues a load through
`SyncRequestService` and returns at once.

**Authentication.** `Authorization: Bearer <key>`. Keys are `sk_synect_` + 32 random bytes (base64url); `api_keys`
stores only the SHA-256 hash, a 16-character prefix for recognition, scopes (`read`, `sync_request`, `review`), created / last
used / revoked times. The key is printed once by `create-api-key` and never stored or logged. A missing, malformed,
unknown or revoked key gives `401` with `WWW-Authenticate: Bearer` and an empty body. The organizations come from the key (see Accounts and organizations),
(the `organization` argument only chooses among them). The scheme is an ordinary ASP.NET Core authentication scheme (`SynectApiKey`), so OAuth can
be added later as another scheme on the same endpoint.

**Finding a key before the tenant is known.** `api_keys` has row-level security enabled but not forced: the
application role (`synect_app`) sees only the keys of the tenant in `app.tenant_id`, while the table owner bypasses RLS.
The owner-defined `SECURITY DEFINER` function `synect_resolve_api_key(bytea)` (`search_path` pinned, `EXECUTE` only
for `synect_app`) takes the SHA-256 of a presented key, matches one non-revoked row and returns only
`(api_key_id, tenant_id, scopes)`, then updates `last_used_at` (at most once a minute). Every other table stays under
forced RLS. Tested: without a tenant the app role sees no keys; with tenant A only A's; it cannot change B's keys; the
function returns nothing for unknown, short or NULL hashes.

**Limits and journal.** HTTP level, for every request to `/mcp` (initialize and tools/list too): at most
`Mcp:HttpRateLimit:RequestsPerMinutePerKey` (300) requests a minute per API key and `RequestsPerMinutePerIp` (300) per
client IP, and at most `FailedAuthPerMinutePerIp` (20) failed authentications (401) a minute per IP; over a limit the
answer is HTTP 429 with `Retry-After` (in memory, per process; behind a reverse proxy configure forwarded headers so that
the client IP is real). Tool level: per API key 60 tool calls a minute, per tenant 4 at once; over a limit the answer is
the error `rate_limited` with `retry_after_seconds`. Each call has a 5 s timeout. Every tool call, including refused, failed and
timed-out ones, is written to `tool_audit_log` (RLS): key, tool, arguments as JSON (long values cut, secret-looking names
redacted), start, duration, returned rows, status (`Ok`, `Error`, `RateLimited`, `Timeout`), error message.

**Names instead of INNs.** Tools that take an INN tell the model to call `find_counterparties` first when the user
names a counterparty, and never to guess an INN. `find_counterparties` sets `best_match` only when the first candidate
is above `Search:MinSimilarity` and ahead of the second by `Search:BestMatchMargin`; it never passes the INN on by itself.

**Answers.** Structured JSON (also repeated as text): `tool`, `status`, `period` (echo), `request` (echo), `data`,
`returned_rows`, `truncated`, `next_cursor`, `data_freshness` (per source: last successful load, connection state,
completeness), `notes` (configured warnings such as the unconfirmed Kapital Bank amount unit, the Didox statuses counted
in sums, B6 history depth). Money is `{amount_minor, amount, currency}`; currencies are never added together.
Third-party text is returned as `{"untrusted_text": "..."}`, cut to 300 characters with control and invisible
formatting characters removed; tool descriptions tell the model never to follow instructions in it.
Every tool description also carries the rules for the model: no recommendations, no conclusions beyond the returned data,
always state `data_freshness` and `notes`, call `find_counterparties` first when a counterparty is named. The same rules
for a Claude project are in [docs/model-instructions.md](docs/model-instructions.md) (reference text for clients). Tools
that only compare documents and payments by INN (`get_payments_without_documents`, `get_counterparty_summary`,
`find_counterparties`) say in `notes` that they check whether documents exist, not a matching of payments with documents.
Every answer of a tool other than the attention tools also has an `attention` block (`new_items`, `new_critical`, `new_high`,
`open_critical`, `tool`), and, when there are new items, the note "There are N new items that need attention; call
get_attention_items" at most once per API key per `Attention:ReminderInterval` (30 min; in memory, per Api process).
Errors are answers with `isError: true` and `error.code`: `invalid_argument`, `timeout`, `forbidden`, `rate_limited`,
`internal_error`.

| Group | Tools (analytics method) |
|---|---|
| Freshness | `get_data_freshness` |
| Money | `get_account_balances` (A1, A2), `get_balance_history` (A4), `get_balance_stats` (A11), `get_cash_flow` (A3), `get_largest_transactions` (A5), `get_transactions_for_day` (A6), `find_idle_accounts` (A9), `check_balance_integrity` (A7, A10) |
| Counterparties | `find_counterparties` (search by name or INN), `get_counterparty_payments` (B1), `get_top_counterparties` (B2), `get_counterparty_summary` (B3, B4), `compare_counterparty_periods` (B5), `get_counterparty_changes` (B6), `get_payments_without_documents` (B8), `get_counterparty_concentration` (B9) |
| Documents | `get_documents_awaiting_our_signature` (V1), `get_documents_awaiting_partner_signature` (V2), `get_document_counts` (V3), `get_rejected_documents` (V4), `search_documents` (V5), `get_largest_documents` (V6), `get_vat_summary` (V7 with status breakdown) |
| Reconciliation | `get_paid_documents` (G1), `get_unpaid_documents` (G2), `get_partially_paid_documents` (G3), `get_unmatched_payments` (G4, G5 with `direction = outgoing`), `get_signed_unpaid_acts` (G6), `get_multi_document_payments` (G7), `get_amount_differences` (G8), `get_payments_without_reference` (G9), `get_payment_delay_stats` (G10, E11), `get_account_mismatches` (G11), `get_possible_duplicate_payments` (G12), `get_counterparty_balance_estimate` (E3 with `inn`; without `inn` an extension of E3 to all counterparties), `get_match_details` |
| Review | `confirm_match`, `reject_match` (scope `review`; change only Synect's reconciliation data) |
| Attention | `get_attention_items`, `get_attention_summary`, `update_attention_item` (scope `review`; changes only Synect's attention list) |
| Refresh | `request_data_refresh` (scope `sync_request`) |
| 1C | `get_1c_documents`, `get_1c_payment_orders`, `get_1c_settlement_balances`, `get_1c_bank_account_balances`, `compare_bank_with_1c`, `compare_didox_with_1c`, `compare_bank_balances_with_1c`, `compare_counterparty_with_1c` (see "1C questions and comparison") |

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
   Add `review` to the scopes (`--scopes read,review`) for a key that may confirm or reject reconciliation matches.
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

## Reconciliation (block G)

`Synect.Application/Reconciliation` matches bank payments with Didox documents deterministically (no machine learning);
every match is explainable by its reasons. It reads and writes only Synect's own tables and has no dependency on
adapters, sync, queue, secrets or Infrastructure (enforced by a test). Analytics and MCP read its results.

**Tables** (row-level security like every tenant table, `tenant_id` first in every index, migration `ReconciliationStage5`):

| Table | Content |
|---|---|
| `reconciliation_runs` | one recalculation: started / finished, scope (`Full` or `Inns` with the INN list), proposed matches, created / removed, rules version (`r1-<hash of Reconciliation:*>`) |
| `reconciliation_matches` | `match_type` (`Exact`, `OneToMany`, `ManyToOne`, `Partial`), `status` (`Proposed`, `Confirmed`, `Rejected`), `confidence` 0-100, `reasons` jsonb (`[{code, weight, params}]`), INN, currency, document direction, payment and document totals, signature, created / updated / decided at, `decided_by_key`, decision comment |
| `reconciliation_match_items` | `kind` (`BankTransaction` or `EdoDocument`), `ref_id`, `allocated_amount_minor`; a copy of the match status lets a partial unique index keep every payment and document in at most one Proposed match |
| `reconciliation_findings` | derived facts of the last recalculation per INN: `NearestCandidate` (closest candidate of an unmatched payment or document, below the threshold), `PossibleDuplicate`, `NoReference` (purpose without a document or contract number) |

The migration also adds `edo_documents.currency` (default UZS) and `edo_documents.seller_account`, and allows the API key
scope `review`.

**Decisions are kept.** Confirmed and Rejected matches are never changed or removed by a recalculation; amounts of
Confirmed matches are subtracted before matching; payment-document pairs of a Rejected match are never proposed again.
Proposed matches are recalculated: a match with the same signature (type, items, allocations) keeps its row and id, so
`ReconcileAsync` is idempotent. Recalculations and decisions of one tenant are serialized with a PostgreSQL advisory lock.
`confirm_match` / `reject_match` (scope `review`) store the latest decision, time, API key and optional comment on the
match. Decisions are not irreversible: a Confirmed match can be rejected and a Rejected match confirmed. Every change
(including the first decision) is appended to `reconciliation_match_decisions` (who = API key, when, previous and new
status, comment), and the tool call is in `tool_audit_log` with `details` = match, previous and new status. After a change
of an already decided match the counterparty's INN is recalculated in the same transaction. Confirming a Rejected match
whose payments or documents are already fully used by other Confirmed matches is refused (`Conflict`, with those match
ids); one of them has to be rejected first.

**Inputs.**
- Payments: `bank_transactions` without errors (`WithoutErrors()`), only `Reconciliation:Currency` (UZS); other currencies
  are listed as `currency_not_reconciled`. Payments without an INN are never matched automatically (category "without INN").
- Documents: `edo_documents` in `Analytics:Edo:CountedStatuses`, of the paid types `Reconciliation:DocumentTypes`
  (002, 008, 023 invoices, 005 acts), in UZS; amount = `total_delivery_sum_with_vat` (`total_sum` when it is 0); date =
  document date (creation date when missing). Contracts, powers of attorney, reconciliation acts etc. are never matched.
- Direction: an outgoing document (owner 1, we sell) pairs with incoming payments, an incoming document (owner 0, we buy)
  with outgoing payments. The INN of the payment must equal the INN of the document.

**Purpose parser** (`PurposeParser`): document and contract numbers and dates from Russian and Uzbek (Latin and
Cyrillic) purposes: "счёт-фактура / с/ф / СФ / ЭСФ / акт / накладная № 15 от 12.10.2026", "договор № 7/2026",
"hisob-faktura №45 bo'yicha", "15-sonli shartnoma", "45-сонли ҳисоб-фактура", lists ("№ 10, 11 и 12"), month names. It
reuses `LatinTransliterator` (shared with `CounterpartyNameNormalizer`, including o' and g'); the words live in the
embedded `Reconciliation/PurposeDictionary.json` and can be extended without code changes. Numbers are compared by keys
(letters and digits, upper case, leading zeros dropped: "INV-0012" = "inv 12"; a digits-only reference also matches the
digits of the number: "№ 101" = "СФ-101"). Numbers of 16 and more digits (accounts) are never references.

**Rules and weights** (`Reconciliation:Weights`; confidence = sum of the signals present, clamped to 0..100):

| Signal | Weight |
|---|---|
| Document number in the purpose (`DocumentNumberInPurpose`; share of the documents named for combinations) | 50 |
| Equal amount 1:1 (`AmountEqual`, within `AmountTolerance`) / exact sum of a combination (`AmountSumEqual`) | 35 / 35 |
| Contract number in the purpose (`ContractNumberInPurpose`) | 15 |
| Payment date minus document date within `DateWindowFromDays..DateWindowToDays` = -30..180 (`DateInWindow`) | 10 |
| Document date written in the purpose (`DocumentDateInPurpose`) | 5 |
| The only candidate on both sides (`UniqueCandidate`; amount and combination matches only) | 25 |
| Less paid than documented (`PartialPayment`) / more (`Overpaid`), counted only with a number reference | 15 / 10 |
| Incoming document: payee account (`acc_ct`) equals `sellerAccount` (`AccountMatch`) / differs (`AccountMismatch`, a warning, not a veto) | 5 / -15 |

Order: (1) document numbers in the purpose (several payments naming one document: one payment of exactly the document
amount wins, else an exact subset, else all of them in date order); (2) equal amounts inside the date window, narrowed by
a contract number in the purpose; (3) exact combinations: one payment for several documents (`OneToMany`) and several
payments for one document (`ManyToOne`), from at most `MaxCombinationCandidates` (12) nearest candidates and subsets of at
most `MaxCombinationSize` (6). Matches below `AutoMatchThreshold` (70) are not created; the items stay unmatched and the
closest candidate (from `HintMinConfidence` = 30) is stored as a hint. Resulting confidences: number + amount 95 (100 with
the document date), amount + window + unique 70, contract + amount + window + unique 85, number + partial payment 75,
number + overpayment 70, unique exact combination 70; amount + window alone (45) and ambiguous candidates never pass.
Types: equal totals 1:1 `Exact`, 1:N `OneToMany`, N:1 `ManyToOne`; different totals `Partial` (reason `PartialPayment`
or `Overpaid`). The smaller side is allocated in full, the larger one oldest first. Ties are broken by date and id;
inputs are sorted, so the result does not depend on row order. Possible double payments (G12): same direction, currency
and amount and the same document named in both purposes (90; never when both together fit into that document, which is
an instalment), the same purpose within `DuplicateSamePurposeDays` (30 days, 70), dates within
`DuplicateSameAmountDays` (3 days, 50).

**Assumptions to check on real data:** every `Reconciliation:*` value above (weights, threshold, window, tolerance,
document types, combination limits, duplicate windows), the Didox currency field (`currency` / `currency_code`,
read as optional, not confirmed by documentation; `sellerAccount` is confirmed), that Didox documents are in UZS, and that the document date (not a signing date, which the Didox list
does not confirm) is the right base for G10.

**Recalculation.** `ReconciliationService.ReconcileAsync(scope)` (tenant from the context). After every successful bank or
Didox load, in the same transaction as the monthly totals, it recalculates exactly the INNs whose operations or documents
changed (old and new INN of every changed row), each with its whole history; a load that changed nothing records no run
(`Reconciliation:RunAfterLoad`). `reconcile --tenant X [--inn ...]` recalculates (idempotent), `rebuild-reconciliation`
drops Proposed matches and findings first.

Timings on the load-test data ("Load test": 203 865 operations, 30 000 documents of which 9 427 are of paid types in
counted statuses, 2 001 INNs; `reconcile-bench`): full recalculation 5.1 s (6.0 s with the rebuild), incremental
recalculation of one INN 0.2-0.4 s for the busiest INNs (130-140 payments). The fake load data produces almost no matches
(random amounts and numbers), so these times measure reading, parsing and writing, not a high match count.

**Golden set.** The fake adapters carry a deterministic data set with known answers (`ReconciliationGoldenSet`: numbers
in Russian, Uzbek Latin and Uzbek Cyrillic purposes, no number, contract number, partial payments, one payment for several
documents, several payments for one document, overpayment, double payment, payment to another account, without INN, in
USD, uncounted status, the same number at two counterparties, equal amounts at two counterparties, ambiguous candidates,
opposite directions, a signed act without payment). `dotnet run --project src/Synect.DevTools -- reconcile-eval` loads it
through the sync services and prints precision, recall and every error; tests pin the result (14 expected matches,
precision 1.0, recall 1.0; ambiguous and excluded cases are expected "not found").

**Queries** (`ReconciliationQueries`, read only, `DataFreshness`, keyset paging, local dates, currencies never mixed;
every row carries the reasons, confidence, decision status and the document and payment directions as explicit fields):
G1 paid documents (paid amount, last payment date), G2 unpaid (nearest candidate), G3 partially paid (remaining amount),
G4 unmatched payments (without-INN flag, exclusion reason, nearest candidate), G5 = G4 for outgoing payments (whether the
counterparty has documents at all), G6 signed (status 3) acts not fully paid, G7 one-to-many and many-to-one matches,
G8 amount differences, G9 payments without a document or contract number, G10 / E11 days from the document date to payment per
counterparty and overall (from the document date: the Didox list gives no confirmed signing date), G11 payments to an account other than `sellerAccount`, G12 possible double payments, E3 balance
estimate (documents minus matched payments by INN; an estimate from bank and Didox data, not the accounting debt; without an
INN the same estimate for every counterparty, an extension of E3), and the details of one match.

## Attention inbox (stage 5.5)

After every load and reconciliation decision the platform itself lists what needs the accountant's attention; MCP shows
the list and the accountant marks items. There are no external notifications (Telegram, email) at this stage.
`Synect.Application/Attention` reads only the Synect database (never a source).

**Tables** (row-level security forced like every tenant table, `tenant_id` first in every index, migration `AttentionInbox`):

| Table | Content |
|---|---|
| `attention_items` | `rule_code`, `category` (`Connection`, `Bank`, `Reconciliation`, `Documents`), `severity` (`Critical`, `High`, `Medium`, `Low`), `title` and `body` (short English text; the model translates it), `details` jsonb (identifiers and amounts only: masked accounts, cleaned document numbers, no names or purposes), `entity_kind` / `entity_ref` (`connection`, `bank_account`, `transaction`, `document`, `tenant` for grouped items), `dedup_key` (unique per client among non-resolved, non-archived items, partial unique index), `member_keys` (cases of a grouped item), `status` (`New`, `Seen`, `Snoozed`, `Dismissed`, `Resolved`, `Archived`), `snoozed_until`, `first_seen_at`, `last_seen_at`, `resolved_at`, `decided_by_key`, `comment`, `updated_at` |
| `attention_item_events` | append-only history: `item_id`, `kind` (`Created`, `Reopened`, `Seen`, `Snoozed`, `Dismissed`, `AutoResolved`, `Archived`), `at`, `api_key_id` (null for the platform), `comment` |

**Life cycle.** A case is identified by its `dedup_key` = rule code + significant parameters (for single cases the ids and
the amount, e.g. `reconciliation.possible_duplicate:<payment>:<earlier payment>:<amount>`; for grouped items
`<rule>:all`), so it is never created twice; repeated evaluations only refresh `last_seen_at` and the content. When the
condition is gone, a New, Seen or Snoozed item becomes `Resolved` (event `AutoResolved`); when it comes back, the same row
opens again as `New` (event `Reopened`). A `Snoozed` item counts as `New` in lists and counters once `snoozed_until` has
passed and is stored as `New` (event `Reopened`) by the next evaluation if the condition still holds. A `Dismissed`
item stays dismissed (it is neither resolved nor reopened by the platform) until its significant content changes: a
different amount gives a different key, hence a new item; a dismissed grouped item opens again only when a case appears
that was not in it when it was dismissed. A `Dismissed` item whose condition has not been found for
`Attention:DismissedRetentionDays` (90, counted from `last_seen_at`) becomes `Archived` (event `Archived`, stage 6): it
leaves the default lists and the counters, its events stay, a person cannot change it, and it opens again as `New` on the
same row (event `Reopened`) if the condition comes back. Evaluations and person decisions of a client are serialized with
a PostgreSQL advisory lock.

**When the rules run** (`AttentionTrigger`, its own DI scope and transaction; a failed evaluation is logged and never fails
the load): after a successful bank load (connection, bank and reconciliation rules), after a successful Didox load
(connection, documents, reconciliation), after a failed load (connection rules), after `confirm_match` / `reject_match`
(reconciliation rules), and from the worker's queue tick every `Attention:PeriodicInterval` (1 h) per client (all rules:
stale data, waiting documents, end of a snooze). Only on PostgreSQL (the rules use its SQL; the SQLite logic tests skip it).
Each run evaluates the whole category it covers (not only the changed rows): this is what lets it resolve items whose
condition is gone, and it stays far below the 2 s target (see Timing).

**Rules and thresholds.** All in `Attention:*`. **These defaults are assumptions to check on real data.** Rules that
report single cases make one grouped item instead when they find more than `GroupAbove` (5) cases; list rules always make
one grouped item with `count`, `totals` per currency and at most `MaxListedEntries` (20) entries in `details`.

| Rule (`rule_code`) | Severity | Condition |
|---|---|---|
| `connection.needs_attention` | Critical | the connection is in `NeedsAttention` (authentication failure or `Sync:CircuitBreaker:FailureThreshold` failed loads) |
| `connection.stale_data` | High | an Active connection's last successful load (or creation, if never loaded) is older than `StaleAfter` (36 h); for file import connections the last import is older than `FileStaleAfter` (7 days) |
| `connection.incomplete_load` | High | bank and file import: a stored day older than yesterday is still not closed (today and yesterday are open in normal operation; imported days are always closed); Didox: the last successful load is incomplete (page limit) |
| `bank.balance_mismatch` | High | per account, closed days of the last `BalanceCheckDays` (60): opening + credit - debit != closing, or the day totals differ from the stored error-free operations (the day part of `check_balance_integrity`; carry-over between days and gaps stay in that tool) |
| `bank.failed_operations` | Medium | operations with `has_error` posted in the last `FailedOperationsDays` (7) (grouped) |
| `bank.idle_accounts` | Low | no error-free movement for more than `IdleAccountDays` (90), counted from the account's first appearance if it never moved (grouped) |
| `reconciliation.possible_duplicate` | High | a `PossibleDuplicate` finding of the reconciliation (G12) in the last `LookbackDays` (180), unless the payment is in a Confirmed match |
| `reconciliation.account_mismatch` | High | G11 in the last `LookbackDays`: payee account differs from `sellerAccount` of the matched incoming document (match not rejected) |
| `reconciliation.large_unmatched_payments` | Medium | error-free `Reconciliation:Currency` payments of at least `LargePaymentMinor` (5 000 000 000 = 50 000 000.00 UZS) in no proposed or confirmed match more than `UnmatchedGraceDays` (7) after the payment, within `LookbackDays` (grouped). **The Kapital Bank amount unit is not confirmed** (`Adapters:KapitalBank:AmountUnit`): the comparison may be off by 100; `details.note` says so |
| `reconciliation.signed_unpaid_acts` | Low | acts in the signed status dated more than `UnpaidActDays` (30) ago (within `LookbackDays`) not fully covered by non-rejected matches (grouped) |
| `documents.awaiting_our_signature` | Medium | incoming documents in status 2 whose document date (creation date without it) is more than `AwaitingOurSignatureDays` (5) ago (grouped) |
| `documents.awaiting_partner_signature` | Low | outgoing documents in status 1 older than `AwaitingPartnerSignatureDays` (14) (grouped) |
| `documents.rejected_recently` | Medium | documents in status 4 whose Didox update time (or, without it, when Synect saw the rejection) is within `RejectedRecentDays` (7) (grouped) |

NeedsAttention and Disabled connections get no stale or incomplete item (the Critical item covers the first; the second is
off on purpose). Bank-amount rules carry `details.note` about the unconfirmed amount unit.

**MCP** (common answer rules: freshness, notes, masked accounts, untrusted text wrapped):
`get_attention_items` (filters `severity`, `category`, `status` as comma separated lists including `archived`, default `new,seen`; `limit`,
`cursor`; sorted by severity, then the newest case), `get_attention_summary` (open = new + seen by severity and category,
new by severity, snoozed, dismissed, last evaluation time; a note when there are new critical or high items) and
`update_attention_item` (scope `review`: `seen`, `snooze` with `until` = local date or `days` 1-90, `dismiss` with a
required comment, `reopen`; resolved items cannot be changed). Every change is in `attention_item_events` (API key) and
in `tool_audit_log` with `details` = item, action, previous and new status. Model instructions:
[docs/model-instructions.md](docs/model-instructions.md) (call `get_attention_summary` at the start of a conversation about
finances; findings are possible discrepancies with their basis, never conclusions about violations).

**Golden set.** `AttentionGoldenSet` (fake adapters, real sync services, fixed clock) extends the reconciliation golden set
with a broken connection, stale data, a day whose balance does not add up, an operation rejected by the bank, an idle
account, a large payment without a document, documents waiting for a signature and a rejected document.
`dotnet run --project src/Synect.DevTools -- attention-eval` loads it (removing earlier attention items of that client
first, so the output is the same on every run) and prints the created items with their severity and what is expected not
to be created; tests pin the result.

**Timing** (`attention-bench` on the "Load test" client: 203 865 operations, 30 000 documents): full evaluation of every
rule median 58 ms (first, cold run 674 ms); after a bank load 24 ms, after a Didox load 42 ms. With
`Attention__LargePaymentMinor=1000000000` (17 211 large unmatched payments in one grouped item) the full evaluation takes
about 200 ms (750 ms cold). Target: under 2 s.

## Bank statement files (stage 6)

Banks without an API, and reconciliation checks on real statements: statement files are imported into the same model and
through the same writer as the bank API load (`Sync/BankLoadWriter`: raw layer, operations by (account, operation id), day
balances, counterparties and name spellings, monthly totals and reconciliation of the touched INNs), then the attention
rules run. Analytics, reconciliation, attention and MCP see imported data like any other bank data. Formats, profiles,
duplicate handling, balance check and anonymization: [docs/import-profiles.md](docs/import-profiles.md).

**Model.** A file import connection has `adapter_kind = 'File'`, `source_code = 'file_import'`, settings `bank_name` and
`default_profile`; it has the usual state, its freshness is the time of the last import (`last_successful_sync_at`), and
every import writes a `sync_runs` row (result, record count, date range, error; complete). It is never queued. Migration
`StatementImports`:

| Table / column | Content |
|---|---|
| `statement_imports` | `connection_id`, `sync_run_id`, `file_name`, `file_sha256` (unique per client: a file is imported once), `format` (`Mt940`, `Csv`), `profile`, `imported_at`, `imported_by_key` (API key, null for DevTools), `account` (comma separated when several), `period_from`, `period_to`, `transactions_total`, `transactions_new`, `transactions_duplicate`, `rows_rejected`, `warning_count`, `balance_check` (`Passed`, `Failed`, `Skipped`). Forced RLS, `tenant_id` first in every index |
| `bank_transactions.id_source` | `Bank` (bank id), `Synthetic` (API without stable ids, `syn:`), `File` (id given by the file), `FileHash` (computed, less reliable) |
| `bank_transactions.details` | jsonb: `weak_operation_id`, `reversal`, `transaction_type`, `format` for imported rows |

**Code.** `Synect.Application/Statements`: `IStatementParser` (`Mt940Parser`, `CsvStatementParser`; pure, no database,
enforced by a test), `StatementProfile` / `StatementProfiles` (built-in `generic_mt940`, `kapitalbank_mt940_assumed` -
**assumed, not verified** -, `generic_csv`; JSON profiles), encoding detection (UTF-8 with or without BOM, Windows-1251,
CP866), strict decimal-to-tiyin conversion, `BankStatementImportService` (stream, format, profile, `Force`,
`CreateAccounts`, `DryRun`, `ImportedByKey`; one transaction per file; ready for a future HTTP API, which is not built
yet: it needs its own authentication), `FileConnectionService`, `StatementAnonymizer`.

**Write path.** On PostgreSQL `BankLoadWriter` writes operations and raw records in bulk
(`INSERT ... SELECT FROM unnest(...) ON CONFLICT`, existing ids and raw hashes read once per batch), all statements of an
account at once, one `SaveChanges` per file, totals / reconciliation / attention once per import. Elsewhere (SQLite tests)
it uses EF entities; `BulkWrites = false` forces that path, and a test checks that both paths store identical data. The
bank API load uses the same writer. Statement imports leave unchanged raw records untouched (no `last_seen_at` refresh).

**Timing** (`statement-bench --sizes ... --timeout 120`, local PostgreSQL 18 with default settings, a database already
holding about 300 000 operations; synthetic MT940, one statement per day, 300 counterparties; new tenant per size).
Target: 20 000 operations under 10 s, a repeated import under 2 s.

| Operations | Before (EF, one SaveChanges per statement) | After: first import | After: same file again | After: same operations, other file |
|---|---|---|---|---|
| 500 | 1.67 s, 185 round trips | 1.05 s, 44 | 0.01 s (SHA-256) | 0.09 s |
| 2 000 | 5.13 s, 635 round trips | 0.77 s, 45 | 0.00 s | 0.19 s |
| 5 000 | not measured | 2.66 s, 53 | 0.00 s | 0.40 s |
| 10 000 | not measured | 4.46 s, 59 | 0.00 s | 0.52 s |
| 20 000 | stopped after 38 min | 7.97-8.14 s, 73 | 0.01 s | 1.03-1.12 s |

Before, time grew faster than the number of operations: `SaveChanges` after every statement of a file made EF scan every
entity tracked so far (quadratic). 20 000 operations after the fix, by phase: bulk insert of operations 2.8 s, reconciliation
1.7 s, raw layer 1.2 s, monthly totals 0.7 s, parse 0.6 s, plan 0.4 s, attention rules 0.3 s. The same bulk INSERT takes
about 1 s on the server; the rest of the variance (up to 2 s between equal runs) is I/O of a PostgreSQL with default
`shared_buffers` (128 MB) and about 200 MB of indexes on the two tables.

## Accounts and organizations (stage 6.5)

An accounting company (**account**) serves many client **organizations** (each a tenant with its own row-level security).
One API key and one MCP connector per account; the accountant names the organization in the question. Isolation of
organizations in the database is unchanged: every read and write still runs in one tenant's transaction.

**Tables** (migration `AccountsStage65`):

| Table | Content |
|---|---|
| `accounts` | `id`, `name` (unique), `status` (`Active`, `Suspended`), `created_at` |
| `account_tenants` | `account_id`, `tenant_id` (a tenant belongs to at most one account), optional `display_name`, `added_at`, `disabled_at` |
| `api_key_tenants` | `key_id`, `tenant_id`: an account key with rows here sees only those organizations, otherwise every enabled one of its account |
| `account_tool_audit_log` | calls of an account key that did not resolve to one organization (missing or ambiguous `organization`, several organizations): key, account, tool, arguments, status, the organizations read |
| `api_keys` | + `account_id`; `tenant_id` now nullable (exactly one of them is set, `ck_api_keys_owner`); primary key `id` |
| `tool_audit_log` | + `account_id`; `tenant_id` stays the organization whose data was read; the foreign key to the key is now `api_key_id` |
| `tenants` | `inn` renamed to `tin` (9 or 14 digits, optional), unique within an account (trigger `synect_check_account_tin` under a per-account advisory lock) |
| `connections` | + `onboarding_status` (`NotStarted`, `WaitingForAccess`, `Connected`, `NeedsAttention`) and `onboarding_note` (up to 500 characters): manual marks only, they never change loading |

**Why the application role cannot read other accounts.** `accounts`, `account_tenants`, `api_key_tenants` and
`account_tool_audit_log` have row-level security enabled **without any policy** (no row is visible or writable to any role
but the owner) and the grants of `synect_app` are revoked. Account keys have `tenant_id = NULL`, so the existing
`api_keys` policy (`tenant_id = app.tenant_id`) never shows them. The application reaches account data only through
owner-defined `SECURITY DEFINER` functions with `search_path` pinned and `EXECUTE` only for `synect_app`:

- `synect_resolve_api_key(hash)` → `(api_key_id, account_id | null, tenant_id | null, scopes)`. Nothing for a revoked key,
  a key of a suspended account, or a single-organization key of a disabled organization.
- `synect_api_key_organizations(hash)` → `(tenant_id, name, tin)` of the organizations the key may see (its own
  organization; or the enabled organizations of its active account, narrowed to `api_key_tenants`). It takes the SHA-256 of
  the **key itself**, not the key id, so only a caller that holds the key learns anything; the hash is kept in memory on
  the authenticated identity (`ApiKeyClaimsIdentity`, not a claim) and never logged.
- `synect_write_account_tool_audit(hash, ...)` writes `account_tool_audit_log`; the key and account come from the hash and
  every listed organization must belong to that account.

Tested: without a context and in an organization's context the application role sees 0 rows of the account tables and of
account keys, and every insert is refused (`42501`).

**Choosing the organization** (`OrganizationScopeResolver`, one mechanism in `ToolCallFilter` for every tool; tools/list
adds the optional `organization` parameter to every tool schema). The organizations come from the key only; the argument
chooses among them by TIN (9 or 14 digits), id or name (`CounterpartyNameNormalizer`: Russian, Uzbek Cyrillic and Latin
spellings, legal forms and apostrophes; one exact normalized match wins, otherwise containment or trigram similarity
≥ `Accounts:MinNameSimilarity` gives candidates):

- a key with one organization needs no argument;
- several organizations and no argument → `organization_required` with the list (name, TIN), never a guess;
- several similar names → `organization_ambiguous` with the candidates;
- anything else → `organization_not_found`, the same answer for an organization of another account and one that does
  not exist (organizations outside the key's list are never looked at);
- `organization = "all"` (also «все», «barchasi», «барчаси») is accepted by `get_attention_items` and
  `get_attention_summary`; the portfolio tools read every organization by default; `update_attention_item` without
  `organization` finds the organization that holds `item_id`.

Every answer has an `organization` block (`organization_id`, `name` as untrusted text, `tin`), or `{scope: "several",
count}` with an organization block per row.

**Several organizations never share a context.** `OrganizationFanOut` runs each organization in its own DI scope, hence its
own `DbContext`, transaction and `SET LOCAL app.tenant_id`, at most `Accounts:MaxParallelOrganizations` (4) at a time; an
error of one organization is reported for it alone (`read_error`, `unreadable_organizations`), the others still answer.
Tested with a failure in the middle of the run: each scope sees only its tenant, and pooled connections carry no tenant
afterwards.

**Tools** (common answer rules: freshness, notes, masked accounts, untrusted text, timeout, journal):

| Tool | What |
|---|---|
| `list_organizations` | name, TIN, connections and their states, new attention items by severity |
| `get_portfolio_overview` | per organization (all, or `organizations` = list of TINs / names): sources with freshness, new attention items by severity, unmatched payments of the last `Accounts:UnmatchedLookbackDays` (90) days, documents waiting for our signature; most important first (new critical items or a broken connection, then high ...); 25 organizations per page, at most 100 |
| `get_connections_overview` | every source of every organization: status, reason, last successful load, completeness, onboarding mark; `only_needing_attention` |
| `get_attention_items` / `get_attention_summary` | `organization = "all"`: merged list (same keyset order, ids unique) / counts per organization and totals |

They read only ready data (connection states, `attention_items`, reconciliation match items, document statuses), one
query set per organization.

**Limits and journal.** In addition to the per-key limits: `Mcp:RateLimit:MaxConcurrentPerAccount` (8) calls at the same
time per account (all its keys). Every call writes `tool_audit_log` per organization read (`tenant_id` = organization,
`account_id`), and an account key call that did not resolve to exactly one organization also writes one
`account_tool_audit_log` row.

**Administration** (DevTools, as the schema owner: `ConnectionStrings:SynectOwner`):

```
dotnet run --project src/Synect.DevTools -- create-account --name "Buxgalteriya A"
dotnet run --project src/Synect.DevTools -- add-organization --account "Buxgalteriya A" --name "Ромашка" --tin 301234567 --create-tenant
dotnet run --project src/Synect.DevTools -- import-organizations --account "Buxgalteriya A" --file organizations.csv
dotnet run --project src/Synect.DevTools -- list-accounts [--account "Buxgalteriya A"]
dotnet run --project src/Synect.DevTools -- create-account-key --account "Buxgalteriya A" [--organizations 301234567,Ромашка] --scopes read,review --name "Claude"
dotnet run --project src/Synect.DevTools -- revoke-api-key --account "Buxgalteriya A" --key <key id or prefix>
dotnet run --project src/Synect.DevTools -- disable-organization --account "Buxgalteriya A" --organization 301234567
dotnet run --project src/Synect.DevTools -- set-onboarding --tenant "Ромашка" --source "Kapital Bank" --status WaitingForAccess --note "заявление в банк подано"
dotnet run --project src/Synect.DevTools -- seed-account --name "Demo accounting" --organizations 50
dotnet run --project src/Synect.DevTools -- account-bench --account "Demo accounting" --runs 20
dotnet run --project src/Synect.DevTools -- list-tenants
dotnet run --project src/Synect.DevTools -- delete-tenant --name "<name>" --confirm "<name>" [--id <id>] [--force]
```

`import-organizations` reads a CSV with the columns `name` and `tin` (comma or semicolon), is idempotent and reports errors
per row. `create-account-key` prints the key once. `delete-tenant` deletes a tenant and every row of it (all tables with a
`tenant_id`, children first by the foreign keys, then `tenants`) in one transaction as the owner, logs what it deleted,
and refuses while the tenant has active keys unless `--force`. `account-bench` needs a running Api with tool limits raised
(`Mcp__RateLimit__RequestsPerMinutePerKey`, `Mcp__HttpRateLimit__RequestsPerMinutePerKey`); it creates two temporary keys
and revokes them at the end.

**Timings** (`account-bench`, 50 organizations of `seed-account`, real HTTP, 20 runs after 2 warm-up calls, local
PostgreSQL 18; second of two runs, the first was up to 1.5 times slower):

| Tool | p50 ms | p95 ms | Target p95 |
|---|---|---|---|
| `list_organizations` | 72 | 79 | 500 |
| `get_portfolio_overview`, 25 organizations | 57 | 61 | |
| `get_portfolio_overview`, 50 organizations | 82 | 93 | 1000 |
| `get_connections_overview` | 67 | 77 | |
| `get_attention_items`, `organization = "all"` | 63 | 66 | |
| `get_account_balances`, `organization` = TIN | 8 | 9 | without + 100 |
| `get_account_balances` without `organization` (one-organization key) | 8 | 9 | |

Language: [docs/glossary.md](docs/glossary.md) (about 40 accounting terms in Russian, Uzbek Latin and Cyrillic; a draft to
be checked by native speakers), [docs/tool-selection-eval.md](docs/tool-selection-eval.md) (40 hypothetical questions with
the expected tool; a test checks only that each tool exists and has Russian and Uzbek examples, the model's choice is
checked by hand at the demonstration). Uzbek example questions of every tool are in `Synect.Mcp/Tools/ToolExamples.cs`.

**Analytics connection budget.** Opening the database connection now counts against `Analytics:QueryTimeout`: Npgsql does
not abort the startup handshake on cancellation (it would wait for the connection `Timeout`, 15 s by default), so
`AnalyticsContext` opens the connection itself and bounds the wait; the connection strings and the timeouts of every other
part are unchanged.

## 1C data: storage and receiver (stage 8a)

Our side for 1C data: tables and a receiver of packages pushed by a 1C extension. The extension itself and attention rules
for 1C data are not part of this stage; the MCP tools and the comparison with the bank and Didox came with stage 8b (below). The real 1C object and field names are not
known yet: the receiver works with the logical fields of the export contract ("Контракт выгрузки данных 1С в Synect").

**Design rules.**
- Required contract fields (level M) are NOT NULL columns, optional known ones (R) nullable columns; everything else an
  item carries is kept as received in the jsonb column `extra` and the whole item in the raw layer (`raw_objects`, type
  `1c.<entity>`). Unknown fields are never an error. The contract lives in one table of field descriptions,
  `Synect.Application/Ingest/IngestSchema.cs` (name, column, type, level, aliases); names are matched ignoring case, `_`
  and `-`. The writer generates its SQL from that table.
- `schema_version` is stored in `ingest_batches` and in every record; accepted versions are `Ingest:SupportedSchemaVersions`
  (default 1), others get `400 unsupported_schema_version` with the supported list.
- A record is (organization, entity, ref). An update is applied only if its `modified_at` is not older than the stored one;
  equal content (SHA-256 of the item with sorted keys) is not rewritten. Without `modified_at` the package's `exported_at`
  is used.
- Deletions and unposting are marks (`is_deleted`, `posted`); rows are never removed.

**Tables** (migration `Accounting1CIngest`; row-level security forced, `tenant_id` first in every index; no foreign keys
between accounting records: a document may arrive before its counterparty and links are resolved by ref; composite
`(tenant_id, ...)` foreign keys from lines / settlements to their document, from records to the package that last changed
them, from packages and keys to the 1C connection). Amounts are `bigint` minor units; currency is a letter code; refs are
strings of at most 64 characters. Every record table also has `is_deleted`, `modified_at`, `content_hash`, `extra`,
`schema_version`, `last_batch_id`, `first_seen_at`, `last_seen_at`, `updated_at`.

| Table | Key | Fields (M = required) |
|---|---|---|
| `accounting_counterparties` | ref | name (M), tin, full_name, kind |
| `accounting_contracts` | ref | counterparty_ref (M), number, date, kind, currency, valid_until, payment_term_days |
| `accounting_bank_accounts` | ref | number (M), bank_code, bank_name, currency, gl_account_code, is_active |
| `accounting_documents` | ref | doc_kind Sales / Purchase (M), number (M), date (M), posted (M), amount_total (M), doc_type, counterparty_ref / tin, contract_ref / number / date, currency, vat_amount, esf_number / date / id / status, due_date, comment |
| `accounting_document_lines` | document_ref, line_no | amount (M), name, product_code, quantity, price, vat_rate; replaced as a whole with the document |
| `accounting_bank_documents` | ref | direction Receipt / Payment (M), number (M), date (M), posted (M), amount (M), bank_account_ref / number, counterparty_ref / tin, currency, purpose, contract_ref, bank_document_number / date, cash_flow_item |
| `accounting_bank_document_settlements` | document_ref, line_no | amount (M), settled_document_ref; replaced as a whole with the document |
| `accounting_payment_orders` | ref | number (M), date (M), amount (M), posted, bank_account_ref, counterparty_tin, currency, purpose, bank_status |
| `accounting_settlement_balances` | as_of_date, counterparty_ref, contract_ref, gl_account_code, currency (NULLs equal) | as_of_date (M), gl_account_code (M), counterparty_tin, balance and turnover debit / credit |
| `accounting_bank_account_balances` | as_of_date, bank_account_ref | balance (M), turnover debit / credit |
| `ingest_batches` | (organization, batch_id, sequence) | journal: connection, key, entity, mode, full_sync_id, is_last_batch, schema / extension version, exported / received / finished, items_total, accepted, unchanged, rejected, marked_deleted, status (`Processed`, `ProcessedWithErrors`, `Rejected`, `Failed`), error, payload SHA-256 and size, rejected items (index, key, code, message), unknown envelope fields, attempts |
| `ingest_keys` | id | connection, name, key_hash, key_prefix, created / last used / revoked |
| `ingest_full_sync_items` | (organization, full_sync_id, entity, item_key) | keys seen in a full export in progress |

**Connection.** `AdapterKind.Accounting1C` (source code `1c`), created with the first ingest key. It is never planned or
queued (`request_data_refresh` answers `NotLoadable`). `last_successful_sync_at` is the time of the last package with at
least one accepted or unchanged item; a `Disabled` connection refuses packages (`403 connection_disabled`).
`ISourceFreshnessQuery` returns it with `StaleAfter` = `Ingest:StaleAfter` (48 h) and `IsStale`; `get_data_freshness` and
`get_connections_overview` show it (`stale`, `stale_after_hours`). No attention rule covers 1C at this stage (the existing
`connection.stale_data` skips it).

**Ingest keys.** `ik_synect_` + 32 random bytes (base64url); only the SHA-256 is stored, with a 16-character prefix. The
key is printed once by `create-ingest-key`. `ingest_keys` has row-level security enabled but not forced (like `api_keys`):
the application role sees only the keys of the organization in `app.tenant_id`; before the organization is known a key is
found only through the owner's `SECURITY DEFINER` function `synect_resolve_ingest_key(bytea)` (`search_path` pinned,
`EXECUTE` only for `synect_app`), which takes the hash, matches one non-revoked key of an enabled organization and returns
only `(ingest_key_id, tenant_id, connection_id, tenant_tin)`. Ingest keys never work on `/mcp` and MCP keys never on the
ingest route: different prefixes, different tables, different lookup functions, and the MCP scheme authenticates only
requests to `/mcp`.

**Receiver.** `POST /ingest/1c/v1/batches`, `Authorization: Bearer <ingest key>`, `Content-Type: application/json`,
optional `Content-Encoding: gzip`.

```json
{ "schema_version": 1, "extension_version": "1.0.3", "organization_tin": "301234567", "batch_id": "b-2026-11-11-01",
  "sequence": 1, "exported_at": "2026-11-11T09:00:00+05:00", "mode": "delta", "entity": "counterparties",
  "items": [ { "ref": "...", "name": "...", "tin": "...", "modified_at": "2026-11-10T18:30:00" } ] }
```

Entities: `counterparties`, `contracts`, `bank_accounts`, `documents` (with `lines`), `bank_documents` (with
`settlements`), `payment_orders`, `settlement_balances`, `bank_account_balances`. `mode` is `full` or `delta`; a full
export also carries `full_sync_id` and, on its last package, `is_last_batch: true`.

| Case | Answer |
|---|---|
| Missing, malformed, unknown or revoked key; key of a disabled organization | `401`, `WWW-Authenticate: Bearer`, empty body |
| More than `Ingest:RateLimit:FailedAuthPerMinutePerIp` (20) failed authentications from an IP | `429` with `Retry-After` until the window ends |
| More than `Ingest:RateLimit:PackagesPerMinute` (60) packages of a key | `429` with `Retry-After` |
| `organization_tin` differs from the TIN of the key's organization (or it has none) | `403 organization_mismatch`, nothing about other organizations |
| Other Content-Type / Content-Encoding | `415` |
| More than `Ingest:MaxItems` (500) items or `Ingest:MaxBytes` (2 MB, counted after decompression) | `413 too_many_items` / `payload_too_large` |
| Invalid JSON, envelope or gzip; unknown entity; unsupported schema version | `400` with `error` and `message` |
| Same `batch_id` and `sequence` with other content after it was processed | `409 batch_conflict` |
| Processed | `200`: `status` (`accepted`, `partially_accepted`, `rejected`), counters, `full_sync`, and per item `index`, `ref`, `status` (`accepted`, `unchanged`, `rejected`), `code`, `message` |
| Same package again | `200`, `status: duplicate`, counters and rejected items of the first processing; nothing is changed |
| System error | `500 internal_error`; the package is rolled back, journaled as `Failed` and may be sent again |

One transaction per package, serialized per organization with an advisory lock; a refused item never cancels the others.
Item checks: TIN of 9 or 14 digits, ISO 8601 dates (a date-time is accepted for a date; the empty 1C date `0001-01-01` and
the empty reference `00000000-...` count as missing), amounts as decimal sum in a number or a string (`"1 234,50"` too),
stored x100 with fractions of a tiyin refused, currency as a letter code, ISO numeric code or a known name, non-empty ref of
at most 64 characters, required fields. Item codes: `empty_ref`, `ref_too_long`, `missing_field`, `invalid_tin`,
`invalid_date`, `invalid_timestamp`, `invalid_amount`, `fractional_minor_units`, `amount_out_of_range`,
`invalid_currency`, `invalid_number`, `invalid_boolean`, `invalid_value`, `text_too_long`, `invalid_text`,
`invalid_lines`, `duplicate_line_no`; unchanged items carry `same_content`, `older_than_stored` or `unknown_ref_deleted`.
An item with `is_deleted: true` and only its ref marks a known record deleted and keeps its other fields. Lines and
settlements are replaced when the item carries the field (an empty array removes them; no field keeps them). The last
refused version of an item with a readable key is kept in the raw layer as `1c.<entity>.rejected`, so that it can be
processed again after a contract fix. Third-party text (purpose, comment, names) is stored as received; future tools must
pass it through the usual cleaning (`TextSafety`). Logs carry counters and package ids only. A package refused after its
envelope could be read (schema version, organization, too many items) is journaled as `Rejected` and may be sent again
under the same `batch_id` and `sequence`.

**Full exports.** Every package of a full export records the keys of its items (refused ones too: they exist in 1C). On
the package with `is_last_batch`, if every sequence 1..N of that `full_sync_id` and entity was processed, records of the
entity that were not in the export and were not received since it started are marked `is_deleted` (balances: only on the
dates the export covered). Without `is_last_batch`, or with a missing sequence, nothing is marked and the answer says so
(`full_sync.reason`: `waiting_for_last_batch`, `missing_sequences`).

**Sample sender and timings.** `send-1c-sample --tenant X --scenario basic|messy|large [--url http://localhost:5299]`
is a fake 1C extension that sends synthetic packages over HTTP to a running Api (without `--key` it creates a temporary
ingest key and revokes it). `basic`: normal data of every entity. `messy`: unknown fields, items with errors, a repeated
package, a late package, a document before its counterparty, deletion and unposting, a truncated and a complete full
export, an unsupported schema version. `large`: 20 000 records in 40 packages of 500 (4 000 counterparties, 10 000
documents with 2 lines each, 6 000 bank documents with a settlement). Measured on the development machine (local
PostgreSQL 18, Debug build, three runs): first receipt 10.4 s (goal 10 s, about 260 ms a package; the time is spent in
PostgreSQL writes: 20 000 records, 26 000 child rows and 20 000 raw objects), the same packages again 0.15-0.19 s
(duplicates), the same records under new batch ids 1.4-1.6 s (all unchanged). `Ingest:RateLimit:PackagesPerMinute` must
be raised for this run (the sender waits for `Retry-After` otherwise).

## 1C questions and comparison with the bank and Didox (stage 8b)

Eight read-only MCP tools over the data received from 1C (`Synect.Application/Accounting1C`, `Synect.Mcp/Tools/AccountingTools.cs`).
Nothing is stored: every comparison is computed on request from `accounting_*`, `bank_transactions`, `edo_documents` and
`bank_daily_balances`. They read only the Synect database, never 1C.

| Tool | Question |
|---|---|
| `get_1c_documents` | Sales and purchase documents of 1C by period, kind, counterparty, state (`active`, `posted`, `unposted`, `deleted`, `all`), `without_esf`, number |
| `get_1c_payment_orders` | Payment orders created in 1C and their bank status |
| `get_1c_settlement_balances` | Receivables and payables by the accounting records (latest 1C export on or before a date), per counterparty and currency, with totals |
| `get_1c_bank_account_balances` | Bank account balances as 1C shows them |
| `compare_bank_with_1c` | Bank statement against 1C bank documents: `missing_in_1c`, `missing_in_bank`, `amount_differs`, `not_posted_in_1c` |
| `compare_didox_with_1c` | Didox invoices and acts (counted statuses) against 1C documents: `missing_in_1c`, `missing_in_didox`, `amount_differs`, `not_posted_in_1c` |
| `compare_bank_balances_with_1c` | Per account: balance by 1C against the bank's closing balance (`equal`, `differs`, `no_1c_balance`, `no_bank_data`) |
| `compare_counterparty_with_1c` | One counterparty and period: documents (Didox against 1C) and payments (bank against 1C) per currency, differences, 1C settlement balance |

**Pairing rules** (`AccountingMatcher`, deterministic, each record in at most one pair, ties by date and id; **assumptions
to check on real data**, `Accounting:*`):
- Payments: same direction, currency, amount and INN with dates at most `PaymentDateWindowDays` (3) apart; then the same
  when one side has no INN; then same direction, currency and INN with another amount (`amount_differs`).
- Documents: the Didox id stored in the 1C document (`esf_id`); then direction, INN and document number (number keys of
  the reconciliation: "СФ-101" = "сф 101"); then direction, INN and amount with dates at most `DocumentDateWindowDays` (5)
  apart. A pair with different amounts is `amount_differs`.
- A pair whose 1C document is not posted is `not_posted_in_1c`. Deleted 1C records and bank operations rejected by the
  bank are never compared. Matching runs over the period widened by the window; a row is reported by its own date.
- A comparison reads at most `MaxCompareRows` (50 000) records a side and asks to narrow the period otherwise.

Answers follow the common rules: `data_freshness` (with the 1C source), `notes` (1C data is what the extension sent; the
field mapping and the pairing rules are unconfirmed; rows are possible discrepancies), masked account numbers, 1C text
(names, purposes, comments, statuses) wrapped as `untrusted_text`. Fields named `accounting_*` are the 1C side.

**Data with known answers.** `OneCMirror` builds 1C data from what Synect already holds for an organization: every
error-free bank operation becomes a 1C bank document, every comparable Didox document a 1C document, plus counterparties,
contracts, bank accounts, payment orders and balances. An unchanged mirror compares as "everything agrees".
`Accounting1CGoldenSet` is the mirror of the reconciliation golden set with planted differences:

| Planted | Expected |
|---|---|
| Receipt of 333 333.33 (p4) left out of 1C | `compare_bank_with_1c`: `missing_in_1c` |
| Receipt of 500 000 (p2) entered as 505 000 | `amount_differs`, +5 000.00 |
| Receipt of 555 000 (p17) entered and not posted | `not_posted_in_1c` |
| Payment of 150 000 only in 1C | `missing_in_bank` |
| Payment entered a day later (p8b); cash receipt without an INN (p12) | found, not reported |
| Invoice F-3 (d7c) left out of 1C | `compare_didox_with_1c`: `missing_in_1c` |
| Invoice 47 (d9) entered as 110 000 instead of 100 000 | `amount_differs`, +10 000.00 |
| Invoice H-20 (d16h) not posted | `not_posted_in_1c` |
| Sales document H-99 only in 1C, without ESF | `missing_in_didox`; `get_1c_documents` with `without_esf` |
| Documents without the Didox id in 1C (d2, d3, d15e); act without ESF data (d20) | found by number and INN / by amount, INN and date |
| A document marked deleted in 1C | never compared; `get_1c_documents` with `state = deleted` |
| USD account: 1C balance 1 000.00 higher | `compare_bank_balances_with_1c`: `differs` |
| Payment order prepared and not sent | `get_1c_payment_orders` with `state = unposted` |

Pairs without a difference in October 2026: 20 of 23 payments, 21 of 24 documents. Tests ask the tools through the real
MCP transport and pin these answers (`AccountingToolsTests`), and check that an unchanged mirror has no differences.

`send-1c-sample --tenant X --scenario mirror [--plant] [--date yyyy-MM-dd]` sends a mirror of any organization
(`--plant` adds differences by fixed rules and prints how many); `--scenario golden` sends the golden data for a tenant
holding the reconciliation golden set (`reconcile-eval` creates "Reconciliation golden set"; give it a TIN with `add-organization`) and prints the expected answers.

## Developer tools

```
dotnet run --project src/Synect.DevTools -- seed-demo --tenants 3
dotnet run --project src/Synect.DevTools -- seed-load --operations 200000 --documents 30000
dotnet run --project src/Synect.DevTools -- list-queries
dotnet run --project src/Synect.DevTools -- rebuild-counterparty-aliases --all
dotnet run --project src/Synect.DevTools -- query s1-find-counterparties --tenant "Demo client 1" --q "Ромашка"
dotnet run --project src/Synect.DevTools -- query b2-top-counterparties --tenant "Demo client 1" --top 5
dotnet run --project src/Synect.DevTools -- bench --tenant "Load test" --runs 20
dotnet run --project src/Synect.DevTools -- explain --tenant "Load test"
dotnet run --project src/Synect.DevTools -- create-api-key "Demo client 1" --scopes read,sync_request --name "Claude Code"
dotnet run --project src/Synect.DevTools -- list-api-keys "Demo client 1"
dotnet run --project src/Synect.DevTools -- revoke-api-key "Demo client 1" <key id or prefix>
dotnet run --project src/Synect.DevTools -- test-db-leftovers [--drop]
dotnet run --project src/Synect.DevTools -- create-ingest-key --tenant "Demo client 1" [--name "1C extension"]
dotnet run --project src/Synect.DevTools -- list-ingest-keys --tenant "Demo client 1"
dotnet run --project src/Synect.DevTools -- revoke-ingest-key --tenant "Demo client 1" --key <key id or prefix>
dotnet run --project src/Synect.DevTools -- list-ingest-batches --tenant "Demo client 1" [--limit 50]
dotnet run --project src/Synect.DevTools -- send-1c-sample --tenant "Demo client 1" --scenario basic|messy|large
dotnet run --project src/Synect.DevTools -- send-1c-sample --tenant "Demo client 1" --scenario mirror [--plant]
dotnet run --project src/Synect.DevTools -- send-1c-sample --tenant "Reconciliation golden set" --scenario golden
dotnet run --project src/Synect.DevTools -- normalize-currency --all
dotnet run --project src/Synect.DevTools -- reconcile --tenant "Demo client 1" [--inn 300000005]
dotnet run --project src/Synect.DevTools -- rebuild-reconciliation --tenant "Demo client 1"
dotnet run --project src/Synect.DevTools -- reconcile-eval
dotnet run --project src/Synect.DevTools -- reconcile-bench --tenant "Load test" --runs 7
dotnet run --project src/Synect.DevTools -- attention --tenant "Demo client 1"
dotnet run --project src/Synect.DevTools -- attention-eval
dotnet run --project src/Synect.DevTools -- attention-bench --tenant "Load test" --runs 7
dotnet run --project src/Synect.DevTools -- create-file-connection --tenant "Demo client 1" --bank-name "Test bank" [--profile generic_mt940]
dotnet run --project src/Synect.DevTools -- import-statement --tenant "Demo client 1" --connection "Test bank (file import)" --file <path> --format mt940|csv [--profile <name|path.json>] [--force] [--create-accounts] [--dry-run]
dotnet run --project src/Synect.DevTools -- list-imports --tenant "Demo client 1"
dotnet run --project src/Synect.DevTools -- anonymize-statement --file <path> --format mt940|csv [--profile ...] --out <path>
dotnet run --project src/Synect.DevTools -- statement-bench [--operations 20000] [--days 250]
```

Data is loaded through the real sync services and the fake adapters. Timings are measured by `bench`, never asserted in tests.

## Secrets

`connection_secrets` holds Data Protection ciphertext only (bound to tenant, connection and kind); `connections.credentials_ref`
points to the row. Keys live in `Secrets:KeysPath` (default `%APPDATA%/Synect/DataProtection-Keys`, DPAPI-protected on
Windows), never in the repository. `ISecretStore` (Get/Set/Rotate) writes secrets; adapters read them through
`ICredentialProvider`. `SecretValue.ToString()` is redacted. Didox tokens (360 min) and Kapital Bank sessions (`sid`) are
cached in memory per connection.

**Entering a client's access** (`ConnectionCredentialsService`, DevTools; tenant context, one organization at a time):

```
dotnet run --project src/Synect.DevTools -- list-sources
dotnet run --project src/Synect.DevTools -- create-connection --tenant "ООО Клиент" --source <code> --login <login> [--name "Kapital Bank"] [--test]
dotnet run --project src/Synect.DevTools -- set-credentials --tenant "ООО Клиент" --connection <id|name> --login <login> [--test]
dotnet run --project src/Synect.DevTools -- test-connection --tenant "ООО Клиент" --connection <id|name>
dotnet run --project src/Synect.DevTools -- list-connections --tenant "ООО Клиент"
```

- The password is never a command-line argument (`--password` is refused: it would stay in the shell history and the
  process list). It is typed twice without echo, or read as one line of standard input with `--password-stdin` (for a
  password manager or a script). Login and password are stored together, encrypted, as the `credentials` secret; they are
  never logged or printed. `list-connections` shows the login masked (`ac********`), the secret version and dates.
- `--source` must be the source code of an adapter registered in this build (`list-sources`); today only the fake
  adapters `fake_kapitalbank` and `fake_didox` exist, so real client access cannot be used until the real adapters are written.
- Without `--test` a new connection is Active and loaded on the schedule. With `--test` (and `test-connection`) the
  connection waits in NeedsAttention, the adapter's connection test runs with the stored access, and on success the
  connection becomes Active and its first load is queued; on failure it stays in NeedsAttention with the reason and is
  not loaded. `set-credentials` replaces the access as a rotation (version + 1, `rotated_at`).
- Only bank and Didox connections take credentials (not file imports, not 1C). The connection name is unique within the
  organization.

## Tests

```
dotnet test --solution Synect.slnx
```

PostgreSQL tests (migrations, RLS, queue, secrets, analytics, reconciliation, attention, statement imports, MCP, 1C ingest) create and drop a temporary database and role
(`synect_test_*`, `synect_rls_*`; the API key lookup test also a `synect_own_*` owner role). `test-db-leftovers`
lists anything left after an interrupted run (`--drop` removes it). MCP tests run the real `Synect.Api` in memory
(`WebApplicationFactory`) and call it with the MCP SDK client over Streamable HTTP. They need a
superuser connection string and are skipped otherwise:

```
dotnet user-secrets set "ConnectionStrings:SynectTestAdmin" "Host=localhost;Username=postgres;Password=..." --project tests/Synect.Tests
```

or the `SYNECT_TEST_PG` environment variable.

Each temporary database is warmed up right after the migrations (every table touched once as the application role), and
test hosts use `Analytics:QueryTimeout` = 30 s: the timeout covers the whole call including opening a connection, and the
whole suite runs in parallel on one local server, which on a cold server (after a reboot) pushed analytics calls past
5 s. Production keeps 5 s; `BalanceStatsVolumeTests` runs a long A11 query on 30 accounts x 3 years with the production
timeout, so a genuinely slow query still fails.

## Configuration

| Key | Default | Notes |
|---|---|---|
| `Accounts:MaxParallelOrganizations` | `4` | Organizations processed at the same time by one multi-organization call (each in its own scope). |
| `Accounts:UnmatchedLookbackDays` | `90` | Days of unmatched payments counted by `get_portfolio_overview`. |
| `Accounts:MinNameSimilarity` | `0.45` | Trigram similarity from which an organization name is a candidate. |
| `Mcp:RateLimit:MaxConcurrentPerAccount` | `8` | Tool calls at the same time per account. |
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
| `Search:MinSimilarity` | 0.3 | Smallest trigram similarity of a name match (substring matches always count). |
| `Search:DefaultPeriodMonths` | 12 | Activity period of search candidates when none is given. |
| `Search:MaxCandidates` | 200 | Best-scoring INNs considered before ordering by activity and paging. |
| `Search:BestMatchMargin` | 0.15 | Lead over the second candidate needed for `best_match`. |
| `Search:AliasWarningThreshold` | 30000 | Name spellings per tenant above which a warning is logged (once a day); 0 turns it off. |
| `Reconciliation:Currency` | UZS | Only payments in this currency are matched. |
| `Reconciliation:DocumentTypes` / `ActDocumentTypes` | 002, 008, 023, 005 / 005 | Paid document types / acts for G6. **Assumption.** |
| `Reconciliation:SignedStatus` | 3 | Signed status for G6. |
| `Reconciliation:AmountTolerance` | 0 | Minor units. **Assumption.** |
| `Reconciliation:DateWindowFromDays` / `DateWindowToDays` | -30 / 180 | Payment date minus document date. **Assumption.** |
| `Reconciliation:AutoMatchThreshold` / `HintMinConfidence` | 70 / 30 | Matches below the threshold are not created; hints from 30. **Assumption.** |
| `Reconciliation:MaxCombinationCandidates` / `MaxCombinationSize` | 12 / 6 | Combination search limits. |
| `Reconciliation:DuplicateSamePurposeDays` / `DuplicateSameAmountDays` | 30 / 3 | Possible double payments. **Assumption.** |
| `Reconciliation:RunAfterLoad` | true | Recalculate touched INNs after each load. |
| `Reconciliation:Weights:*` | see Reconciliation | Signal weights. **Assumption.** |
| `Mcp:HttpRateLimit:RequestsPerMinutePerKey` / `PerIp` | 300 / 300 | HTTP requests to `/mcp` per window (1 min). |
| `Mcp:HttpRateLimit:FailedAuthPerMinutePerIp` | 20 | Failed authentications per IP per window, then 429. |
| `Mcp:Notes:Reconciliation` / `NotReconciliation` | see `McpOptions` | Notes of the reconciliation tools / of tools that only compare totals by INN. |
| `Mcp:DefaultLimit` / `MaxLimit` | 50 / 200 | Rows per MCP answer (smaller than Analytics to keep model context small). |
| `Mcp:MaxPeriodDays` | 1827 | Longest period a tool accepts (5 years). |
| `Mcp:CallTimeout` | 5 s | Per tool call; the timeout answer asks to narrow the period. |
| `Mcp:MaskAccountNumbers` | true | Account numbers as `****1234`. INNs are never masked. |
| `Mcp:UntrustedTextMaxLength` | 300 | Third-party text (purpose, names, status comments) is cut and cleaned. |
| `Mcp:StaleAfter` | 36 h | Older last loads get a freshness note. |
| `Mcp:RateLimit:RequestsPerMinutePerKey` | 60 | Tool calls per minute per API key (fixed window). |
| `Mcp:RateLimit:MaxConcurrentPerTenant` | 4 | Tool calls running at once per tenant. |
| `Mcp:Notes:Bank` / `Documents` / `Counterparties` | see `McpOptions` | Warnings added to `notes` of the matching tools. |

| `Attention:*` | see "Attention inbox" | Rule thresholds (assumptions), `GroupAbove` 5, `MaxListedEntries` 20, `PeriodicInterval` 1 h, `ReminderInterval` 30 min, `MaxSnoozeDays` 90, `Enabled`. |
| `Attention:DismissedRetentionDays` | 90 | A dismissed item whose condition has been gone this long becomes `Archived`. |
| `Attention:FileStaleAfter` | 7 days | `connection.stale_data` threshold of file import connections. **Assumption.** |
| `Ingest:MaxItems` / `MaxBytes` | 500 / 2 MB | Largest 1C package (bytes after decompression), else 413. |
| `Ingest:SupportedSchemaVersions` | 1 | Accepted contract versions (an array); others get 400. |
| `Ingest:RateLimit:PackagesPerMinute` | 60 | Packages per ingest key per window (1 min), then 429. |
| `Ingest:RateLimit:FailedAuthPerMinutePerIp` | 20 | Failed ingest authentications per IP per window, then 429. |
| `Ingest:StaleAfter` | 48 h | The 1C source is stale when its last accepted package is older. |
| `Ingest:DefaultCurrency` | UZS | Currency of an item without one. **Assumption.** |
| `Ingest:SourceUtcOffset` | 05:00 | Offset of 1C date-times sent without one. **Assumption.** |
| `Ingest:MaxTextLength` | 4000 | Longest free text of an item (names, purpose, comment). |
| `Ingest:FullSyncRetention` | 7 days | Key lists of full exports that never finished are removed after this. |
| `Accounting:PaymentDateWindowDays` | 3 | Bank payment and 1C bank document are one payment within this many days. **Assumption.** |
| `Accounting:DocumentDateWindowDays` | 5 | Didox and 1C documents without a common number, same amount and INN. **Assumption.** |
| `Accounting:MaxCompareRows` | 50 000 | Most records a side of a comparison reads; a longer period must be narrowed. |
| `Statements:MaxFileBytes` | 50 MB | Largest statement file. |
| `Statements:PreviewRows` | 5 | Operations shown by a dry run. |
| `Statements:MaxPeriodDays` | 3700 | Longest period of one statement. |

All money is stored as `long` in minor units. Statement files hold decimal sum: imports multiply by 100 exactly and reject
rows with fractions of a tiyin; the Kapital Bank API amount unit (`Adapters:KapitalBank:AmountUnit`) is unrelated.

Bank operations with `err != 0` are stored with `has_error = true`, `error_code`, `error_message`; balances and
reconciliation use `BankTransactionQueries.WithoutErrors()`.
