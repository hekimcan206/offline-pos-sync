using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Npgsql;
using PosSync.Core.Model;
using PosSync.Core.Protocol;
using PosSync.Core.Tickets;
using PosSync.Device;
using PosSync.IntegrationTests.Infrastructure;

namespace PosSync.IntegrationTests;

/// <summary>The protocol against PostgreSQL, with no network faults: correctness, idempotency, scope, security.</summary>
public sealed class SyncServerTests(SyncEnvironment env)
{
    [Fact]
    public async Task Terminals_and_server_converge_on_identical_state()
    {
        var store = Ids.Store();
        await using var t1 = await TestTerminal.CreateAsync(env, store, "T1");
        await using var t2 = await TestTerminal.CreateAsync(env, store, "T2");

        var ticket = await t1.Pos.OpenTicketAsync("12", 4);
        var soup = await t1.Pos.AddLineAsync(ticket, Menu.Soup, 2);
        await t2.Pos.OpenTicketAsync("3", 2);
        await t1.Sync.SyncOnceAsync();
        await t2.Sync.SyncOnceAsync();
        await t2.Pos.ChangeQtyAsync(soup, +1);
        await t2.Sync.SyncOnceAsync();
        await t1.Sync.SyncOnceAsync();

        var server = await StateAsync(store);
        (await t1.Store.HashAsync()).Should().Be(server.Hash);
        (await t2.Store.HashAsync()).Should().Be(server.Hash);
        server.Entities.Should().Be(3, "two tickets and one line");
        (await t1.Pos.TicketsAsync()).Single(t => t.Id == ticket).TotalMinor.Should().Be(27_000);
    }

    [Fact]
    public async Task Replaying_a_push_stores_nothing_twice()
    {
        var store = Ids.Store();
        await using var t1 = await TestTerminal.CreateAsync(env, store, "T1");
        var ticket = await t1.Pos.OpenTicketAsync("1", 1);
        var line = await t1.Pos.AddLineAsync(ticket, Menu.Kebab);
        await t1.Pos.ChangeQtyAsync(line, +1);
        var batch = (await t1.Store.PeekOutboxAsync(10)).Select(i => i.Change).ToList();

        var first = await t1.Transport.PushAsync(new PushRequest("T1", 1, batch), default);
        var replay = await t1.Transport.PushAsync(new PushRequest("T1", 1, batch), default);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => t1.Transport.PushAsync(new PushRequest("T1", 1, batch), default)));

        first.Accepted.Should().HaveCount(3);
        replay.Duplicates.Should().BeEquivalentTo(batch.Select(c => c.ChangeId));
        concurrent.Should().OnlyContain(r => r.Accepted.Count == 0 && r.Duplicates.Count == 3);
        (await StateAsync(store)).Head.Should().Be(3);
        (await CountChangesAsync(store)).Should().Be(3);
        var tickets = await TicketsAsync(store);
        tickets.Single().Lines.Single().Qty.Should().Be(2, "the +1 was applied exactly once");
    }

    [Fact]
    public async Task Concurrent_first_pushes_of_the_same_change_store_it_once()
    {
        var store = Ids.Store();
        await using var t1 = await TestTerminal.CreateAsync(env, store, "T1");
        await t1.Pos.OpenTicketAsync("1", 1);
        var batch = (await t1.Store.PeekOutboxAsync(10)).Select(i => i.Change).ToList();

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => t1.Transport.PushAsync(new PushRequest("T1", 1, batch), default)));

        results.Sum(r => r.Accepted.Count).Should().Be(1, "exactly one request wins the unique index");
        results.Sum(r => r.Duplicates.Count).Should().Be(11);
        (await CountChangesAsync(store)).Should().Be(1);
    }

    [Fact]
    public async Task Same_change_id_with_a_different_body_is_rejected()
    {
        var store = Ids.Store();
        await using var t1 = await TestTerminal.CreateAsync(env, store, "T1");
        var original = await t1.Store.WriteAsync(SyncSchema.Entities.Ticket, Guid.NewGuid(), _ => [FieldOp.Set("note", "original")]);
        await t1.Sync.SyncOnceAsync();

        var forged = original with { Ops = [FieldOp.Set("note", "tampered")] };
        var response = await t1.Transport.PushAsync(new PushRequest("T1", 1, [forged]), default);

        response.Rejected.Should().ContainSingle(r => r.Code == RejectionCodes.ConflictingDuplicate);
        (await TicketsAsync(store)).Single().Note.Should().Be("original");
    }

    [Fact]
    public async Task A_device_may_only_write_as_itself_into_its_own_store()
    {
        var storeA = Ids.Store();
        var storeB = Ids.Store();
        await using var a = await TestTerminal.CreateAsync(env, storeA, "A1-" + storeA);
        await using var b = await TestTerminal.CreateAsync(env, storeB, "B1-" + storeB);
        var change = await b.Store.WriteAsync(SyncSchema.Entities.Ticket, Guid.NewGuid(), _ => [FieldOp.Set("note", "x")]);

        // Device A replays B's change (other node, other store) with its own valid token.
        var response = await a.Transport.PushAsync(new PushRequest(a.Store.Identity.NodeId, 1, [change]), default);

        response.Rejected.Should().ContainSingle(r => r.Code == RejectionCodes.Forbidden);
        (await CountChangesAsync(storeB)).Should().Be(0);
    }

    [Fact]
    public async Task Partial_sync_scopes_by_store_and_entity()
    {
        var store = Ids.Store();
        var other = Ids.Store();
        using var admin = env.Admin();
        await PutMenuAsync(admin, store, "SOUP-01", 9_000);
        await PutMenuAsync(admin, other, "OTHER-01", 1_000);
        await using var pos = await TestTerminal.CreateAsync(env, store, "P-" + store);
        await using var kitchen = await TestTerminal.CreateAsync(env, store, "K-" + store,
            options: new SyncClientOptions { Entities = Subscriptions.Kitchen });
        await using var foreign = await TestTerminal.CreateAsync(env, other, "F-" + other);
        await pos.Pos.OpenTicketAsync("5", 2);

        await pos.Sync.SyncOnceAsync();
        await kitchen.Sync.SyncOnceAsync();
        await foreign.Sync.SyncOnceAsync();

        (await pos.Pos.MenuAsync()).Select(m => m.Sku).Should().Equal("SOUP-01");
        (await kitchen.Pos.MenuAsync()).Should().BeEmpty("the kitchen display subscribes to tickets and lines only");
        (await kitchen.Pos.TicketsAsync()).Should().ContainSingle();
        (await foreign.Pos.TicketsAsync()).Should().BeEmpty("another store's tickets are never sent");
        (await foreign.Pos.MenuAsync()).Select(m => m.Sku).Should().Equal("OTHER-01");
    }

    [Fact]
    public async Task Devices_cannot_write_the_menu()
    {
        var store = Ids.Store();
        await using var t1 = await TestTerminal.CreateAsync(env, store, "T1");
        var forged = new Change(Guid.NewGuid(), store, SyncSchema.Entities.MenuItem, Guid.NewGuid(),
            t1.Store.Clock.Now(), "T1", [FieldOp.Set("price_minor", "1")]);

        var response = await t1.Transport.PushAsync(new PushRequest("T1", 1, [forged]), default);

        response.Rejected.Should().ContainSingle(r => r.Code == "read_only_entity");
    }

    [Fact]
    public async Task Future_stamped_changes_are_rejected_and_kept_on_the_device()
    {
        var store = Ids.Store();
        var fastClock = new OffsetTime(TimeSpan.FromMinutes(30));
        await using var t1 = await TestTerminal.CreateAsync(env, store, "T1", time: fastClock);
        await t1.Pos.OpenTicketAsync("2", 2);

        var report = await t1.Sync.SyncOnceAsync();

        report.Rejected.Should().Be(1);
        (await t1.Store.RejectedAsync()).Single().Code.Should().Be(RejectionCodes.ClockSkew);
        (await t1.Pos.TicketsAsync()).Should().ContainSingle();
        (await CountChangesAsync(store)).Should().Be(0);
    }

    [Fact]
    public async Task Requests_without_a_valid_token_or_schema_are_refused()
    {
        var store = Ids.Store();
        await using var t1 = await TestTerminal.CreateAsync(env, store, "T1");
        using var anonymous = new HttpClient { BaseAddress = env.ServerUri };
        using var wrongToken = new HttpClient { BaseAddress = env.ServerUri };
        wrongToken.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
        using var noAdmin = new HttpClient { BaseAddress = env.ServerUri };

        (await anonymous.GetAsync(new Uri("api/v1/sync/pull", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await wrongToken.GetAsync(new Uri("api/v1/sync/pull", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await noAdmin.GetAsync(new Uri($"api/v1/admin/stores/{store}/tickets", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var oldSchema = async () => await t1.Transport.PushAsync(new PushRequest("T1", 99, []), default);
        (await oldSchema.Should().ThrowAsync<SyncRejectedException>()).Which.Status.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Pull_cursor_never_skips_a_change_while_pushes_commit_concurrently()
    {
        var store = Ids.Store();
        const int writers = 6;
        const int changesPerWriter = 60;
        var terminals = new List<TestTerminal>();
        for (var i = 0; i < writers; i++)
        {
            terminals.Add(await TestTerminal.CreateAsync(env, store, $"W{i}-{store}",
                options: new SyncClientOptions { PushBatchSize = 7 }));
        }

        await using var reader = await TestTerminal.CreateAsync(env, store, "R-" + store,
            options: new SyncClientOptions { PullBatchSize = 13 });
        foreach (var t in terminals)
        {
            for (var n = 0; n < changesPerWriter; n++)
            {
                await t.Store.WriteAsync(SyncSchema.Entities.Ticket, Guid.NewGuid(), _ => [FieldOp.Set("note", $"n{n}")]);
            }
        }

        // Writers push in small batches, all at once, while the reader pulls in a tight loop with small pages.
        using var done = new CancellationTokenSource();
        var seqs = new List<long>();
        var pulling = Task.Run(async () =>
        {
            while (!done.IsCancellationRequested)
            {
                var since = await reader.Store.GetCursorAsync();
                var page = await reader.Transport.PullAsync(since, 13, null, default);
                seqs.AddRange(page.Changes.Select(c => c.Seq));
                await reader.Store.ApplyPulledAsync(page);
            }
        });
        await Task.WhenAll(terminals.Select(t => t.Sync.SyncOnceAsync()));
        await done.CancelAsync();
        await pulling;
        await reader.Sync.SyncOnceAsync();

        (await reader.Store.AllStatesAsync()).Should().HaveCount(writers * changesPerWriter, "no change was skipped by the cursor");
        seqs.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        (await reader.Store.HashAsync()).Should().Be((await StateAsync(store)).Hash);
        foreach (var t in terminals)
        {
            await t.DisposeAsync();
        }
    }

    [Fact]
    public async Task Pay_versus_void_through_the_server_keeps_the_sale_and_flags_it()
    {
        var store = Ids.Store();
        await using var t1 = await TestTerminal.CreateAsync(env, store, "T1");
        await using var t2 = await TestTerminal.CreateAsync(env, store, "T2");
        var ticket = await t1.Pos.OpenTicketAsync("6", 2);
        await t1.Pos.AddLineAsync(ticket, Menu.Kebab);
        await t1.Sync.SyncOnceAsync();
        await t2.Sync.SyncOnceAsync();

        await t1.Pos.PayAsync(ticket);   // both offline from each other
        await t2.Pos.VoidAsync(ticket);
        await t2.Sync.SyncOnceAsync();
        await t1.Sync.SyncOnceAsync();
        await t2.Sync.SyncOnceAsync();

        var server = (await TicketsAsync(store)).Single();
        server.Status.Should().Be(SyncSchema.TicketStatus.Paid);
        server.Flags.Should().Contain(TicketProjection.VoidOverriddenByPayment);
        (await t2.Pos.TicketsAsync()).Single().Status.Should().Be(SyncSchema.TicketStatus.Paid);
    }

    [Fact]
    public async Task The_change_log_is_append_only_for_the_application_and_the_owner()
    {
        var store = Ids.Store();
        await using var t1 = await TestTerminal.CreateAsync(env, store, "T1");
        await t1.Pos.OpenTicketAsync("1", 1);
        await t1.Sync.SyncOnceAsync();

        await using var app = env.DataSource(env.AppConnectionString);
        var asApp = async () => await Exec(app, $"DELETE FROM sync.changes WHERE store_id = '{store}'");
        (await asApp.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);

        await using var owner = env.DataSource(env.MigratorConnectionString);
        var asOwner = async () => await Exec(owner, $"UPDATE sync.changes SET node_id = 'X' WHERE store_id = '{store}'");
        (await asOwner.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23001");
    }

    [Fact]
    public async Task Menu_changes_from_head_office_replicate_to_terminals()
    {
        var store = Ids.Store();
        using var admin = env.Admin();
        await using var t1 = await TestTerminal.CreateAsync(env, store, "T1");
        await PutMenuAsync(admin, store, "MAIN-07", 45_000);
        await t1.Sync.SyncOnceAsync();
        (await t1.Pos.MenuAsync()).Single().PriceMinor.Should().Be(45_000);

        await PutMenuAsync(admin, store, "MAIN-07", 52_000);
        await t1.Sync.SyncOnceAsync();

        (await t1.Pos.MenuAsync()).Single().PriceMinor.Should().Be(52_000);
    }

    private static async Task Exec(NpgsqlDataSource ds, string sql)
    {
        await using var cmd = ds.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountChangesAsync(string store)
    {
        await using var ds = env.DataSource(env.SuperuserConnectionString);
        await using var cmd = ds.CreateCommand("SELECT count(*) FROM sync.changes WHERE store_id = $1");
        cmd.Parameters.Add(new() { Value = store });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<StoreState> StateAsync(string store)
    {
        using var admin = env.Admin();
        return (await admin.GetFromJsonAsync<StoreState>($"api/v1/admin/stores/{store}/state"))!;
    }

    private async Task<IReadOnlyList<TicketView>> TicketsAsync(string store)
    {
        using var admin = env.Admin();
        return (await admin.GetFromJsonAsync<List<TicketView>>($"api/v1/admin/stores/{store}/tickets"))!;
    }

    private static readonly Dictionary<string, Guid> MenuIds = [];

    private static async Task PutMenuAsync(HttpClient admin, string store, string sku, long price)
    {
        Guid id;
        lock (MenuIds)
        {
            id = MenuIds.TryGetValue(store + sku, out var existing) ? existing : MenuIds[store + sku] = Guid.NewGuid();
        }

        var response = await admin.PutAsJsonAsync($"api/v1/admin/stores/{store}/menu/{id}",
            new { sku, name = sku, category = "Test", priceMinor = price, available = true });
        response.EnsureSuccessStatusCode();
    }

    private sealed record StoreState(string StoreId, long Head, int Entities, string Hash);
}

/// <summary>A wall clock running a fixed offset from real time (a terminal with a wrong clock).</summary>
internal sealed class OffsetTime(TimeSpan offset) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
}
