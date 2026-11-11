# 0001: Order writes with hybrid logical clocks, refuse clocks from the future

- Status: accepted
- Date: 2026-11-11

## Context

Every conflict rule that says "the later write wins" needs a definition of *later* that every replica computes the
same way, without talking to each other. Terminals in a restaurant are cheap tablets and fixed POS boxes. Their wall
clocks drift, get set by hand, jump when NTP comes back after an outage, and reset when a real-time-clock battery
dies. Some of them are offline for hours. The server cannot be asked "what time is it" at the moment a cashier rings
up an order, because the reason this system exists is that the server is sometimes unreachable.

Two properties matter:

1. **Causality.** If terminal B saw terminal A's edit before making its own, B's edit must win, even if B's clock is
   behind A's. Otherwise a manager who corrects a note after syncing silently loses the correction.
2. **Closeness to real time.** Timestamps end up on receipts, reports and audit trails. A counter that says "event
   48 213" is useless to a person reading the log of a disputed bill.

## Options considered

1. **Wall-clock timestamps (Unix ms) + node id tie-break.** Simple and human-readable, but it breaks causality as soon
   as clocks disagree: a terminal that is 10 minutes slow loses every edit to a terminal that is right, even edits it
   made *after* seeing the other one. Conflict scenario S05 is that case.
2. **Lamport clocks** (Lamport, 1978). Causality holds, but the value has no relation to physical time, so it cannot
   serve as a timestamp for people. A node that was offline for a day comes back with small counters and loses to
   everything, which is right for causality but surprising for "which edit happened later today".
3. **Vector clocks / version vectors.** They detect concurrency exactly (they can say "these two edits are
   concurrent" rather than picking one). The cost is a vector per write that grows with the number of nodes that ever
   wrote, and garbage collection when terminals are replaced. The merge rules in [ADR 0002](0002-merge-strategies-field-registers-and-domain-lattices.md)
   resolve concurrency with domain lattices and a total order, not by keeping siblings, so exact concurrency detection
   would be paid for and not used.
4. **Hybrid logical clocks** (Kulkarni, Demirbas et al., OPODIS 2014) (chosen). A timestamp is `(l, c)`. `l` is the
   largest physical time the node has seen, its own or in a message it received. `c` is a counter that orders events
   sharing the same `l`. HLC keeps the "happened before implies smaller" property of Lamport clocks, and `l` stays
   within the clock uncertainty of physical time, so it still reads as a timestamp.

## Decision

- `HybridLogicalClock` implements the paper's send/receive rules. A timestamp packs into one sortable 64-bit integer:
  48 bits of milliseconds, 16 bits of counter. The same integer is used on the wire, in SQLite and in PostgreSQL.
- The total order used by every merge is `(HLC, node id)`, ordinal on the node id. Two nodes can issue the same HLC.
  The node id makes the order total, so all replicas choose the same winner (scenario S04).
- **Forward-drift guard.** The server refuses a change stamped more than `Sync:MaxClockDrift` (default 5 min) ahead of
  its own physical clock (`clock_skew`). Without this guard, one terminal whose clock says 2099 would pull every
  node's HLC to 2099 and win every last-writer-wins comparison from then on. Terminals accept server timestamps with a
  generous bound (1 day): a terminal with a slow clock must catch up to correct time, not reject it.
- **Restart safety.** Each local write persists the HLC in the same SQLite transaction. On start the clock resumes from
  that value, so a wall clock that went backwards while the terminal was off cannot make it reissue old timestamps.
- **Counter overflow.** When the 16-bit counter would overflow, the logical millisecond advances instead. That needs
  65 536 events in one millisecond with a stalled physical clock. The clock then runs a few ms ahead, which the
  paper's bound already allows.

## Consequences

- Property tests (`HlcPropertyTests`) check over arbitrary event sequences, including backward clock steps of up to
  2 minutes, that timestamps are strictly increasing, that a receive event is later than the message received, and
  that the wall component never exceeds the largest physical or remote time the node has seen.
- A terminal whose clock is more than 5 minutes fast gets its changes rejected. They stay in its outbox as rejected,
  visible, never deleted (`Future_stamped_changes_are_rejected_and_kept_on_the_device`). Fixing the clock does not
  re-stamp them. Re-issuing a rejected change with a fresh stamp is an operator action, and it is listed as a
  limitation in the README.
- HLC does not detect concurrency. Two edits that happened unaware of each other are ordered by stamp anyway. Where
  that would lose data (counters, state machines), the merge does not use "later wins" at all (ADR 0002).

## References

- S. Kulkarni, M. Demirbas, D. Madappa, B. Avva, M. Leone. *Logical Physical Clocks.* OPODIS 2014, LNCS 8878.
  doi:10.1007/978-3-319-14472-6_2. Technical report: <https://cse.buffalo.edu/~demirbas/publications/hlc.pdf>
- L. Lamport. *Time, Clocks, and the Ordering of Events in a Distributed System.* CACM 21(7), 1978.
