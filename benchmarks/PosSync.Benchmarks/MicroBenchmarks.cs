using System.Text.Json;
using BenchmarkDotNet.Attributes;
using PosSync.Core.Clock;
using PosSync.Core.Merge;
using PosSync.Core.Model;
using PosSync.Core.Protocol;

namespace PosSync.Benchmarks;

/// <summary>CPU cost of the pieces every change goes through: stamping, merging, (de)serialising state.</summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class MicroBenchmarks
{
    private readonly HybridLogicalClock _clock = new(TimeProvider.System);
    private readonly EntitySpec _ticket = SyncSchema.Default.Get(SyncSchema.Entities.Ticket);
    private readonly EntitySpec _line = SyncSchema.Default.Get(SyncSchema.Entities.TicketLine);
    private readonly Guid _id = Guid.NewGuid();
    private Change _noteChange = null!;
    private Change _qtyChange = null!;
    private EntityState _state = null!;
    private string _stateJson = null!;

    [GlobalSetup]
    public void Setup()
    {
        _noteChange = new Change(Guid.NewGuid(), "s", SyncSchema.Entities.Ticket, _id, _clock.Now(), "T1",
            [FieldOp.Set("note", "no onions"), FieldOp.Set("status", "sent")]);
        _qtyChange = new Change(Guid.NewGuid(), "s", SyncSchema.Entities.TicketLine, _id, _clock.Now(), "T1", [FieldOp.Add("qty", 1)]);
        _state = new EntityState("s", SyncSchema.Entities.Ticket, _id);
        foreach (var f in new[] { "table_no", "guest_count", "note", "opened_by", "status", "void_requested" })
        {
            _state.Registers[f] = new Register(f == "status" ? "open" : "value", new Stamp(_clock.Now(), "T1"));
        }

        _stateJson = EntityStateJson.Serialize(_state);
    }

    [Benchmark]
    public HlcTimestamp HlcNow() => _clock.Now();

    [Benchmark]
    public void MergeTicketChange() => MergeEngine.Apply(_ticket, _state, _noteChange);

    [Benchmark]
    public void MergeCounterChange()
    {
        var line = new EntityState("s", SyncSchema.Entities.TicketLine, _id);
        MergeEngine.Apply(_line, line, _qtyChange);
    }

    [Benchmark]
    public string SerializeState() => EntityStateJson.Serialize(_state);

    [Benchmark]
    public EntityState DeserializeState() => EntityStateJson.Deserialize(_stateJson);

    [Benchmark]
    public byte[] SerializeChange() => JsonSerializer.SerializeToUtf8Bytes(_noteChange, SyncJson.Options);

    [Benchmark]
    public ValidationError? ValidateChange() => ChangeValidator.Validate(SyncSchema.Default, _noteChange, fromDevice: true);
}
