using System.Globalization;
using PosSync.Core.Model;
using PosSync.Core.Tickets;
using PosSync.Server.Sync;

namespace PosSync.Server.Admin;

internal sealed record RegisterDeviceRequest(string NodeId, string StoreId);

internal sealed record RegisterDeviceResponse(string NodeId, string StoreId, string Token);

internal sealed record MenuItemRequest(string Sku, string Name, string? Category, long PriceMinor, bool Available = true);

internal sealed record StoreStateResponse(string StoreId, long Head, int Entities, string Hash);

/// <summary>
/// Head-office operations: provision terminals, maintain the menu, and read the merged state (the simulator and the
/// compose demo use these to prove convergence). Protected by an admin key; disabled when none is configured.
/// </summary>
internal static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").WithTags("admin")
            .AddEndpointFilter(async (context, next) =>
            {
                var registry = context.HttpContext.RequestServices.GetRequiredService<DeviceRegistry>();
                return registry.IsAdmin(context.HttpContext.Request.Headers["X-Admin-Key"])
                    ? await next(context)
                    : Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Admin key required.");
            });

        admin.MapPost("/devices", async (RegisterDeviceRequest request, DeviceRegistry devices, CancellationToken ct) =>
        {
            if (!ValidId(request.NodeId) || !ValidId(request.StoreId) || request.NodeId == "server")
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Node and store ids are 1-64 characters [A-Za-z0-9_-].");
            }

            try
            {
                var token = await devices.RegisterAsync(request.NodeId, request.StoreId, ct);
                return Results.Ok(new RegisterDeviceResponse(request.NodeId, request.StoreId, token));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: ex.Message);
            }
        }).WithSummary("Register a terminal (or rotate its token); the token is returned once");

        admin.MapPut("/stores/{storeId}/menu/{itemId:guid}", async (string storeId, Guid itemId, MenuItemRequest item, SyncStore store, CancellationToken ct) =>
        {
            if (!ValidId(storeId))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid store id.");
            }

            try
            {
                var change = await store.AuthorAsync(storeId, SyncSchema.Entities.MenuItem, itemId,
                [
                    FieldOp.Set("sku", item.Sku),
                    FieldOp.Set("name", item.Name),
                    FieldOp.Set("category", item.Category),
                    FieldOp.Set("price_minor", item.PriceMinor.ToString(CultureInfo.InvariantCulture)),
                    FieldOp.Set("available", item.Available ? "true" : "false"),
                ], ct);
                return Results.Ok(new { change.ChangeId, hlc = change.Hlc.Pack() });
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: ex.Message);
            }
        }).WithSummary("Create or update a menu item; replicates to the store's terminals as a server-authored change");

        admin.MapGet("/stores/{storeId}/tickets", async (string storeId, SyncStore store, CancellationToken ct) =>
            Results.Ok(TicketProjection.Build(await store.StatesAsync(storeId, ct))))
            .WithSummary("Merged tickets with derived review flags");

        admin.MapGet("/stores/{storeId}/state", async (string storeId, SyncStore store, CancellationToken ct) =>
        {
            var states = await store.StatesAsync(storeId, ct);
            return Results.Ok(new StoreStateResponse(storeId, await store.HeadAsync(storeId, ct), states.Count, EntityStateJson.Hash(states)));
        }).WithSummary("Store head sequence and a hash of every merged entity (equal on converged replicas)");

        return api;
    }

    private static bool ValidId(string? id) =>
        id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
