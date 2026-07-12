using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RelayForge.Infrastructure.Security;

/// <summary>
/// Signs RelayForge webhook deliveries. The protocol frame is the exact UTF-8 bytes of
/// <c>{unixTimestamp}.{deliveryId}.</c> followed immediately by the unmodified payload bytes.
/// The delivery id and timestamp bind a payload to one delivery attempt context and prevent replay.
/// </summary>
public static class WebhookSigner
{
    private const string Prefix = "sha256=";
    private const int Sha256HexLength = 64;

    public static string Sign(
        ReadOnlySpan<byte> secret,
        long unixTimestamp,
        string deliveryId,
        ReadOnlySpan<byte> payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        if (secret.IsEmpty)
        {
            throw new ArgumentException("Signing secret cannot be empty.", nameof(secret));
        }

        var hash = ComputeHash(secret, unixTimestamp, deliveryId, payload);
        return $"{Prefix}{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    public static bool Verify(
        ReadOnlySpan<byte> secret,
        long unixTimestamp,
        string deliveryId,
        ReadOnlySpan<byte> payload,
        string signature,
        TimeSpan timestampTolerance,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (secret.IsEmpty || string.IsNullOrWhiteSpace(deliveryId) || timestampTolerance < TimeSpan.Zero)
        {
            return false;
        }

        if (signature.Length != Prefix.Length + Sha256HexLength ||
            !signature.StartsWith(Prefix, StringComparison.Ordinal) ||
            !IsLowercaseHex(signature.AsSpan(Prefix.Length)))
        {
            return false;
        }

        byte[] supplied;
        try
        {
            supplied = Convert.FromHexString(signature[Prefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        DateTimeOffset signedAt;
        try
        {
            signedAt = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        var offset = signedAt - timeProvider.GetUtcNow();
        if (offset < -timestampTolerance || offset > timestampTolerance)
        {
            return false;
        }

        var expected = ComputeHash(secret, unixTimestamp, deliveryId, payload);
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    private static byte[] ComputeHash(
        ReadOnlySpan<byte> secret,
        long unixTimestamp,
        string deliveryId,
        ReadOnlySpan<byte> payload)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, secret);
        var framePrefix = Encoding.UTF8.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"{unixTimestamp}.{deliveryId}."));
        hmac.AppendData(framePrefix);
        hmac.AppendData(payload);
        return hmac.GetHashAndReset();
    }

    private static bool IsLowercaseHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
