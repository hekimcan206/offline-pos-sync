# Contributing

Thanks for taking the time to improve offline-pos-sync.

## Prerequisites

- .NET SDK 10.0.400 or later (`global.json` rolls forward to the latest feature band)
- Docker (integration tests start PostgreSQL and Toxiproxy through Testcontainers; `docker compose up` runs the demo)

## Workflow

1. Open an issue first for anything larger than a small fix, so we can agree on the approach.
2. Branch from `main` and keep each pull request to one concern.
3. Before pushing:

   ```bash
   dotnet build                 # must be warning-free: warnings are errors
   dotnet test                  # unit, property and integration tests (Docker running)
   dotnet format --verify-no-changes
   ```

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/) are required. They keep the history readable, make the
hand-written CHANGELOG easy to assemble at release time, and show which version bump a change needs:

- `feat:` new capability (minor bump)
- `fix:` bug fix (patch bump)
- `feat!:` or a `BREAKING CHANGE:` footer (major bump)
- `docs:`, `test:`, `build:`, `ci:`, `refactor:`, `perf:`, `chore:` for everything else

## Changing merge behaviour

Merge rules are the contract between every replica. A change to `MergeEngine`, `SyncSchema` or `TicketOps`:

1. needs a new row (or an updated outcome) in `tests/PosSync.UnitTests/Conflicts/ConflictScenarios.cs`;
2. regenerates the documented table: `UPDATE_GOLDEN=1 dotnet test --project tests/PosSync.UnitTests`, then review the
   diff of `docs/conflict-scenarios.md` like code;
3. bumps `SyncSchema.CurrentVersion` if replicas on the old and new code would merge differently. The server refuses
   pushes from other schema versions, so terminals must be updated before or with the server;
4. keeps the convergence properties green. If `ConvergencePropertyTests` fails, the new rule is order-dependent.

## Architecture decisions

A change to a decision recorded in `docs/adr/` needs a new ADR that supersedes the old one. Do not edit the old ADR in
place. Use the existing files as the format (MADR: context, options considered, decision, consequences).

## Database migrations

Migrations are numbered SQL files in `src/PosSync.Server/Persistence/Migrations/` (`NNNN_name.sql`). They are embedded
in the assembly and applied by `--migrate-only`. Never edit a migration that has been applied anywhere: the runner
compares checksums and refuses to start. Add a new, higher-numbered file instead.
