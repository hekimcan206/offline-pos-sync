using PosSync.Core.Clock;
using PosSync.Core.Merge;
using PosSync.Core.Model;

namespace PosSync.UnitTests.Merge;

/// <summary>The server treats every pushed change as untrusted input; these are the shapes it must refuse.</summary>
public sealed class ChangeValidatorTests
{
    private static readonly HlcTimestamp Hlc = new(1_762_862_400_000, 0);

    private static Change Ticket(params FieldOp[] ops) =>
        new(Guid.NewGuid(), "store-001", SyncSchema.Entities.Ticket, Guid.NewGuid(), Hlc, "T1", ops);

    private static Change Line(params FieldOp[] ops) => Ticket(ops) with { Entity = SyncSchema.Entities.TicketLine };

    public static TheoryData<string, Change> Invalid => new()
    {
        { "invalid_id", Ticket(FieldOp.Set("note", "x")) with { ChangeId = Guid.Empty } },
        { "invalid_scope", Ticket(FieldOp.Set("note", "x")) with { NodeId = new string('n', 65) } },
        { "unknown_entity", Ticket(FieldOp.Set("note", "x")) with { Entity = "payment_card" } },
        { "read_only_entity", Ticket(FieldOp.Set("price_minor", "1")) with { Entity = SyncSchema.Entities.MenuItem } },
        { "invalid_ops", Ticket() },
        { "unknown_field", Ticket(FieldOp.Set("discount", "100")) },
        { "duplicate_field", Ticket(FieldOp.Set("note", "a"), FieldOp.Set("note", "b")) },
        { "invalid_value", Ticket(FieldOp.Set("status", "refunded")) },
        { "invalid_value", Ticket(FieldOp.Set("status", null)) },
        { "invalid_value", Ticket(FieldOp.Set("note", new string('x', 201))) },
        { "invalid_value", Ticket(FieldOp.Set("guest_count", "two")) },
        { "invalid_op", Ticket(FieldOp.Add("note", 1)) },
        { "invalid_op", Line(FieldOp.Set("qty", "5")) },
        { "invalid_value", Line(FieldOp.Add("qty", 1001)) },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Refuses_malformed_changes(string code, Change change) =>
        ChangeValidator.Validate(SyncSchema.Default, change, fromDevice: true)!.Code.Should().Be(code);

    [Fact]
    public void Accepts_a_well_formed_ticket_and_line()
    {
        ChangeValidator.Validate(SyncSchema.Default, Ticket(FieldOp.Set("note", "no onions"), FieldOp.Set("status", "sent")), true)
            .Should().BeNull();
        ChangeValidator.Validate(SyncSchema.Default, Line(FieldOp.Add("qty", -1), FieldOp.Set("voided", "true")), true)
            .Should().BeNull();
    }

    [Fact]
    public void Under_field_lww_a_quantity_is_a_plain_register()
    {
        var schema = SyncSchema.Default.With(SyncSchema.Entities.TicketLine, MergeStrategy.FieldLww);

        ChangeValidator.Validate(schema, Line(FieldOp.Set("qty", "5")), true).Should().BeNull();
        ChangeValidator.Validate(schema, Line(FieldOp.Add("qty", 1)), true)!.Code.Should().Be("invalid_op");
    }

    [Fact]
    public void The_server_itself_may_author_menu_items()
    {
        var menu = Ticket(FieldOp.Set("price_minor", "4500")) with { Entity = SyncSchema.Entities.MenuItem };

        ChangeValidator.Validate(SyncSchema.Default, menu, fromDevice: false).Should().BeNull();
    }
}
