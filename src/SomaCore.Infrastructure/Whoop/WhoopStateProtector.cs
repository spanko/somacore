using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.DataProtection;

namespace SomaCore.Infrastructure.Whoop;

/// <summary>
/// Mints and verifies opaque, signed-and-encrypted state tokens for the WHOOP
/// OAuth roundtrip. The token wraps a one-shot nonce plus the SomaCore user ID
/// expected on callback, so even a session-broken roundtrip can't be bound to
/// the wrong user.
/// </summary>
public interface IWhoopStateProtector
{
    string Protect(WhoopOAuthState state);
    WhoopOAuthState? Unprotect(string protectedState);
}

public sealed record WhoopOAuthState(Guid SomaCoreUserId, string Nonce, DateTimeOffset CreatedAt);

public sealed class WhoopStateProtector(IDataProtectionProvider provider) : IWhoopStateProtector
{
    private const string Purpose = "SomaCore.Whoop.OAuth.State.v1";
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(WhoopOAuthState state)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(state);
        return _protector.Protect(Convert.ToBase64String(json));
    }

    public WhoopOAuthState? Unprotect(string protectedState)
    {
        try
        {
            var b64 = _protector.Unprotect(protectedState);
            var bytes = Convert.FromBase64String(b64);
            var state = JsonSerializer.Deserialize<WhoopOAuthState>(bytes);
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
