namespace SomaCore.Infrastructure.Whoop;

/// <summary>
/// Pure scope-set comparison for an <c>ExternalConnection</c>'s stored scopes
/// against the currently-required scopes from <see cref="WhoopOptions"/>.
///
/// Necessary because OAuth refresh under RFC 6749 §6 cannot widen scope — when
/// the configured default in <see cref="WhoopOptions.Scopes"/> grows (as it did
/// in commit c96f62c to add <c>read:sleep</c> and <c>read:workout</c>), existing
/// connections keep refreshing successfully with the narrower originally-granted
/// scopes indefinitely. The connection looks healthy; refresh keeps working;
/// but downstream API calls that need the new scopes will 403.
///
/// The <c>/me</c> render path uses this to surface the existing reconnect
/// banner for connections whose stored scopes are missing one or more required
/// scopes. Render-time only — no status mutation.
/// </summary>
public static class WhoopConnectionScopes
{
    /// <summary>
    /// Returns <c>true</c> iff every scope in <paramref name="required"/> is
    /// present in <paramref name="stored"/>. Order-insensitive. Whitespace
    /// around stored entries is trimmed. Extra scopes WHOOP may have granted
    /// beyond what was requested are ignored. Null or empty
    /// <paramref name="stored"/> returns <c>false</c>.
    /// </summary>
    public static bool HasRequiredScopes(
        IEnumerable<string>? stored,
        IEnumerable<string> required)
    {
        ArgumentNullException.ThrowIfNull(required);

        if (stored is null)
        {
            return false;
        }

        var storedSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in stored)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            storedSet.Add(s.Trim());
        }
        if (storedSet.Count == 0)
        {
            return false;
        }

        foreach (var r in required)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            if (!storedSet.Contains(r.Trim()))
            {
                return false;
            }
        }
        return true;
    }
}
