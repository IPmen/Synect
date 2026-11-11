# Bank statement import: formats and profiles

Statement files (MT940, CSV) are imported into the same model and pipeline as the bank API load (raw layer, operations,
day balances, counterparties and name spellings, monthly totals, reconciliation, attention rules). Use it for banks
without an API and to check reconciliation on real statements.

```
dotnet run --project src/Synect.DevTools -- create-file-connection --tenant "Demo client 1" --bank-name "Hamkorbank" [--profile <name|path.json>]
dotnet run --project src/Synect.DevTools -- import-statement --tenant "Demo client 1" --connection "Hamkorbank (file import)" --file samples-private/stmt.sta --format mt940 --dry-run
dotnet run --project src/Synect.DevTools -- import-statement --tenant "Demo client 1" --connection "Hamkorbank (file import)" --file samples-private/stmt.sta --format mt940 [--profile ...] [--create-accounts] [--force]
dotnet run --project src/Synect.DevTools -- list-imports --tenant "Demo client 1" [--connection ...]
dotnet run --project src/Synect.DevTools -- anonymize-statement --file samples-private/stmt.sta --format mt940 --out samples-private/stmt-anon.sta
```

`--profile` is a built-in profile name or a path to a profile JSON file; without it the connection's default profile
(from `create-file-connection --profile`) is used, else `generic_mt940` / `generic_csv`. Start every new bank with
`--dry-run`: it reads and checks the file and prints the account, period, balances, warnings and the first operations
(account numbers masked) without writing anything.

## What the import does and refuses

* **Same file twice** (SHA-256 of the bytes, per client): refused with "already imported" and the date of the first
  import; nothing is written.
* **Operation ids.** Without a stable id in the file an operation gets `fh:<sha256>` of account, posting date, amount,
  direction, reversal flag, document number, normalized purpose (lower case, spaces collapsed) and its ordinal among
  identical rows of the statement; `bank_transactions.id_source = 'FileHash'` and `details.weak_operation_id = true`.
  Re-importing the same period or overlapping periods creates no duplicates; two identical payments on one day stay two
  rows. With `operationId` = `bank_reference` / `owner_reference` (MT940) or `column` (CSV) the file's id is used
  (`f:<id>`, `id_source = 'File'`); use that only when the bank confirms the id is stable across exports.
* **Amounts** are decimal sum in files and stored in tiyin: strictly times 100. A value with non-zero digits beyond two
  decimal places (`100,005`) is not rounded: that row is rejected with a warning (`fractional_minor_units`).
* **Balances.** MT940 `:60F:`/`:62F:` (CSV: `openingBalance` / `closingBalance` columns, see below) must satisfy
  opening + credit - debit = closing. Otherwise the file is rejected with the figures and the difference, unless
  `--force`: then it is imported with `balance_check = Failed`, the bank's closing balance is kept on the last day and the
  attention rule `bank.balance_mismatch` reports the day. Without balances the check is `Skipped` and no day balances are
  stored. With balances, every day of the period gets a closed day balance (days without operations too).
* **Accounts** are matched by number. An account the client does not have is created only with `--create-accounts`;
  otherwise the file is rejected. An account already loaded by another connection (e.g. the bank API) is refused: a
  file for it would count its operations twice. The currency (letters or ISO digits, e.g. 860) is normalized to letters
  and must match the stored account.
* **Rejected files** (unreadable, empty, unknown account, balances) record a failed `sync_runs` row with the reason but
  do not feed the circuit breaker; a dry run records nothing.
* One database transaction per file. Payment texts are stored as data and shown to the model as `untrusted_text`, like
  every other source; whitespace is collapsed and control characters are removed while reading.

## Built-in profiles

| Name | Format | Notes |
|---|---|---|
| `generic_mt940` | MT940 | Standard structure. From `:86:`: INN / PINFL after `ИНН`, `INN`, `STIR`, `СТИР`, `ПИНФЛ`, `PINFL`, `JSHSHIR`; account after `Р/С`, `СЧ`, `СЧЕТ`, `ACC`, `H/R`, `Ҳ/Р`; MFO after `МФО`, `MFO`; document number after `№`, `N`, `No`, `DOC`, `ДОК` or before `-sonli` / `-сонли`; name after `NAME`, `НАИМ`, `NOMI`, `НОМИ` up to `/`, `;` or `|`. The purpose is the whole `:86:` text. |
| `kapitalbank_mt940_assumed` | MT940 | **ASSUMED structure, NOT verified**: `:86:/INN/.../NAME/.../ACC/.../MFO/.../DOC/.../PURP/<code> <text>`, SWIFT line wrapping joined without spaces. No real Kapital Bank MT940 (GetMT940) example was available; fix this profile on the first real (anonymized) file. |
| `generic_csv` | CSV | Header row with common Russian, Uzbek or English column names (`Дата`, `Дебет`, `Кредит`, `Сумма`, `Назначение платежа`, `ИНН`, `Счет`, `Date`, `Amount`, `Purpose`, `Sana`, `Kirim`, `Chiqim`, ...), delimiter detected, debit/credit columns or one signed amount; INN, document number, account and MFO from the purpose when there is no column; rows starting with `Итого`, `Всего`, `Jami`, `Total` are skipped. |

## Profile JSON

```jsonc
{
  "name": "hamkorbank_csv",          // stored in statement_imports.profile
  "format": "csv",                   // "mt940" or "csv"
  "encoding": "auto",                // auto (UTF-8 with/without BOM, else Windows-1251 or CP866), utf-8, windows-1251, cp866
  "accountNumber": null,             // statement account when the file has none (CSV without an account column)
  "currency": null,                  // when the file has none ("UZS" or "860"); else digits 6-8 of a 20-digit account
  "bankCode": null,                  // MFO when the file has none
  "operationId": "hash",             // hash | bank_reference | owner_reference (MT940) | column (CSV)
  "separators": "/;|",               // characters that end a value found after a marker
  "textFields": {                    // read from MT940 :86: / the CSV purpose column when there is no own column
    "inn":            { "markers": ["ИНН", "INN", "STIR"], "pattern": "\\d{14}|\\d{9}" },
    "name":           { "markers": ["НАИМ", "NOMI"] },                         // no pattern: up to a separator
    "account":        { "markers": ["Р/С", "H/R"], "pattern": "\\d{20}" },
    "mfo":            { "markers": ["МФО"], "pattern": "\\d{5}" },
    "documentNumber": { "regex": ["(?<v>\\d+)-sonli"], "markers": ["№"], "pattern": "\\d[\\d/-]*" },
    "purposeCode":    { "regex": ["^(?<v>\\d{5})\\s"] },
    "purpose":        { "regex": ["/PURP/(?<v>.+)$"] }                         // default: the whole text
  },
  "mt940": {
    "openingBalanceDate": "first_day",  // :60F: date is the first day of the statement; "previous_day" = the day before it
    "accountPattern": null,             // regex for :25: with groups "account" and optional "bank"
    "subfieldMarker": null,             // structured :86: like "?20...?32...": "?" and the map below
    "subfields": { "20": "purpose", "21": "purpose", "32": "name", "33": "name" },
    "lineJoin": " "                     // how the lines of :86: are joined ("" for SWIFT 65-character wrapping)
  },
  "csv": {
    "delimiter": "auto",               // auto (; , tab |) or the character, "\\t" for tab
    "quote": "\"",
    "headerRow": 1,                    // 1-based among non-empty lines; 0 = no header, columns as "#1", "#2", ...
    "columns": {                       // field -> header name(s) (case and extra spaces ignored) or "#n"
      "account": "Счет", "postingDate": ["Дата", "Дата проводки"], "valueDate": null,
      "debit": "Дебет", "credit": "Кредит",      // or "amount" (signed) with optional "direction"
      "currency": "Валюта", "documentNumber": "№ док", "counterpartyName": "Контрагент", "counterpartyInn": "ИНН",
      "counterpartyAccount": "Счет контрагента", "counterpartyMfo": "МФО", "purpose": "Назначение платежа",
      "purposeCode": null, "operationId": null,
      "openingBalance": "Входящий остаток",      // first value (by date) = opening balance of the account's statement
      "closingBalance": "Исходящий остаток"      // last value = closing balance
    },
    "amountMode": "auto",              // auto | debit_credit | signed
    "positiveIsCredit": true,          // signed: positive = money received
    "creditValues": ["C", "К", "КРЕДИТ", "KIRIM"], "debitValues": ["D", "Д", "ДЕБЕТ", "CHIQIM"],  // direction column
    "dateFormats": ["dd.MM.yyyy", "yyyy-MM-dd"],
    "decimalSeparator": "auto",        // auto: the last of ',' and '.'; set "," or "." when amounts have thousands separators
    "skipRowsMatching": ["^\\W*(итого|jami)\\b"]
  }
}
```

Regular expressions are .NET syntax, case-insensitive, with a 250 ms timeout; `regex` entries need a group named `v`
(or the first group). A value after a marker may be preceded by `:`, `=`, `.`, `№`, `#`, `/`, `-` or spaces. INNs are
accepted with 9 digits (company) or 14 (PINFL). A complete example of a custom CSV profile:
`tests/Synect.Tests/Fixtures/Statements/profile_cp866_bank.json`.

**MT940 notes.** Tags `:20:`, `:25:`, `:28C:`, `:60F:`/`:60M:`, `:61:` (with an optional multi-line `:86:`),
`:62F:`/`:62M:`, `:64:`, `:65:`; several statements per file; a statement continued in the next message (`:62M:` then
`:60M:`) is joined; SWIFT envelopes `{1:..}{2:..}{4: ... -}` are removed. `:61:` marks `C`, `D`, `RC` (reversal of a
credit: money out), `RD` (reversal of a debit: money in); value date `YYMMDD` plus optional entry date `MMDD` (the posting
date; year taken from the value date, across New Year too); decimal comma. Reversals are stored with `source_state =
'reversal'` and `details.reversal = true`.

## Real statements and anonymization

Real statements are never committed: put them in `samples-private/` (ignored by git). Before a statement leaves the
company (as a test fixture or for analysis) anonymize it:

1. `anonymize-statement --file samples-private/x.sta --format mt940 [--profile ...] --out samples-private/x-anon.sta`
   replaces every counterparty name and INN the profile reads with a stable pseudonym (`COUNTERPARTY 001`, `900000001`)
   everywhere in the file, inside payment texts too, and masks every 20-digit account number the same way everywhere
   (the first 8 digits - balance account and currency - stay). Amounts, dates and the rest of the payment texts stay, in
   the original encoding.
2. Review the result by hand: the tool only knows the names and INNs the profile reads. Replace what it missed (the
   client's own name, people's names, phone numbers, addresses, contract details that identify someone) with the same
   code everywhere in the file. Keep amounts, dates and the wording of purposes: they are what reconciliation needs.
3. Import the anonymized file with `--dry-run` and compare the totals with the original.

## Unknown about real bank formats

* No real MT940 or CSV of a Uzbek bank has been seen. Which `:86:` structure each bank uses (free text, `?NN`
  subfields, `/CODE/` pairs), where it writes the counterparty INN, name, account and the document number, whether
  `:61:` references are stable, what the `:60F:` date means, which encoding and line wrapping it uses: all of this is a
  profile setting, to be filled from the first real files.
* Kapital Bank GetMT940: the profile `kapitalbank_mt940_assumed` and the fixture `mt940_kapitalbank_assumed.sta` are an
  assumption, not the documented format.
* CSV exports: per-day balance columns (opening / closing repeated per day) are read as statement-level balances (first
  and last value); a bank that writes something else needs a check of the result.
