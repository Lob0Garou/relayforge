using System.Buffers;
using System.Globalization;
using System.Collections.Immutable;
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
    // IANA Special-Purpose Address Registry snapshot 2026-07. This table blocks non-global ranges plus
    // explicitly selected special-purpose ranges conservatively; it is not an automatically complete future registry.
    // Whole listed blocks are denied fail-closed, including blocks with narrowly defined globally reachable exceptions.
    private static readonly ImmutableArray<CidrBlock> ForbiddenIpv4 = CreateCidrs(
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12",
        "192.0.0.0/24", "192.0.2.0/24", "192.31.196.0/24", "192.52.193.0/24", "192.88.99.0/24", "192.168.0.0/16",
        "192.175.48.0/24", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4");
    private static readonly ImmutableArray<CidrBlock> ForbiddenIpv6 = CreateCidrs(
        "::/128", "::1/128", "::ffff:0:0/96", "64:ff9b::/96", "64:ff9b:1::/48", "100::/64", "2001::/23",
        "2001:db8::/32", "2002::/16", "2620:4f:8000::/48", "3ffe::/16", "3fff::/20", "fc00::/7", "fe80::/10", "fec0::/10", "ff00::/8");
    private static readonly CidrBlock GlobalIpv6Unicast = CidrBlock.Parse("2000::/3");
    private readonly HashSet<string> _allowedPrivateHosts;

    public DestinationPolicy(IEnumerable<string> allowedPrivateHosts)
    {
        ArgumentNullException.ThrowIfNull(allowedPrivateHosts);
        _allowedPrivateHosts = new(allowedPrivateHosts.Select(CanonicalizeHost), StringComparer.Ordinal);
        if (_allowedPrivateHosts.Any(string.IsNullOrEmpty)) throw new InvalidOperationException("Private host allowlist entries must be exact hostnames.");
        if (_allowedPrivateHosts.Any(host => host.Contains('*', StringComparison.Ordinal))) throw new InvalidOperationException("Private host allowlist entries cannot contain wildcards.");
        if (_allowedPrivateHosts.Any(host => IPAddress.TryParse(host, out _))) throw new InvalidOperationException("Private IP literals cannot be allowlisted.");
    }

    public DestinationDecision Evaluate(Uri destination)
    {
        if (!destination.IsAbsoluteUri || destination.Scheme is not ("http" or "https") || destination.Port is < 1 or > 65_535 || destination.UserInfo.Length != 0)
            return new(false, string.Empty, false);
        var host = CanonicalizeHost(destination.IdnHost);
        if (IPAddress.TryParse(host, out var literal)) return new(IsPublicAddress(literal), host, false);
        return new(host.Length != 0, host, _allowedPrivateHosts.Contains(host));
    }

    public static string CanonicalizeHost(string host)
    {
        var trimmed = host.Trim().TrimEnd('.');
        if (trimmed.Length == 0) return string.Empty;
        var canonical = new IdnMapping().GetAscii(trimmed).ToLowerInvariant();
        if (canonical.Any(char.IsWhiteSpace) || Uri.CheckHostName(canonical) == UriHostNameType.Unknown)
            throw new InvalidOperationException("Private host allowlist entries must be valid exact hostnames.");
        return canonical;
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.ScopeId == 0 && GlobalIpv6Unicast.Contains(address) && !ForbiddenIpv6.Any(block => block.Contains(address));
        }
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        return !ForbiddenIpv4.Any(block => block.Contains(address));
    }

    private static ImmutableArray<CidrBlock> CreateCidrs(params string[] values) => values.Select(CidrBlock.Parse).ToImmutableArray();

    private readonly record struct CidrBlock(IPAddress Network, int PrefixLength)
    {
        public static CidrBlock Parse(string value)
        {
            var parts = value.Split('/');
            return new(IPAddress.Parse(parts[0]), int.Parse(parts[1], CultureInfo.InvariantCulture));
        }

        public bool Contains(IPAddress address)
        {
            if (address.AddressFamily != Network.AddressFamily) return false;
            var candidate = address.GetAddressBytes(); var network = Network.GetAddressBytes(); var wholeBytes = PrefixLength / 8;
            if (!candidate.AsSpan(0, wholeBytes).SequenceEqual(network.AsSpan(0, wholeBytes))) return false;
            var remainingBits = PrefixLength % 8;
            if (remainingBits == 0) return true;
            var mask = (byte)(0xff << (8 - remainingBits));
            return (candidate[wholeBytes] & mask) == (network[wholeBytes] & mask);
        }
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
            var text = DecodeValidUtf8Prefix(output.GetBuffer(), (int)output.Length);
            var normalized = new string(text.Where(c => !char.IsControl(c)).ToArray());
            var redacted = SensitiveHeaderLikeContent.Replace(normalized, "[redacted]");
            var normalizedSensitiveValues = sensitiveValues
                .Select(value => new string(value.Where(c => !char.IsControl(c)).ToArray()))
                .Where(value => value.Length != 0)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(value => value.Length);
            foreach (var normalizedSensitiveValue in normalizedSensitiveValues)
            {
                redacted = RedactKnownValue(redacted, normalizedSensitiveValue);
            }
            var sanitized = new string(redacted.Where(c => !char.IsControl(c) || c is '\t').ToArray());
            return sanitized.Length == 0 && !truncated ? null : sanitized + (truncated ? TruncatedMarker : string.Empty);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static string DecodeValidUtf8Prefix(byte[] buffer, int length)
    {
        var bytes = buffer.AsSpan(0, length);
        return Encoding.UTF8.GetString(bytes[..ValidUtf8PrefixLength(bytes)]);
    }

    private static int ValidUtf8PrefixLength(ReadOnlySpan<byte> bytes)
    {
        var consumedTotal = 0;
        while (consumedTotal < bytes.Length)
        {
            var status = Rune.DecodeFromUtf8(bytes[consumedTotal..], out _, out var consumed);
            if (status != OperationStatus.Done) break;
            consumedTotal += consumed;
        }
        return consumedTotal;
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
