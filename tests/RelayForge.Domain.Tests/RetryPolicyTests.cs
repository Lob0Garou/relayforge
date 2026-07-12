using RelayForge.Domain.Retry;
using Xunit;

namespace RelayForge.Domain.Tests;

public sealed class RetryPolicyTests
{
    public static TheoryData<int, DeliveryFailureKind> Statuses => new()
    {
        { 200, DeliveryFailureKind.Success }, { 299, DeliveryFailureKind.Success },
        { 300, DeliveryFailureKind.Permanent }, { 399, DeliveryFailureKind.Permanent },
        { 400, DeliveryFailureKind.Permanent }, { 408, DeliveryFailureKind.Transient },
        { 425, DeliveryFailureKind.Transient }, { 429, DeliveryFailureKind.Transient },
        { 499, DeliveryFailureKind.Permanent }, { 500, DeliveryFailureKind.Transient },
        { 599, DeliveryFailureKind.Transient }, { 600, DeliveryFailureKind.Permanent }
    };

    [Theory, MemberData(nameof(Statuses))]
    public void Classifies_status_boundaries(int status, DeliveryFailureKind expected) =>
        Assert.Equal(expected, DeliveryFailureClassifier.FromHttpStatus(status).Kind);

    [Fact]
    public void Timeout_and_network_are_transient()
    {
        Assert.Equal(DeliveryFailureKind.Transient, DeliveryFailureClassifier.Timeout().Kind);
        Assert.Equal(DeliveryFailureKind.Transient, DeliveryFailureClassifier.Network().Kind);
    }

    [Theory]
    [InlineData(1, true)] [InlineData(4, true)] [InlineData(5, false)]
    public void Five_total_attempts_are_allowed(int attempt, bool retries)
    {
        var decision = Policy().Decide(DeliveryFailureClassifier.FromHttpStatus(503), attempt, null, DateTimeOffset.UnixEpoch);
        Assert.Equal(retries, decision is RetryDecision.Retry);
    }

    [Fact]
    public void Permanent_failure_dead_letters_immediately() =>
        Assert.Equal("http_permanent", Assert.IsType<RetryDecision.DeadLetter>(Policy().Decide(DeliveryFailureClassifier.FromHttpStatus(400), 1, null, DateTimeOffset.UnixEpoch)).ReasonCode);

    [Fact]
    public void Exponential_delay_has_injected_deterministic_bounded_jitter()
    {
        var low = Policy(0).Decide(DeliveryFailureClassifier.Network(), 3, null, DateTimeOffset.UnixEpoch);
        var high = Policy(1).Decide(DeliveryFailureClassifier.Network(), 3, null, DateTimeOffset.UnixEpoch);
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.IsType<RetryDecision.Retry>(low).Delay);
        Assert.Equal(TimeSpan.FromSeconds(6), Assert.IsType<RetryDecision.Retry>(high).Delay);
        Assert.Equal(Assert.IsType<RetryDecision.Retry>(low).Delay, Assert.IsType<RetryDecision.Retry>(Policy(0).Decide(DeliveryFailureClassifier.Network(), 3, null, DateTimeOffset.UnixEpoch)).Delay);
    }

    [Theory]
    [InlineData("120", 30)] [InlineData("-1", 1)] [InlineData("garbage", 1)]
    public void Retry_after_delta_is_robust_and_capped(string value, int seconds)
    {
        var decision = Assert.IsType<RetryDecision.Retry>(Policy().Decide(DeliveryFailureClassifier.FromHttpStatus(429), 1, value, DateTimeOffset.UnixEpoch));
        Assert.Equal(TimeSpan.FromSeconds(seconds), decision.Delay);
    }

    [Fact]
    public void Retry_after_date_is_respected_but_only_for_transient()
    {
        var now = new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(20), Assert.IsType<RetryDecision.Retry>(Policy().Decide(DeliveryFailureClassifier.FromHttpStatus(503), 1, now.AddSeconds(20).ToString("R"), now)).Delay);
        Assert.IsType<RetryDecision.DeadLetter>(Policy().Decide(DeliveryFailureClassifier.FromHttpStatus(400), 1, "20", now));
    }

    [Fact]
    public void Retry_after_past_http_date_means_immediate_retry_and_future_date_is_capped()
    {
        var now = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
        var failure = DeliveryFailureClassifier.FromHttpStatus(429);
        Assert.Equal(TimeSpan.Zero, Assert.IsType<RetryDecision.Retry>(Policy().Decide(failure, 1, now.AddMinutes(-1).ToString("R"), now)).Delay);
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.IsType<RetryDecision.Retry>(Policy().Decide(failure, 1, now.AddHours(1).ToString("R"), now)).Delay);
    }

    [Theory]
    [InlineData("9223372036854775807")]
    [InlineData("9223372036854775806")]
    [InlineData("10675199")]
    public void Extreme_retry_after_delta_never_throws_and_is_capped(string header)
    {
        var exception = Record.Exception(() => Policy().Decide(DeliveryFailureClassifier.FromHttpStatus(429), 1, header, DateTimeOffset.UnixEpoch));
        Assert.Null(exception);
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.IsType<RetryDecision.Retry>(Policy().Decide(DeliveryFailureClassifier.FromHttpStatus(429), 1, header, DateTimeOffset.UnixEpoch)).Delay);
    }

    private static RetryPolicy Policy(double jitter = .5) => new(new RetryPolicyOptions(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), .5), new FixedJitter(jitter));
    private sealed class FixedJitter(double value) : IJitterSource { public double NextUnit() => value; }
}
