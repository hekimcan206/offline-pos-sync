# Threat model

Scope: the sync server, the device-side store and the protocol between them. The demo stack (compose, Toxiproxy,
simulator) is local-only and out of scope, except where its defaults could be copied into production.

All data in this repository is synthetic.

## Assets

| Asset | Why it matters |
|---|---|
| Tickets and lines (what was ordered, quantities, prices) | Revenue; the basis for the bill and for kitchen work |
| Ticket status (paid / voided) | Cash reconciliation; a silent void of a paid ticket is a classic fraud pattern |
| Menu and prices | One bad write changes the price on every terminal of a store |
| Change log (`sync.changes`) | Audit trail: who (node id) wrote what, and when (HLC) |
| Device tokens | Whoever holds one can write tickets into that store |
| Admin key | Registers devices, edits menus, reads every store |

## Actors

- **Cashier / staff with a terminal**: legitimate writes. A dishonest employee may try to void a paid ticket or hide
  a sale.
- **Stolen or decommissioned tablet**: holds a valid token and a SQLite file with the store's recent tickets.
- **Misconfigured terminal**: a wrong clock, an old app version, a bug that resends garbage.
- **Network attacker on the restaurant LAN or Wi-Fi**: observes or alters traffic.
- **Another store's device**: valid credentials, wrong scope.

## Threats and mitigations (STRIDE)

| # | Threat | Mitigation in this repository | Residual risk / next step |
|---|---|---|---|
| S1 | **Spoofing** a device | Per-device random 256-bit bearer token, shown once at registration. Only its SHA-256 is stored, and lookup is by hash (no secret comparison in code). Tokens can be revoked (`revoked_at`) and rotated by re-registering | No mTLS or device attestation. A copied token works from any machine until revoked |
| S2 | Spoofing another **node id** inside a valid session (to win LWW tie-breaks or blame someone else) | The server rejects any change whose `node_id` or `store_id` differs from the authenticated device (`forbidden_scope`) | None known |
| T1 | **Tampering** in transit | TLS is expected in production (terminate at the ingress). The compose demo uses plain HTTP on loopback / a private Docker network | Not enforced by the server; deployment responsibility |
| T2 | Replaying a stored change id with **altered content** | Unique `change_id` plus a stored SHA-256 of the canonical body. A different body under a known id is rejected as `conflicting_duplicate` and never merged | None known |
| T3 | **Voiding a paid ticket** from a second terminal | Paid outranks voided in the status lattice. The void is kept as `void_requested` and the ticket is flagged `void_overridden_by_payment` (scenario S10) | Authorisation of *who* may void (manager PIN) is application policy, not implemented |
| T4 | **Clock poisoning**: a terminal with a clock far in the future wins every LWW comparison forever | The server refuses changes stamped more than 5 min ahead of its clock (`clock_skew`). The device keeps them as rejected, nothing is deleted | Changes up to 5 min ahead are accepted and can win LWW against honest edits made in that window |
| T5 | Editing the **menu/prices** from a terminal | `menu_item` is not device-writable (`read_only_entity`). Only the admin endpoint authors menu changes | Admin key is a single shared secret (see E1) |
| R1 | **Repudiation**: "I never voided that" | Append-only change log with node id and HLC per change. A trigger forbids UPDATE/DELETE even for the schema owner (`23001`), and the app role lacks UPDATE/DELETE privileges (`42501`). Both are tested | A DBA with superuser can still alter history. Signed or hash-chained logs are out of scope |
| I1 | **Cross-store disclosure** | Pull is scoped to the token's store. Entities are keyed by `(store_id, entity, entity_id)`. Tested in memory, against PostgreSQL and in the compose demo | — |
| I2 | **Lost/stolen tablet** exposes recent tickets | Tickets contain no card data or customer identity in this model. Revoke the token | SQLite is not encrypted at rest (SQLCipher or the OS keystore is the next step). Token stored in device config |
| I3 | Health/error endpoints leak internals | Health responses omit exception details. Errors are RFC 9457 problem details with a trace id, no stack traces | — |
| D1 | **Oversized pushes** / gzip bombs | Kestrel body limit of 2 MiB, enforced on the *decompressed* stream by the request decompression middleware. At most 500 changes per push, 32 ops per change, per-field length limits | No per-device rate limiting (add a token-bucket limiter at the ingress or in-process) |
| D2 | **Hot-store lock contention** (one store's pushes serialise on its row) | `lock_timeout = 5 s`, then a retryable 503. Devices back off with full jitter. Other stores are unaffected | A malicious device in one store can slow that store's sync. Rate limiting (D1) |
| D3 | Unbounded growth of the change log and the device `applied_changes` set | — | Compaction below a horizon all devices have passed. Documented as a limitation |
| E1 | **Admin key** compromise | Separate from device tokens; constant-time comparison; endpoints disabled when no key is configured; demo key is `*-dev-only` and lives in `.env.example` | A single shared key with no roles. Use an identity provider (OIDC) with per-operator accounts in production |
| E2 | Database privilege escalation via the app | The server connects as `sync_app` (DML only, no DDL, no UPDATE/DELETE on the log, no access to migration history). Migrations run as `sync_migrator` in a one-shot container. All SQL is parameterised | — |
| E3 | Toxiproxy admin API (can cut the network) exposed | Bound to `127.0.0.1` in compose, only part of the demo | Never deploy Toxiproxy in production paths |

## Privacy note

The model holds no personal data: no customer names, no card numbers, no phone numbers. `opened_by` is a terminal id,
not a person. If a deployment adds staff ids or customer details, KVKK (the Turkish Personal Data Protection Law,
No. 6698) and GDPR obligations apply. That would mean a data inventory, retention limits for the change log
(currently forever, see D3), and encryption at rest on devices (I2).
