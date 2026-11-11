using System.Globalization;
using PosSync.Core.Model;
using PosSync.Core.Tickets;

namespace PosSync.Device;

public sealed record MenuEntry(Guid Id, string Sku, string Name, string? Category, long PriceMinor, bool Available);

/// <summary>
/// What a cashier does, expressed as local writes. Every method returns as soon as the change is committed to the
/// terminal database; replication happens later, in the background, whether the network is there or not.
/// </summary>
public sealed class PosTerminal(DeviceStore store)
{
    private static readonly string Ticket = SyncSchema.Entities.Ticket;
    private static readonly string Line = SyncSchema.Entities.TicketLine;

    public DeviceStore Store { get; } = store;

    public string NodeId => Store.Identity.NodeId;

    /// <summary>Raised after every local write, so a sync loop can push immediately.</summary>
    public event Action? Changed;

    public async Task<Guid> OpenTicketAsync(string tableNo, int guests, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await WriteAsync(Ticket, id, _ => TicketOps.OpenTicket(tableNo, guests, NodeId), ct);
        return id;
    }

    public async Task<Guid> AddLineAsync(Guid ticketId, MenuEntry item, int qty = 1, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var id = Guid.NewGuid();
        var spec = Store.Schema.Get(Line);
        await WriteAsync(Line, id, _ => TicketOps.AddLine(spec, ticketId, item.Sku, item.Name, item.PriceMinor, qty), ct);
        return id;
    }

    public Task ChangeQtyAsync(Guid lineId, int delta, CancellationToken ct = default)
    {
        var spec = Store.Schema.Get(Line);
        return WriteAsync(Line, lineId, s => TicketOps.ChangeQty(spec, s ?? throw Missing(lineId), delta), ct);
    }

    public Task SetNoteAsync(Guid ticketId, string note, CancellationToken ct = default) =>
        WriteAsync(Ticket, ticketId, _ => [FieldOp.Set("note", note)], ct);

    public Task SetTableAsync(Guid ticketId, string tableNo, CancellationToken ct = default) =>
        WriteAsync(Ticket, ticketId, _ => [FieldOp.Set("table_no", tableNo)], ct);

    public Task SendToKitchenAsync(Guid ticketId, CancellationToken ct = default) =>
        WriteAsync(Ticket, ticketId, _ => TicketOps.SendToKitchen(), ct);

    public Task PayAsync(Guid ticketId, CancellationToken ct = default) =>
        WriteAsync(Ticket, ticketId, _ => TicketOps.Pay(), ct);

    public Task VoidAsync(Guid ticketId, CancellationToken ct = default) =>
        WriteAsync(Ticket, ticketId, _ => TicketOps.Void(), ct);

    public Task VoidLineAsync(Guid lineId, CancellationToken ct = default) =>
        WriteAsync(Line, lineId, _ => TicketOps.VoidLine(), ct);

    public async Task<IReadOnlyList<TicketView>> TicketsAsync(CancellationToken ct = default) =>
        TicketProjection.Build(await Store.AllStatesAsync(ct: ct));

    public async Task<IReadOnlyList<MenuEntry>> MenuAsync(CancellationToken ct = default) =>
        [.. (await Store.AllStatesAsync(SyncSchema.Entities.MenuItem, ct))
            .Select(s => new MenuEntry(
                s.EntityId,
                s.Get("sku") ?? "?",
                s.Get("name") ?? "?",
                s.Get("category"),
                long.TryParse(s.Get("price_minor"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0,
                s.Get("available") != "false"))
            .OrderBy(m => m.Sku, StringComparer.Ordinal)];

    private async Task WriteAsync(string entity, Guid id, Func<EntityState?, IReadOnlyList<FieldOp>> edit, CancellationToken ct)
    {
        await Store.WriteAsync(entity, id, edit, ct);
        Changed?.Invoke();
    }

    private static InvalidOperationException Missing(Guid id) => new($"Line {id} does not exist on this terminal.");
}
