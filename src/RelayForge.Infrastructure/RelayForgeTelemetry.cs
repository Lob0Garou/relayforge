using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RelayForge.Infrastructure;

public static class RelayForgeTelemetry
{
    private static long _backlog;
    public const string Name = "RelayForge";
    public static readonly ActivitySource ActivitySource = new(Name);
    public static readonly Meter Meter = new(Name);
    public static readonly Counter<long> Events = Meter.CreateCounter<long>("relayforge.events");
    public static readonly Counter<long> DeliveryOutcomes = Meter.CreateCounter<long>("relayforge.delivery.outcomes");
    public static readonly Counter<long> Replays = Meter.CreateCounter<long>("relayforge.delivery.replays");
    public static readonly Histogram<double> DeliveryDuration = Meter.CreateHistogram<double>("relayforge.delivery.duration", "ms");
    public static readonly ObservableGauge<long> Backlog = Meter.CreateObservableGauge("relayforge.delivery.backlog", () => Interlocked.Read(ref _backlog));
    public static Activity? StartReplay(Guid deliveryId) { var a = ActivitySource.StartActivity("delivery.replay"); a?.SetTag("delivery.id", deliveryId.ToString("D")); return a; }
    public static void RecordReplay(string outcome) => Replays.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
    public static void SetBacklog(long value) => Interlocked.Exchange(ref _backlog, value);
}
