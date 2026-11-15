# 0001: Streaming parsers with one format-neutral statement model

- Status: accepted
- Date: 2026-11-13

## Context

Corporate customers receive account statements in two formats:

- **SWIFT MT940**, a line-oriented FIN message: `:20:` reference, `:25:` account, `:28C:` sequence, `:60F:` opening
  balance, then one `:61:` statement line per movement (`6!n[4!n]2a[1!a]15d1!a3!c16x[//16x]` followed by an optional
  34-character supplementary line), each optionally followed by `:86:` free text, then `:62F:` closing and `:64:`
  available balance. The `:86:` field has no common structure: German banks use GVC `?NN` subfields, Benelux banks a
  `/KEY/value` keyword style, and many banks, Turkish ones among them, plain text.
- **ISO 20022 camt.053** (BankToCustomerStatement), XML, versions 001.02 through 001.13 in circulation. 001.02 and
  001.08 differ in the places a parser touches: the entry status is text in 02 and a `Cd` element in 08, party names
  sit directly under `Dbtr`/`Cdtr` in 02 and under `Dbtr/Pty` in 08, and the transaction amount moved from
  `AmtDtls/TxAmt/Amt` to `TxDtls/Amt`. One `Ntry` can carry several `TxDtls` (a batch booking).

Files can be large (a payment service provider's daily camt.053 has hundreds of thousands of entries) and they are
uploaded by users, so they are untrusted input.

## Options considered

1. **DOM parsing** (`XDocument.Load`, `File.ReadAllLines`) into format-specific classes. Simple, but memory grows with
   the file and the rest of the system has to know two models.
2. **A third-party MT940/ISO 20022 library.** Saves code, but the ones known to us either buffer the whole message,
   cover only one format, or model `:86:` as an opaque string; and a parser is the part of this repository that has to
   be reviewed line by line anyway.
3. **Own streaming readers that emit one format-neutral model as a sequence of parts**
   (`StatementStarted`, `EntryRead`, `StatementEnded`).

## Decision

Option 3.

- `Mt940Reader` reads line by line with a hard line-length limit (`TextReader.ReadLine` would buffer a 2 GB file
  without newlines), assembles fields, and yields each entry as soon as its `:86:` (or the next field) has been read.
  It accepts plain files and FIN-wrapped messages, messages separated by `-` or only by the next `:20:`, the optional
  entry date (year inferred as the one closest to the value date, so 31 December booked on 2 January works), funds
  codes, `RC`/`RD` reversals (stored as the effective direction plus a reversal flag) and `NONREF`.
- `Narrative86` detects the three `:86:` styles and extracts remittance text, counterparty name, IBAN and
  end-to-end id where the style allows; the raw text is always kept.
- `Camt053Reader` uses `XmlReader` and materialises only one `Ntry` (or `Acct`, `Bal`) at a time as an `XElement`.
  DTDs are prohibited and no resolver is set, which rules out entity expansion and external entities.
- Every malformed input surfaces as `StatementFormatException` with a line number where one exists. Nothing else may
  escape a parser; the fuzz tests enforce it.
- The running balance (opening plus movements) is computed while streaming and compared with the stated closing
  balance; an unbalanced statement is reported, and the importer refuses it.
- Writers exist for both formats. They make the round-trip property tests possible and feed the synthetic data
  generator, and they reject what the format cannot carry (a 17-character reference) instead of truncating silently.

## Consequences

- Memory is flat in the number of entries (measured in [benchmarks](../benchmarks.md)): the importer streams entries
  from the parser straight into a PostgreSQL binary `COPY`.
- MT940 has no encoding declaration. Valid UTF-8 in the first 64 KiB is read as UTF-8, otherwise as Windows-1254
  (Turkish). A file whose first invalid byte appears after 64 KiB is decoded as UTF-8 with replacement characters;
  documented as a limitation.
- `:86:` continuation lines are joined without a separator, because banks hard-wrap at 65 characters. A bank that
  wraps at word boundaries and drops the space loses that space; matching folds whitespace anyway.
- Writers do not validate against the official XSDs (not available offline here); element order follows the
  published message definitions and bank implementation guides.

## Sources

- SWIFT MT940 field layout as published by banks: Bank Millennium, Westpac, Lloyds, Nordea MT940 descriptions.
- `:86:` subfield conventions: CSOB and Oberbank format descriptions; Ziraat Bankası publishes an MT940/942 document
  (not machine-readable here, so the Turkish free-text variant in this repository is synthetic, not copied from any
  bank).
- camt.053 versions: Nordea MIG camt.053.001.02, Handelsbanken guides, Numeral's camt.053.001.08 XSD notes, SIX
  Swiss Payment Standards cash-management guide.
