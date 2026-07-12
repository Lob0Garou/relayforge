using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using RelayForge.Infrastructure.Security;

namespace RelayForge.Infrastructure.Delivery;

public sealed class DeliveryDispatcher(DeliveryLeaseRepository repository, IHttpClientFactory clients, IDataProtectionProvider protection, TimeProvider clock)
{
    public async Task DispatchAsync(DeliveryLease lease, CancellationToken cancellationToken)
    {
        var startedAt = clock.GetUtcNow(); int? status = null; string? error = null;
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
            if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices) error = $"HTTP {(int)response.StatusCode}";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { error = "Request timed out."; }
        catch (OperationCanceledException)
        {
            await repository.ReleaseAsync(lease, startedAt, "Delivery cancelled.", CancellationToken.None);
            throw;
        }
        catch (HttpRequestException exception) { error = $"Network error: {exception.HttpRequestError}."; }
        catch (Exception) { error = "Delivery processing failed."; }
        await repository.FinalizeAsync(lease, startedAt, status, error, cancellationToken);
    }
}
