using System.Diagnostics;
using System.Diagnostics.Metrics;
using RelayForge.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using RelayForge.Infrastructure.Delivery;
using Microsoft.AspNetCore.DataProtection;
using RelayForge.Domain.Endpoints;
using RelayForge.Domain.Events;
using RelayForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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
        Assert.NotNull(captured); Assert.Equal("delivery.replay", captured.OperationName); Assert.DoesNotContain(id.ToString("D"), string.Join('|', captured.TagObjects));
        Assert.DoesNotContain(captured.TagObjects, x => x.Key.Contains("payload") || x.Key.Contains("url") || x.Key.Contains("secret") || x.Key.Contains("key"));
        Assert.Equal(1, measurements);
    }
}

[Collection(PostgreSqlCollection.Name)]
public sealed class DispatchTelemetryTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Dispatch_span_contains_only_safe_tags()
    {
        await fixture.ResetAsync(); Activity? captured = null; var allTags = new List<string>();
        using var listener = new ActivityListener { ShouldListenTo = _ => true, Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData, ActivityStopped = x => { allTags.AddRange(x.TagObjects.Select(t => $"{t.Key}={t.Value}")); if (x.OperationName == "delivery.dispatch_attempt") captured = x; } }; ActivitySource.AddActivityListener(listener);
        var protector = fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("RelayForge.WebhookEndpointSecrets.v1");
        var endpointResult = WebhookEndpoint.Create("Receiver", "http://localhost:1/url-sentinel", TimeSpan.FromSeconds(1), protector.Protect("secret-sentinel")); Assert.True(endpointResult.TryGetValue(out var endpoint));
        var eventResult = IncomingEvent.Create(endpoint.Id, "safe.type", "{\"payload\":\"payload-sentinel\"}", Guid.NewGuid().ToString("N"), new string('b', 64)); Assert.True(eventResult.TryGetValue(out var incoming));
        await using var scope = fixture.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<RelayForgeDbContext>(); db.Add(endpoint); db.Add(incoming); await db.SaveChangesAsync();
        var lease = Assert.Single(await scope.ServiceProvider.GetRequiredService<DeliveryLeaseRepository>().ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        await scope.ServiceProvider.GetRequiredService<DeliveryDispatcher>().DispatchAsync(lease, default);
        Assert.NotNull(captured); var tags = string.Join('|', allTags);
        Assert.DoesNotContain("url-sentinel", tags); Assert.DoesNotContain("localhost", tags); Assert.DoesNotContain("payload-sentinel", tags); Assert.DoesNotContain("secret-sentinel", tags);
    }

    [Fact]
    public async Task Stale_finalize_emits_rejected_metric_without_delivery_outcome()
    {
        await fixture.ResetAsync(); var measurements = new List<string>(); Activity? dispatch = null;
        using var meter = new MeterListener(); meter.InstrumentPublished = (i, l) => { if (i.Meter.Name == RelayForgeTelemetry.Name) l.EnableMeasurementEvents(i); };
        meter.SetMeasurementEventCallback<long>((i, _, tags, _) => measurements.Add(i.Name + ":" + string.Join(',', tags.ToArray().Select(x => $"{x.Key}={x.Value}")))); meter.Start();
        using var activities = new ActivityListener { ShouldListenTo = x => x.Name == RelayForgeTelemetry.Name, Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData, ActivityStopped = x => { if (x.OperationName == "delivery.dispatch_attempt") dispatch = x; } }; ActivitySource.AddActivityListener(activities);
        var protector = fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("RelayForge.WebhookEndpointSecrets.v1");
        var endpointResult = WebhookEndpoint.Create("Receiver", "http://localhost:1/stale", TimeSpan.FromSeconds(1), protector.Protect("safe-secret")); Assert.True(endpointResult.TryGetValue(out var endpoint));
        var eventResult = IncomingEvent.Create(endpoint.Id, "safe.type", "{}", Guid.NewGuid().ToString("N"), new string('c', 64)); Assert.True(eventResult.TryGetValue(out var incoming));
        await using var scope = fixture.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<RelayForgeDbContext>(); db.Add(endpoint); db.Add(incoming); await db.SaveChangesAsync();
        var lease = Assert.Single(await scope.ServiceProvider.GetRequiredService<DeliveryLeaseRepository>().ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE deliveries SET version = version + 1 WHERE \"Id\" = {lease.DeliveryId}");
        await scope.ServiceProvider.GetRequiredService<DeliveryDispatcher>().DispatchAsync(lease, default);
        Assert.Equal("finalize_rejected", dispatch?.GetTagItem("outcome"));
        Assert.Contains(measurements, x => x.StartsWith("relayforge.delivery.finalize_rejected", StringComparison.Ordinal));
        Assert.DoesNotContain(measurements, x => x.StartsWith("relayforge.delivery.outcomes", StringComparison.Ordinal));
        Assert.DoesNotContain(lease.DeliveryId.ToString("D"), string.Join('|', dispatch!.TagObjects));
    }
}
