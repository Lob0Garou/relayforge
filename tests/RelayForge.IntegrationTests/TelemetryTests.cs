using System.Diagnostics;
using System.Diagnostics.Metrics;
using RelayForge.Infrastructure;

namespace RelayForge.IntegrationTests;

public sealed class TelemetryTests
{
    [Fact]
    public void Replay_emits_sanitized_span_and_metric()
    {
        Activity? captured = null; long measurements = 0;
        using var activities = new ActivityListener { ShouldListenTo = x => x.Name == RelayForgeTelemetry.Name, Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData, ActivityStopped = x => captured = x };
        ActivitySource.AddActivityListener(activities);
        using var meters = new MeterListener();
        meters.InstrumentPublished = (instrument, listener) => { if (instrument.Meter.Name == RelayForgeTelemetry.Name) listener.EnableMeasurementEvents(instrument); };
        meters.SetMeasurementEventCallback<long>((_, value, _, _) => measurements += value); meters.Start();
        var id = Guid.NewGuid(); using (RelayForgeTelemetry.StartReplay(id)) RelayForgeTelemetry.RecordReplay("Accepted");
        Assert.NotNull(captured); Assert.Equal("delivery.replay", captured.OperationName); Assert.Equal(id.ToString("D"), captured.GetTagItem("delivery.id"));
        Assert.DoesNotContain(captured.TagObjects, x => x.Key.Contains("payload") || x.Key.Contains("url") || x.Key.Contains("secret") || x.Key.Contains("key"));
        Assert.Equal(1, measurements);
    }
}
