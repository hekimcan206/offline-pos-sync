using PosSync.Core.Clock;
using PosSync.Core.Merge;
using PosSync.Core.Model;
using PosSync.Core.Tickets;
using PosSync.UnitTests.Support;

namespace PosSync.UnitTests.Conflicts;

/// <summary>
/// A tiny restaurant for conflict scenarios: a server replica and two terminals, each with its own wall clock, HLC and
/// outbox. Ids are deterministic so the same scenario under a different delivery order can be compared byte for byte.
/// </summary>
public sealed class ScenarioWorld
{
    public const string Store = "store-001";
    private readonly List<Change> _serverLog = [];
    private int _ids;

    public ScenarioWorld(SyncSchema schema, bool reverseDelivery)
    {
        Schema = schema;
        ReverseDelivery = reverseDelivery;
        Server = new InMemoryReplica(schema);
        ServerClock = new HybridLogicalClock(ServerTime);
        T1 = new ScenarioTerminal(this, "T1");
        T2 = new ScenarioTerminal(this, "T2");
    }

    public SyncSchema Schema { get; }
    public bool ReverseDelivery { get; }
    public InMemoryReplica Server { get; }
    internal ManualTime ServerTime { get; } = new();
    public HybridLogicalClock ServerClock { get; }
    public ScenarioTerminal T1 { get; }
    public ScenarioTerminal T2 { get; }

    public IEnumerable<InMemoryReplica> Replicas => [Server, T1.Replica, T2.Replica];

    internal Guid NextId() => new($"00000000-0000-4000-8000-{++_ids:D12}");

    /// <summary>Push the terminal outbox, then pull everything the server has.</summary>
    public void Sync(ScenarioTerminal terminal)
    {
        Push(terminal);
        terminal.Outbox.Clear();
        foreach (var change in Ordered(_serverLog))
        {
            terminal.Clock.Observe(change.Hlc);
            terminal.Replica.Apply(change);
        }
    }

    /// <summary>The server stores the push but the acknowledgement is lost: the outbox keeps the changes.</summary>
    public void PushWithLostAck(ScenarioTerminal terminal) => Push(terminal);

    public void SyncAll()
    {
        Sync(T1);
        Sync(T2);
        Sync(T1);
    }

    public Change ServerWrite(string entity, Guid id, IReadOnlyList<FieldOp> ops)
    {
        var change = new Change(NextId(), Store, entity, id, ServerClock.Now(), "server", ops);
        Server.Apply(change);
        _serverLog.Add(change);
        return change;
    }

    public TicketView Ticket(InMemoryReplica replica, Guid id) =>
        TicketProjection.Build(replica.States).Single(t => t.Id == id);

    public IReadOnlyList<TicketView> Tickets(InMemoryReplica replica) => TicketProjection.Build(replica.States);

    /// <summary>T1 opens a ticket for table 12 with one lentil soup; both terminals then sync, so it is shared.</summary>
    public (Guid Ticket, Guid Line) SharedTicket()
    {
        var ticket = T1.OpenTicket("12");
        var line = T1.AddLine(ticket, "SOUP-01", "Lentil soup", 9_000, 1);
        SyncAll();
        return (ticket, line);
    }

    private void Push(ScenarioTerminal terminal)
    {
        foreach (var change in Ordered(terminal.Outbox))
        {
            ServerClock.Observe(change.Hlc);
            if (Server.Apply(change))
            {
                _serverLog.Add(change);
            }
        }
    }

    private List<Change> Ordered(List<Change> changes) =>
        ReverseDelivery ? Enumerable.Reverse(changes).ToList() : changes.ToList();
}

public sealed class ScenarioTerminal
{
    private readonly ScenarioWorld _world;

    internal ScenarioTerminal(ScenarioWorld world, string name)
    {
        _world = world;
        Name = name;
        Clock = new HybridLogicalClock(Time, maxForwardDrift: TimeSpan.FromDays(1));
        Replica = new InMemoryReplica(world.Schema);
    }

    public string Name { get; }
    internal ManualTime Time { get; } = new();
    public HybridLogicalClock Clock { get; }
    public InMemoryReplica Replica { get; }
    public List<Change> Outbox { get; } = [];

    /// <summary>Moves this terminal's wall clock (a later edit, or a clock that is simply wrong).</summary>
    public ScenarioTerminal At(TimeSpan offsetFromEpoch)
    {
        Time.Now = ManualTime.Epoch + offsetFromEpoch;
        return this;
    }

    public Change Write(string entity, Guid id, Func<EntityState?, IReadOnlyList<FieldOp>> edit)
    {
        var spec = _world.Schema.Get(entity);
        var state = Replica.Find(entity, id);
        var ops = TicketOps.ForStrategy(spec, state, edit(state));
        var change = new Change(_world.NextId(), ScenarioWorld.Store, entity, id, Clock.Now(), Name, ops);
        ChangeValidator.Validate(_world.Schema, change, fromDevice: true).Should().BeNull();
        Replica.Apply(change);
        Outbox.Add(change);
        return change;
    }

    public Guid OpenTicket(string table)
    {
        var id = _world.NextId();
        Write(SyncSchema.Entities.Ticket, id, _ => TicketOps.OpenTicket(table, 2, Name));
        return id;
    }

    public Guid AddLine(Guid ticket, string sku, string name, long price, int qty)
    {
        var id = _world.NextId();
        var spec = _world.Schema.Get(SyncSchema.Entities.TicketLine);
        Write(SyncSchema.Entities.TicketLine, id, _ => TicketOps.AddLine(spec, ticket, sku, name, price, qty));
        return id;
    }

    public Change ChangeQty(Guid line, int delta) =>
        Write(SyncSchema.Entities.TicketLine, line,
            s => TicketOps.ChangeQty(_world.Schema.Get(SyncSchema.Entities.TicketLine), s!, delta));

    public Change Set(Guid ticket, string field, string value) =>
        Write(SyncSchema.Entities.Ticket, ticket, _ => [FieldOp.Set(field, value)]);

    public Change Pay(Guid ticket) => Write(SyncSchema.Entities.Ticket, ticket, _ => TicketOps.Pay());

    public Change Void(Guid ticket) => Write(SyncSchema.Entities.Ticket, ticket, _ => TicketOps.Void());

    public Change SendToKitchen(Guid ticket) => Write(SyncSchema.Entities.Ticket, ticket, _ => TicketOps.SendToKitchen());

    public Change VoidLine(Guid line) => Write(SyncSchema.Entities.TicketLine, line, _ => TicketOps.VoidLine());
}
