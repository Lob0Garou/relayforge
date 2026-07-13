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
            return MatchesPrefix(bytes, [0x20], 3) &&
                   !MatchesPrefix(bytes, [0x20, 0x01, 0x00], 23) &&
                   !MatchesPrefix(bytes, [0x20, 0x01, 0x0d, 0xb8], 32) &&
                   !MatchesPrefix(bytes, [0x20, 0x02], 16) &&
                   !MatchesPrefix(bytes, [0x3f, 0xfe], 16);
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

    private static bool MatchesPrefix(ReadOnlySpan<byte> address, ReadOnlySpan<byte> prefix, int prefixLength)
    {
        var wholeBytes = prefixLength / 8;
        if (!address[..wholeBytes].SequenceEqual(prefix[..wholeBytes])) return false;
        var remainingBits = prefixLength % 8;
        if (remainingBits == 0) return true;
        var mask = (byte)(0xff << (8 - remainingBits));
        return (address[wholeBytes] & mask) == (prefix[wholeBytes] & mask);
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

public interface IAddressConnector
{
    ValueTask<Stream> ConnectAsync(IPAddress address, DnsEndPoint originalEndPoint, CancellationToken cancellationToken);
}

public sealed class SocketAddressConnector : IAddressConnector
{
    public async ValueTask<Stream> ConnectAsync(IPAddress address, DnsEndPoint originalEndPoint, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, originalEndPoint.Port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

public sealed class DestinationConnector
{
    private readonly DestinationPolicy _policy;
    private readonly IDestinationResolver _resolver;
    private readonly IAddressConnector _connect;

    public DestinationConnector(DestinationPolicy policy, IDestinationResolver resolver, IAddressConnector connect)
    { _policy = policy; _resolver = resolver; _connect = connect; }

    public DestinationConnector(DestinationPolicy policy, IDestinationResolver resolver, PublicAddressConnector connect)
        : this(policy, resolver, new DelegateAddressConnector(connect)) { }

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        => await ConnectAsync(context.DnsEndPoint, cancellationToken);

    public async ValueTask<Stream> ConnectAsync(DnsEndPoint endPoint, CancellationToken cancellationToken)
    {
        var uri = new UriBuilder("http", endPoint.Host, endPoint.Port).Uri;
        var decision = _policy.Evaluate(uri);
        if (!decision.Allowed) throw new HttpRequestException("destination_denied");
        var addresses = await _resolver.ResolveAsync(decision.Host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(address => !DestinationPolicy.IsPublicAddress(address) && !decision.AllowsPrivateAddresses))
            throw new HttpRequestException("destination_denied");
        var selected = addresses.FirstOrDefault(DestinationPolicy.IsPublicAddress) ?? addresses[0];
        return await _connect.ConnectAsync(selected, endPoint, cancellationToken);
    }

    private sealed class DelegateAddressConnector(PublicAddressConnector connect) : IAddressConnector
    {
        public ValueTask<Stream> ConnectAsync(IPAddress address, DnsEndPoint originalEndPoint, CancellationToken cancellationToken) => connect(address, originalEndPoint.Port, cancellationToken);
    }
}

public static class BoundedResponseReader
{
    public const string TruncatedMarker = "[truncated]";
    private static readonly Regex SensitiveHeaderLikeContent = new(
        @"(?im)(authorization|proxy-authorization|set-cookie|cookie|x-relayforge-signature)\s*:[^\r\n]*",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static async Task<string?> ReadAsync(HttpContent content, int byteLimit, CancellationToken cancellationToken)
        => await ReadAsync(content, byteLimit, [], cancellationToken);

    public static async Task<string?> ReadAsync(HttpContent content, int byteLimit, IEnumerable<string> sensitiveValues, CancellationToken cancellationToken)
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
            var normalized = new string(text.Where(c => !char.IsControl(c)).ToArray());
            var redacted = SensitiveHeaderLikeContent.Replace(normalized, "[redacted]");
            foreach (var sensitiveValue in sensitiveValues)
            {
                var normalizedSensitiveValue = new string(sensitiveValue.Where(c => !char.IsControl(c)).ToArray());
                if (normalizedSensitiveValue.Length != 0) redacted = RedactKnownValue(redacted, normalizedSensitiveValue);
            }
            var sanitized = new string(redacted.Where(c => !char.IsControl(c) || c is '\t').ToArray());
            return sanitized.Length == 0 && !truncated ? null : sanitized + (truncated ? TruncatedMarker : string.Empty);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static string RedactKnownValue(string text, string sensitiveValue)
    {
        var redacted = text.Replace(sensitiveValue, "[redacted]", StringComparison.Ordinal);
        var maximumPrefix = Math.Min(redacted.Length, sensitiveValue.Length - 1);
        for (var length = maximumPrefix; length > 0; length--)
        {
            if (redacted.AsSpan().EndsWith(sensitiveValue.AsSpan(0, length), StringComparison.Ordinal))
                return string.Concat(redacted.AsSpan(0, redacted.Length - length), "[redacted]");
        }
        return redacted;
    }
}
