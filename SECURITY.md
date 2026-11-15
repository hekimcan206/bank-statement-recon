# Security policy

## Supported versions

Only the latest release receives fixes.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private vulnerability reporting instead:
**Security -> Report a vulnerability** on this repository. You can expect an acknowledgement within 5 working days. If
the report is confirmed, a fix or mitigation plan follows within 30 days.

Please include the affected version or commit, steps to reproduce and the impact you observed.

## Security-relevant design

See [docs/threat-model.md](docs/threat-model.md). In short:

| Area | Default |
|---|---|
| Untrusted files | Line, field, entry and request-size limits; DTDs prohibited and no XML resolver; only `StatementFormatException` escapes a parser (fuzz-tested) |
| Integrity | Unbalanced statements rejected; duplicate files and re-exported statements rejected; unique constraints make every bank line and ledger item part of at most one match |
| Concurrency | Queue writes fenced by lease token; fixed lock order between runs and manual resolutions |
| Database | Separate migrator and application roles; the application role cannot delete anything or rewrite imported facts |
| API | Shared `X-Api-Key` (constant-time comparison); put an identity provider in front for real use |
| Containers | Chiseled .NET image, non-root user, no shell; compose binds ports to 127.0.0.1 |
| Secrets | None in the repository; `.env.example` holds development-only values |
| Data | All data in this repository is synthetic |
