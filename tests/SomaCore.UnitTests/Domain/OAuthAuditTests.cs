using FluentAssertions;

using SomaCore.Domain.OAuthAudit;

namespace SomaCore.UnitTests.Domain;

public class OAuthAuditTests
{
    [Fact]
    public void Should_define_oauth_audit_constants_and_construct_entry()
    {
        OAuthAuditAction.All.Should().BeEquivalentTo(new[]
        {
            "authorize",
            "callback_success",
            "callback_failed",
            "token_refresh_success",
            "token_refresh_failed",
            "revoke_detected",
            "manual_disconnect",
        });

        OAuthAuditSource.All.Should().BeEquivalentTo(new[]
        {
            "whoop", "oura", "strava", "apple_health",
        });

        var entry = new OAuthAuditEntry
        {
            Source = OAuthAuditSource.Whoop,
            Action = OAuthAuditAction.TokenRefreshSuccess,
            Success = true,
            HttpStatusCode = 200,
        };

        entry.Action.Should().Be("token_refresh_success");
        entry.Success.Should().BeTrue();
    }
}
