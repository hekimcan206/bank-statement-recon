# Threat model

Scope: the API (`BankRecon.Api`), its PostgreSQL database, and the statement files it accepts. STRIDE-style.

## Assets

| Asset | Why it matters |
|---|---|
| Bank lines and ledger items | Account numbers, counterparty names, amounts: confidential commercial data, personal data for sole traders (KVKK, the Turkish Personal Data Protection Law, applies) |
| Matches and write-offs | Decide what is booked as paid; a wrong one hides a real difference |
| Imported statement files (hashes, metadata) | Proof of what the bank reported |
| Work-queue state | Who decided what, and when |

## Actors

- Clerks and finance systems calling the API with the shared key (demo) or, in a deployment, an identity token.
- A malicious or careless uploader (wrong file, crafted file, the same file twice).
- An attacker on the network without credentials.
- An operator with database access.

## Threats and mitigations

| STRIDE | Threat | Mitigation | Residual risk |
|---|---|---|---|
| Spoofing | Calls without credentials | `X-Api-Key` on every `/api` route, constant-time comparison, several keys for rotation; probes stay open but reveal no details | Shared key: no per-user identity. Production puts the API behind the organisation's identity provider and derives `workerId` from the token |
| Spoofing | A clerk resolves as another worker | Fencing token required on every queue write | `workerId` is self-asserted under the shared key |
| Tampering | Crafted XML: entity expansion, external entities | `DtdProcessing.Prohibit`, no `XmlResolver`, tested with a billion-laughs and an `file://` entity document | - |
| Tampering | Crafted MT940: no newlines, gigantic fields, millions of entries | Line limit 2,048, field limit 8,192, per-statement entry limit, request body limit 64 MB, mutation fuzzing in CI | A 64 MB file still costs CPU; rate limiting belongs to the gateway |
| Tampering | A statement that does not add up, or a re-export of an imported one | Opening + movements must equal closing, else the whole file is rolled back (`422`); SHA-256 file hash and `UNIQUE NULLS NOT DISTINCT (account, statement_ref, sequence)` reject duplicates (`200 duplicate` / `409`) | A bank that re-issues a corrected statement under the same reference must be handled by an operator |
| Tampering | Two clerks book the same item | Row locks in a fixed order, `UNIQUE` on both match link tables (race test: one wins, one gets `422`) | - |
| Tampering | Application role rewrites history | `recon_app` has no `DELETE` anywhere and no `UPDATE` on files, matches and link tables; statements only the closing columns (integration test asserts `42501`) | The schema owner can still change anything; protect it as a break-glass credential |
| Tampering | SQL injection | Parameterised SQL only; the two table names that vary come from constants | - |
| Repudiation | "I did not write that off" | Write-off needs a note; `resolved_by`, `resolved_at`, attempts and the match's `decided_by` are stored | No append-only audit log or signature (see Limitations) |
| Information disclosure | Logs leak statement content | Structured logs carry ids, counts and timings, not narratives or names; problem details never echo file content beyond the parser's message | A parser message can quote up to 40 characters of a malformed value |
| Information disclosure | Database exposed | Compose binds PostgreSQL and the API to 127.0.0.1 only; separate migrator and application roles | TLS between API and database is not configured in the demo |
| Denial of service | Long reconciliation run blocks clerks | `lock_timeout=5s` turns waiting into a retryable `503`; runs per account are serialised by an advisory lock; queue claims never wait (`SKIP LOCKED`) | Very large accounts make a run hold row locks for seconds (measured) |
| Elevation of privilege | Container escape or misuse | Chiseled .NET image without shell or package manager, non-root UID 1654 | - |

## Data protection notes

All data in this repository is synthetic. A deployment processing real statements must define retention for
narratives and counterparty names, restrict who can read the queue, and treat exported match reports as personal
data under KVKK where counterparties are natural persons.
