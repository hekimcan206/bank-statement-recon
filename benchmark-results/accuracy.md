Seeds 101-120 per format (the rules were tuned on seeds 1-3 only), ledger items per scenario: 2000. Median over seeds (min in brackets).

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

Full configuration, by case (summed over seeds):

| Format | Case | Groups | Truth pairs | Found | Recall | Wrongly matched items |
|---|---|---|---|---|---|---|
| MT940, Turkish free-text :86: | bank-debit | 841 | 0 | 0 | - | 0 |
| MT940, Turkish free-text :86: | bulk | 2768 | 8339 | 7959 | 0.954 | 0 |
| MT940, Turkish free-text :86: | charges-deducted | 2389 | 2389 | 2201 | 0.921 | 0 |
| MT940, Turkish free-text :86: | clean | 13070 | 13070 | 13069 | 1.000 | 0 |
| MT940, Turkish free-text :86: | duplicate-payment | 485 | 0 | 0 | - | 9 |
| MT940, Turkish free-text :86: | instalments | 1636 | 4110 | 4094 | 0.996 | 0 |
| MT940, Turkish free-text :86: | mangled-reference | 4861 | 4861 | 4803 | 0.988 | 0 |
| MT940, Turkish free-text :86: | no-reference | 2738 | 2738 | 2679 | 0.978 | 0 |
| MT940, Turkish free-text :86: | unexpected-receipt | 1074 | 0 | 0 | - | 25 |
| MT940, Turkish free-text :86: | unpaid | 6968 | 0 | 0 | - | 33 |
| MT940, GVC ?NN subfields | bank-debit | 883 | 0 | 0 | - | 0 |
| MT940, GVC ?NN subfields | bulk | 2715 | 8206 | 7850 | 0.957 | 0 |
| MT940, GVC ?NN subfields | charges-deducted | 2493 | 2493 | 2318 | 0.930 | 0 |
| MT940, GVC ?NN subfields | clean | 13259 | 13259 | 13259 | 1.000 | 0 |
| MT940, GVC ?NN subfields | duplicate-payment | 463 | 0 | 0 | - | 9 |
| MT940, GVC ?NN subfields | instalments | 1630 | 4104 | 4086 | 0.996 | 0 |
| MT940, GVC ?NN subfields | mangled-reference | 4833 | 4833 | 4779 | 0.989 | 0 |
| MT940, GVC ?NN subfields | no-reference | 2725 | 2725 | 2627 | 0.964 | 0 |
| MT940, GVC ?NN subfields | unexpected-receipt | 1054 | 0 | 0 | - | 17 |
| MT940, GVC ?NN subfields | unpaid | 6859 | 0 | 0 | - | 33 |
| MT940, SEPA /KEY/ :86: | bank-debit | 883 | 0 | 0 | - | 0 |
| MT940, SEPA /KEY/ :86: | bulk | 2715 | 8206 | 7850 | 0.957 | 0 |
| MT940, SEPA /KEY/ :86: | charges-deducted | 2493 | 2493 | 2319 | 0.930 | 0 |
| MT940, SEPA /KEY/ :86: | clean | 13259 | 13259 | 13259 | 1.000 | 0 |
| MT940, SEPA /KEY/ :86: | duplicate-payment | 463 | 0 | 0 | - | 9 |
| MT940, SEPA /KEY/ :86: | instalments | 1630 | 4104 | 4086 | 0.996 | 0 |
| MT940, SEPA /KEY/ :86: | mangled-reference | 4833 | 4833 | 4786 | 0.990 | 0 |
| MT940, SEPA /KEY/ :86: | no-reference | 2725 | 2725 | 2671 | 0.980 | 0 |
| MT940, SEPA /KEY/ :86: | unexpected-receipt | 1054 | 0 | 0 | - | 20 |
| MT940, SEPA /KEY/ :86: | unpaid | 6859 | 0 | 0 | - | 34 |
| camt.053.001.08 | bank-debit | 853 | 0 | 0 | - | 0 |
| camt.053.001.08 | bulk | 1344 | 4047 | 3883 | 0.959 | 0 |
| camt.053.001.08 | bulk-itemised | 4227 | 4227 | 4227 | 1.000 | 0 |
| camt.053.001.08 | charges-deducted | 2426 | 2426 | 2253 | 0.929 | 0 |
| camt.053.001.08 | clean | 13217 | 13217 | 13217 | 1.000 | 0 |
| camt.053.001.08 | duplicate-payment | 456 | 0 | 0 | - | 4 |
| camt.053.001.08 | instalments | 1712 | 4311 | 4302 | 0.998 | 0 |
| camt.053.001.08 | mangled-reference | 4807 | 4807 | 4746 | 0.987 | 0 |
| camt.053.001.08 | no-reference | 2678 | 2678 | 2632 | 0.983 | 0 |
| camt.053.001.08 | unexpected-receipt | 1091 | 0 | 0 | - | 17 |
| camt.053.001.08 | unpaid | 6892 | 0 | 0 | - | 34 |
