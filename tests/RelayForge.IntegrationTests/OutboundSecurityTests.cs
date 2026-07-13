using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RelayForge.Infrastructure.Delivery;

namespace RelayForge.IntegrationTests;

public sealed class OutboundSecurityTests
{
    public static TheoryData<string> ForbiddenAddresses => new()
    {
        "0.0.0.0", "10.0.0.1", "100.64.0.1", "127.0.0.1", "169.254.1.1", "172.16.0.1", "192.168.1.1",
        "192.0.2.1", "192.31.196.1", "192.52.193.1", "192.88.99.2", "192.175.48.1", "198.18.0.1", "198.51.100.1", "224.0.0.1", "240.0.0.1", "255.255.255.255",
        "::", "::1", "fe80::1", "fec0::1", "fc00::1", "ff02::1", "2001:db8::1", "::ffff:127.0.0.1", "::ffff:10.0.0.1"
        , "64:ff9b::1", "64:ff9b:1::1", "100::1", "2001:2::1", "2001:20::1", "2001:30::1", "3ffe::1", "2001::1", "2002::1"
    };

    [Theory]
    [InlineData("2606:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    public void Destination_policy_accepts_global_unicast_addresses(string value) =>
        Assert.True(DestinationPolicy.IsPublicAddress(IPAddress.Parse(value)));

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
    public void Destination_policy_rejects_userinfo_scoped_ipv6_and_invalid_allowlists()
    {
        Assert.False(new DestinationPolicy([]).Evaluate(new Uri("https://user:pass@example.com/hook")).Allowed);
        var scoped = IPAddress.Parse("2606:4700::1111%1");
        Assert.False(DestinationPolicy.IsPublicAddress(scoped));
        Assert.ThrowsAny<Exception>(() => new DestinationPolicy(null!));
        Assert.ThrowsAny<Exception>(() => new DestinationPolicy(["bad host"]));
    }

    [Theory]
    [InlineData("192.88.98.255", true)]
    [InlineData("192.88.99.0", false)]
    [InlineData("192.88.99.255", false)]
    [InlineData("192.88.100.0", true)]
    [InlineData("198.17.255.255", true)]
    [InlineData("198.18.0.0", false)]
    [InlineData("198.19.255.255", false)]
    [InlineData("198.20.0.0", true)]
    public void Cidr_boundaries_are_matched_exactly(string address, bool expected) =>
        Assert.Equal(expected, DestinationPolicy.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("3ffe:ffff:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [InlineData("3fff::", false)]
    [InlineData("3fff:fff:ffff:ffff:ffff:ffff:ffff:ffff", false)]
    [InlineData("3fff:1000::", true)]
    [InlineData("2620:4f:7fff:ffff:ffff:ffff:ffff:ffff", true)]
    [InlineData("2620:4f:8000::", false)]
    [InlineData("2620:4f:8000:ffff:ffff:ffff:ffff:ffff", false)]
    [InlineData("2620:4f:8001::", true)]
    public void Selected_ipv6_special_purpose_boundaries_are_fail_closed(string address, bool expected) =>
        Assert.Equal(expected, DestinationPolicy.IsPublicAddress(IPAddress.Parse(address)));

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

    [Fact]
    public async Task Configured_http_client_denies_private_by_default_before_low_level_connect()
    {
        var resolver = new AlternatingResolver([IPAddress.Loopback]);
        var lowLevel = new RecordingAddressConnector();
        using var app = new RelayForgeApiFactory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IDestinationResolver>(resolver);
            services.AddSingleton<IAddressConnector>(lowLevel);
        }));
        var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("RelayForgeDelivery");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://blocked.test/hook"));
        Assert.Equal(1, resolver.Calls);
        Assert.Equal(0, lowLevel.Calls);
    }

    [Fact]
    public async Task Configured_http_client_rejects_mixed_dns_before_connector_and_resolves_once()
    {
        var resolver = new AlternatingResolver([IPAddress.Parse("93.184.216.34"), IPAddress.Loopback], [IPAddress.Parse("93.184.216.34")]);
        var lowLevel = new RecordingAddressConnector();
        using var app = new RelayForgeApiFactory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IDestinationResolver>(resolver);
            services.AddSingleton<IAddressConnector>(lowLevel);
        }));
        var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("RelayForgeDelivery");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://mixed.test/hook"));
        Assert.Equal(1, resolver.Calls);
        Assert.Equal(0, lowLevel.Calls);
    }

    [Fact]
    public async Task Configured_development_client_allows_exact_host_preserves_host_and_does_not_follow_redirect()
    {
        await using var server = new SingleResponseServer("HTTP/1.1 302 Found\r\nLocation: http://localhost/second\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        var resolver = new AlternatingResolver([IPAddress.Loopback], [IPAddress.Parse("10.0.0.1")]);
        using var app = new RelayForgeApiFactory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IDestinationResolver>(resolver)));
        var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("RelayForgeDelivery");
        using var response = await client.GetAsync($"http://localhost:{server.Port}/first");
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains($"Host: localhost:{server.Port}", await server.Request);
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task Connector_preserves_https_hostname_and_port_for_tls_sni_and_uses_first_validated_ip()
    {
        var resolver = new AlternatingResolver([IPAddress.Parse("2606:4700::1111")], [IPAddress.Loopback]);
        var lowLevel = new RecordingAddressConnector();
        var connector = new DestinationConnector(new DestinationPolicy([]), resolver, lowLevel);
        await connector.ConnectAsync(new DnsEndPoint("secure.example", 8443), default);
        Assert.Equal(IPAddress.Parse("2606:4700::1111"), lowLevel.Address);
        Assert.Equal("secure.example", lowLevel.EndPoint!.Host);
        Assert.Equal(8443, lowLevel.EndPoint.Port);
        Assert.Equal(1, resolver.Calls);
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

    [Theory]
    [InlineData("é", 1)]
    [InlineData("€", 1)]
    [InlineData("€", 2)]
    [InlineData("😀", 1)]
    [InlineData("😀", 2)]
    [InlineData("😀", 3)]
    public async Task Response_reader_never_emits_replacement_character_for_partial_utf8_runes(string rune, int bytes)
    {
        using var content = new StringContent("A" + rune + "Z", Encoding.UTF8);
        var snippet = await BoundedResponseReader.ReadAsync(content, 1 + bytes, default);
        Assert.DoesNotContain('\uFFFD', snippet!);
        Assert.Equal("A[truncated]", snippet);
    }

    [Theory]
    [InlineData("é", 2)]
    [InlineData("€", 3)]
    [InlineData("😀", 4)]
    public async Task Response_reader_preserves_complete_multibyte_runes_at_exact_and_plus_one_byte_limits(string rune, int runeBytes)
    {
        using var exactContent = new StringContent(rune + "X", Encoding.UTF8);
        Assert.Equal(rune + "[truncated]", await BoundedResponseReader.ReadAsync(exactContent, runeBytes, default));
        using var plusOneContent = new StringContent(rune + "XY", Encoding.UTF8);
        Assert.Equal(rune + "X[truncated]", await BoundedResponseReader.ReadAsync(plusOneContent, runeBytes + 1, default));
    }

    [Fact]
    public async Task Response_reader_redacts_overlapping_sensitive_values_at_multibyte_cut()
    {
        const string longer = "segredo-😀-final";
        using var content = new StringContent("prefix:" + longer, Encoding.UTF8);
        var snippet = await BoundedResponseReader.ReadAsync(content, 17, ["segredo-😀", longer], default);
        Assert.Equal("prefix:[redacted][truncated]", snippet);
        Assert.DoesNotContain('\uFFFD', snippet!);
    }

    [Fact]
    public async Task Response_reader_redacts_longest_overlapping_sensitive_value_first()
    {
        using var content = new StringContent("abcdef");
        Assert.Equal("[redacted]", await BoundedResponseReader.ReadAsync(content, 32, ["abc", "abcdef"], default));
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
    public async Task Response_reader_redacts_known_values_even_when_echoed_bare_or_with_controls()
    {
        string[] sensitive = ["SECRET-VALUE", "sha256=SIGNATURE", "{\"payload\":true}", "cookie=KNOWN"];
        using var content = new StringContent("SECRET-VALUE sha256=SIGN\tAT\0URE {\"payload\":true} cookie=KNOWN");
        var snippet = await BoundedResponseReader.ReadAsync(content, 256, sensitive, default);
        Assert.DoesNotContain("SECRET", snippet);
        Assert.DoesNotContain("SIGNATURE", snippet);
        Assert.DoesNotContain("payload", snippet);
        Assert.DoesNotContain("KNOWN", snippet);
    }

    [Fact]
    public async Task Response_reader_redacts_sensitive_prefix_cut_by_the_byte_limit()
    {
        var sensitive = "SECRET-" + new string('x', 128);
        using var content = new StringContent("prefix:" + sensitive);
        var snippet = await BoundedResponseReader.ReadAsync(content, 24, [sensitive], default);
        Assert.Equal("prefix:[redacted][truncated]", snippet);
    }

    [Fact]
    public async Task Configured_https_client_emits_original_hostname_as_sni()
    {
        await using var server = new SniCaptureServer();
        var resolver = new AlternatingResolver([IPAddress.Loopback]);
        using var app = new RelayForgeApiFactory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IDestinationResolver>(resolver)));
        var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("RelayForgeDelivery");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"https://localhost:{server.Port}/hook"));
        Assert.Equal("localhost", await server.ServerName);
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task Response_reader_observes_cancellation_while_streaming()
    {
        var stream = new SignaledBlockingStream();
        using var content = new StreamContent(stream);
        using var cancellation = new CancellationTokenSource();
        var read = BoundedResponseReader.ReadAsync(content, 16, cancellation.Token);
        await stream.ReadStarted.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
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

    private sealed class SignaledBlockingStream : Stream
    {
        private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { ReadStarted.TrySetResult(); await _never.Task.WaitAsync(cancellationToken); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RecordingAddressConnector : IAddressConnector
    {
        public int Calls { get; private set; } public IPAddress? Address { get; private set; } public DnsEndPoint? EndPoint { get; private set; }
        public ValueTask<Stream> ConnectAsync(IPAddress address, DnsEndPoint originalEndPoint, CancellationToken cancellationToken) { Calls++; Address = address; EndPoint = originalEndPoint; return ValueTask.FromResult<Stream>(new MemoryStream()); }
    }

    private sealed class SingleResponseServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly string _response;
        public SingleResponseServer(string response) { _response = response; _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port; Request = ServeAsync(); }
        public int Port { get; }
        public Task<string> Request { get; }
        private async Task<string> ServeAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync(); await using var stream = client.GetStream();
            var buffer = new byte[4096]; var read = await stream.ReadAsync(buffer); var request = Encoding.ASCII.GetString(buffer, 0, read);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(_response)); return request;
        }
        public ValueTask DisposeAsync() { _listener.Stop(); return ValueTask.CompletedTask; }
    }

    private sealed class SniCaptureServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _certificate;
        public SniCaptureServer()
        {
            using var key = RSA.Create(2048); var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            _certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port; ServerName = CaptureAsync();
        }
        public int Port { get; }
        public Task<string?> ServerName { get; }
        private async Task<string?> CaptureAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync(); await using var ssl = new SslStream(client.GetStream()); string? serverName = null;
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateSelectionCallback = (_, name) => { serverName = name; return _certificate; }, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 });
            }
            catch (AuthenticationException) when (serverName is not null) { return serverName; }
            return serverName;
        }
        public ValueTask DisposeAsync() { _listener.Stop(); _certificate.Dispose(); return ValueTask.CompletedTask; }
    }
}
