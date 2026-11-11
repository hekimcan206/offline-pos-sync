# 0005: Demo client is a multi-terminal console simulator, not an Expo app

- Status: accepted
- Date: 2026-11-11

## Context

The brief asks for "a small demo client you can actually show": either a React Native/Expo app or a console simulator
with several terminals. The behaviour worth showing is not a screen. It is several terminals selling while the link
is cut, then flapping, then healed, with convergence and zero loss checked at the end. Whatever is chosen should also
run in CI, so the demo cannot quietly break.

## Options considered

1. **Expo app.** Visually convincing on a phone. But:
   - The sync engine is C# (shared `MergeEngine`, HLC, outbox). An Expo client needs either a TypeScript port of the
     merge rules, which is a second implementation that must stay byte-compatible with the server and needs its own
     property tests, or a native bridge to .NET, which is not a small demo.
   - Showing a *partition* means several devices or emulators plus a proxy that the phones route through. That is
     hard to reproduce for a reviewer and hard to run in CI.
   - A single app shows one terminal. The interesting part is three terminals disagreeing and converging.
2. **.NET MAUI.** The same language as the engine, but heavy tooling, and still one device per window. CI would need
   platform workloads. Rejected for the same reasons as Expo, plus build weight.
3. **Console simulator with several terminals in one process** (chosen). Each terminal is the production device stack:
   its own SQLite file, outbox, `SyncClient` and `SyncLoop`. All of them talk to the real server through a real
   Toxiproxy proxy that the simulator cuts and flaps.

## Decision

`samples/PosSync.Simulator` runs three front-of-house terminals in `store-001` and one in `store-002`, ringing up
synthetic orders through four phases:

1. online;
2. hard partition (the proxy is disabled), with scripted conflicts on one shared ticket: two concurrent `+1` taps,
   pay against void, and a line added after payment;
3. flapping link;
4. heal.

It prints a status line per second (link state, outbox depth, tickets per terminal, tickets on the server). At the end
it verifies convergence (every terminal hash equals the server hash), zero loss (every ticket id any terminal opened
is on the server), partial-sync isolation (the store-002 terminal holds no store-001 data) and the documented
conflict outcomes. It prints a `SUMMARY {...}` JSON line and exits non-zero on any failure.

## Consequences

- `docker compose up` runs it with zero setup, and the CI `compose-demo` job asserts its exit code and summary, so the
  demo is tested, not just documented.
- Terminal output is the visual proof (a block in the README). No screenshot pipeline is needed.
- A mobile client remains a valid next step. The protocol is plain JSON over HTTP and the merge rules are small. A
  TypeScript client would need the conflict catalogue ported as a shared golden test suite so both implementations
  are held to the same 17 outcomes.
