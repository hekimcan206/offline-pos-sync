# 0002: Merge with per-field registers plus domain lattices, not whole-row LWW and not a general CRDT library

- Status: accepted
- Date: 2026-11-11

## Context

Two terminals edit the same ticket while neither can reach the server. When they reconnect, every replica (the server
and every terminal) must end with the same ticket. That is convergence. The result must also be what a restaurant
manager would call correct: no lost order lines, no lost quantity taps, and no paid sale that silently disappears.

The data is small and structured: tickets, lines and menu items with a dozen fields. Text is short (notes, table
numbers). Nothing needs character-level collaborative editing.

## Options considered

1. **Whole-row last writer wins.** The row image with the highest stamp replaces the row. It is trivial to implement
   and to explain, and it is what many sync layers do by default. It loses concurrent edits to *different* fields
   (scenario S01: a table move is lost because someone else added a note).
2. **Per-field last writer wins (field-level merge).** Each field is its own LWW register. Edits to different fields
   both survive (S02). It still loses data when the field's meaning is not "latest value": two "+1" taps become
   "2, 2" → 2 (S06), and a later void erases a payment (S09).
3. **Keep siblings and let the application resolve** (multi-value registers, Couchbase-style conflict revisions, or a
   rebase of pending client mutations as in Replicache). Nothing is lost silently, but each conflict becomes a UI
   problem. A cashier cannot be asked to resolve "two versions of table 12" at the counter on a Friday night.
4. **A general-purpose CRDT library (Automerge, Yjs).** Proven convergence for rich documents. It brings a document
   model, a binary format and history growth that this data does not need, and it does not encode the domain rules
   (paid outranks voided) by itself. Those would still have to be written.
5. **Per-field registers plus domain lattices, chosen per field in a versioned schema** (chosen).

## Decision

Three strategies share one `MergeEngine`, chosen per entity type in `SyncSchema` (the same schema version on every
replica):

| Strategy | Used for | Rule |
|---|---|---|
| `RowLww` | kept for comparison only | highest `(HLC, node)` row image wins, fields it does not mention are dropped |
| `FieldLww` | menu items (single writer: head office) | each field is a LWW register |
| `DomainRules` | tickets, ticket lines | per field: **Register** (LWW), **Counter** (sum of uniquely identified deltas), **Ranked** (highest rank wins whatever the clocks say) |

Ticket rules expressed with those kinds:

- `status` is Ranked `open < sent < voided < paid`. It is a forward-only state machine, so a stale write cannot reopen
  a ticket (S11). Paid outranks voided on purpose: money was taken, and a concurrent void must not erase the sale. The
  void is recorded in `void_requested` (Ranked `false < true`) and the ticket is flagged for review (S10). For an
  equal rank the *earliest* stamp is kept, which records when the ticket was first closed.
- Line `qty` is a Counter. A tap is an `add +1` operation with its own change id, so concurrent taps all count (S07).
  Replicas apply each change id exactly once (ADR 0003), which keeps the sum safe under retries (S16).
- Lines are separate entities with device-generated ids, so concurrent additions never collide (add-wins, S08). A
  void is Ranked and forward-only (S13). Prices are copied into the line when it is rung up and never re-read from
  the menu (S15).
- **Review flags are derived, not replicated.** `void_overridden_by_payment`, `line_added_after_close` (S12) and
  `duplicate_open_table` (S14) are computed by `TicketProjection` from converged state. A flag that is a pure
  function of converged state is identical on every replica and cannot itself be the subject of a conflict.

Every rule is a join on a semilattice: max by stamp, max by rank (min stamp within a rank), or a sum over a set of
unique deltas. State is therefore a function of the *set* of applied changes, and any delivery order or duplication
converges. That is the strong eventual consistency argument from the CRDT literature (Shapiro et al.), applied to a
fixed schema instead of a generic document.

## Consequences

- The conflict table ([docs/conflict-scenarios.md](../conflict-scenarios.md)) is generated from the same catalogue
  the tests execute. Each of the 17 rows runs on three replicas under in-order and reverse delivery and must
  converge byte for byte.
- FsCheck checks convergence for all three strategies over random change sets, delivery orders and duplicates, with
  cross-node HLC ties forced to occur. A deliberately broken merge (ties resolved by arrival order) fails these
  properties and four conflict scenarios.
- The ranked status cannot express "reopen a paid ticket". In this design that is a new ticket or a refund, both
  separate entities. A business that needs reopening would need a different lattice (for example a version counter
  in the rank), and that would need a new ADR.
- Adding a field kind is a schema version bump. Replicas on different schema versions are refused (409) rather than
  allowed to diverge.

## References

- M. Shapiro, N. Preguiça, C. Baquero, M. Zawirski. *Conflict-free Replicated Data Types.* SSS 2011.
- N. Preguiça, C. Baquero, M. Shapiro. *Conflict-free Replicated Data Types (CRDTs).* Encyclopedia of Big Data
  Technologies, 2018. arXiv:1805.06358. LWW is itself a simple state-based CRDT; the paper discusses LWW versus
  multi-value semantics.
- Couchbase Lite, *Handling Data Conflicts*: automatic resolution (the most recent change wins, deletions win) or a
  custom resolver: <https://docs.couchbase.com/couchbase-lite/current/c/conflict.html>
- Replicache and PowerSync rely on server-authoritative writes, where the client rebases or re-applies pending
  mutations over canonical server state. The comparison is in [ADR 0003](0003-sync-protocol-op-log-outbox-and-store-sequencer.md).
