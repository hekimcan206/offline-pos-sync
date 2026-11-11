# 0004: Partial sync by store scope and entity subscription; authority per entity type

- Status: accepted
- Date: 2026-11-11

## Context

A chain has many restaurants. A terminal in one restaurant must never receive another restaurant's tickets. Some of
that data is commercially sensitive, and all of it costs bandwidth on a weak uplink. Within a restaurant, devices
need different things: a front-of-house terminal needs tickets, lines and the menu, while a kitchen display needs
tickets and lines only. Reference data (the menu, prices) is authored by head office, and a terminal must not be
able to change a price for everyone.

## Options considered

1. **Full replication, filter in the UI.** Simple, but it leaks data and wastes bandwidth. Rejected.
2. **Arbitrary per-device queries** (sync rules / shapes defined by queries, as offered by PowerSync's sync rules or
   Couchbase Sync Gateway channels). Very flexible, but every query becomes a cursor, and a change that moves *into*
   a device's query needs special handling. That is more machinery than a restaurant scope needs.
3. **Fixed scope: the device's store, plus an entity-type subscription** (chosen). The scope is part of device
   registration and comes from the token, never from the request.

## Decision

- Every change carries a `store_id`. The server stores entities under `(store_id, entity, entity_id)`, so an id
  collision across stores can never touch another store's row.
- A device is registered to exactly one store. Pull returns only that store's log, optionally filtered by entity type
  (`entities=ticket,ticket_line` for a kitchen display).
- Push enforces scope per change: `store_id` must be the device's store and `node_id` must be the device itself
  (`forbidden_scope` otherwise). The node id is the tie-break in every LWW comparison and the audit trail of who
  wrote what, so it must not be forgeable.
- Entity types have an authority flag. `menu_item` is not device-writable: devices get `read_only_entity`, and only
  the server authors menu changes (admin endpoint), through the same log and merge.

## Consequences

- Tests: a store-002 terminal never receives store-001 data (in memory, against PostgreSQL, and in the compose demo,
  where the simulator checks GARDEN's database); a kitchen subscription receives tickets but not the menu; a device
  replaying another store's change is refused; a device-authored menu change is refused.
- The entity filter is applied when the page is read, and the cursor still advances over filtered-out rows, so a
  kitchen display does not re-scan menu changes. A device that later widens its subscription must reset its cursor
  to 0 to receive the history it skipped. That is documented in the README and not automated.
- Finer scopes (only today's tickets, only one section) would need a horizon or query-shaped cursors, as in option 2.
  Left for later.
