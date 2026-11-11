using System.Net.Http.Json;
using PosSync.Core.Model;
using PosSync.Device;

namespace PosSync.IntegrationTests.Infrastructure;

/// <summary>A complete terminal (SQLite store, outbox, HTTP sync client) registered with the real server.</summary>
public sealed class TestTerminal : IAsyncDisposable
{
    private readonly HttpClient _http;

    private TestTerminal(DeviceStore store, HttpClient http, string token, SyncClientOptions options)
    {
        Store = store;
        _http = http;
        Token = token;
        Transport = new HttpSyncTransport(http, token);
        Sync = new SyncClient(store, Transport, options);
        Pos = new PosTerminal(store);
    }

    public DeviceStore Store { get; }
    public string Token { get; }
    public HttpSyncTransport Transport { get; }
    public SyncClient Sync { get; }
    public PosTerminal Pos { get; }

    public static async Task<TestTerminal> CreateAsync(
        SyncEnvironment env, string storeId, string nodeId, Uri? via = null,
        TimeSpan? timeout = null, TimeProvider? time = null, SyncClientOptions? options = null)
    {
        // Node ids are global on the server; suffixing the store keeps tests that reuse "T1" independent.
        nodeId = $"{nodeId}-{storeId}";
        var token = await RegisterAsync(env, storeId, nodeId);
        var store = await DeviceStore.OpenAsync(":memory:", new DeviceIdentity(nodeId, storeId), SyncSchema.Default, time ?? TimeProvider.System);
        var http = HttpSyncTransport.CreateHttpClient(via ?? env.ServerUri, timeout ?? TimeSpan.FromSeconds(10));
        return new TestTerminal(store, http, token, options ?? new SyncClientOptions());
    }

    public static async Task<string> RegisterAsync(SyncEnvironment env, string storeId, string nodeId)
    {
        using var admin = env.Admin();
        var response = await admin.PostAsJsonAsync("api/v1/admin/devices", new { nodeId, storeId });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Registration>())!.Token;
    }

    /// <summary>A transport for this device over another path (same token, different proxy or timeout).</summary>
    public HttpSyncTransport TransportVia(Uri via, TimeSpan timeout) =>
        new(HttpSyncTransport.CreateHttpClient(via, timeout), Token);

    /// <summary>Sync until a round succeeds or the deadline passes (the link may still be settling after a heal).</summary>
    public async Task<SyncReport> SyncUntilSuccessAsync(TimeSpan deadline)
    {
        var until = DateTime.UtcNow + deadline;
        while (true)
        {
            try
            {
                return await Sync.SyncOnceAsync();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && DateTime.UtcNow < until)
            {
                await Task.Delay(200);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        _http.Dispose();
    }

    private sealed record Registration(string NodeId, string StoreId, string Token);
}

public static class Ids
{
    public static string Store() => "s-" + Guid.NewGuid().ToString("N")[..12];
}

public static class Menu
{
    public static readonly MenuEntry Soup = new(Guid.NewGuid(), "SOUP-01", "Lentil soup", "Starters", 9_000, true);
    public static readonly MenuEntry Kebab = new(Guid.NewGuid(), "MAIN-07", "Adana kebab", "Mains", 45_000, true);
    public static readonly MenuEntry Tea = new(Guid.NewGuid(), "DRINK-02", "Black tea", "Drinks", 1_500, true);
    public static readonly MenuEntry[] All = [Soup, Kebab, Tea];
}
