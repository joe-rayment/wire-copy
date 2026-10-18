using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AudioEngine.Client;

/// <summary>
/// Verifies the Standard-Webhooks-style signature on a callback: headers <c>Webhook-Id</c>, <c>Webhook-Timestamp</c>
/// (unix seconds) and <c>Webhook-Signature</c> = <c>v1=&lt;lowercase hex HMAC-SHA256(secret_utf8, "{timestamp}.{rawBody}")&gt;</c>.
/// The header may hold several space-separated <c>v1=</c> values (secret rotation); any match passes.
/// </summary>
public static class WebhookVerifier
{
    public const string TimestampHeader = "Webhook-Timestamp";
    public const string SignatureHeader = "Webhook-Signature";
    public const string IdHeader = "Webhook-Id";

    /// <summary>Maximum clock skew, in either direction, between the timestamp header and now.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(300);

    /// <summary>The lowercase-hex v1 signature for a timestamp and the exact raw request body.</summary>
    public static string Sign(string secret, string timestamp, string body)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return Convert.ToHexStringLower(mac);
    }

    /// <summary>Verify using a header collection (names matched case-insensitively). <paramref name="body"/> must be the raw, unparsed body.</summary>
    public static bool Verify(string secret, IEnumerable<KeyValuePair<string, string>> headers, string body, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(headers);
        string? timestamp = null;
        string? signature = null;
        foreach (var (name, value) in headers)
        {
            if (name.Equals(TimestampHeader, StringComparison.OrdinalIgnoreCase))
            {
                timestamp = value;
            }
            else if (name.Equals(SignatureHeader, StringComparison.OrdinalIgnoreCase))
            {
                signature = value;
            }
        }

        return Verify(secret, timestamp, signature, body, now);
    }

    /// <summary>Verify from the two header values directly. Returns false (never throws) on a missing, malformed, stale or wrong signature.</summary>
    public static bool Verify(string secret, string? timestamp, string? signatureHeader, string body, DateTimeOffset? now = null)
    {
        if (string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signatureHeader) || body is null)
        {
            return false;
        }

        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        var skew = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() - seconds;
        if (Math.Abs(skew) > Tolerance.TotalSeconds)
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Sign(secret, timestamp, body));
        var matched = false;
        foreach (var part in signatureHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!part.StartsWith("v1=", StringComparison.Ordinal))
            {
                continue;
            }

            // Check every candidate (no early exit) so timing does not reveal which one matched.
            matched |= CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(part[3..]), expected);
        }

        return matched;
    }
}
