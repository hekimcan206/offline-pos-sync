using Microsoft.Extensions.Options;
using PosSync.Core.Model;
using PosSync.Core.Protocol;

namespace PosSync.Server.Sync;

internal static class SyncEndpoints
{
    public static RouteGroupBuilder MapSyncEndpoints(this RouteGroupBuilder api)
    {
        var sync = api.MapGroup("/sync").WithTags("sync");

        sync.MapPost("/push", async (HttpContext http, PushRequest request, DeviceRegistry devices, SyncStore store,
            IOptions<SyncOptions> options, CancellationToken ct) =>
        {
            var device = await devices.AuthenticateAsync(http.Request.Headers.Authorization, ct);
            if (device is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unknown or revoked device token.");
            }

            if (request.SchemaVersion != SyncSchema.CurrentVersion)
            {
                // Replicas merging with different rules would never converge: refuse rather than accept and diverge.
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Schema version mismatch.",
                    detail: $"Server runs schema v{SyncSchema.CurrentVersion}, device sent v{request.SchemaVersion}. Update the terminal.");
            }

            if (request.Changes is null || request.Changes.Count > options.Value.MaxPushBatch)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid batch.",
                    detail: $"A push carries 0-{options.Value.MaxPushBatch} changes.");
            }

            return Results.Ok(await store.PushAsync(device, request, ct));
        })
        .WithSummary("Upload changes from the device outbox")
        .WithDescription("Idempotent: a change id already stored is reported as a duplicate and never applied twice.");

        sync.MapGet("/pull", async (HttpContext http, long? since, int? limit, string? entities, DeviceRegistry devices,
            SyncStore store, CancellationToken ct) =>
        {
            var device = await devices.AuthenticateAsync(http.Request.Headers.Authorization, ct);
            if (device is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unknown or revoked device token.");
            }

            IReadOnlyList<string>? filter = null;
            if (!string.IsNullOrWhiteSpace(entities))
            {
                filter = entities.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (filter.Any(e => SyncSchema.Default.Find(e) is null))
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown entity in filter.");
                }
            }

            return Results.Ok(await store.PullAsync(device, Math.Max(0, since ?? 0), limit ?? 500, filter, ct));
        })
        .WithSummary("Download changes in the device's store after a cursor")
        .WithDescription("Partial sync: only the device's store, optionally only some entity types.");

        return api;
    }
}
