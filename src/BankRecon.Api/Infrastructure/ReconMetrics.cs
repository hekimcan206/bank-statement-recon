using System.Diagnostics.Metrics;

namespace BankRecon.Api.Infrastructure;

/// <summary>Domain metrics: what operations staff watch besides request rates.</summary>
internal sealed class ReconMetrics : IDisposable
{
    public const string MeterName = "BankRecon";

    private readonly Meter _meter;
    private readonly Counter<long> _entriesImported;
    private readonly Counter<long> _matches;
    private readonly Counter<long> _queued;
    private readonly Counter<long> _leaseConflicts;
    private readonly Histogram<double> _runDuration;

    public ReconMetrics(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(MeterName);
        _entriesImported = _meter.CreateCounter<long>("recon.bank_lines.imported", description: "Bank lines imported from statements.");
        _matches = _meter.CreateCounter<long>("recon.matches", description: "Matches created, by rule.");
        _queued = _meter.CreateCounter<long>("recon.work_items.queued", description: "Items sent to the work queue, by reason.");
        _leaseConflicts = _meter.CreateCounter<long>("recon.work_items.lease_conflicts", description: "Resolve or heartbeat attempts with a stale lease.");
        _runDuration = _meter.CreateHistogram<double>("recon.run.duration", unit: "s", description: "Reconciliation run duration.");
    }

    public void EntriesImported(long count) => _entriesImported.Add(count);

    public void Matched(string rule, long count) => _matches.Add(count, new KeyValuePair<string, object?>("rule", rule));

    public void Queued(string reason, long count) => _queued.Add(count, new KeyValuePair<string, object?>("reason", reason));

    public void LeaseConflict() => _leaseConflicts.Add(1);

    public void RunCompleted(TimeSpan duration) => _runDuration.Record(duration.TotalSeconds);

    public void Dispose() => _meter.Dispose();
}
