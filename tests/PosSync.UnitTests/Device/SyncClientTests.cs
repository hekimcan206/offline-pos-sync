using PosSync.Core.Model;
using PosSync.Core.Protocol;
using PosSync.Core.Tickets;
using PosSync.Device;
using PosSync.UnitTests.Support;

namespace PosSync.UnitTests.Device;

/// <summary>Outbox replay, idempotency and partial sync against the in-memory hub.</summary>
public sealed class SyncClientTests : IAsyncDisposable
{
    private const string Store = "store-001";
    private static readonly MenuEntry Soup = new(Guid.NewGuid(), "SOUP-01", "Lentil soup", "Starters", 9_000, true);
    private readonly ManualTime _time = new();
    private readonly InMemoryHub _hub;
    private readonly List<DeviceStore> _stores = [];

    public SyncClientTests() => _hub = new InMemoryHub(SyncSchema.Default, _time);

    [Fact]
    public async Task Offline_writes_replicate_after_the_link_returns()
    {
        var (t1, link1, sync1) = await Terminal("T1");
        var (t2, _, sync2) = await Terminal("T2");
        link1.Online = false;

        var ticket = await t1.OpenTicketAsync("4", 2);
        await t1.AddLineAsync(ticket, Soup);
        var offline = async () => await sync1.SyncOnceAsync();
        await offline.Should().ThrowAsync<HttpRequestException>();
        (await t1.Store.PendingCountAsync()).Should().Be(2);

        link1.Online = true;
        await sync1.SyncOnceAsync();
        await sync2.SyncOnceAsync();

        (await t1.Store.PendingCountAsync()).Should().Be(0);
        (await t2.TicketsAsync()).Single().Id.Should().Be(ticket);
        (await t2.Store.HashAsync()).Should().Be(await t1.Store.HashAsync()).And.Be(_hub.Replica(Store).Hash());
    }

    [Fact]
    public async Task A_lost_push_response_is_replayed_without_applying_twice()
    {
        var (t1, link, sync) = await Terminal("T1");
        var ticket = await t1.OpenTicketAsync("4", 2);
        var line = await t1.AddLineAsync(ticket, Soup);
        await t1.ChangeQtyAsync(line, +1);

        link.DropNextPushResponse = true;
        var first = async () => await sync.SyncOnceAsync();
        await first.Should().ThrowAsync<HttpRequestException>();
        _hub.StoredChanges.Should().Be(3, "the hub committed the batch before the response was lost");
        (await t1.Store.PendingCountAsync()).Should().Be(3, "the device never saw the acknowledgement");

        var report = await sync.SyncOnceAsync();

        report.Duplicates.Should().Be(3);
        report.Pushed.Should().Be(0);
        _hub.StoredChanges.Should().Be(3);
        (await t1.Store.PendingCountAsync()).Should().Be(0);
        TicketOps.ReadQty(_hub.Replica(Store).Find(SyncSchema.Entities.TicketLine, line)!).Should().Be(2);
    }

    [Fact]
    public async Task A_pull_page_applied_twice_changes_nothing()
    {
        var (t1, _, sync1) = await Terminal("T1");
        var (t2, link2, sync2) = await Terminal("T2");
        var ticket = await t1.OpenTicketAsync("8", 3);
        var line = await t1.AddLineAsync(ticket, Soup);
        await t1.ChangeQtyAsync(line, +2);
        await sync1.SyncOnceAsync();

        await sync2.SyncOnceAsync();
        var hash = await t2.Store.HashAsync();
        var again = await t2.Store.ApplyPulledAsync(_hub.Pull(Store, 0, 100, null));

        again.Should().Be(0);
        (await t2.Store.HashAsync()).Should().Be(hash);
        (await t2.TicketsAsync()).Single().Lines.Single().Qty.Should().Be(3);
        link2.LastPull.Should().NotBeNull();
    }

    [Fact]
    public async Task Partial_sync_gives_the_kitchen_display_tickets_but_not_the_menu()
    {
        _hub.Author(Store, SyncSchema.Entities.MenuItem, Guid.NewGuid(), [FieldOp.Set("sku", "SOUP-01"), FieldOp.Set("price_minor", "9000")]);
        _hub.Author("store-002", SyncSchema.Entities.MenuItem, Guid.NewGuid(), [FieldOp.Set("sku", "OTHER")]);
        var (pos, _, posSync) = await Terminal("T1");
        var (kds, _, kdsSync) = await Terminal("KDS1", Subscriptions.Kitchen);
        await pos.OpenTicketAsync("2", 2);

        await posSync.SyncOnceAsync();
        await kdsSync.SyncOnceAsync();

        (await pos.MenuAsync()).Should().ContainSingle(m => m.Sku == "SOUP-01", "store-002's menu is never sent to store-001");
        (await kds.MenuAsync()).Should().BeEmpty();
        (await kds.TicketsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Changes_stamped_far_in_the_future_are_rejected_but_never_deleted()
    {
        var (t1, _, sync) = await Terminal("T1", clockOffset: TimeSpan.FromHours(1));
        await t1.OpenTicketAsync("5", 1);

        var report = await sync.SyncOnceAsync();

        report.Rejected.Should().Be(1);
        var rejected = (await t1.Store.RejectedAsync()).Single();
        rejected.Code.Should().Be(RejectionCodes.ClockSkew);
        (await t1.TicketsAsync()).Should().ContainSingle("the ticket is still on the terminal for an operator to resolve");
        _hub.StoredChanges.Should().Be(0);
    }

    [Fact]
    public async Task A_device_cannot_push_on_behalf_of_another_store()
    {
        var (t1, _, _) = await Terminal("T1");
        var change = await t1.Store.WriteAsync(SyncSchema.Entities.Ticket, Guid.NewGuid(), _ => [FieldOp.Set("note", "x")]);

        var response = _hub.Push(new DeviceIdentity("T1", "store-002"), new PushRequest("T1", 1, [change]));

        response.Rejected.Should().ContainSingle(r => r.Code == RejectionCodes.Forbidden);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }
    }

    private async Task<(PosTerminal Pos, HubTransport Link, SyncClient Sync)> Terminal(
        string node, IReadOnlyList<string>? entities = null, TimeSpan? clockOffset = null)
    {
        var identity = new DeviceIdentity(node, Store);
        var clock = clockOffset is { } offset ? new ManualTime(_time.Now + offset) : _time;
        var store = await DeviceStore.OpenAsync(":memory:", identity, SyncSchema.Default, clock);
        _stores.Add(store);
        var link = new HubTransport(_hub, identity);
        return (new PosTerminal(store), link, new SyncClient(store, link, new SyncClientOptions { Entities = entities, PullBatchSize = 2 }));
    }
}
