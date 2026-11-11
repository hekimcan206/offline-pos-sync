using System.Globalization;
using PosSync.Core.Model;

namespace PosSync.Core.Tickets;

public sealed record TicketLineView(
    Guid Id, string? Sku, string? Name, long UnitPriceMinor, long Qty, bool Voided, bool AddedAfterClose);

public sealed record TicketView(
    Guid Id,
    string StoreId,
    string? TableNo,
    string? Status,
    string? Note,
    string? OpenedBy,
    IReadOnlyList<TicketLineView> Lines,
    long TotalMinor,
    IReadOnlyList<string> Flags);

/// <summary>
/// Read model over merged states. Review flags are derived here, from state alone, instead of being written as
/// changes: a derived value is a pure function of converged state, so every replica raises the same flags without
/// any extra coordination (and a flag can never be "lost" in a conflict of its own).
/// </summary>
public static class TicketProjection
{
    public const string VoidOverriddenByPayment = "void_overridden_by_payment";
    public const string LineAddedAfterClose = "line_added_after_close";
    public const string DuplicateOpenTable = "duplicate_open_table";

    public static IReadOnlyList<TicketView> Build(IEnumerable<EntityState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        var all = states.ToList();
        var linesByTicket = all
            .Where(s => s.Entity == SyncSchema.Entities.TicketLine && Guid.TryParse(s.Get("ticket_id"), out _))
            .ToLookup(s => Guid.Parse(s.Get("ticket_id")!));

        var tickets = all.Where(s => s.Entity == SyncSchema.Entities.Ticket).OrderBy(s => s.EntityId).ToList();
        var openTables = tickets
            .Where(t => t.Get("status") is SyncSchema.TicketStatus.Open or SyncSchema.TicketStatus.Sent && t.Get("table_no") is not null)
            .GroupBy(t => t.Get("table_no")!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        return [.. tickets.Select(t => BuildOne(t, linesByTicket[t.EntityId], openTables))];
    }

    private static TicketView BuildOne(EntityState ticket, IEnumerable<EntityState> lineStates, HashSet<string> openTables)
    {
        var status = ticket.Get("status");
        // "Closed" is the first time the ticket reached paid or voided (ranked fields keep the earliest stamp).
        Stamp? closedAt = status is SyncSchema.TicketStatus.Paid or SyncSchema.TicketStatus.Voided ? ticket.StampOf("status") : null;

        var lines = lineStates
            .OrderBy(l => l.StampOf("ticket_id") ?? default)
            .ThenBy(l => l.EntityId)
            .Select(l => new TicketLineView(
                l.EntityId,
                l.Get("sku"),
                l.Get("name"),
                ParseLong(l.Get("unit_price_minor")),
                TicketOps.ReadQty(l),
                l.Get("voided") == "true",
                closedAt is { } closed && l.StampOf("ticket_id") is { } created && created > closed))
            .ToList();

        var flags = new List<string>();
        if (status == SyncSchema.TicketStatus.Paid && ticket.Get("void_requested") == "true")
        {
            flags.Add(VoidOverriddenByPayment);
        }

        if (lines.Any(l => l.AddedAfterClose && !l.Voided))
        {
            flags.Add(LineAddedAfterClose);
        }

        if (ticket.Get("table_no") is { } table && openTables.Contains(table) &&
            status is SyncSchema.TicketStatus.Open or SyncSchema.TicketStatus.Sent)
        {
            flags.Add(DuplicateOpenTable);
        }

        return new TicketView(
            ticket.EntityId,
            ticket.StoreId,
            ticket.Get("table_no"),
            status,
            ticket.Get("note"),
            ticket.Get("opened_by"),
            lines,
            lines.Where(l => !l.Voided).Sum(l => l.UnitPriceMinor * l.Qty),
            flags);
    }

    private static long ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
