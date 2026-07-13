using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using RelayForge.Infrastructure.Security;
using RelayForge.Domain.Retry;
using RelayForge.Infrastructure;

namespace RelayForge.Infrastructure.Delivery;

public sealed class FinalizedDeliveryPolicyException(Guid deliveryId, Exception innerException) : Exception("delivery_policy_failure", innerException)
{
    public Guid DeliveryId { get; } = deliveryId;
}

public sealed class DeliveryDispatcher(DeliveryLeaseRepository repository, IHttpClientFactory clients, IDataProtectionProvider protection, TimeProvider clock, RetryPolicy? retryPolicy = null, OutboundDeliveryOptions? responseOptions = null)
{
    public async Task DispatchAsync(DeliveryLease lease, CancellationToken cancellationToken)
    {
        using var activity = RelayForgeTelemetry.ActivitySource.StartActivity("delivery.dispatch_attempt");
        activity?.SetTag("attempt.number", lease.AttemptNumber);
        var startedAt = clock.GetUtcNow(); int? status = null; string? retryAfter = null; string? responseSnippet = null; var failure = DeliveryFailureClassifier.Processing();
        try
        {
            var body = Encoding.UTF8.GetBytes(lease.Payload);
            var secret = protection.CreateProtector("RelayForge.WebhookEndpointSecrets.v1").Unprotect(lease.ProtectedSecret);
            var timestamp = startedAt.ToUnixTimeSeconds();
            using var request = new HttpRequestMessage(HttpMethod.Post, lease.Url) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new("application/json");
            request.Headers.Add("RelayForge-Delivery-Id", lease.DeliveryId.ToString("D"));
            request.Headers.Add("RelayForge-Timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var signature = WebhookSigner.Sign(Encoding.UTF8.GetBytes(secret), timestamp, lease.DeliveryId, body);
            request.Headers.Add("RelayForge-Signature", signature);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(lease.Timeout);
            using var response = await clients.CreateClient("RelayForgeDelivery").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            status = (int)response.StatusCode;
            responseSnippet = await BoundedResponseReader.ReadAsync(response.Content, (responseOptions ?? new()).MaxResponseSnippetBytes, [lease.Payload, signature, secret], timeout.Token);
            failure = DeliveryFailureClassifier.FromHttpStatus(status.Value);
            retryAfter = response.Headers.RetryAfter?.ToString();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { failure = DeliveryFailureClassifier.Timeout(); }
        catch (OperationCanceledException)
        {
            var cancelled = DeliveryFailureClassifier.Cancelled();
            var cancellationDecision = await DecideAsync(lease, startedAt, null, cancelled, null);
            await repository.FinalizeAsync(lease, startedAt, null, cancelled.ReasonCode, cancellationDecision, CancellationToken.None);
            throw;
        }
        catch (HttpRequestException) { failure = DeliveryFailureClassifier.Network(); }
        catch (Exception) { failure = DeliveryFailureClassifier.Processing(); }
        var decision = failure.Kind == DeliveryFailureKind.Success ? null : await DecideAsync(lease, startedAt, status, failure, retryAfter);
        var reasonCode = decision is RetryDecision.DeadLetter deadLetter ? deadLetter.ReasonCode : failure.ReasonCode;
        var finalized = await repository.FinalizeAsync(lease, startedAt, status, reasonCode, decision, cancellationToken, responseSnippet);
        if (!finalized)
        {
            activity?.SetTag("outcome", "finalize_rejected");
            RelayForgeTelemetry.FinalizationRejected.Add(1);
            return;
        }
        var outcome = decision switch { null => "delivered", RetryDecision.Retry => "retry", _ => "dead_lettered" };
        activity?.SetTag("outcome", outcome); RelayForgeTelemetry.DeliveryOutcomes.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        RelayForgeTelemetry.DeliveryDuration.Record(Math.Max(0, (clock.GetUtcNow() - startedAt).TotalMilliseconds), new KeyValuePair<string, object?>("outcome", outcome));
    }

    private async Task<RetryDecision> DecideAsync(DeliveryLease lease, DateTimeOffset startedAt, int? statusCode, DeliveryFailure failure, string? retryAfter)
    {
        try { return (retryPolicy ?? new RetryPolicy(new(), new SystemJitterSource())).Decide(failure, lease.AttemptNumber, retryAfter, clock.GetUtcNow()); }
        catch (Exception exception)
        {
            var finalized = await repository.FinalizeAsync(lease, startedAt, statusCode, "processing_failure", new RetryDecision.DeadLetter("processing_failure"), CancellationToken.None);
            if (finalized) throw new FinalizedDeliveryPolicyException(lease.DeliveryId, exception);
            throw;
        }
    }
}
