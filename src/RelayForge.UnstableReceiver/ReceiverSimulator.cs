using System.Collections.Concurrent;

namespace RelayForge.UnstableReceiver;

public sealed record ReceiverScenario(int FailuresBeforeSuccess, int FailureStatusCode, int DelayMilliseconds);

public sealed record DeliverySimulationState(
    string DeliveryId,
    int Attempts,
    int FailuresBeforeSuccess,
    int FailureStatusCode,
    int DelayMilliseconds);

/// <summary>
/// Thread-safe, in-memory failure simulator for local demonstrations. This state is deliberately
/// ephemeral and process-local; it is not a broker, durable queue, or delivery source of truth.
/// </summary>
public sealed class ReceiverSimulator
{
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private ReceiverScenario _scenario = new(0, StatusCodes.Status503ServiceUnavailable, 0);

    public ReceiverScenario Scenario => Volatile.Read(ref _scenario);

    public void Configure(ReceiverScenario scenario) => Volatile.Write(ref _scenario, scenario);

    public DeliverySimulationState RecordAttempt(string deliveryId)
    {
        var scenario = Scenario;
        var attempts = _attempts.AddOrUpdate(deliveryId, 1, static (_, current) => checked(current + 1));
        return new(deliveryId, attempts, scenario.FailuresBeforeSuccess, scenario.FailureStatusCode, scenario.DelayMilliseconds);
    }

    public DeliverySimulationState? GetState(string deliveryId)
    {
        if (!_attempts.TryGetValue(deliveryId, out var attempts))
        {
            return null;
        }

        var scenario = Scenario;
        return new(deliveryId, attempts, scenario.FailuresBeforeSuccess, scenario.FailureStatusCode, scenario.DelayMilliseconds);
    }
}
