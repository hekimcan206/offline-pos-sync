using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PosSync.Chaos;
using PosSync.Core.Model;
using PosSync.Core.Tickets;
using PosSync.Device;
using PosSync.Simulator;

// A restaurant demo you can watch: three front-of-house terminals in store-001 and one in store-002 sell through a
// healthy link, a hard partition and a flapping link (all injected with Toxiproxy), then the link heals and the
// simulator proves convergence and zero loss against the server. Exit code 0 only if every check passes.
var cfg = new SimConfig(
    AdminUrl: Env("ADMIN_URL", "http://127.0.0.1:58280/"),
    AdminKey: Env("ADMIN_KEY", "admin-dev-only"),
    ToxiproxyUrl: Env("TOXIPROXY_URL", "http://127.0.0.1:58474/"),
    UplinkUrl: Env("UPLINK_URL", "http://127.0.0.1:58666/"),
    ProxyUpstream: Env("PROXY_UPSTREAM", "server:8080"),
    DataDir: Env("DATA_DIR", Path.Combine(Path.GetTempPath(), "possync-sim")),
    Online: Seconds("ONLINE_SECONDS", 6),
    Partition: Seconds("PARTITION_SECONDS", 10),
    Flapping: Seconds("FLAP_SECONDS", 4),
    ConvergeTimeout: Seconds("CONVERGE_TIMEOUT_SECONDS", 60));

const string Link = "terminal-link";
const string Store1 = "store-001";
const string Store2 = "store-002";
Directory.CreateDirectory(cfg.DataDir);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

using var adminHttp = new HttpClient { BaseAddress = new Uri(cfg.AdminUrl), Timeout = TimeSpan.FromSeconds(10) };
adminHttp.DefaultRequestHeaders.Add("X-Admin-Key", cfg.AdminKey);
var admin = new ServerAdmin(adminHttp);
using var toxiHttp = new HttpClient { BaseAddress = new Uri(cfg.ToxiproxyUrl), Timeout = TimeSpan.FromSeconds(10) };
var toxiproxy = new ToxiproxyClient(toxiHttp);

Say("offline-pos-sync simulator: all data is synthetic");
await admin.WaitUntilReadyAsync(TimeSpan.FromSeconds(60), ct);
await toxiproxy.CreateProxyAsync(Link, "0.0.0.0:8666", cfg.ProxyUpstream, ct);
Say($"uplink {cfg.UplinkUrl} -> Toxiproxy '{Link}' -> {cfg.ProxyUpstream}");

var specs = new (string Name, string Store, int Seed)[] { ("BAR", Store1, 1), ("FLOOR", Store1, 2), ("PATIO", Store1, 3), ("GARDEN", Store2, 4) };
var terminals = new List<SimTerminal>();
foreach (var (name, store, seed) in specs)
{
    var token = await admin.RegisterAsync(name, store, ct);
    terminals.Add(await SimTerminal.CreateAsync(name, store, token, new Uri(cfg.UplinkUrl), cfg.DataDir, seed, Subscriptions.FrontOfHouse));
}

var bar = terminals[0];
var floor = terminals[1];
var garden = terminals[3];
foreach (var t in terminals)
{
    t.StartSync();
}

// Terminals need the menu (server-authored, partial sync per store) before they can sell.
foreach (var t in terminals)
{
    await WaitFor(async () => (await t.Pos.MenuAsync(ct)).Count > 0, TimeSpan.FromSeconds(30), $"{t.Name} menu");
}

var menus = new Dictionary<string, IReadOnlyList<MenuEntry>>();
foreach (var t in terminals)
{
    menus[t.Name] = await t.Pos.MenuAsync(ct);
}

Say($"menus synced: {string.Join(", ", terminals.Select(t => $"{t.Name}={menus[t.Name].Count} items"))}");

var clock = Stopwatch.StartNew();
using var selling = new CancellationTokenSource();
var sellers = terminals.Select((t, i) => t.SellAsync(menus[t.Name], i + 1, selling.Token)).ToList();
var linkState = "UP";
using var statusStop = new CancellationTokenSource();
var status = Task.Run(async () =>
{
    while (!statusStop.IsCancellationRequested)
    {
        await PrintStatusAsync();
        try
        {
            await Task.Delay(1000, statusStop.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
    }
});

// ---- phase 1: online. BAR opens a shared ticket that FLOOR receives, for the scripted conflicts later.
Phase($"ONLINE for {cfg.Online.TotalSeconds:0} s");
var kebab = menus["BAR"].First(m => m.Sku == "MAIN-07");
var dessert = menus["FLOOR"].First(m => m.Sku == "DESSERT-01");
var shared = await bar.Pos.OpenTicketAsync("42", 4, ct);
bar.Opened.Add(shared);
var sharedLine = await bar.Pos.AddLineAsync(shared, kebab, 1, ct);
await WaitFor(async () => (await floor.Pos.TicketsAsync(ct)).Any(t => t.Id == shared), TimeSpan.FromSeconds(15), "shared ticket on FLOOR");
Say("table 42 opened on BAR and visible on FLOOR");
await Task.Delay(cfg.Online, ct);

// ---- phase 2: hard partition. Every terminal keeps selling; outboxes grow.
Phase($"PARTITION for {cfg.Partition.TotalSeconds:0} s (Toxiproxy proxy disabled)");
await toxiproxy.SetEnabledAsync(Link, false, ct);
linkState = "DOWN";
await Task.Delay(TimeSpan.FromSeconds(1), ct);
await bar.Pos.ChangeQtyAsync(sharedLine, +1, ct);
await floor.Pos.ChangeQtyAsync(sharedLine, +1, ct);
await bar.Pos.PayAsync(shared, ct);
await floor.Pos.VoidAsync(shared, ct);
await Task.Delay(50, ct);
await floor.Pos.AddLineAsync(shared, dessert, 1, ct);
Say("conflicts on table 42 while partitioned: BAR +1 and FLOOR +1 on the kebab, BAR pays, FLOOR voids, FLOOR adds a dessert");
await Task.Delay(cfg.Partition - TimeSpan.FromSeconds(1), ct);

// ---- phase 3: flapping link.
Phase($"FLAPPING for {cfg.Flapping.TotalSeconds:0} s");
var rng = new Random(7);
var flapUntil = clock.Elapsed + cfg.Flapping;
var up = false;
while (clock.Elapsed < flapUntil)
{
    up = !up;
    await toxiproxy.SetEnabledAsync(Link, up, ct);
    linkState = up ? "FLAP:UP" : "FLAP:DOWN";
    await Task.Delay(rng.Next(300, 800), ct);
}

// ---- phase 4: heal and measure convergence.
await selling.CancelAsync();
await Task.WhenAll(sellers);
await toxiproxy.ResetAsync(ct);
linkState = "UP";
Phase("HEALED: waiting for convergence");
var healed = Stopwatch.StartNew();
var converged = await WaitConvergedAsync(cfg.ConvergeTimeout);
healed.Stop();
await statusStop.CancelAsync();
await status;
await PrintStatusAsync();

// ---- verification
var failures = new List<string>();
var server1 = await admin.TicketsAsync(Store1, ct);
var server2 = await admin.TicketsAsync(Store2, ct);
var created1 = terminals.Where(t => t.Store.Identity.StoreId == Store1).SelectMany(t => t.Opened).ToHashSet();
var created2 = garden.Opened.ToHashSet();
var lost1 = created1.Except(server1.Select(t => t.Id)).Count();
var lost2 = created2.Except(server2.Select(t => t.Id)).Count();
if (!converged)
{
    failures.Add("terminals did not converge with the server in time");
}

if (lost1 + lost2 > 0)
{
    failures.Add($"{lost1 + lost2} tickets missing on the server");
}

var gardenStates = await garden.Store.AllStatesAsync(ct: ct);
if (gardenStates.Any(s => s.StoreId != Store2))
{
    failures.Add("store-002 terminal received store-001 data (partial sync broken)");
}

var table42 = server1.Single(t => t.Id == shared);
var kebabQty = table42.Lines.Single(l => l.Id == sharedLine).Qty;
if (table42.Status != SyncSchema.TicketStatus.Paid || !table42.Flags.Contains(TicketProjection.VoidOverriddenByPayment) ||
    kebabQty != 3 || !table42.Flags.Contains(TicketProjection.LineAddedAfterClose))
{
    failures.Add("table 42 conflicts did not resolve as documented");
}

Console.WriteLine();
Say("RESULT");
Console.WriteLine($"  store-001: {created1.Count} tickets rung up on 3 terminals, {created1.Count - lost1} of them on the server, {lost1} lost (server history: {server1.Count})");
Console.WriteLine($"  store-002: {created2.Count} tickets rung up on GARDEN, {created2.Count - lost2} of them on the server, {lost2} lost; GARDEN holds no store-001 data");
Console.WriteLine($"  convergence after heal: {healed.ElapsedMilliseconds} ms (all terminal hashes equal the server hash)");
Console.WriteLine($"  table 42: status={table42.Status}, kebab qty={kebabQty} (1 + BAR +1 + FLOOR +1), total={table42.TotalMinor / 100m:0.00} TRY");
Console.WriteLine($"            flags for the manager: {string.Join(", ", table42.Flags)}");
var flagged = server1.Where(t => created1.Contains(t.Id) && t.Flags.Count > 0).ToList();
Console.WriteLine($"  tickets from this run with review flags: {flagged.Count} ({string.Join(", ", flagged.SelectMany(t => t.Flags).Distinct())})");

var summary = new
{
    converged,
    convergenceMs = healed.ElapsedMilliseconds,
    ticketsCreated = created1.Count + created2.Count,
    ticketsOnServer = created1.Count - lost1 + created2.Count - lost2,
    lost = lost1 + lost2,
    table42 = new { table42.Status, kebabQty, table42.Flags },
    failures,
};
Console.WriteLine("SUMMARY " + JsonSerializer.Serialize(summary));

foreach (var t in terminals)
{
    await t.DisposeAsync();
}

if (failures.Count > 0)
{
    foreach (var f in failures)
    {
        Console.Error.WriteLine("FAIL: " + f);
    }

    return 1;
}

Say("PASS: no ticket lost, all replicas converged");
return 0;

async Task PrintStatusAsync()
{
    var cells = new List<string>();
    foreach (var t in terminals)
    {
        var pending = await t.Store.PendingCountAsync(CancellationToken.None);
        var tickets = (await t.Store.AllStatesAsync(SyncSchema.Entities.Ticket, CancellationToken.None)).Count;
        cells.Add($"{t.Name,-6} {(t.Loop.State == LinkState.Online ? "on " : "off")} outbox {pending,4} tickets {tickets,3}");
    }

    var s1 = await TryState(Store1);
    Console.WriteLine($"[t+{clock.Elapsed.TotalSeconds,5:0.0}s] link {linkState,-9} | {string.Join(" | ", cells)} | server store-001 tickets {s1}");
}

async Task<string> TryState(string store)
{
    try
    {
        return (await admin.TicketsAsync(store, CancellationToken.None)).Count.ToString(CultureInfo.InvariantCulture);
    }
    catch (HttpRequestException)
    {
        return "?";
    }
}

async Task<bool> WaitConvergedAsync(TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until)
    {
        var hashes = new Dictionary<string, string>
        {
            [Store1] = (await admin.StateAsync(Store1, ct)).Hash,
            [Store2] = (await admin.StateAsync(Store2, ct)).Hash,
        };
        var all = true;
        foreach (var t in terminals)
        {
            if (await t.Store.PendingCountAsync(ct) > 0 || await t.Store.HashAsync(ct) != hashes[t.Store.Identity.StoreId])
            {
                all = false;
                break;
            }
        }

        if (all)
        {
            return true;
        }

        await Task.Delay(100, ct);
    }

    return false;
}

async Task WaitFor(Func<Task<bool>> condition, TimeSpan timeout, string what)
{
    var until = DateTime.UtcNow + timeout;
    while (!await condition())
    {
        if (DateTime.UtcNow > until)
        {
            throw new TimeoutException($"Timed out waiting for {what}.");
        }

        await Task.Delay(200, ct);
    }
}

static void Say(string message) => Console.WriteLine($"== {message}");

void Phase(string name) => Console.WriteLine($"[t+{clock.Elapsed.TotalSeconds,5:0.0}s] ---- {name}");

static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

static TimeSpan Seconds(string name, int fallback) =>
    TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : fallback);

internal sealed record SimConfig(
    string AdminUrl, string AdminKey, string ToxiproxyUrl, string UplinkUrl, string ProxyUpstream, string DataDir,
    TimeSpan Online, TimeSpan Partition, TimeSpan Flapping, TimeSpan ConvergeTimeout);
