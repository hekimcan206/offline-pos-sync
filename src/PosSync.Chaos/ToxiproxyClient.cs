using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PosSync.Chaos;

/// <summary>A Toxiproxy toxic. See https://github.com/Shopify/toxiproxy#toxics for types and attributes.</summary>
public sealed record Toxic(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("stream")] string Stream,
    [property: JsonPropertyName("toxicity")] double Toxicity,
    [property: JsonPropertyName("attributes")] IReadOnlyDictionary<string, long> Attributes)
{
    /// <summary>Delay every chunk by <paramref name="latencyMs"/> ± <paramref name="jitterMs"/>.</summary>
    public static Toxic Latency(string name, long latencyMs, long jitterMs = 0, string stream = "downstream") =>
        new(name, "latency", stream, 1.0, new Dictionary<string, long> { ["latency"] = latencyMs, ["jitter"] = jitterMs });

    /// <summary>Close the connection after <paramref name="bytes"/> bytes: the request arrives, the response is cut.</summary>
    public static Toxic LimitData(string name, long bytes, string stream = "downstream") =>
        new(name, "limit_data", stream, 1.0, new Dictionary<string, long> { ["bytes"] = bytes });

    /// <summary>Stop all data and close the connection after <paramref name="timeoutMs"/> (0 = hold it open forever).</summary>
    public static Toxic Timeout(string name, long timeoutMs, string stream = "upstream") =>
        new(name, "timeout", stream, 1.0, new Dictionary<string, long> { ["timeout"] = timeoutMs });

    /// <summary>Reset the TCP connection (RST) after <paramref name="afterMs"/>.</summary>
    public static Toxic ResetPeer(string name, long afterMs, string stream = "downstream") =>
        new(name, "reset_peer", stream, 1.0, new Dictionary<string, long> { ["timeout"] = afterMs });
}

/// <summary>
/// The handful of Toxiproxy admin calls this repository needs, over plain HTTP. A dedicated client library would be one
/// more dependency for five endpoints.
/// </summary>
public sealed class ToxiproxyClient(HttpClient http)
{
    public async Task CreateProxyAsync(string name, string listen, string upstream, CancellationToken ct = default)
    {
        using var existing = await http.GetAsync(new Uri($"proxies/{name}", UriKind.Relative), ct);
        if (existing.IsSuccessStatusCode)
        {
            using var deleted = await http.DeleteAsync(new Uri($"proxies/{name}", UriKind.Relative), ct);
            deleted.EnsureSuccessStatusCode();
        }

        using var response = await http.PostAsJsonAsync("proxies", new { name, listen, upstream, enabled = true }, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Disabling a proxy closes its open connections and refuses new ones: a clean network partition.</summary>
    public async Task SetEnabledAsync(string proxy, bool enabled, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"proxies/{proxy}", new { enabled }, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task AddToxicAsync(string proxy, Toxic toxic, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"proxies/{proxy}/toxics", toxic, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task RemoveToxicAsync(string proxy, string toxic, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync(new Uri($"proxies/{proxy}/toxics/{toxic}", UriKind.Relative), ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Re-enable every proxy and remove every toxic.</summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync(new Uri("reset", UriKind.Relative), null, ct);
        response.EnsureSuccessStatusCode();
    }
}
