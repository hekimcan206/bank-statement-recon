# 0004: Synthetic labelled data and the testing strategy

- Status: accepted
- Date: 2026-11-13

## Context

Real statements contain personal and commercial data and are not available to this project. Matching quality can
only be measured against ground truth, and parsers can only be trusted if they survive input nobody wrote by hand.

## Decision

- **Generator** (`BankRecon.Synthetic`): a seeded receivables ledger and the statement that pays it, with the damage
  seen in practice: mangled references (digit typo, transposition, separators dropped, prefix dropped, lower case),
  missing references, charges deducted, bulk payments (with and without listed invoice numbers; itemised batch
  bookings in camt.053), instalments, duplicate payments, unexpected receipts, bank charges and unpaid invoices.
  About a third of invoice amounts come from a list of round amounts so collisions are frequent. Names are built
  from Turkish word lists and appear in upper case, transliterated or cut at 35 characters. IBANs use bank code
  99901 with valid MOD 97 check digits. Every bank line and ledger item belongs to exactly one truth group (tested).
- **Evaluation** is pair-level: a group match of one bank line to three invoices is three pairs, so one wrong invoice
  costs one false pair. Each scenario goes through its file format and back, so truncation and transliteration count.
- **Hold-out**: rules and thresholds were tuned on seeds 1-3; reported numbers use seeds 101-120.
- **Tests**: golden files for every sample (approved JSON, regenerated only with `UPDATE_GOLDEN=1`); FsCheck round-trip
  properties `parse(format(s)) == s` for MT940 (plain and FIN-wrapped, several messages) and camt.053 001.02 and
  001.08, and `format(parse(format(s))) == format(s)`; mutation fuzzing (3,000 mutations per sample plus random
  bytes) asserting that only `StatementFormatException` escapes and no input takes longer than 2 s; matcher
  properties (each item used once, every match within tolerance, order independence, idempotence on leftovers);
  Testcontainers PostgreSQL 17 integration tests for import idempotency, unbalanced and malformed files,
  least-privilege grants, run idempotency and concurrency, queue claims by 8 workers, lease expiry and fencing, and a
  repeated two-clerk race on one ledger item.

## Consequences

- Precision and recall are honest about the synthetic distribution only; real data will contain patterns the
  generator does not. The per-case table makes clear where the matcher is weak (charges-deducted 0.92-0.93, no-reference
  0.96-0.98 recall).
- Fuzzing is mutation-based inside the test suite, not coverage-guided (no SharpFuzz/libFuzzer); it is cheap enough
  to run on every build.
