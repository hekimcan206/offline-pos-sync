# Conflict scenarios

<!-- Generated from tests/PosSync.UnitTests/Conflicts/ConflictScenarios.cs. Do not edit by hand:
     run the unit tests with UPDATE_GOLDEN=1 to regenerate. A golden test fails if this file drifts. -->

Every row is executed by `ConflictScenarioTests.Scenario_produces_the_documented_outcome` twice (changes
delivered in order, and in reverse order) on three replicas (server, T1, T2). The test also asserts that all
replicas end with byte-identical state. "Concurrent" means neither terminal had synced the other's edit.

Strategies: **RowLww** = whole-row last writer wins; **FieldLww** = per-field last writer wins (field-level
merge); **DomainRules** = field-level merge plus counters and forward-only state machines (the default for
tickets and lines). Rows in bold show data loss a strategy causes, which is why tickets use DomainRules.

| # | Input | Strategy | Outcome on every replica |
|---|---|---|---|
| S01 | T1 moves the ticket from table 12 to 14; concurrently T2 (later HLC) adds the note "no onions" | RowLww | T2's row image wins: note kept, **table move lost** (still 12) |
| S02 | Same as S01 | FieldLww | Both edits kept: table 14 and note "no onions" |
| S03 | T1 sets note "allergy: nuts"; concurrently T2 (later HLC) sets note "birthday" | FieldLww | Higher HLC wins on every replica: "birthday" |
| S04 | T1 and T2 set different notes with identical HLCs (same millisecond, same counter) | FieldLww | Tie broken by node id (ordinal): T2's note wins everywhere, no replica disagrees |
| S05 | T2's wall clock is 10 min slow. T2 syncs, sees T1's note "A", then edits it to "B" | FieldLww | "B" wins: the HLC carries causality, so the later edit has the larger stamp even from a slow clock (wall-clock LWW would keep "A") |
| S06 | The soup line has qty 1. T1 and T2 each tap "+1" concurrently | FieldLww | qty = 2: both wrote the absolute value 2, **one increment lost** |
| S07 | Same as S06 | DomainRules | qty = 3: quantity is a counter of uniquely identified deltas, both increments count |
| S08 | T1 adds a lemonade and T2 adds a baklava to the same ticket concurrently | DomainRules | Both lines kept (lines are separate entities: add-wins), total = 90.00 + 35.00 + 70.00 |
| S09 | T1 takes payment; concurrently T2 (later HLC) voids the ticket | FieldLww | status = voided: **a paid sale silently disappears** |
| S10 | Same as S09 | DomainRules | status = paid (paid outranks voided whatever the clocks say); flag `void_overridden_by_payment` for the manager |
| S11 | T1 sends the ticket to the kitchen; T2, working from a stale copy, writes status "open" with a later HLC | DomainRules | status stays "sent": the status is a forward-only state machine (max rank), a stale write cannot move it back |
| S12 | T1 takes payment; T2, offline, adds a dessert to the same ticket afterwards (later HLC) | DomainRules | The dessert is **kept** and counted in the total, line marked added-after-close, ticket flagged `line_added_after_close` |
| S13 | T1 voids the soup line; concurrently T2 taps "+1" on it | DomainRules | Line stays voided (void is forward-only) and is excluded from the total; the increment is recorded but has no effect |
| S14 | Both terminals are offline and each opens a new ticket for table 7 | DomainRules | Two tickets kept (never merged: they may hold different orders), both flagged `duplicate_open_table` |
| S15 | Head office raises the menu price of the kebab from 450.00 to 520.00 while T1 is offline and rings one up | DomainRules | The line keeps the captured price 450.00 (prices are copied into the line, not referenced); the menu shows 520.00 everywhere |
| S16 | T1 taps "+1"; the server stores the push but the response is lost, so T1 pushes the same change again | DomainRules | Applied exactly once (deduplicated by change id): qty = 2, not 3 |
| S17 | Offline, T1 opens a ticket, adds a line, taps "+1" and takes payment; the server receives the four changes in reverse order | DomainRules | Same state as in-order delivery: paid, qty 2, total 180.00 (merges are commutative) |
