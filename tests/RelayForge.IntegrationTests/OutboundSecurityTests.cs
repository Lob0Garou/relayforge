using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using RelayForge.Infrastructure.Delivery;

namespace RelayForge.IntegrationTests;

public sealed class OutboundSecurityTests
{
    public static TheoryData<string> ForbiddenAddresses => new()
    {
        "0.0.0.0", "10.0.0.1", "100.64.0.1", "127.0.0.1", "169.254.1.1", "172.16.0.1", "192.168.1.1",
        "192.0.2.1", "198.18.0.1", "198.51.100.1", "224.0.0.1", "240.0.0.1", "255.255.255.255",
        "::", "::1", "fe80::1", "fec0::1", "fc00::1", "ff02::1", "2001:db8::1", "::ffff:127.0.0.1", "::ffff:10.0.0.1"
    };

    [Theory]
    [MemberData(nameof(ForbiddenAddresses))]
    public void Destination_policy_rejects_non_public_addresses(string value) =>
        Assert.False(DestinationPolicy.IsPublicAddress(IPAddress.Parse(value)));

    [Theory]
    [InlineData("https://example.com/hook")]
    [InlineData("http://EXAMPLE.com.:443/hook")]
    public void Destination_policy_accepts_http_hosts_and_canonicalizes_names(string value)
    {
        var result = new DestinationPolicy([]).Evaluate(new Uri(value));
        Assert.True(result.Allowed);
        Assert.Equal("example.com", result.Host);
    }

    [Fact]
    public void Destination_policy_canonicalizes_idn_and_rejects_wildcard_allowlists()
    {
        Assert.Equal("xn--bcher-kva.example", new DestinationPolicy([]).Evaluate(new Uri("https://bücher.example/hook")).Host);
        Assert.Throws<InvalidOperationException>(() => new DestinationPolicy(["*.internal"]));
    }

    [Theory]
    [InlineData("ftp://example.com/hook")]
    [InlineData("http://127.0.0.1/hook")]
    [InlineData("http://[::ffff:127.0.0.1]/hook")]
    [InlineData("http://example.com:0/hook")]
    public void Destination_policy_rejects_unsupported_or_literal_private_destinations(string value) =>
        Assert.False(new DestinationPolicy(["localhost"]).Evaluate(new Uri(value)).Allowed);

    [Fact]
    public async Task Mixed_dns_answers_fail_closed_and_rebinding_resolver_is_called_once()
    {
        var resolver = new AlternatingResolver(
            [IPAddress.Parse("93.184.216.34"), IPAddress.Loopback],
            [IPAddress.Loopback]);
        var connector = new DestinationConnector(new DestinationPolicy([]), resolver, (_, _, _) =>
            ValueTask.FromResult<Stream>(new MemoryStream()));

        await Assert.ThrowsAsync<HttpRequestException>(() => connector.ConnectAsync(
            new DnsEndPoint("example.com", 443), default).AsTask());
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task Allowlisted_private_hostname_uses_only_resolved_address_for_socket()
    {
        var publicThenPrivate = new AlternatingResolver([IPAddress.Parse("10.0.0.9")], [IPAddress.Loopback]);
        IPAddress? connected = null;
        var connector = new DestinationConnector(new DestinationPolicy(["DEV-RECEIVER."]), publicThenPrivate, (address, _, _) =>
        {
            connected = address;
            return ValueTask.FromResult<Stream>(new MemoryStream());
        });

        await connector.ConnectAsync(new DnsEndPoint("dev-receiver", 8080), default);
        Assert.Equal(IPAddress.Parse("10.0.0.9"), connected);
        Assert.Equal(1, publicThenPrivate.Calls);
    }

    [Theory]
    [InlineData(8, "12345678", false)]
    [InlineData(8, "12345678[truncated]", true)]
    public async Task Response_reader_handles_exact_limit_and_limit_plus_one(int limit, string expected, bool extra)
    {
        var content = new StringContent(extra ? "123456789" : "12345678");
        var snippet = await BoundedResponseReader.ReadAsync(content, limit, default);
        Assert.Equal(expected, snippet);
    }

    [Fact]
    public async Task Response_reader_sanitizes_controls_and_never_persists_headers()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("safe\0\r\nAuthoriz\0ation: bearer SENTINEL\u001b")
        };
        response.Headers.Add("Set-Cookie", "session=COOKIE-SENTINEL");
        var snippet = await BoundedResponseReader.ReadAsync(response.Content, 128, default);
        Assert.DoesNotContain('\0', snippet!);
        Assert.DoesNotContain('\u001b', snippet!);
        Assert.DoesNotContain("COOKIE-SENTINEL", snippet!);
        Assert.DoesNotContain("SENTINEL", snippet!);
    }

    [Fact]
    public void Outbound_options_reject_invalid_limits_and_private_allowlist_outside_development()
    {
        Assert.Throws<InvalidOperationException>(() => OutboundDeliveryOptions.Validate(new() { MaxResponseSnippetBytes = 0 }, true));
        Assert.Throws<InvalidOperationException>(() => OutboundDeliveryOptions.Validate(new() { AllowedPrivateHosts = ["localhost"] }, false));
    }

    private sealed class AlternatingResolver(params IPAddress[][] answers) : IDestinationResolver
    {
        private int _calls;
        public int Calls => _calls;
        public ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            ValueTask.FromResult(answers[Math.Min(Interlocked.Increment(ref _calls) - 1, answers.Length - 1)]);
    }
}
