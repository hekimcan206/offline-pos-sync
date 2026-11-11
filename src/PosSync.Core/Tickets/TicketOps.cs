using System.Globalization;
using PosSync.Core.Model;

namespace PosSync.Core.Tickets;

/// <summary>
/// Turns POS intents ("add one more", "pay") into field operations for the entity's merge strategy. The intent is the
/// same for every strategy; the operations differ, and that difference is exactly what the conflict table measures:
/// <list type="bullet">
/// <item><see cref="MergeStrategy.RowLww"/> must send the whole row image (current state plus the edit).</item>
/// <item>A quantity change is an <c>add</c> delta under <see cref="MergeStrategy.DomainRules"/>, but an absolute
/// <c>set</c> (current + delta) under the LWW strategies, which is how an increment gets lost.</item>
/// </list>
/// </summary>
public static class TicketOps
{
    public static IReadOnlyList<FieldOp> OpenTicket(string tableNo, int guests, string openedBy) =>
    [
        FieldOp.Set("table_no", tableNo),
        FieldOp.Set("guest_count", guests.ToString(CultureInfo.InvariantCulture)),
        FieldOp.Set("opened_by", openedBy),
        FieldOp.Set("status", SyncSchema.TicketStatus.Open),
        FieldOp.Set("void_requested", "false"),
    ];

    public static IReadOnlyList<FieldOp> AddLine(EntitySpec lineSpec, Guid ticketId, string sku, string name, long unitPriceMinor, int qty)
    {
        ArgumentNullException.ThrowIfNull(lineSpec);
        return
        [
            FieldOp.Set("ticket_id", ticketId.ToString()),
            FieldOp.Set("sku", sku),
            FieldOp.Set("name", name),
            FieldOp.Set("unit_price_minor", unitPriceMinor.ToString(CultureInfo.InvariantCulture)),
            UsesCounters(lineSpec) ? FieldOp.Add("qty", qty) : FieldOp.Set("qty", qty.ToString(CultureInfo.InvariantCulture)),
            FieldOp.Set("voided", "false"),
        ];
    }

    /// <summary>Change a line quantity by <paramref name="delta"/>, as seen from the local state.</summary>
    public static IReadOnlyList<FieldOp> ChangeQty(EntitySpec lineSpec, EntityState line, int delta)
    {
        ArgumentNullException.ThrowIfNull(lineSpec);
        return UsesCounters(lineSpec)
            ? [FieldOp.Add("qty", delta)]
            : [FieldOp.Set("qty", (ReadQty(line) + delta).ToString(CultureInfo.InvariantCulture))];
    }

    public static IReadOnlyList<FieldOp> SendToKitchen() => [FieldOp.Set("status", SyncSchema.TicketStatus.Sent)];

    public static IReadOnlyList<FieldOp> Pay() => [FieldOp.Set("status", SyncSchema.TicketStatus.Paid)];

    public static IReadOnlyList<FieldOp> Void() =>
        [FieldOp.Set("status", SyncSchema.TicketStatus.Voided), FieldOp.Set("void_requested", "true")];

    public static IReadOnlyList<FieldOp> VoidLine() => [FieldOp.Set("voided", "true")];

    /// <summary>
    /// Final shape of an edit for the strategy: under row-level LWW the operations are widened to the full row image
    /// (local state overlaid with the edit), because a row-LWW write replaces the whole row.
    /// </summary>
    public static IReadOnlyList<FieldOp> ForStrategy(EntitySpec spec, EntityState? current, IReadOnlyList<FieldOp> edit)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(edit);
        if (spec.Strategy != MergeStrategy.RowLww || current is null)
        {
            return edit;
        }

        var row = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (field, register) in current.Registers)
        {
            row[field] = register.Value;
        }

        foreach (var op in edit)
        {
            row[op.Field] = op.Value;
        }

        return [.. row.Select(kv => FieldOp.Set(kv.Key, kv.Value))];
    }

    public static long ReadQty(EntityState line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Counters.TryGetValue("qty", out var counted))
        {
            return counted;
        }

        return long.TryParse(line.Get("qty"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var set) ? set : 0;
    }

    private static bool UsesCounters(EntitySpec spec) =>
        spec.Strategy == MergeStrategy.DomainRules && spec.Fields.TryGetValue("qty", out var f) && f.Kind == FieldKind.Counter;
}
