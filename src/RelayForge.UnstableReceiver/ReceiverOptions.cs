namespace RelayForge.UnstableReceiver;

public sealed class ReceiverOptions
{
    public const string SectionName = "Receiver";

    public required string SigningSecret { get; init; }

    public int TimestampToleranceSeconds { get; init; } = 300;

    public int MaxBodyBytes { get; init; } = 65_536;
}

public sealed class ReceiverSigningConfiguration(byte[] secret, TimeSpan timestampTolerance, int maxBodyBytes)
{
    private byte[] _secret = secret;
    public ReadOnlyMemory<byte> Secret => Volatile.Read(ref _secret);
    public TimeSpan TimestampTolerance { get; } = timestampTolerance;
    public int MaxBodyBytes { get; } = maxBodyBytes;
    public void Rotate(byte[] secret) => Interlocked.Exchange(ref _secret, secret);
}

public sealed record RotateSecretRequest(string Secret);
