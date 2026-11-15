# 0002: Ordered rule passes, an ambiguity guard and bounded subset-sum groups

- Status: accepted
- Date: 2026-11-13

## Context

A bank line has to be paired with the ledger item it settles. The evidence is noisy: payers mistype invoice
numbers, MT940 cuts the customer reference at 16 characters, banks deduct transfer charges, customers pay several
invoices with one transfer or one invoice in instalments, names arrive as `ŞİMŞEK GIDA LTD ŞTİ`, `SIMSEK GIDA` or
`Şimşek Gıda Ltd. Şti.`, and round amounts (1,000.00, 2,500.00) collide constantly. A wrong automatic match is worse
than no match: it hides a real difference and someone books against the wrong customer.

## Options considered

1. **Amount and date window, first fit.** What many spreadsheets do. Measured: precision 0.64-0.69.
2. **One weighted score for every pair, global assignment** (Hungarian algorithm). Optimal for the score, but every
   decision depends on all others, scores are hard to explain to an auditor, and one-to-many is outside the model.
3. **A learned model** (gradient boosting on pair features). Needs labelled history this project does not have, and
   its errors are harder to explain.
4. **Ordered rule passes from most to least certain, each item used once, with an explicit refusal to guess.**

## Decision

Option 4, in `MatchingEngine`:

1. **Exact reference**: equal amount, ledger reference found verbatim on the bank line after folding (letters and
   digits only, culture-independent Turkish folding: every `I/ı/İ/i` becomes `i`), within 30 days.
2. **Unique amount**: equal amount within 5 days, and the pair is the only candidate in both directions; otherwise,
   the only candidate of this counterparty in both directions. A clearly different counterparty vetoes it.
3. **Fuzzy**: `0.5 reference + 0.2 counterparty + 0.2 amount + 0.1 date` (without a counterparty the weights become
   0.65/0.2/0.15), threshold 0.72. Reference similarity is Jaro-Winkler over 1-3 token windows of the bank text,
   with two domain corrections: digits matter more than letters (a digit difference beyond one edit halves the
   score, because `INV-00123` and `INV-00132` are different invoices), and a prefix counts only when the `:61:`
   customer reference fills all 16 characters, i.e. may really have been cut. Counterparty similarity is a soft
   token-set Dice coefficient (tokens equal at Jaro-Winkler 0.9) that ignores legal-form words; against free text it
   is the share of name tokens found. A counterparty below 0.3 halves the score.
   **Ambiguity guard**: a pair is accepted only if no other open candidate of either side scores within 0.05;
   otherwise both sides go to the work queue marked `Ambiguous`, with their top candidates.
4. **Groups**: many-to-one (one transfer, several invoices) and one-to-many (instalments) by depth-first subset-sum
   over at most 20 related candidates (reference found, or counterparty at least 0.8), 2-5 members, within a
   15-day window and the amount tolerance. Among several solutions the one with most reference-confirmed members
   wins; a tie is ambiguous and nothing is booked.

Amount tolerance for deducted charges is `min(25.00, 1 %)`. The passes repeat until nothing new matches, because each
rule is defined over the items still open; without that, matching the leftovers again would find more and a
reconciliation run would not be idempotent (a property test checks it).

## Consequences

- Every match carries its rule, score, amount difference and a one-line explanation (`reference 0.97, counterparty
  1.00, amount difference -12.50, value date +2 day(s)`), which is what an auditor asks for.
- On 20 hold-out seeds (the rules were tuned on seeds 1-3) the full configuration reaches precision 0.998-0.999 and
  recall 0.979-0.988 across the four file styles; the first-fit baseline reaches 0.64-0.69 precision. Each pass's
  contribution is in [benchmarks](../benchmarks.md).
- The remaining wrong matches are structurally ambiguous: an unexpected receipt from a customer who also has an open
  invoice of the same round amount, or whose open invoices happen to add up to it. Without a reference no rule can
  tell them apart; turning off name-only unique-amount and group rules would trade them for a lower recall.
- The ambiguity guard changes little on this synthetic data (recall -0.001, precision equal or +0.0003). It is kept
  because it protects against the expensive error class (two near-identical candidates) that the generator only
  produces rarely; the measurement is reported rather than tuned away.
- The generator and the matcher were written by the same person. The numbers show the relative value of each rule
  and catch regressions; they are not a claim about any real bank's data.

## Sources

- Jaro-Winkler for short identifiers with transpositions and shared prefixes: common practice in record linkage;
  reference values MARTHA/MARHTA 0.961, DWAYNE/DUANE 0.84, DIXON/DICKSONX 0.813 are checked in the tests.
- Subset-sum formulation of many-to-one reconciliation: "The Subset Sum Matching Problem" (arXiv 2508.19218) and
  patents on reconciling combinations of transactions (US 7403917, US 8548971).
