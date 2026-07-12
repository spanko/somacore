using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.DataProtection;

namespace SomaCore.Infrastructure.Strava;

/// <summary>
/// Mints and verifies opaque, signed-and-encrypted state tokens for the Strava
/// OAuth roundtrip — mirrors <see cref="Whoop.WhoopStateProtector"/> with its
/// own protector purpose so a WHOOP state token can never be replayed against
/// the Strava callback (and vice versa).
/// </summary>
public interface IStravaStateProtector
{
    string Protect(StravaOAuthState state);
    StravaOAuthState? Unprotect(string protectedState);
}

public sealed record StravaOAuthState(Guid SomaCoreUserId, string Nonce, DateTimeOffset CreatedAt);

public sealed class StravaStateProtector(IDataProtectionProvider provider) : IStravaStateProtector
{
    private const string Purpose = "SomaCore.Strava.OAuth.State.v1";
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(StravaOAuthState state)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(state);
        return _protector.Protect(Convert.ToBase64String(json));
    }

    public StravaOAuthState? Unprotect(string protectedState)
    {
        try
        {
            var b64 = _protector.Unprotect(protectedState);
            var bytes = Convert.FromBase64String(b64);
            var state = JsonSerializer.Deserialize<StravaOAuthState>(bytes);
            if (state is null) return null;

            if (DateTimeOffset.UtcNow - state.CreatedAt > MaxAge)
            {
                return null;
            }

            return state;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string NewNonce()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }
}
