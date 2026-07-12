namespace RelayForge.UnstableReceiver;

public sealed class ReceiverOptions
{
    public const string SectionName = "Receiver";

    public required string SigningSecret { get; init; }

    public int TimestampToleranceSeconds { get; init; } = 300;

    public int MaxBodyBytes { get; init; } = 65_536;
}
