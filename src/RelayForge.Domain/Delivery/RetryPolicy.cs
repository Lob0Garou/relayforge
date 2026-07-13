using System.Globalization;

namespace RelayForge.Domain.Retry;

public enum DeliveryFailureKind { Success, Transient, Permanent }
public sealed record DeliveryFailure(DeliveryFailureKind Kind, string ReasonCode);

public static class DeliveryFailureClassifier
{
    public static DeliveryFailure FromHttpStatus(int status) => status switch
    {
        >= 200 and <= 299 => new(DeliveryFailureKind.Success, "delivered"),
        408 or 425 or 429 or >= 500 and <= 599 => new(DeliveryFailureKind.Transient, "http_transient"),
        _ => new(DeliveryFailureKind.Permanent, "http_permanent")
    };
    public static DeliveryFailure Timeout() => new(DeliveryFailureKind.Transient, "timeout");
    public static DeliveryFailure Network() => new(DeliveryFailureKind.Transient, "network");
    public static DeliveryFailure Cancelled() => new(DeliveryFailureKind.Transient, "delivery_cancelled");
    public static DeliveryFailure Processing() => new(DeliveryFailureKind.Permanent, "processing_failure");
}

public abstract record RetryDecision
{
    public sealed record Retry(TimeSpan Delay) : RetryDecision;
    public sealed record DeadLetter(string ReasonCode) : RetryDecision;
}

public interface IJitterSource { double NextUnit(); }
public sealed class SystemJitterSource : IJitterSource
{
    private readonly Random _random = new();
    private readonly object _gate = new();
    public double NextUnit() { lock (_gate) return _random.NextDouble(); }
}

/// <summary>Retry policy counts attempts, not retries. The default permits five total attempts.</summary>
public sealed record RetryPolicyOptions(int MaxAttempts = 5, TimeSpan BaseDelay = default, TimeSpan MaxDelay = default, double JitterRatio = .2)
{
    public const int DefaultMaxAttempts = 5;
    public TimeSpan EffectiveBaseDelay => BaseDelay == default ? TimeSpan.FromSeconds(2) : BaseDelay;
    public TimeSpan EffectiveMaxDelay => MaxDelay == default ? TimeSpan.FromMinutes(5) : MaxDelay;
}

public sealed class RetryPolicy(RetryPolicyOptions options, IJitterSource jitter)
{
    public RetryDecision Decide(DeliveryFailure failure, int attemptNumber, string? retryAfter, DateTimeOffset now)
    {
        if (failure.Kind != DeliveryFailureKind.Transient || attemptNumber >= options.MaxAttempts)
            return new RetryDecision.DeadLetter(failure.Kind == DeliveryFailureKind.Transient ? "attempts_exhausted" : failure.ReasonCode);

        var max = options.EffectiveMaxDelay;
        var exponent = Math.Max(0, attemptNumber - 1);
        var rawTicks = options.EffectiveBaseDelay.Ticks * Math.Pow(2, exponent);
        var capped = TimeSpan.FromTicks((long)Math.Min(rawTicks, max.Ticks));
        var unit = Math.Clamp(jitter.NextUnit(), 0, 1);
        var factor = 1 - options.JitterRatio + (2 * options.JitterRatio * unit);
        var delay = TimeSpan.FromTicks((long)Math.Min(capped.Ticks * factor, max.Ticks));
        if (TryParseRetryAfter(retryAfter, now, max, out var requested)) delay = requested;
        return new RetryDecision.Retry(delay);
    }

    // Negative delta-seconds are invalid and fall back to the policy backoff. A valid past HTTP-date means retry now.
    private static bool TryParseRetryAfter(string? value, DateTimeOffset now, TimeSpan maxDelay, out TimeSpan delay)
    {
        delay = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (long.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
        {
            delay = seconds >= Math.Ceiling(maxDelay.TotalSeconds) ? maxDelay : TimeSpan.FromSeconds(seconds);
            return true;
        }
        if (DateTimeOffset.TryParseExact(value.Trim(), "R", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        { var requested = date <= now ? TimeSpan.Zero : date - now; delay = requested > maxDelay ? maxDelay : requested; return true; }
        return false;
    }
}
