using System.Globalization;
using PosSync.Core.Model;

namespace PosSync.Core.Merge;

/// <summary>Why a change was refused. Codes are stable API values; the detail is for humans.</summary>
public sealed record ValidationError(string Code, string Detail);

/// <summary>
/// Structural validation against the schema. Runs on the server for every pushed change (a device is not trusted
/// to send well-formed data) and on the device before a change enters the outbox, so a bad change fails at the
/// terminal instead of sitting in the outbox forever.
/// </summary>
public static class ChangeValidator
{
    public const int MaxOpsPerChange = 32;
    public const long MaxCounterDelta = 1000;

    public static ValidationError? Validate(SyncSchema schema, Change change, bool fromDevice)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(change);

        if (change.ChangeId == Guid.Empty || change.EntityId == Guid.Empty)
        {
            return new("invalid_id", "Change and entity ids must be non-empty GUIDs.");
        }

        if (string.IsNullOrWhiteSpace(change.StoreId) || change.StoreId.Length > 64 ||
            string.IsNullOrWhiteSpace(change.NodeId) || change.NodeId.Length > 64)
        {
            return new("invalid_scope", "Store and node ids must be 1-64 characters.");
        }

        var spec = schema.Find(change.Entity ?? string.Empty);
        if (spec is null)
        {
            return new("unknown_entity", $"Entity '{change.Entity}' is not in schema v{schema.Version}.");
        }

        if (fromDevice && !spec.DeviceWritable)
        {
            return new("read_only_entity", $"Devices may not write '{change.Entity}'.");
        }

        if (change.Ops is null || change.Ops.Count == 0 || change.Ops.Count > MaxOpsPerChange)
        {
            return new("invalid_ops", $"A change carries 1-{MaxOpsPerChange} operations.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in change.Ops)
        {
            if (op is null || !spec.Fields.TryGetValue(op.Field ?? string.Empty, out var field))
            {
                return new("unknown_field", $"'{change.Entity}.{op?.Field}' is not in schema v{schema.Version}.");
            }

            if (!seen.Add(field.Name))
            {
                // Two writes to one field in one change share a stamp: there would be no defined winner.
                return new("duplicate_field", $"'{field.Name}' appears twice in one change.");
            }

            var error = ValidateOp(spec, field, op);
            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

    private static ValidationError? ValidateOp(EntitySpec spec, FieldSpec field, FieldOp op)
    {
        var counterSemantics = spec.Strategy == MergeStrategy.DomainRules && field.Kind == FieldKind.Counter;
        if (op.Kind == OpKind.Add)
        {
            if (!counterSemantics)
            {
                return new("invalid_op", $"'{field.Name}' is not a counter under {spec.Strategy}; use set.");
            }

            return long.TryParse(op.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var delta) &&
                   Math.Abs(delta) <= MaxCounterDelta
                ? null
                : new("invalid_value", $"'{field.Name}' delta must be an integer within ±{MaxCounterDelta}.");
        }

        if (op.Kind != OpKind.Set)
        {
            return new("invalid_op", $"Unknown operation on '{field.Name}'.");
        }

        if (counterSemantics)
        {
            return new("invalid_op", $"'{field.Name}' is a counter under {spec.Strategy}; use add.");
        }

        if (op.Value is null)
        {
            return field.Ranks is null ? null : new("invalid_value", $"'{field.Name}' cannot be cleared.");
        }

        if (op.Value.Length > field.MaxLength)
        {
            return new("invalid_value", $"'{field.Name}' is longer than {field.MaxLength} characters.");
        }

        if (field.IsInteger && !long.TryParse(op.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
        {
            return new("invalid_value", $"'{field.Name}' must be an integer.");
        }

        if (field.Ranks is not null && field.RankOf(op.Value) < 0)
        {
            return new("invalid_value", $"'{field.Name}' must be one of: {string.Join(", ", field.Ranks)}.");
        }

        return null;
    }
}
