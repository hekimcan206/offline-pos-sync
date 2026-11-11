# Security policy

## Supported versions

Only the latest release receives fixes.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private vulnerability reporting instead:
**Security → Report a vulnerability** on this repository. You can expect an acknowledgement within 5 working days. If
the report is confirmed, a fix or mitigation plan follows within 30 days.

Please include the affected version or commit, steps to reproduce and the impact you observed.

## Security-relevant design

See [docs/threat-model.md](docs/threat-model.md) for assets, threats and mitigations. In short:

| Area | Default |
|---|---|
| Device authentication | Per-device random bearer token. Only its SHA-256 is stored. Revocable, and rotated on re-registration |
| Write scope | A device writes only as its own node id, into its own store. Menu items are server-authored only |
| Replay / tampering | Unique change id plus a stored body hash: a replayed id with a different body is rejected |
| Clock poisoning | Changes stamped more than 5 minutes ahead of server time are rejected (`clock_skew`) and kept on the device |
| Audit trail | Append-only change log, enforced by a trigger and by privileges (the app role has no UPDATE/DELETE) |
| Database access | The server runs as `sync_app` (DML only). Migrations run as `sync_migrator` in a one-shot process |
| Input limits | 2 MiB body (also after gzip decompression), 500 changes per push, 32 ops per change, per-field length limits |
| Secrets | None in the repository. `.env.example` holds clearly marked development-only values |
