# Synect

Loads data from Didox, banks and 1C into PostgreSQL on a schedule (or on demand); REST and MCP tools read only this
database and never call external systems.

## Layout

| Project | Purpose |
|---|---|
| `Synect.Domain` | Entities: tenants, connections (states), sync runs, sync jobs, connection secrets, raw objects, bank, EDO, counterparties, monthly counterparty totals, counterparty name spellings (aliases), API keys, tool audit log, reconciliation runs / matches / match items / findings, attention items and their events; shared transliteration (`Text/`) and currency codes (`Common/IsoCurrency`) |
| `Synect.Application` | Adapter contracts and registry, sync services, queue (planner, dispatcher, requests), rate limiting and retries, secrets and credentials contracts, freshness query, read-only analytics (`Analytics/`), reconciliation of payments with documents (`Reconciliation/`), attention rules and inbox (`Attention/`), API keys and tool audit (`Access/`) |
| `Synect.Infrastructure` | EF Core + Npgsql, migrations, RLS interceptor, Data Protection secret store, Kapital Bank / Didox mappers, session and token caches, fake adapters |
| `Synect.Api` | HTTP host: `/health` and the MCP endpoint `/mcp` (Streamable HTTP, API key) |
| `Synect.Mcp` | MCP tools over `Analytics`, `Reconciliation`, `Attention`, `ISourceFreshnessQuery` and `SyncRequestService`; API key authentication, HTTP and tool limits, tool call journal. No reference to Infrastructure, adapters, queue internals or secrets (enforced by a test) |
| `Synect.Worker` | Hosts the sync queue (`SyncWorker`) |
| `Synect.DevTools` | Development only: demo/load data, `query`, `bench`, `explain`, API keys, reconciliation (`reconcile`, `rebuild-reconciliation`, `reconcile-eval`, `reconcile-bench`), attention (`attention`, `attention-eval`, `attention-bench`), `normalize-currency`, test database cleanup (not referenced by Api or Worker) |
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
| `attention_items` | `rule_code`, `category` (`Connection`, `Bank`, `Reconciliation`, `Documents`), `severity` (`Critical`, `High`, `Medium`, `Low`), `title` and `body` (short English text; the model translates it), `details` jsonb (identifiers and amounts only: masked accounts, cleaned document numbers, no names or purposes), `entity_kind` / `entity_ref` (`connection`, `bank_account`, `transaction`, `document`, `tenant` for grouped items), `dedup_key` (unique per client among non-resolved items, partial unique index), `member_keys` (cases of a grouped item), `status` (`New`, `Seen`, `Snoozed`, `Dismissed`, `Resolved`), `snoozed_until`, `first_seen_at`, `last_seen_at`, `resolved_at`, `decided_by_key`, `comment`, `updated_at` |
| `attention_item_events` | append-only history: `item_id`, `kind` (`Created`, `Reopened`, `Seen`, `Snoozed`, `Dismissed`, `AutoResolved`), `at`, `api_key_id` (null for the platform), `comment` |

**Life cycle.** A case is identified by its `dedup_key` = rule code + significant parameters (for single cases the ids and
the amount, e.g. `reconciliation.possible_duplicate:<payment>:<earlier payment>:<amount>`; for grouped items
`<rule>:all`), so it is never created twice; repeated evaluations only refresh `last_seen_at` and the content. When the
condition is gone, a New, Seen or Snoozed item becomes `Resolved` (event `AutoResolved`); when it comes back, the same row
opens again as `New` (event `Reopened`). A `Snoozed` item counts as `New` in lists and counters once `snoozed_until` has
passed and is stored as `New` (event `Reopened`) by the next evaluation if the condition still holds. A `Dismissed`
item stays dismissed (it is neither resolved nor reopened by the platform) until its significant content changes: a
different amount gives a different key, hence a new item; a dismissed grouped item opens again only when a case appears
that was not in it when it was dismissed. Evaluations and person decisions of a client are serialized with a PostgreSQL
advisory lock.

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
| `connection.stale_data` | High | an Active connection's last successful load (or creation, if never loaded) is older than `StaleAfter` (36 h) |
| `connection.incomplete_load` | High | bank: a stored day older than yesterday is still not closed by the bank (today and yesterday are open in normal operation); Didox: the last successful load is incomplete (page limit) |
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
`get_attention_items` (filters `severity`, `category`, `status` as comma separated lists, default `new,seen`; `limit`,
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
dotnet run --project src/Synect.DevTools -- normalize-currency --all
dotnet run --project src/Synect.DevTools -- reconcile --tenant "Demo client 1" [--inn 300000005]
dotnet run --project src/Synect.DevTools -- rebuild-reconciliation --tenant "Demo client 1"
dotnet run --project src/Synect.DevTools -- reconcile-eval
dotnet run --project src/Synect.DevTools -- reconcile-bench --tenant "Load test" --runs 7
dotnet run --project src/Synect.DevTools -- attention --tenant "Demo client 1"
dotnet run --project src/Synect.DevTools -- attention-eval
dotnet run --project src/Synect.DevTools -- attention-bench --tenant "Load test" --runs 7
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

PostgreSQL tests (migrations, RLS, queue, secrets, analytics, reconciliation, attention, MCP) create and drop a temporary database and role
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

All money is stored as `long` in minor units.

Bank operations with `err != 0` are stored with `has_error = true`, `error_code`, `error_message`; balances and
reconciliation use `BankTransactionQueries.WithoutErrors()`.
| `Attention:*` | see "Attention inbox" | Rule thresholds (assumptions), `GroupAbove` 5, `MaxListedEntries` 20, `PeriodicInterval` 1 h, `ReminderInterval` 30 min, `MaxSnoozeDays` 90, `Enabled`. |
