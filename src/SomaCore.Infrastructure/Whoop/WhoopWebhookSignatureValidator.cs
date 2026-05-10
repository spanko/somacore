using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

using SomaCore.Domain.Common;

namespace SomaCore.Infrastructure.Whoop;

public interface IWhoopWebhookSignatureValidator
{
    Result<bool> Validate(string? signatureHeader, string? timestampHeader, ReadOnlySpan<byte> rawBody);
}

public sealed class WhoopWebhookSignatureValidator(IOptions<WhoopOptions> options)
    : IWhoopWebhookSignatureValidator
{
    /// <summary>
    /// Reject signatures whose timestamp is outside this window (replay protection).
    /// 5 minutes mirrors WHOOP's documented expectation and what most webhook
    /// providers default to.
    /// </summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    private readonly WhoopOptions _options = options.Value;

    public Result<bool> Validate(
        string? signatureHeader,
        string? timestampHeader,
        ReadOnlySpan<byte> rawBody)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            return Result<bool>.Failure("Missing X-WHOOP-Signature header.");
        }

        if (string.IsNullOrWhiteSpace(timestampHeader))
        {
            return Result<bool>.Failure("Missing X-WHOOP-Signature-Timestamp header.");
        }

        if (!long.TryParse(timestampHeader, out var unixMillis))
        {
            return Result<bool>.Failure("Timestamp header is not a valid integer.");
        }

        var sentAt = DateTimeOffset.FromUnixTimeMilliseconds(unixMillis);
        var skew = (DateTimeOffset.UtcNow - sentAt).Duration();
        if (skew > MaxClockSkew)
        {
            return Result<bool>.Failure(
                $"Timestamp out of range (skew={skew.TotalSeconds:F0}s, max={MaxClockSkew.TotalSeconds:F0}s).");
        }

        // signature = base64(HMAC-SHA256(timestamp || raw_body, client_secret))
        var key = Encoding.UTF8.GetBytes(_options.ClientSecret);
        var prefix = Encoding.UTF8.GetBytes(timestampHeader);

        Span<byte> hash = stackalloc byte[32];
        using (var hmac = new HMACSHA256(key))
        {
            // We need to feed (timestamp || rawBody) — HMAC supports incremental
            // hashing but the simpler path here is a small allocation.
            var buffer = new byte[prefix.Length + rawBody.Length];
            Buffer.BlockCopy(prefix, 0, buffer, 0, prefix.Length);
            rawBody.CopyTo(buffer.AsSpan(prefix.Length));

            if (!hmac.TryComputeHash(buffer, hash, out _))
            {
                return Result<bool>.Failure("Failed to compute HMAC.");
            }
        }

        var expected = Convert.ToBase64String(hash);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signatureHeader.Trim()))
            ? Result<bool>.Success(true)
            : Result<bool>.Failure("Signature did not match.");
    }
}
