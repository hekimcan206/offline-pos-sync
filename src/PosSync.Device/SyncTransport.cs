using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PosSync.Core.Protocol;

namespace PosSync.Device;

/// <summary>How a device talks to the sync server. HTTP in production; an in-memory hub in the property tests.</summary>
public interface ISyncTransport
{
    Task<PushResponse> PushAsync(PushRequest request, CancellationToken ct);

    Task<PullResponse> PullAsync(long since, int limit, IReadOnlyList<string>? entities, CancellationToken ct);
}

/// <summary>The server answered, but refused the request as a whole (auth, schema version). Not retryable as is.</summary>
public sealed class SyncRejectedException(HttpStatusCode status, string body)
    : Exception($"Sync server refused the request: {(int)status} {body}")
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>JSON over HTTP, one bearer token per device. Request bodies above 1 KiB are gzip-compressed.</summary>
public sealed class HttpSyncTransport : ISyncTransport
{
    private const int CompressAbove = 1024;
    private readonly HttpClient _http;

    /// <param name="http">
    /// A client whose BaseAddress points at the server (directly or through a proxy). Its handler should enable
    /// automatic gzip decompression; <see cref="CreateHttpClient"/> builds one.
    /// </param>
    public HttpSyncTransport(HttpClient http, string deviceToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);
    }

    public bool CompressRequests { get; init; } = true;

    /// <summary>
    /// An HttpClient suited to a terminal: short timeout (a hung request must not stall the outbox longer than a
    /// retry would), gzip responses, and connections recycled often so a healed network path is picked up quickly.
    /// </summary>
    public static HttpClient CreateHttpClient(Uri baseAddress, TimeSpan? timeout = null) =>
        new(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip,
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
            ConnectTimeout = TimeSpan.FromSeconds(3),
        })
        {
            BaseAddress = baseAddress,
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        };

    public async Task<PushResponse> PushAsync(PushRequest request, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(request, SyncJson.Options);
        using var content = CompressRequests && json.Length > CompressAbove ? Gzip(json) : new ByteArrayContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _http.PostAsync("api/v1/sync/push", content, ct);
        return await ReadAsync<PushResponse>(response, ct);
    }

    public async Task<PullResponse> PullAsync(long since, int limit, IReadOnlyList<string>? entities, CancellationToken ct)
    {
        var url = $"api/v1/sync/pull?since={since}&limit={limit}";
        if (entities is { Count: > 0 })
        {
            url += "&entities=" + Uri.EscapeDataString(string.Join(',', entities));
        }

        using var response = await _http.GetAsync(url, ct);
        return await ReadAsync<PullResponse>(response, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if ((int)response.StatusCode is >= 400 and < 500)
        {
            throw new SyncRejectedException(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(SyncJson.Options, ct)
            ?? throw new InvalidDataException("Empty sync response.");
    }

    private static ByteArrayContent Gzip(byte[] json)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(json);
        }

        var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentEncoding.Add("gzip");
        return content;
    }
}
