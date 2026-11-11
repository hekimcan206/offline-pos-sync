using System.Net.Http.Json;
using PosSync.Core.Tickets;

namespace PosSync.Simulator;

internal sealed record StoreState(string StoreId, long Head, int Entities, string Hash);

/// <summary>Head-office calls the simulator needs: provision terminals and read the merged server state.</summary>
internal sealed class ServerAdmin(HttpClient http)
{
    private sealed record Registration(string NodeId, string StoreId, string Token);

    public async Task WaitUntilReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                using var response = await http.GetAsync(new Uri("health/ready", UriKind.Relative), ct);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException) when (DateTime.UtcNow < until)
            {
            }

            if (DateTime.UtcNow >= until)
            {
                throw new TimeoutException("Sync server did not become ready.");
            }

            await Task.Delay(500, ct);
        }
    }

    public async Task<string> RegisterAsync(string nodeId, string storeId, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/v1/admin/devices", new { nodeId, storeId }, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Registration>(ct))!.Token;
    }

    public async Task<StoreState> StateAsync(string storeId, CancellationToken ct) =>
        (await http.GetFromJsonAsync<StoreState>($"api/v1/admin/stores/{storeId}/state", ct))!;

    public async Task<IReadOnlyList<TicketView>> TicketsAsync(string storeId, CancellationToken ct) =>
        (await http.GetFromJsonAsync<List<TicketView>>($"api/v1/admin/stores/{storeId}/tickets", ct))!;
}
