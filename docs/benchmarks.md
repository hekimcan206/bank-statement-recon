# Benchmarks

Every number on this page was measured on the machine described below with the lab in `benchmarks/BankRecon.Lab`.
Nothing is estimated or rounded up. Raw outputs (Markdown and JSON) are in [`benchmark-results/`](../benchmark-results).

## Environment and host load

| Item | Value |
|---|---|
| CPU | Intel Core i7-13620H, 10 cores / 16 threads (laptop) |
| RAM | 15.7 GB, of which about 0.5 GB was free during the runs |
| OS | Windows 11 Home 10.0.26200 |
| .NET | SDK 10.0.401, Release build, default GC settings |
| Docker | Docker Desktop 29.9.1 on WSL2, VM limited to 7.6 GB and 16 CPUs |
| PostgreSQL | `postgres:17-alpine` started by Testcontainers, default settings except `max_connections=200` |
| Load | Shared, busy host: CPU load 44-46 % before the runs from other work, about 10 other containers running, another build agent active. Timings are therefore pessimistic and noisy; the medians are what to compare, not absolute values |

## 1. Matching precision and recall

`dotnet run -c Release --project benchmarks/BankRecon.Lab -- accuracy 20 2000`

Each scenario is generated (ADR 0004), **written to its file format and parsed back**, then matched. Scoring is per
(bank line, ledger item) pair. The rules were tuned on seeds 1-3; this table uses seeds 101-120. Queue share is the
fraction of all items (both sides) left for a person.


| Format | Configuration | Precision | Recall | F1 | Queue share |
|---|---|---|---|---|---|
| MT940, Turkish free-text :86: | amount + date window, first fit (baseline) | 0.6422 (0.6152) | 0.3889 (0.3563) | 0.4856 | 40.9 % |
| MT940, Turkish free-text :86: | exact reference only | 1.0000 (0.9987) | 0.4341 (0.4064) | 0.6054 | 57.4 % |
| MT940, Turkish free-text :86: | + unique amount/date | 0.9990 (0.9971) | 0.5776 (0.5425) | 0.7316 | 43.3 % |
| MT940, Turkish free-text :86: | + fuzzy (Jaro-Winkler, token set) | 0.9987 (0.9974) | 0.6396 (0.6056) | 0.7795 | 37.1 % |
| MT940, Turkish free-text :86: | + one-to-many / many-to-one (full) | 0.9988 (0.9960) | 0.9791 (0.9726) | 0.9884 | 14.6 % |
| MT940, Turkish free-text :86: | full, no ambiguity guard | 0.9988 (0.9960) | 0.9799 (0.9737) | 0.9886 | 14.4 % |
| MT940, GVC ?NN subfields | amount + date window, first fit (baseline) | 0.6520 (0.6233) | 0.3912 (0.3612) | 0.4852 | 40.0 % |
| MT940, GVC ?NN subfields | exact reference only | 1.0000 (1.0000) | 0.4395 (0.3885) | 0.6106 | 56.8 % |
| MT940, GVC ?NN subfields | + unique amount/date | 0.9991 (0.9971) | 0.5827 (0.5301) | 0.7360 | 42.5 % |
| MT940, GVC ?NN subfields | + fuzzy (Jaro-Winkler, token set) | 0.9992 (0.9966) | 0.6487 (0.5909) | 0.7865 | 35.9 % |
| MT940, GVC ?NN subfields | + one-to-many / many-to-one (full) | 0.9988 (0.9945) | 0.9812 (0.9717) | 0.9897 | 14.0 % |
| MT940, GVC ?NN subfields | full, no ambiguity guard | 0.9988 (0.9945) | 0.9819 (0.9729) | 0.9901 | 13.9 % |
| MT940, SEPA /KEY/ :86: | amount + date window, first fit (baseline) | 0.6520 (0.6233) | 0.3912 (0.3612) | 0.4852 | 40.0 % |
| MT940, SEPA /KEY/ :86: | exact reference only | 1.0000 (1.0000) | 0.4395 (0.3885) | 0.6106 | 56.8 % |
| MT940, SEPA /KEY/ :86: | + unique amount/date | 0.9991 (0.9971) | 0.5855 (0.5314) | 0.7382 | 42.2 % |
| MT940, SEPA /KEY/ :86: | + fuzzy (Jaro-Winkler, token set) | 0.9992 (0.9966) | 0.6507 (0.5914) | 0.7880 | 35.8 % |
| MT940, SEPA /KEY/ :86: | + one-to-many / many-to-one (full) | 0.9986 (0.9945) | 0.9826 (0.9734) | 0.9905 | 13.8 % |
| MT940, SEPA /KEY/ :86: | full, no ambiguity guard | 0.9983 (0.9945) | 0.9835 (0.9734) | 0.9908 | 13.7 % |
| camt.053.001.08 | amount + date window, first fit (baseline) | 0.6897 (0.6670) | 0.4677 (0.4391) | 0.5575 | 35.6 % |
| camt.053.001.08 | exact reference only | 1.0000 (1.0000) | 0.5507 (0.5297) | 0.7103 | 47.7 % |
| camt.053.001.08 | + unique amount/date | 1.0000 (0.9983) | 0.6940 (0.6601) | 0.8194 | 34.3 % |
| camt.053.001.08 | + fuzzy (Jaro-Winkler, token set) | 1.0000 (0.9985) | 0.7553 (0.7343) | 0.8606 | 28.3 % |
| camt.053.001.08 | + one-to-many / many-to-one (full) | 0.9989 (0.9960) | 0.9881 (0.9765) | 0.9932 | 13.2 % |
| camt.053.001.08 | full, no ambiguity guard | 0.9989 (0.9960) | 0.9889 (0.9793) | 0.9936 | 13.1 % |

Reading the table:

- The **first-fit baseline** (same amount within 5 days, first open item) books a third of its matches wrongly.
- **Exact reference** alone is perfectly precise but finds well under half of the pairs.
- **Unique amount** and **fuzzy** add 20 points of recall at precision 0.998-1.000.
- **Groups** (many-to-one, one-to-many) are the biggest single step: bulk payments and instalments make up a third
  of the pairs.
- The **ambiguity guard** costs 0.001 recall and buys nothing measurable here; see ADR 0002 for why it stays.

Full configuration, by case, summed over the 20 seeds (Turkish free-text MT940; the other formats are in
`benchmark-results/accuracy.md`):

| Case | Truth pairs | Found | Recall | Items wrongly matched |
|---|---|---|---|---|
| bank-debit | 0 | 0 | - | 0 |
| bulk | 8339 | 7959 | 0.954 | 0 |
| charges-deducted | 2389 | 2201 | 0.921 | 0 |
| clean | 13070 | 13069 | 1.000 | 0 |
| duplicate-payment | 0 | 0 | - | 9 |
| instalments | 4110 | 4094 | 0.996 | 0 |
| mangled-reference | 4861 | 4803 | 0.988 | 0 |
| no-reference | 2738 | 2679 | 0.978 | 0 |
| unexpected-receipt | 0 | 0 | - | 25 |
| unpaid | 0 | 0 | - | 33 |

The wrong matches (25 unexpected receipts, 9 duplicate payments, 33 unpaid invoices out of about 35,500 truth pairs)
are the structurally ambiguous kind: a receipt with no reference from a customer who also has an open invoice of the
same round amount, or whose open invoices add up to it. The weakest found case is **charges deducted** (0.92-0.93):
charges above `min(25.00, 1 %)` of a small invoice are outside the tolerance by design.

## 2. Matching time and scale

`dotnet run -c Release --project benchmarks/BankRecon.Lab -- throughput 5` (in memory, one account)

| Ledger items | Bank lines | Days | Median (ms) | Min-max (ms) | Items/s (both sides) | Precision | Recall |
|---|---|---|---|---|---|---|---|
| 1,000 | 851 | 20 | 237.4 | 187.1-280.0 | 7,797 | 0.9989 | 0.9868 |
| 5,000 | 4,080 | 20 | 2887.6 | 2732.9-2944.3 | 3,145 | 0.9963 | 0.9776 |
| 10,000 | 8,164 | 40 | 11435.9 | 11407.1-13310.4 | 1,588 | 0.9795 | 0.8549 |
| 20,000 | 16,246 | 80 | 37805.1 | 32513.4-75915.1 | 959 | 0.9662 | 0.7848 |

Two honest findings:

- **Time grows faster than linearly** (5,000 items 2.9 s, 20,000 items 37.8 s). The group passes compute counterparty
  similarity for every open ledger item in a 15-day window, repeated until nothing new matches. An index of ledger
  items by folded counterparty token would remove most of that work; it is listed as next step.
- **Quality drops with scale on this generator** (recall 0.985 at 1,000 items, 0.785 at 20,000). The generator draws
  company names from 30 x 16 x 4 = 1,920 combinations and creates one customer per four invoices, so at 20,000 items
  most names are shared by several customers. Counterparty-only evidence (unique amount per counterparty, name-related
  groups) then stops being unique and the matcher refuses or, rarely, errs (precision 0.966). Real counterparty
  names are far more distinct, but the effect is real: name-only evidence is only as good as the names.

## 3. Parse throughput: streaming vs buffering

`dotnet run -c Release --project benchmarks/BankRecon.Lab -- parse 500000 150000 5`

| Format | Mode | File size | Entries | Time (s) | MB/s | Entries/s | Allocated per entry | Peak managed heap |
|---|---|---|---|---|---|---|---|---|
| MT940 (Turkish free text) | streaming | 58.0 MB | 500,000 | 0.52 (0.50-0.93) | 111.5 | 961,653 | 1,593 B | 12 MB |
| MT940 (Turkish free text) | buffered (ToStatements, 1 run) | 58.0 MB | 500,000 | 2.14 (2.14-2.14) | 27.1 | 233,849 | 1,620 B | 195 MB |
| camt.053.001.08 | streaming | 166.6 MB | 150,000 | 1.81 (1.50-2.45) | 92.2 | 82,983 | 6,110 B | 12 MB |
| camt.053.001.08 | buffered (ToStatements, 1 run) | 166.6 MB | 150,000 | 2.32 (2.32-2.32) | 71.8 | 64,667 | 6,138 B | 85 MB |

- Streaming keeps the managed heap at about **12 MB** for a 58 MB MT940 file and a 167 MB camt.053 file; buffering the
  same files into `Statement` objects needs 195 MB and 85 MB, growing with the file.
- MT940 streams at about 960,000 entries/s. camt.053 is XML with roughly 1.1 KB per entry and parses at about
  83,000 entries/s (92 MB/s).
- Allocation per entry (1.6 KB MT940, 6.1 KB camt) is mostly strings and, for camt, the per-entry `XElement`; it is
  short-lived garbage, which is why the peak heap stays flat.
- An earlier run of the same command on this host, while memory was nearly exhausted, gave camt.053 streaming runs
  between 1.6 s and 27 s (median 12.3 s). That is paging on the host, not the parser; the run above was repeated when
  the host was calmer. The sizes are 500,000 and 150,000 entries rather than a million because of the free memory.

## 4. PostgreSQL: import, reconciliation run, work queue

`dotnet run -c Release --project benchmarks/BankRecon.Lab -- database` (production services called in-process, no
HTTP; median of 3 accounts per size)

Import and reconciliation (one account per run, MT940 Turkish free text):

| Ledger items | Bank lines | Ledger insert (ms) | Statement import (ms) | Import lines/s | Reconciliation run (ms) | Matches | Queued |
|---|---|---|---|---|---|---|---|
| 2,000 | 1,569 | 190 | 177 | 8,841 | 4177 | 1,335 | 543 |
| 5,000 | 4,100 | 327 | 291 | 14,088 | 19672 | 3,441 | 1,238 |
| 10,000 | 8,140 | 645 | 507 | 16,054 | 44537 | 6,496 | 3,784 |

Work-queue drain (claim + write-off per item, in-process service calls, one account with 3,000 queued items):

| Workers | Items | Drain time (s) | Items/s | Double claims | Items resolved exactly once |
|---|---|---|---|---|---|
| 1 | 2,933 | 91.95 | 32 | 0 | True |
| 2 | 2,888 | 30.69 | 94 | 0 | True |
| 4 | 2,837 | 16.83 | 169 | 0 | True |
| 8 | 2,816 | 13.91 | 203 | 0 | True |
| 16 | 2,857 | 9.02 | 317 | 0 | True |

Crash simulation: 8 workers, every 10th claim abandoned with a 5 s lease: 939 items resolved in 25.8 s, double claims 0, every item resolved exactly once: True.

- **Import** streams into a binary `COPY` at 9,000-16,000 bank lines/s including SHA-256, parsing and the balance check.
- **Reconciliation runs are dominated by database round trips**, not by matching: 10,000 items take 44.5 s of which
  in-memory matching is about 11 s (section 2). Every match is one `INSERT ... RETURNING` with two link inserts, and
  every work item is upserted in one batch per side. Batching the match inserts is the obvious next step.
- **A run holds row locks for its whole duration.** With 40,000 items the in-transaction matching took longer than the
  connection's `idle_in_transaction_session_timeout` (30 s) allows between statements, and PostgreSQL ended the
  session; that is why the table stops at 10,000. Large accounts need the run split into snapshot, match outside the
  transaction, then verify-and-book; listed under Limitations.
- **Queue drain** scales with workers (32 items/s with one worker, 317 with 16) and never hands the same item to two
  workers. With every tenth claim abandoned, the abandoned items came back after their 5 s lease and every item was
  resolved exactly once. People resolve a few items per minute, so the queue is never the bottleneck.

## How to reproduce

```bash
cd benchmarks/BankRecon.Lab
LAB_OUTPUT=../../benchmark-results dotnet run -c Release -- accuracy 20 2000
LAB_OUTPUT=../../benchmark-results dotnet run -c Release -- throughput 5
LAB_OUTPUT=../../benchmark-results dotnet run -c Release -- parse 500000 150000 5
LAB_OUTPUT=../../benchmark-results dotnet run -c Release -- database      # needs Docker
```
