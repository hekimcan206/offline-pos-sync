using System.Diagnostics.Metrics;

namespace PosSync.Server.Infrastructure;

/// <summary>Sync-specific metrics. Rejections are tagged with their code so a clock-skewed terminal shows up on a dashboard.</summary>
internal sealed class SyncMetrics : IDisposable
{
    public const string MeterName = "PosSync.Server";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _accepted;
    private readonly Counter<long> _duplicates;
    private readonly Counter<long> _rejected;
    private readonly Counter<long> _pulled;
    private readonly Histogram<int> _batch;

    public SyncMetrics()
    {
        _accepted = _meter.CreateCounter<long>("possync.changes.accepted", description: "Changes stored for the first time.");
        _duplicates = _meter.CreateCounter<long>("possync.changes.duplicate", description: "Replayed changes the server already held.");
        _rejected = _meter.CreateCounter<long>("possync.changes.rejected", description: "Changes refused, by code.");
        _pulled = _meter.CreateCounter<long>("possync.changes.pulled", description: "Changes sent to devices.");
        _batch = _meter.CreateHistogram<int>("possync.push.batch_size", description: "Changes per push request.");
    }

    public void Push(int batch, int accepted, int duplicates)
    {
        _batch.Record(batch);
        _accepted.Add(accepted);
        _duplicates.Add(duplicates);
    }

    public void Rejected(string code) => _rejected.Add(1, new KeyValuePair<string, object?>("code", code));

    public void Pulled(int count) => _pulled.Add(count);

    public void Dispose() => _meter.Dispose();
}
