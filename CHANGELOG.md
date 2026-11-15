# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.0.0] - 2026-11-15

### Added

- Streaming MT940 reader and writer: plain and FIN-wrapped messages, optional entry date with year inference across
  new year, funds codes, `RC`/`RD` reversals, `NONREF`, supplementary details, line and field length limits, running
  balance check. `:86:` parsing for GVC `?NN` subfields (with SEPA `EREF+`/`SVWZ+`), SEPA `/KEY/` keywords and free
  text; UTF-8 or Windows-1254 detection for Turkish text; SWIFT X-charset transliteration for Turkish letters.
- Streaming camt.053 reader for versions 001.02 to 001.13 (one `Ntry` in memory at a time, DTDs prohibited) and a
  writer for 001.02 and 001.08; batch bookings with itemised `TxDtls` become one bank line per detail.
- Culture-independent Turkish text folding, Jaro-Winkler and soft token-set similarity.
- Rule-based matcher: exact reference, unique amount (globally or per counterparty), fuzzy score with an ambiguity
  guard and a contradicting-counterparty veto, many-to-one and one-to-many by bounded subset-sum; passes repeat until
  nothing new matches; every match carries rule, score, amount difference and explanation.
- Seeded synthetic generator with ground truth (mangled and truncated references, charges, bulk payments,
  instalments, duplicates, unexpected receipts, colliding round amounts) and pair-level evaluation.
- ASP.NET Core API on PostgreSQL 17: idempotent statement import (SHA-256 and statement identity) streaming into a
  binary `COPY`, ledger item import, reconciliation runs per account under an advisory lock, work queue with
  `SKIP LOCKED` claims, leases, heartbeats and fencing tokens, manual matches and write-offs; least-privilege roles,
  API key, health checks, structured logs, OpenTelemetry traces and metrics.
- `docker compose up` demo with two synthetic accounts (MT940 and camt.053) seeded and reconciled.
- Unit, golden, FsCheck round-trip and matcher property, mutation fuzz and Testcontainers integration tests.
- Lab: matcher precision/recall per configuration and format, parse throughput (streaming vs buffered), matching
  throughput, import, run and queue drain on PostgreSQL.
- ADRs 0001-0004, threat model, benchmarks with measured results.
- CI: build, NuGet vulnerability audit, format check, tests with coverage, lab smoke run, compose demo; CodeQL.

[Unreleased]: https://github.com/hekimcan206/bank-statement-recon/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/hekimcan206/bank-statement-recon/releases/tag/v1.0.0
