# Contributing

Thanks for taking the time to improve bank-statement-recon.

## Prerequisites

- .NET SDK 10.0.400 or later (`global.json` rolls forward to the latest feature band)
- Docker (integration tests start PostgreSQL through Testcontainers; `docker compose up` runs the demo)

## Workflow

1. Open an issue first for anything larger than a small fix, so we can agree on the approach.
2. Branch from `main` and keep each pull request to one concern.
3. Before pushing:

   ```bash
   dotnet build                 # must be warning-free: warnings are errors
   dotnet test                  # unit, property, golden, fuzz and integration tests (Docker running)
   dotnet format --verify-no-changes
   ```

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/) are required (`feat:`, `fix:`, `docs:`, `test:`,
`build:`, `ci:`, `refactor:`, `perf:`, `chore:`; `feat!:` or a `BREAKING CHANGE:` footer for breaking changes). They
keep the history readable and make the hand-written CHANGELOG easy to assemble.

## Changing a parser

1. Add or extend a sample in `samples/` (synthetic data only: invented names, IBANs with bank code 99901).
2. Regenerate the approved output: `UPDATE_GOLDEN=1 dotnet test --project tests/BankRecon.UnitTests`, then review the
   diff in `tests/BankRecon.UnitTests/Golden/` like code.
3. Keep the round-trip properties and the fuzz tests green: a parser may only ever throw `StatementFormatException`.

## Changing the matcher

Re-run the lab and update `docs/benchmarks.md` and ADR 0002 with the new numbers:

```bash
dotnet run -c Release --project benchmarks/BankRecon.Lab -- accuracy 20 2000
```

Tune on seeds 1-3 (`explain <style> <seed>` prints every wrong pair); report on the hold-out seeds 101-120. Never edit
a number by hand. If a change makes a case worse, say so in the pull request.

## Dependencies

Versions are pinned in `Directory.Packages.props` and updated by hand in reviewed commits. CI fails on any package
with a known vulnerability (`dotnet list package --vulnerable --include-transitive`).

## Architecture decisions

A change to a decision recorded in `docs/adr/` needs a new ADR that supersedes the old one (MADR format).
