using System.Collections.Concurrent;
using PosSync.Core.Model;
using PosSync.Device;

namespace PosSync.Simulator;

/// <summary>
/// One simulated terminal: its own SQLite file, a background sync loop through the (possibly broken) uplink, and a
/// cashier who keeps ringing up synthetic orders whatever the network does.
/// </summary>
internal sealed class SimTerminal : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly Random _rng;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loopTask;

    private SimTerminal(string name, DeviceStore store, HttpClient http, SyncClient client, int seed)
    {
        Name = name;
        Store = store;
        _http = http;
        Pos = new PosTerminal(store);
        Loop = new SyncLoop(client, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2));
        Pos.Changed += Loop.Kick;
        _rng = new Random(seed);
    }

    public string Name { get; }
    public DeviceStore Store { get; }
    public PosTerminal Pos { get; }
    public SyncLoop Loop { get; }

    /// <summary>Every ticket this terminal opened: the simulator checks that each one reached the server.</summary>
    public ConcurrentBag<Guid> Opened { get; } = [];

    public int LinesRung { get; private set; }

    public static async Task<SimTerminal> CreateAsync(
        string name, string storeId, string token, Uri uplink, string dataDir, int seed, IReadOnlyList<string>? entities)
    {
        var path = Path.Combine(dataDir, $"{name}.db");
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            File.Delete(file); // a fresh terminal per demo run; the server keeps its history
        }

        var store = await DeviceStore.OpenAsync(path, new DeviceIdentity(name, storeId), SyncSchema.Default, TimeProvider.System);
        var http = HttpSyncTransport.CreateHttpClient(uplink, TimeSpan.FromSeconds(2));
        var client = new SyncClient(store, new HttpSyncTransport(http, token), new SyncClientOptions { Entities = entities });
        return new SimTerminal(name, store, http, client, seed);
    }

    public void StartSync() => _loopTask = Loop.RunAsync(_stop.Token);

    /// <summary>
    /// Rings up orders until cancelled: open a ticket at a table in this terminal's section, add 1-3 lines, sometimes
    /// +1, send it to the kitchen; guests pay a few orders later. Sections and the payment queue keep tables from
    /// being double-booked, so review flags in the demo come from the scripted conflicts, not from the generator.
    /// </summary>
    public async Task SellAsync(IReadOnlyList<MenuEntry> menu, int section, CancellationToken ct)
    {
        try
        {
            await SellLoopAsync(menu, section, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Closing time: a write cancelled mid-flight rolled back as a whole, nothing half-written remains.
        }
    }

    private async Task SellLoopAsync(IReadOnlyList<MenuEntry> menu, int section, CancellationToken ct)
    {
        var open = new Queue<Guid>();
        var n = 0;
        while (!ct.IsCancellationRequested)
        {
            var table = (section * 100) + (n++ % 15) + 1;
            var ticket = await Pos.OpenTicketAsync(table.ToString(System.Globalization.CultureInfo.InvariantCulture), _rng.Next(1, 7), ct);
            Opened.Add(ticket);
            Guid line = default;
            for (var i = 0; i < _rng.Next(1, 4); i++)
            {
                line = await Pos.AddLineAsync(ticket, menu[_rng.Next(menu.Count)], 1, ct);
                LinesRung++;
            }

            if (_rng.Next(4) == 0)
            {
                await Pos.ChangeQtyAsync(line, +1, ct);
            }

            await Pos.SendToKitchenAsync(ticket, ct);
            open.Enqueue(ticket);
            if (open.Count > 3)
            {
                await Pos.PayAsync(open.Dequeue(), ct);
            }

            await Task.Delay(_rng.Next(300, 900), ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loopTask is not null)
        {
            await _loopTask;
        }

        Loop.Dispose();
        await Store.DisposeAsync();
        _http.Dispose();
        _stop.Dispose();
    }
}
