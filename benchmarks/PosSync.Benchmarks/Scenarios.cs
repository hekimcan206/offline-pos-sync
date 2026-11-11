using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using PosSync.Chaos;
using PosSync.Core.Model;
using PosSync.Core.Protocol;
using PosSync.Device;

namespace PosSync.Benchmarks;

internal static class Scenarios
{
    /// <summary>
    /// Bytes on the wire per change: real changes (GUID ids, packed HLC, realistic ops) serialised exactly as the
    /// transport does, raw and gzip, per batch size. Purely local and deterministic in size.
    /// </summary>
    public static async Task<object> PayloadAsync()
    {
        await using var store = await DeviceStore.OpenAsync(":memory:", new DeviceIdentity("BENCH-T1", "store-bench"), SyncSchema.Default, TimeProvider.System);
        var pos = new PosTerminal(store);
        for (var i = 0; i < 100; i++)
        {
            await Harness.RingUpAsync(pos, i);
        }

        var changes = (await store.PeekOutboxAsync(1000)).Select(o => o.Change).ToList();
        var rows = new List<object>();
        Console.WriteLine("| batch | raw bytes/change | gzip-fastest bytes/change | gzip-optimal bytes/change |");
        Console.WriteLine("|---:|---:|---:|---:|");
        foreach (var batch in new[] { 1, 10, 100, 500 })
        {
            var request = new PushRequest("BENCH-T1", SyncSchema.CurrentVersion, [.. changes.Take(batch)]);
            var raw = JsonSerializer.SerializeToUtf8Bytes(request, SyncJson.Options);
            var fast = Gzip(raw, CompressionLevel.Fastest);
            var best = Gzip(raw, CompressionLevel.Optimal);
            var n = request.Changes.Count;
            rows.Add(new { batch = n, raw = raw.Length / (double)n, gzipFastest = fast / (double)n, gzipOptimal = best / (double)n });
            Console.WriteLine($"| {n} | {raw.Length / (double)n:0.0} | {fast / (double)n:0.0} | {best / (double)n:0.0} |");
        }

        var byOp = changes
            .GroupBy(c => $"{c.Entity}:{string.Join('+', c.Ops.Select(o => o.Field))}")
            .Select(g => new { kind = g.Key, bytes = JsonSerializer.SerializeToUtf8Bytes(g.First(), SyncJson.Options).Length })
            .ToList();
        Console.WriteLine();
        Console.WriteLine("| change | raw JSON bytes |");
        Console.WriteLine("|---|---:|");
        foreach (var op in byOp)
        {
            Console.WriteLine($"| {op.kind} | {op.bytes} |");
        }

        return new { perBatch = rows, perChange = byOp };
    }

    /// <summary>
    /// Sync throughput through the real server and PostgreSQL. Device stores are in memory here so the number is the
    /// sync path (HTTP + server + PostgreSQL), not the terminal disk; terminal write latency is measured separately.
    /// </summary>
    public static async Task<object> ThroughputAsync(Harness h, int ticketsPerTerminal = 200)
    {
        var configs = new (string Name, int Terminals, int Stores)[]
        {
            ("1 terminal", 1, 1),
            ("4 terminals, 4 stores", 4, 4),
            ("4 terminals, 1 store (contended)", 4, 1),
            ("16 terminals, 16 stores", 16, 16),
        };

        var results = new List<object>();
        Console.WriteLine($"| scenario | changes | push median (changes/s) | push min-max | pull median (changes/s) | pull min-max |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|");
        foreach (var (name, terminalCount, storeCount) in configs)
        {
            var push = new List<double>();
            var pull = new List<double>();
            var total = 0;
            for (var run = 0; run <= h.Config.Runs; run++)
            {
                var id = Harness.RunId();
                var terminals = new List<(DeviceStore Store, SyncClient Sync, HttpClient Http)>();
                for (var i = 0; i < terminalCount; i++)
                {
                    var t = await h.TerminalAsync($"tp-{id}-{i % storeCount}", $"tp-{id}-T{i}", h.Config.ServerUrl,
                        options: new SyncClientOptions { PushBatchSize = 100, Entities = ["menu_item"] });
                    var pos = new PosTerminal(t.Store);
                    for (var n = 0; n < ticketsPerTerminal; n++)
                    {
                        await Harness.RingUpAsync(pos, n);
                    }

                    terminals.Add(t);
                }

                total = terminalCount * ticketsPerTerminal * 5;
                // Push phase: every terminal drains its outbox at once (the pull afterwards is filtered to the empty
                // menu, so it costs one round trip and no data).
                var sw = Stopwatch.StartNew();
                await Task.WhenAll(terminals.Select(t => t.Sync.SyncOnceAsync()));
                sw.Stop();

                // Pull phase: one fresh terminal per store downloads that store's whole log, 500 changes per page.
                var readers = new List<(DeviceStore Store, SyncClient Sync, HttpClient Http)>();
                for (var s = 0; s < storeCount; s++)
                {
                    readers.Add(await h.TerminalAsync($"tp-{id}-{s}", $"tp-{id}-R{s}", h.Config.ServerUrl,
                        options: new SyncClientOptions { PullBatchSize = 500 }));
                }

                var pullSw = Stopwatch.StartNew();
                await Task.WhenAll(readers.Select(r => r.Sync.SyncOnceAsync()));
                pullSw.Stop();

                if (run > 0)
                {
                    push.Add(total / sw.Elapsed.TotalSeconds);
                    pull.Add(total / pullSw.Elapsed.TotalSeconds);
                }

                foreach (var t in terminals.Concat(readers))
                {
                    await t.Store.DisposeAsync();
                    t.Http.Dispose();
                }
            }

            results.Add(new { scenario = name, changes = total, push, pull });
            Console.WriteLine($"| {name} | {total} | {Harness.Median(push):0} | {push.Min():0}-{push.Max():0} | {Harness.Median(pull):0} | {pull.Min():0}-{pull.Max():0} |");
        }

        return results;
    }

    /// <summary>
    /// Time from "link restored" until three terminals and the server hold byte-identical state, after each terminal
    /// sold through a partition. Terminals run the same sync loop as the demo (250 ms interval, backoff capped at 2 s),
    /// so the number includes the wait for the next retry, as it would in a restaurant.
    /// </summary>
    public static async Task<object> ConvergenceAsync(Harness h)
    {
        using var toxiHttp = new HttpClient { BaseAddress = h.Config.ToxiproxyUrl };
        var toxi = new ToxiproxyClient(toxiHttp);
        await toxi.CreateProxyAsync("terminal-link", "0.0.0.0:8666", h.Config.ProxyUpstream);

        var results = new List<object>();
        Console.WriteLine("| backlog per terminal | after heal | median ms | min ms | max ms |");
        Console.WriteLine("|---|---|---:|---:|---:|");
        foreach (var (tickets, kick) in new[] { (20, false), (200, false), (20, true), (200, true) })
        {
            var samples = new List<double>();
            for (var run = 0; run <= h.Config.Runs; run++)
            {
                var id = Harness.RunId();
                var store = $"cv-{id}";
                var terminals = new List<(DeviceStore Store, SyncClient Sync, HttpClient Http)>();
                for (var i = 0; i < 3; i++)
                {
                    terminals.Add(await h.TerminalAsync(store, $"cv-{id}-T{i}", h.Config.UplinkUrl, timeout: TimeSpan.FromSeconds(2)));
                }

                using var stop = new CancellationTokenSource();
                var loops = terminals.Select(t => new SyncLoop(t.Sync, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2))).ToList();
                var running = loops.Select(l => l.RunAsync(stop.Token)).ToList();

                await toxi.SetEnabledAsync("terminal-link", false);
                await Task.WhenAll(terminals.Select(async t =>
                {
                    var pos = new PosTerminal(t.Store);
                    for (var n = 0; n < tickets; n++)
                    {
                        await Harness.RingUpAsync(pos, n);
                    }
                }));
                await Task.Delay(3000); // let every loop notice the outage and back off

                await toxi.SetEnabledAsync("terminal-link", true);
                var sw = Stopwatch.StartNew();
                if (kick)
                {
                    // What a terminal can do when the OS reports connectivity is back: retry now instead of
                    // waiting out its backoff. Isolates the cost of moving and merging the backlog.
                    loops.ForEach(l => l.Kick());
                }

                while (true)
                {
                    var server = await h.StateAsync(store);
                    var done = true;
                    foreach (var t in terminals)
                    {
                        if (await t.Store.PendingCountAsync() > 0 || await t.Store.HashAsync() != server.Hash)
                        {
                            done = false;
                            break;
                        }
                    }

                    if (done)
                    {
                        break;
                    }

                    if (sw.Elapsed > TimeSpan.FromSeconds(120))
                    {
                        throw new TimeoutException("No convergence within 120 s.");
                    }

                    await Task.Delay(20);
                }

                sw.Stop();
                await stop.CancelAsync();
                await Task.WhenAll(running);
                if (run > 0)
                {
                    samples.Add(sw.Elapsed.TotalMilliseconds);
                }

                foreach (var (t, loop) in terminals.Zip(loops))
                {
                    loop.Dispose();
                    await t.Store.DisposeAsync();
                    t.Http.Dispose();
                }
            }

            var label = $"{tickets} tickets ({tickets * 5} changes), 3 terminals";
            var mode = kick ? "retry kicked on reconnect" : "next backoff retry (cap 2 s)";
            results.Add(new { backlog = label, mode, samples });
            Console.WriteLine($"| {label} | {mode} | {Harness.Median(samples):0} | {samples.Min():0} | {samples.Max():0} |");
        }

        return results;
    }

    /// <summary>
    /// How long a cashier waits for a change to be durable on the terminal: one SQLite transaction with
    /// synchronous=FULL (fsync) on the host disk, measured per change and per five-change ticket.
    /// </summary>
    public static async Task<object> LocalWriteLatencyAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"latency-{Harness.RunId()}.db");
        var single = new List<double>();
        var ticket = new List<double>();
        await using (var store = await DeviceStore.OpenAsync(path, new DeviceIdentity("LAT-T1", "store-lat"), SyncSchema.Default, TimeProvider.System))
        {
            var pos = new PosTerminal(store);
            for (var i = 0; i < 50; i++)
            {
                await Harness.RingUpAsync(pos, i); // warm-up
            }

            var id = await pos.OpenTicketAsync("1", 2);
            for (var i = 0; i < 500; i++)
            {
                var sw = Stopwatch.StartNew();
                await pos.SetNoteAsync(id, $"note {i}");
                single.Add(sw.Elapsed.TotalMilliseconds);
            }

            for (var i = 0; i < 200; i++)
            {
                var sw = Stopwatch.StartNew();
                await Harness.RingUpAsync(pos, i);
                ticket.Add(sw.Elapsed.TotalMilliseconds);
            }
        }

        foreach (var f in new[] { path, path + "-wal", path + "-shm" })
        {
            File.Delete(f);
        }

        Console.WriteLine("| local write | p50 ms | p95 ms | p99 ms |");
        Console.WriteLine("|---|---:|---:|---:|");
        Console.WriteLine($"| one change (fsync) | {Harness.Percentile(single, 50):0.00} | {Harness.Percentile(single, 95):0.00} | {Harness.Percentile(single, 99):0.00} |");
        Console.WriteLine($"| ticket = 5 changes | {Harness.Percentile(ticket, 50):0.00} | {Harness.Percentile(ticket, 95):0.00} | {Harness.Percentile(ticket, 99):0.00} |");
        return new
        {
            single = new { p50 = Harness.Percentile(single, 50), p95 = Harness.Percentile(single, 95), p99 = Harness.Percentile(single, 99) },
            ticket = new { p50 = Harness.Percentile(ticket, 50), p95 = Harness.Percentile(ticket, 95), p99 = Harness.Percentile(ticket, 99) },
        };
    }

    private static int Gzip(byte[] data, CompressionLevel level)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, level, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return (int)buffer.Length;
    }
}
