# 0006: Prove the promise with property tests and real network faults

- Status: accepted
- Date: 2026-11-11

## Context

"Never loses a ticket" and "replicas converge under any interleaving" are claims about *all* executions. A handful of
example tests can only show that the authors' favourite scenarios work. The proof has to cover two layers: the
algorithm (clocks, merges, protocol state machine), which can be explored exhaustively in memory, and the real stack
(HTTP, Kestrel, Npgsql, PostgreSQL, SQLite), whose failure modes only appear on a real network.

## Options considered

1. **Mocked transport exceptions only.** Fast, but a mock throws where the author expects. It never produces a
   response cut after the server committed, a half-open connection that hangs, or a reset in the middle of a body.
2. **Jepsen-style cluster tests.** The reference for distributed-systems testing, but a Clojure control node plus SSH
   to several VMs is far beyond what this repository and its CI budget should carry.
3. **Two layers** (chosen):
   - FsCheck properties over the in-memory model, and over the *real device stack* (SQLite store, outbox, sync client)
     against an in-memory hub that implements the server semantics;
   - Toxiproxy between real terminals and the real server (PostgreSQL via Testcontainers) for TCP-level faults.

## Decision

**Properties (FsCheck, `PosSync.UnitTests`)**

- HLC: strictly increasing timestamps, receive > received, bounded wall component, under backward clock steps.
- Merge: for every strategy, replicas applying the same change set in any order, with duplicates, are byte-identical,
  and state survives a serialisation round-trip mid-stream.
- Protocol: arbitrary schedules of cashier actions, sync rounds, link toggles, lost acknowledgements and clock skew
  across three SQLite terminals. After healing, all terminals equal the server, every written change is on the
  server, and the server stored each exactly once.

**Faults (Toxiproxy 2.12, `PartitionTests`)**

| Fault | Toxiproxy mechanism | Asserted |
|---|---|---|
| Partition | proxy disabled (connections refused/closed) | 3 terminals sell 30 tickets offline; all 30 arrive, 90 changes stored once |
| Blackhole | `timeout` toxic with 0 (bytes swallowed, connection hangs) | client timeout fires, outbox retained with attempt count, recovers |
| Lost acknowledgement | `limit_data` downstream (response cut after 16 bytes) | server committed 5 changes; retry reports 5 duplicates; qty counted once |
| Latency | `latency` 2 s vs 800 ms client timeout; then 300 ± 100 ms each way | timed-out push is replayed as duplicates; slow link still converges |
| Flapping | proxy toggled every 100-500 ms for 6 s, random `reset_peer` | sync loops + writers concurrently; every ticket stored once, hashes equal |
| Reordering | second proxy with 1.5 s upstream latency | older request lands after newer one (checked by server seq); merge follows HLC |

**Reordering note.** TCP delivers bytes in order within a connection, and Toxiproxy works on a TCP stream, so it
cannot reorder packets inside one request. Reordering between *requests* is what an application sees in practice:
a retry on a degraded connection overtaken by a later request on a fresh one. The test produces exactly that with two
proxies to the same server, and asserts from the server's sequence numbers that the reorder really happened.

The integration test classes run serially (`xunit.runner.json`). The environment is warmed up (first requests through
the tunnel and a cold server) before any test. Toxiproxy reaches the host-run server through Testcontainers'
host-port tunnel, which behaves the same on Docker Desktop and on Linux CI.

## Consequences

- A merge bug fails the build. Sanity check: changing the register rule so that ties are resolved by arrival order
  makes three property tests and four conflict scenarios fail.
- Property tests use fixed iteration counts (60-1000). They are not a proof in the formal sense. The protocol property
  explores 60 schedules of up to 70 steps per run, with a new random seed each run.
- Timing-dependent tests (flapping) assert outcomes, not durations. Durations are measured in `docs/benchmarks.md`
  with medians, not asserted.

## References

- Toxiproxy (toxics: latency, timeout, limit_data, reset_peer, …): <https://github.com/Shopify/toxiproxy>
- FsCheck: <https://fscheck.github.io/FsCheck/>
