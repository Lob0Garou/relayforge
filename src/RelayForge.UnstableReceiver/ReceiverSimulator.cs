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
    private SimulatorState _state = new(
        new(0, StatusCodes.Status503ServiceUnavailable, 0),
        new());

    /// <summary>Atomically installs a scenario and discards all counters from the previous run.</summary>
    public void Configure(ReceiverScenario scenario) =>
        Volatile.Write(ref _state, new(scenario, new()));

    public DeliverySimulationState RecordAttempt(Guid deliveryId)
    {
        var state = Volatile.Read(ref _state);
        var attempts = state.Attempts.AddOrUpdate(deliveryId, 1, static (_, current) => checked(current + 1));
        return CreateDeliveryState(deliveryId, attempts, state.Scenario);
    }

    public DeliverySimulationState? GetState(Guid deliveryId)
    {
        var state = Volatile.Read(ref _state);
        if (!state.Attempts.TryGetValue(deliveryId, out var attempts))
        {
            return null;
        }

        return CreateDeliveryState(deliveryId, attempts, state.Scenario);
    }

    private static DeliverySimulationState CreateDeliveryState(Guid deliveryId, int attempts, ReceiverScenario scenario) =>
        new(deliveryId.ToString("D"), attempts, scenario.FailuresBeforeSuccess, scenario.FailureStatusCode, scenario.DelayMilliseconds);

    private sealed record SimulatorState(
        ReceiverScenario Scenario,
        ConcurrentDictionary<Guid, int> Attempts);
}
