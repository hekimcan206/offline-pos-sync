# Benchmarks

Every number here was measured on the machine described below, on 2026-11-11. Nothing is estimated or rounded up.
The raw samples are in [`benchmark-results/`](../benchmark-results/) (one JSON file per scenario, with the environment
string recorded at run time).

## Environment

| | |
|---|---|
| CPU | Intel Core i7-13620H (10 cores / 16 logical CPUs), laptop |
| RAM | 15.7 GB. The Docker Desktop VM has 7.6 GB, shared with other projects |
| OS | Windows 11 (10.0.26200), x64 |
| .NET | SDK 10.0.401, runtime 10.0.12, Release builds |
| Docker | Docker Desktop 29.9.1 |
| Server side | the compose stack: PostgreSQL 17-alpine, the sync server (chiseled image), Toxiproxy 2.12.0, all in Docker |
| Client side | the benchmark harness on the Windows host, talking to the server through Docker's port forwarding (and through Toxiproxy for convergence) |
| Host load | 5 running containers at measurement time, all from this stack (recorded by the harness) |

**Method.** Each end-to-end scenario runs one warm-up round that is discarded, then `RUNS=5` measured rounds. Every
round uses fresh stores, so earlier rounds' data never inflates a later round's work. Tables report the median, with
min-max to show the spread. This is a laptop with Docker Desktop port forwarding in the
path, so expect run-to-run variation of ±20-30 %. The medians are the numbers to compare.

## 1. Sync throughput

Each terminal rings up 200 tickets (5 changes each: open, two lines, one "+1", send to kitchen), so 1 000 changes per
terminal sit in the outbox. Then every terminal syncs at the same time. Push batches are 100 changes. The pull after
the push is filtered to the menu, which is empty, so the measured time is the push. For the pull, one fresh terminal
per store downloads that store's entire log in pages of 500.

Device stores are SQLite **in memory** for this scenario, so the number is the sync path (HTTP + JSON + server +
PostgreSQL). The terminal disk cost is measured separately in section 4.

| Scenario | Changes | Push median (changes/s) | Push min-max | Pull median (changes/s) | Pull min-max |
|---|---:|---:|---:|---:|---:|
| 1 terminal | 1 000 | **5 231** | 4 905-6 598 | **12 536** | 10 771-20 123 |
| 4 terminals, 4 stores | 4 000 | **18 007** | 16 451-20 694 | **57 045** | 45 236-57 142 |
| 4 terminals, 1 store (contended) | 4 000 | **7 051** | 4 458-7 364 | **22 848** | 14 975-26 962 |
| 16 terminals, 16 stores | 16 000 | **26 999** | 18 002-29 487 | **65 284** | 52 660-87 797 |

Reading it:

- One push of 100 changes is one transaction of five statements. At about 5 200 changes/s, a terminal drains a
  backlog of 1 000 changes in about 0.2 s.
- **Contention is per store, by design** (ADR 0003). Four terminals in one store reach 7 051 changes/s, against
  18 007 when the same four are in four stores, because pushes to one store serialise on its row lock. A busy
  restaurant produces a few changes per second, three orders of magnitude below that ceiling.
- Different stores scale with clients until the single PostgreSQL instance and the laptop's CPUs saturate: 16 stores
  reach about 27 000 changes/s.

## 2. Convergence time after a partition heals

Three terminals in one store run the production sync loop (250 ms interval when online, backoff with full jitter
capped at 2 s when offline, 2 s request timeout) through Toxiproxy. The proxy is disabled. Each terminal rings up the
backlog. The run waits 3 s so every loop is in backoff, then re-enables the proxy. The clock starts when the proxy is
re-enabled and stops when every terminal has an empty outbox and a state hash equal to the server's (polled every
20 ms).

| Backlog per terminal | After the heal | Median ms | Min ms | Max ms |
|---|---|---:|---:|---:|
| 20 tickets (100 changes) | wait for the next backoff retry | **1 527** | 1 181 | 1 745 |
| 200 tickets (1 000 changes) | wait for the next backoff retry | **1 492** | 722 | 1 865 |
| 20 tickets (100 changes) | retry kicked on reconnect | **314** | 313 | 349 |
| 200 tickets (1 000 changes) | retry kicked on reconnect | **493** | 458 | 554 |

Reading it:

- With plain backoff, convergence time is dominated by **when the terminals next retry** (uniform jitter in
  [0, 2 s]), not by the amount of data. That is why 100 and 1 000 changes take about the same median time.
- "Kicked" models a terminal reacting to the operating system's connectivity-change event by retrying immediately.
  What remains is the work itself plus one poll interval. Each terminal's first round pushes its own backlog. Changes
  that other terminals push afterwards arrive at that terminal's next round, 250 ms later. Ten times the backlog
  (3 000 vs 300 changes in total) adds about 180 ms.
- The compose demo reports its own convergence time on every run (1 026 ms in the run shown in the README). Its
  terminals keep the default backoff.

## 3. Payload size per change

These are real changes: GUID ids, packed HLC, field operations as the terminal writes them. They are serialised
exactly as the transport sends them. The transport gzips request bodies above 1 KiB at `CompressionLevel.Fastest`,
and the server gzips JSON responses.

| Batch size | Raw JSON bytes/change | gzip Fastest bytes/change | gzip Optimal bytes/change |
|---:|---:|---:|---:|
| 1 | 421.0 | 316.0 | 263.0 |
| 10 | 346.3 | 116.3 | 81.8 |
| 100 | 342.5 | **77.1** | 47.8 |
| 500 | 342.2 | 73.1 | 44.8 |

| Change | Raw JSON bytes |
|---|---:|
| open ticket (5 fields) | 378 |
| add line (6 fields) | 447 |
| qty +1 | 216 |
| status change | 217 |

About 160 of the ~216 bytes of a one-field change are fixed overhead: two GUIDs, the store, entity and node names,
and the property names. Batching plus gzip removes most of it, at 77 bytes per change on the wire at the default
batch of 100. A binary encoding or dictionary-coded ids would shrink the raw form further. That was not needed at
these volumes.

## 4. Terminal write latency (durability)

How long a cashier's action takes to become durable on the terminal. Each change is one SQLite transaction in WAL
mode with `synchronous=FULL` (fsync), on the laptop's internal disk under Windows. That is 500 single changes and 200
whole tickets, after a 50-ticket warm-up.

| Local write | p50 ms | p95 ms | p99 ms |
|---|---:|---:|---:|
| one change (fsync) | **1.28** | 1.95 | 2.77 |
| ticket = 5 changes | **6.47** | 11.63 | 17.79 |

Terminal-class eMMC or SD storage is typically slower than a laptop disk. That was not measured here.

## 5. Micro-benchmarks (BenchmarkDotNet 0.15.8)

`warmupCount=3, iterationCount=10`, .NET 10.0.12, x86-64-v3.

| Method | Mean | Allocated |
|---|---:|---:|
| `HybridLogicalClock.Now()` (lock + clock read) | 39.30 ns | 0 B |
| merge a 2-field ticket change (older stamp loses, compare path) | 38.48 ns | 32 B |
| merge a counter `+1` into a new line state | 55.54 ns | 408 B |
| validate a change against the schema | 55.28 ns | 208 B |
| serialise a change to JSON | 413.95 ns | 608 B |
| serialise an entity state (canonical JSON) | 928.67 ns | 5 864 B |
| deserialise an entity state | 2 450.35 ns | 1 576 B |

The merge itself is negligible. Per change, the CPU cost on the server is dominated by JSON (de)serialisation of
entity state, and wall time by the PostgreSQL round trips. Storing state as a binary blob would be the first
optimisation if the server ever became CPU-bound.

## Reproduce

```bash
docker compose up -d --build postgres migrator seed server toxiproxy
dotnet run -c Release --project benchmarks/PosSync.Benchmarks -- all       # payload, latency, throughput, convergence
dotnet run -c Release --project benchmarks/PosSync.Benchmarks -- micro     # BenchmarkDotNet
docker compose down -v
```

Environment variables: `SERVER_URL`, `UPLINK_URL`, `TOXIPROXY_URL`, `ADMIN_KEY`, `RUNS` (default 5), `OUT_DIR`
(default `benchmark-results`).
