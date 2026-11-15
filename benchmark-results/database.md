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
