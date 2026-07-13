using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace RelayForge.Infrastructure.Delivery;

public sealed class OutboundDeliveryOptions
{
    public int MaxResponseSnippetBytes { get; set; } = 4096;
    public TimeSpan PooledConnectionLifetime { get; set; } = TimeSpan.FromMinutes(2);
    public string[] AllowedPrivateHosts { get; set; } = [];

    public static void Validate(OutboundDeliveryOptions options, bool isDevelopment)
    {
        if (options.MaxResponseSnippetBytes is < 1 or > 65_536)
            throw new InvalidOperationException("OutboundDelivery:MaxResponseSnippetBytes must be between 1 and 65536.");
        if (options.PooledConnectionLifetime < TimeSpan.FromSeconds(5) || options.PooledConnectionLifetime > TimeSpan.FromMinutes(10))
            throw new InvalidOperationException("OutboundDelivery:PooledConnectionLifetime must be between 5 seconds and 10 minutes.");
        if (!isDevelopment && options.AllowedPrivateHosts.Length != 0)
            throw new InvalidOperationException("OutboundDelivery:AllowedPrivateHosts is permitted only in Development.");
        _ = new DestinationPolicy(options.AllowedPrivateHosts);
    }
}

public readonly record struct DestinationDecision(bool Allowed, string Host, bool AllowsPrivateAddresses);

public sealed class DestinationPolicy
{
    private readonly HashSet<string> _allowedPrivateHosts;

    public DestinationPolicy(IEnumerable<string> allowedPrivateHosts)
    {
        _allowedPrivateHosts = new(allowedPrivateHosts.Select(CanonicalizeHost), StringComparer.Ordinal);
        if (_allowedPrivateHosts.Any(string.IsNullOrEmpty)) throw new InvalidOperationException("Private host allowlist entries must be exact hostnames.");
        if (_allowedPrivateHosts.Any(host => host.Contains('*', StringComparison.Ordinal))) throw new InvalidOperationException("Private host allowlist entries cannot contain wildcards.");
        if (_allowedPrivateHosts.Any(host => IPAddress.TryParse(host, out _))) throw new InvalidOperationException("Private IP literals cannot be allowlisted.");
    }

    public DestinationDecision Evaluate(Uri destination)
    {
        if (!destination.IsAbsoluteUri || destination.Scheme is not ("http" or "https") || destination.Port is < 1 or > 65_535)
            return new(false, string.Empty, false);
        var host = CanonicalizeHost(destination.IdnHost);
        if (IPAddress.TryParse(host, out var literal)) return new(IsPublicAddress(literal), host, false);
        return new(host.Length != 0, host, _allowedPrivateHosts.Contains(host));
    }

    public static string CanonicalizeHost(string host)
    {
        var trimmed = host.Trim().TrimEnd('.');
        if (trimmed.Length == 0) return string.Empty;
        return new IdnMapping().GetAscii(trimmed).ToLowerInvariant();
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return !address.Equals(IPAddress.IPv6Any) && !address.Equals(IPAddress.IPv6Loopback) &&
                   !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !address.IsIPv6Multicast &&
                   (bytes[0] & 0xfe) != 0xfc &&
                   !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);
        }
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b switch
        {
            [0, ..] or [10, ..] or [127, ..] or [255, 255, 255, 255] => false,
            [100, >= 64 and <= 127, ..] => false,
            [169, 254, ..] => false,
            [172, >= 16 and <= 31, ..] => false,
            [192, 0, 0, ..] or [192, 0, 2, ..] or [192, 168, ..] => false,
            [198, 18 or 19, ..] or [198, 51, 100, ..] => false,
            [203, 0, 113, ..] => false,
            [>= 224, ..] => false,
            _ => true
        };
    }
}

public interface IDestinationResolver
{
    ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class SystemDestinationResolver : IDestinationResolver
{
    public async ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken);
}

public delegate ValueTask<Stream> PublicAddressConnector(IPAddress address, int port, CancellationToken cancellationToken);

public sealed class DestinationConnector(DestinationPolicy policy, IDestinationResolver resolver, PublicAddressConnector? connect = null)
{
    private readonly PublicAddressConnector _connect = connect ?? ConnectSocketAsync;

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        => await ConnectAsync(context.DnsEndPoint, cancellationToken);

    public async ValueTask<Stream> ConnectAsync(DnsEndPoint endPoint, CancellationToken cancellationToken)
    {
        var uri = new UriBuilder("http", endPoint.Host, endPoint.Port).Uri;
        var decision = policy.Evaluate(uri);
        if (!decision.Allowed) throw new HttpRequestException("destination_denied");
        var addresses = await resolver.ResolveAsync(decision.Host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(address => !DestinationPolicy.IsPublicAddress(address) && !decision.AllowsPrivateAddresses))
            throw new HttpRequestException("destination_denied");
        var selected = addresses.FirstOrDefault(DestinationPolicy.IsPublicAddress) ?? addresses[0];
        return await _connect(selected, endPoint.Port, cancellationToken);
    }

    private static async ValueTask<Stream> ConnectSocketAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

public static class BoundedResponseReader
{
    public const string TruncatedMarker = "[truncated]";
    private static readonly Regex SensitiveHeaderLikeContent = new(
        @"(?im)(authorization|proxy-authorization|set-cookie|cookie|x-relayforge-signature)\s*:[^\r\n]*",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static async Task<string?> ReadAsync(HttpContent content, int byteLimit, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(byteLimit + 1, 8192));
        try
        {
            using var output = new MemoryStream(Math.Min(byteLimit, 4096));
            var remaining = byteLimit + 1;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, Math.Min(read, byteLimit - (int)output.Length)), cancellationToken);
                remaining -= read;
            }
            var truncated = output.Length == byteLimit && remaining == 0;
            var text = Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
            var normalized = new string(text.Where(c => !char.IsControl(c) || c is '\r' or '\n' or '\t').ToArray());
            var redacted = SensitiveHeaderLikeContent.Replace(normalized, "[redacted]");
            var sanitized = new string(redacted.Where(c => !char.IsControl(c) || c is '\t').ToArray());
            return sanitized.Length == 0 && !truncated ? null : sanitized + (truncated ? TruncatedMarker : string.Empty);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
