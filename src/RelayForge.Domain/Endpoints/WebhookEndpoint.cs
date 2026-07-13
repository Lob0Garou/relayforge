namespace RelayForge.Domain.Endpoints;

public readonly record struct WebhookEndpointId(Guid Value)
{
    public static WebhookEndpointId New() => new(Guid.NewGuid());
}

public sealed class WebhookEndpoint
{
    public const int MaxNameLength = 120;
    public const int MaxUrlLength = 2048;
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 120;

    private WebhookEndpoint() { }

    private WebhookEndpoint(WebhookEndpointId id, string name, Uri url, TimeSpan timeout, string protectedSecret, DateTimeOffset now)
    {
        Id = id;
        Name = name;
        Url = url;
        Timeout = timeout;
        ProtectedSecret = protectedSecret;
        IsActive = true;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public WebhookEndpointId Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public Uri Url { get; private set; } = null!;
    public TimeSpan Timeout { get; private set; }
    public bool IsActive { get; private set; }
    public string ProtectedSecret { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static DomainResult<WebhookEndpoint> Create(string name, string url, TimeSpan timeout, string protectedSecret)
    {
        var normalizedName = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName) || normalizedName.Length > MaxNameLength)
            return DomainResult<WebhookEndpoint>.Failure(new("endpoint.name_required", $"Name is required and must not exceed {MaxNameLength} characters."));

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) ||
            (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps) ||
            parsedUrl.AbsoluteUri.Length > MaxUrlLength)
            return DomainResult<WebhookEndpoint>.Failure(new("endpoint.url_invalid", "URL must be an absolute HTTP or HTTPS URL."));

        if (timeout.TotalSeconds < MinTimeoutSeconds || timeout.TotalSeconds > MaxTimeoutSeconds || timeout.TotalSeconds % 1 != 0)
            return DomainResult<WebhookEndpoint>.Failure(new("endpoint.timeout_invalid", $"Timeout must be between {MinTimeoutSeconds} and {MaxTimeoutSeconds} whole seconds."));

        if (string.IsNullOrWhiteSpace(protectedSecret))
            return DomainResult<WebhookEndpoint>.Failure(new("endpoint.secret_required", "A protected secret is required."));

        return DomainResult<WebhookEndpoint>.Success(new(WebhookEndpointId.New(), normalizedName, parsedUrl, timeout, protectedSecret, DateTimeOffset.UtcNow));
    }
}
