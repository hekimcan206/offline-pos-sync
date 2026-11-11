# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.0.0] - 2026-11-11

### Added

- Hybrid logical clock (Kulkarni et al.) packed into a sortable 64-bit value, with a forward-drift guard, counter
  overflow handling and restart-safe persistence.
- Change model of uniquely identified field operations stamped with `(HLC, node id)`, and a versioned replicated schema
  for tickets, ticket lines and menu items.
- One merge engine with three strategies: whole-row LWW, field-level LWW, and domain rules (counters for quantities,
  a forward-only ranked status where paid outranks voided, forward-only line voids).
- Ticket read model with derived review flags: `void_overridden_by_payment`, `line_added_after_close`,
  `duplicate_open_table`.
- Device store on SQLite (WAL, `synchronous=FULL`): merged state, applied change ids and the outbox are written in one
  transaction. Sync client with batched push, cursor pull, partial-sync subscriptions, and a sync loop with capped
  exponential backoff and full jitter.
- PostgreSQL sync server: idempotent set-based push (store row as sequencer, `ON CONFLICT (change_id) DO NOTHING`,
  body-hash check of replays), single-snapshot cursor pull, per-device tokens stored as SHA-256, scope and clock-drift
  enforcement, server-authored menu changes, append-only change log, least-privilege roles, gzip in both directions,
  OpenTelemetry, health probes, OpenAPI + Scalar.
- Conflict catalogue of 17 scenarios, each executed on three replicas under two delivery orders; the table in
  `docs/conflict-scenarios.md` is generated from it and golden-tested.
- FsCheck properties for HLC monotonicity, causality and bounded drift; merge convergence under any order and
  duplication; and end-to-end convergence, zero loss and exactly-once storage of the real device stack under random
  schedules with link failures, lost acknowledgements and clock skew.
- Integration tests against PostgreSQL and Toxiproxy (Testcontainers): partition, blackhole, cut response after
  commit, latency beyond the client timeout, a flapping link with connection resets, and request reordering.
- Console restaurant simulator and a `docker compose up` demo (PostgreSQL, migrator, seed, server, Toxiproxy,
  simulator) that sells through a partition and a flapping link, then verifies convergence and zero loss.
- Benchmarks: sync throughput, convergence time after a partition heals, payload bytes per change, terminal write
  latency, and BenchmarkDotNet micro-benchmarks.
- ADRs 0001-0006, threat model, benchmark report, CI (build, vulnerable-package audit, format, tests with coverage,
  container images, compose demo) and CodeQL.

[Unreleased]: https://github.com/hekimcan206/offline-pos-sync/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/hekimcan206/offline-pos-sync/releases/tag/v1.0.0
