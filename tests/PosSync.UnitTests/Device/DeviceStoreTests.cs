using PosSync.Core.Clock;
using PosSync.Core.Model;
using PosSync.Core.Protocol;
using PosSync.Device;
using PosSync.UnitTests.Support;

namespace PosSync.UnitTests.Device;

public sealed class DeviceStoreTests : IDisposable
{
    private static readonly DeviceIdentity T1 = new("T1", "store-001");
    private static readonly MenuEntry Soup = new(Guid.NewGuid(), "SOUP-01", "Lentil soup", "Starters", 9_000, true);
    private readonly string _dir = Directory.CreateTempSubdirectory("possync-").FullName;
    private readonly ManualTime _time = new();

    private string DbPath => Path.Combine(_dir, "t1.db");

    [Fact]
    public async Task A_rung_up_ticket_survives_a_crash_before_any_sync()
    {
        Guid ticket;
        await using (var store = await Open())
        {
            var pos = new PosTerminal(store);
            ticket = await pos.OpenTicketAsync("12", 2);
            await pos.AddLineAsync(ticket, Soup, 2);
            // No sync, no graceful shutdown: the process dies here.
        }

        await using var reopened = await Open();
        (await reopened.PendingCountAsync()).Should().Be(2, "both changes are still waiting in the outbox");
        var view = (await new PosTerminal(reopened).TicketsAsync()).Single();
        view.Id.Should().Be(ticket);
        view.TotalMinor.Should().Be(18_000);
    }

    [Fact]
    public async Task Outbox_is_drained_in_write_order()
    {
        await using var store = await Open();
        var pos = new PosTerminal(store);
        var ticket = await pos.OpenTicketAsync("1", 1);
        var line = await pos.AddLineAsync(ticket, Soup);
        await pos.ChangeQtyAsync(line, +1);

        var items = await store.PeekOutboxAsync(10);

        items.Select(i => i.Change.EntityId).Should().Equal(ticket, line, line);
        items.Select(i => i.Change.Hlc).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Clock_never_goes_backwards_across_a_restart_with_a_wrong_wall_clock()
    {
        HlcTimestamp last;
        await using (var store = await Open())
        {
            last = (await store.WriteAsync(SyncSchema.Entities.Ticket, Guid.NewGuid(), _ => [FieldOp.Set("note", "x")])).Hlc;
        }

        _time.Advance(TimeSpan.FromHours(-2));
        await using var reopened = await Open();
        var next = await reopened.WriteAsync(SyncSchema.Entities.Ticket, Guid.NewGuid(), _ => [FieldOp.Set("note", "y")]);

        next.Hlc.Should().BeGreaterThan(last);
    }

    [Fact]
    public async Task Database_refuses_to_open_under_another_node_id()
    {
        await (await Open()).DisposeAsync();

        var act = async () => await DeviceStore.OpenAsync(DbPath, T1 with { NodeId = "T9" }, SyncSchema.Default, _time);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Invalid_local_edit_writes_nothing()
    {
        await using var store = await Open();

        var act = async () => await store.WriteAsync(SyncSchema.Entities.Ticket, Guid.NewGuid(), _ => [FieldOp.Set("status", "lost")]);

        await act.Should().ThrowAsync<ArgumentException>();
        (await store.PendingCountAsync()).Should().Be(0);
        (await store.AllStatesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Devices_cannot_author_menu_items()
    {
        await using var store = await Open();

        var act = async () => await store.WriteAsync(SyncSchema.Entities.MenuItem, Guid.NewGuid(), _ => [FieldOp.Set("price_minor", "1")]);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("read_only_entity*");
    }

    [Fact]
    public async Task Acknowledged_changes_leave_the_outbox_and_rejected_ones_stay_with_their_reason()
    {
        await using var store = await Open();
        var a = await store.WriteAsync(SyncSchema.Entities.Ticket, Guid.NewGuid(), _ => [FieldOp.Set("note", "a")]);
        var b = await store.WriteAsync(SyncSchema.Entities.Ticket, Guid.NewGuid(), _ => [FieldOp.Set("note", "b")]);
        var c = await store.WriteAsync(SyncSchema.Entities.Ticket, Guid.NewGuid(), _ => [FieldOp.Set("note", "c")]);

        await store.AcknowledgeAsync(new PushResponse([a.ChangeId], [b.ChangeId], [new(c.ChangeId, RejectionCodes.ClockSkew, "ahead")], 0));

        (await store.PendingCountAsync()).Should().Be(0);
        var rejected = (await store.RejectedAsync()).Single();
        rejected.Change.ChangeId.Should().Be(c.ChangeId);
        rejected.Code.Should().Be(RejectionCodes.ClockSkew);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a lingering WAL handle on Windows must not fail the test.
        }
    }

    private Task<DeviceStore> Open() => DeviceStore.OpenAsync(DbPath, T1, SyncSchema.Default, _time);
}
