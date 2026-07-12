using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using RelayForge.Infrastructure.Security;
using RelayForge.Domain.Retry;

namespace RelayForge.Infrastructure.Delivery;

public sealed class DeliveryDispatcher(DeliveryLeaseRepository repository, IHttpClientFactory clients, IDataProtectionProvider protection, TimeProvider clock, RetryPolicy? retryPolicy = null)
{
    public async Task DispatchAsync(DeliveryLease lease, CancellationToken cancellationToken)
    {
        var startedAt = clock.GetUtcNow(); int? status = null; string? retryAfter = null; var failure = DeliveryFailureClassifier.Processing();
        try
        {
            var body = Encoding.UTF8.GetBytes(lease.Payload);
            var secret = protection.CreateProtector("RelayForge.WebhookEndpointSecrets.v1").Unprotect(lease.ProtectedSecret);
            var timestamp = startedAt.ToUnixTimeSeconds();
            using var request = new HttpRequestMessage(HttpMethod.Post, lease.Url) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new("application/json");
            request.Headers.Add("X-RelayForge-Delivery", lease.DeliveryId.ToString("D"));
            request.Headers.Add("X-RelayForge-Timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.Add("X-RelayForge-Signature", WebhookSigner.Sign(Encoding.UTF8.GetBytes(secret), timestamp, lease.DeliveryId, body));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(lease.Timeout);
            using var response = await clients.CreateClient("RelayForgeDelivery").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            status = (int)response.StatusCode;
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
        await repository.FinalizeAsync(lease, startedAt, status, reasonCode, decision, cancellationToken);
    }

    private async Task<RetryDecision> DecideAsync(DeliveryLease lease, DateTimeOffset startedAt, int? statusCode, DeliveryFailure failure, string? retryAfter)
    {
        try { return (retryPolicy ?? new RetryPolicy(new(), new SystemJitterSource())).Decide(failure, lease.AttemptNumber, retryAfter, clock.GetUtcNow()); }
        catch
        {
            await repository.FinalizeAsync(lease, startedAt, statusCode, "processing_failure", new RetryDecision.DeadLetter("processing_failure"), CancellationToken.None);
            throw;
        }
    }
}
