Median of 5 runs per row (min-max in brackets). Peak heap is sampled every 10,000 entries.

| Format | Mode | File size | Entries | Time (s) | MB/s | Entries/s | Allocated per entry | Peak managed heap |
|---|---|---|---|---|---|---|---|---|
| MT940 (Turkish free text) | streaming | 58.0 MB | 500,000 | 0.52 (0.50-0.93) | 111.5 | 961,653 | 1,593 B | 12 MB |
| MT940 (Turkish free text) | buffered (ToStatements, 1 run) | 58.0 MB | 500,000 | 2.14 (2.14-2.14) | 27.1 | 233,849 | 1,620 B | 195 MB |
| camt.053.001.08 | streaming | 166.6 MB | 150,000 | 1.81 (1.50-2.45) | 92.2 | 82,983 | 6,110 B | 12 MB |
| camt.053.001.08 | buffered (ToStatements, 1 run) | 166.6 MB | 150,000 | 2.32 (2.32-2.32) | 71.8 | 64,667 | 6,138 B | 85 MB |
