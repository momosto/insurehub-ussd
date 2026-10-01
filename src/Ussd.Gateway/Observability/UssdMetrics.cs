using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Ussd.Gateway.Observability;

/// <summary>Sessions started/completed, views and drop-offs per node, backend fallbacks (architecture §6).
/// Exported via OpenTelemetry and summarised at /stats for the demo.</summary>
public sealed class UssdMetrics
{
    public const string Name = "InsureHub.Ussd";
    public static readonly ActivitySource Activity = new(Name);

    private readonly Counter<long> _started;
    private readonly Counter<long> _completed;
    private readonly Counter<long> _views;
    private readonly Counter<long> _fallbacks;
    private readonly Histogram<double> _latency;
    private readonly ConcurrentDictionary<string, long> _viewsByNode = new();
    private readonly ConcurrentDictionary<string, long> _endsByNode = new();
    private long _sessions, _completedSessions, _fallbackCount;

    public UssdMetrics(IMeterFactory meters)
    {
        var meter = meters.Create(Name);
        _started = meter.CreateCounter<long>("ussd.sessions.started");
        _completed = meter.CreateCounter<long>("ussd.sessions.completed");
        _views = meter.CreateCounter<long>("ussd.node.views");
        _fallbacks = meter.CreateCounter<long>("ussd.backend.fallbacks");
        _latency = meter.CreateHistogram<double>("ussd.request.duration", "ms");
    }

    public void SessionStarted()
    {
        Interlocked.Increment(ref _sessions);
        _started.Add(1);
    }

    public void Viewed(string node)
    {
        _viewsByNode.AddOrUpdate(node, 1, (_, v) => v + 1);
        _views.Add(1, new KeyValuePair<string, object?>("ussd.node", node));
    }

    public void Ended(string node, bool completed)
    {
        _endsByNode.AddOrUpdate(node, 1, (_, v) => v + 1);
        if (!completed) return;
        Interlocked.Increment(ref _completedSessions);
        _completed.Add(1, new KeyValuePair<string, object?>("ussd.node", node));
    }

    public void Fallback()
    {
        Interlocked.Increment(ref _fallbackCount);
        _fallbacks.Add(1);
    }

    public void Duration(double ms) => _latency.Record(ms);

    public object Snapshot() => new
    {
        sessions = Interlocked.Read(ref _sessions),
        completed = Interlocked.Read(ref _completedSessions),
        backendFallbacks = Interlocked.Read(ref _fallbackCount),
        viewsByNode = _viewsByNode.OrderBy(k => k.Key).ToDictionary(),
        endsByNode = _endsByNode.OrderBy(k => k.Key).ToDictionary(),
    };
}
