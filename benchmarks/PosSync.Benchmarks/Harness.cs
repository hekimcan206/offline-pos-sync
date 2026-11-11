using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using PosSync.Core.Model;
using PosSync.Device;

namespace PosSync.Benchmarks;

internal sealed record BenchConfig(Uri ServerUrl, Uri UplinkUrl, Uri ToxiproxyUrl, string AdminKey, string ProxyUpstream, int Runs, string OutDir);

internal sealed record StoreState(string StoreId, long Head, int Entities, string Hash);

/// <summary>Shared plumbing: provisioning terminals on a running server, statistics and environment capture.</summary>
internal sealed class Harness(BenchConfig config) : IDisposable
{
    private readonly HttpClient _admin = CreateAdmin(config);

    public BenchConfig Config { get; } = config;

    public static readonly MenuEntry[] Menu =
    [
        new(Guid.NewGuid(), "SOUP-01", "Lentil soup", "Starters", 9_000, true),
        new(Guid.NewGuid(), "MAIN-07", "Adana kebab", "Mains", 45_000, true),
        new(Guid.NewGuid(), "DRINK-02", "Black tea", "Drinks", 1_500, true),
    ];

    public static string RunId() => Guid.NewGuid().ToString("N")[..8];

    public async Task<(DeviceStore Store, SyncClient Sync, HttpClient Http)> TerminalAsync(
        string storeId, string nodeId, Uri via, string? dbPath = null, SyncClientOptions? options = null, TimeSpan? timeout = null)
    {
        using var response = await _admin.PostAsJsonAsync("api/v1/admin/devices", new { nodeId, storeId });
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<Registration>())!.Token;
        var store = await DeviceStore.OpenAsync(dbPath ?? ":memory:", new DeviceIdentity(nodeId, storeId), SyncSchema.Default, TimeProvider.System);
        var http = HttpSyncTransport.CreateHttpClient(via, timeout ?? TimeSpan.FromSeconds(30));
        return (store, new SyncClient(store, new HttpSyncTransport(http, token), options), http);
    }

    public async Task<StoreState> StateAsync(string storeId) =>
        (await _admin.GetFromJsonAsync<StoreState>($"api/v1/admin/stores/{storeId}/state"))!;

    /// <summary>A realistic ticket: open, two lines, one +1, send to kitchen (5 changes).</summary>
    public static async Task RingUpAsync(PosTerminal pos, int n)
    {
        var ticket = await pos.OpenTicketAsync((n % 30).ToString(CultureInfo.InvariantCulture), 2);
        var line = await pos.AddLineAsync(ticket, Menu[n % 3]);
        await pos.AddLineAsync(ticket, Menu[(n + 1) % 3]);
        await pos.ChangeQtyAsync(line, +1);
        await pos.SendToKitchenAsync(ticket);
    }

    public static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }

    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        var sorted = values.Order().ToArray();
        var rank = (int)Math.Ceiling(p / 100 * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    public static string Environment()
    {
        string docker;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("docker", "ps -q") { RedirectStandardOutput = true, UseShellExecute = false })!;
            var lines = p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            p.WaitForExit();
            docker = $"{lines} running containers";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            docker = "docker CLI not available";
        }

        return $"{RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}; {System.Environment.ProcessorCount} logical CPUs; " +
               $".NET {System.Environment.Version}; host load: {docker}";
    }

    public void Dispose() => _admin.Dispose();

    private static HttpClient CreateAdmin(BenchConfig config)
    {
        var http = new HttpClient { BaseAddress = config.ServerUrl, Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Add("X-Admin-Key", config.AdminKey);
        return http;
    }

    private sealed record Registration(string NodeId, string StoreId, string Token);
}
