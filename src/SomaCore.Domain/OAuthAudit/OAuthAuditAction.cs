namespace SomaCore.Domain.OAuthAudit;

public static class OAuthAuditAction
{
    public const string Authorize = "authorize";
    public const string CallbackSuccess = "callback_success";
    public const string CallbackFailed = "callback_failed";
    public const string TokenRefreshSuccess = "token_refresh_success";
    public const string TokenRefreshFailed = "token_refresh_failed";
    public const string RevokeDetected = "revoke_detected";
    public const string ManualDisconnect = "manual_disconnect";
    /// <summary>Session 5 — admin-triggered historical backfill run for a connection.</summary>
    public const string Backfill = "backfill";

    public static IReadOnlyCollection<string> All { get; } = new[]
    {
        Authorize,
        CallbackSuccess,
        CallbackFailed,
        TokenRefreshSuccess,
        TokenRefreshFailed,
        RevokeDetected,
        ManualDisconnect,
        Backfill,
    };
}
