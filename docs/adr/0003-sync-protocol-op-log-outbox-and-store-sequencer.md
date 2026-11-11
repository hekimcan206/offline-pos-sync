# 0003: Device outbox, idempotent change log, and the store row as the sequencer

- Status: accepted
- Date: 2026-11-11

## Context

The promise is "a restaurant whose internet drops never loses a ticket". Spelled out:

1. A ticket is durable on the terminal the moment the cashier rings it up, whatever the network is doing.
2. Every change reaches the server eventually, and it is **applied exactly once** even though the network will lose
   requests, lose responses after the server committed, deliver retries concurrently, and reorder requests.
3. A terminal that pulls must never miss a change, including one that another terminal pushed at the same moment.

## Options considered

**Device side**

1. *Send immediately, queue on failure.* There is a window between the local write and the queue insert where a crash
   loses the ticket.
2. *Local state only, diff against the server when online.* Diffs lose intent: a counter tap cannot be reconstructed
   from two snapshots, and deletes need tombstones anyway.
3. *Transactional outbox* (chosen). The merged local state, the applied change id and the outbox row are written in
   one SQLite transaction (WAL, `synchronous=FULL`). PowerSync's client keeps a similar persistent FIFO upload queue
   in SQLite.

**What travels**

1. *Row snapshots.* Simple, but a snapshot cannot carry counter or state-machine semantics (ADR 0002).
2. *Operation log of uniquely identified changes* (chosen). A change is `(change id, store, entity, entity id, HLC,
   node, ops)`. The id is generated on the device when the change enters the outbox, so every retry of every push is
   the same change.

**Server ordering for pull cursors**

1. *A global `bigserial` sequence.* The classic trap: transaction A takes seq 10, B takes seq 11, B commits first, and a
   puller that sees 11 moves its cursor past 10. When A commits, 10 is never delivered.
2. *A snapshot watermark* (`pg_snapshot_xmin`, or a logical replication slot). Correct, but more moving parts, and the
   cursor stops being a plain number that a terminal can store.
3. *Lock the store row and allocate from its `head` inside the push transaction* (chosen). `SELECT head ... FOR UPDATE`
   serialises pushes per store. Sequence numbers are allocated while holding the lock, so within a store sequence
   order equals commit order. A pull reads the head and its page in a single statement (one snapshot). If it
   returns "no more rows", the cursor jumps to the head, and nothing can appear below the head later.

**Retries and duplicates**

1. *Check, then insert.* Two concurrent retries both see "absent" and both apply.
2. *Unique index on `change_id` as the arbiter* (chosen). `INSERT ... ON CONFLICT (change_id) DO NOTHING RETURNING`
   decides what is new. Only new changes are merged. A replay with the same id but a different body (bug or forgery)
   is detected by a stored SHA-256 of the canonical body and rejected as `conflicting_duplicate`.

**Server authority**

PowerSync and Replicache make the server authoritative: client writes are tentative, the backend may reject them,
and the client rebases. That suits apps where a central rule (stock, permissions) must veto writes. Here the
terminal must be able to sell with no server at all, so the server validates *structure and scope* (schema, store,
node id, clock drift). It does not veto business decisions. Business conflicts are resolved by the deterministic
merge (ADR 0002) on every replica, the server included.

## Decision

- Push: one transaction per request. Lock the store row, insert all valid changes with `unnest` + `WITH ORDINALITY`,
  check the hashes of replays, load the touched entity states, merge the fresh changes in sequence order with the
  shared `MergeEngine`, upsert the states, bump the head, commit. That is five round trips for a batch of 500.
- Responses list `accepted`, `duplicates` and `rejected` per change id. The device deletes accepted and duplicate
  rows from its outbox and keeps rejected rows, with the reason, as `rejected`.
- Pull: `since` (cursor) + `limit` + an optional entity filter, ordered by the per-store sequence. The device applies
  a page and advances its cursor in one SQLite transaction, and skips change ids it already holds (its own changes
  come back too).
- `lock_timeout` (5 s) bounds how long a second push to a busy store waits. A timed-out push returns a retryable 503,
  and the outbox retries it.

## Consequences

- Throughput per store is bounded by serial pushes. For one restaurant (a handful of terminals) that is far above
  demand. `docs/benchmarks.md` measures 1 store vs 4 contended terminals vs 16 stores. Different stores do not
  contend.
- `applied_changes` on the device and `sync.changes` on the server grow forever. Compaction (snapshot + truncate
  below a horizon that every device has passed) is listed as next work, not implemented.
- Tests: twelve concurrent first pushes of one change store it once; a lost response is replayed as duplicates
  (in-memory and through Toxiproxy `limit_data`); a pull cursor polled during six concurrent writers never skips a
  change; an older request that arrives after a newer one still merges in HLC order.

## References

- PowerSync: SQLite on the client with a persistent upload queue processed by a developer-defined `uploadData()`, and
  a server-authoritative write path:
  <https://powersync.com/blog/introducing-powersync-v1-0-postgres-sqlite-sync-layer>
- Replicache: the client records mutations, the server returns canonical state, and pending mutations are rebased on
  top of it (secondary description; the docs domain was not reachable from this environment).
- ElectricSQL: syncs from Postgres to the client. Descriptions of its write path disagree across versions and
  secondary sources (an earlier CRDT-based design, later read-path sync with application-owned writes), so it is not
  used as a model here.
