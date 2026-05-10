using FluentAssertions;

using Microsoft.AspNetCore.DataProtection;

using SomaCore.Infrastructure.Whoop;

namespace SomaCore.UnitTests.Whoop;

public class WhoopStateProtectorTests
{
    private static IWhoopStateProtector NewProtector()
    {
        var provider = DataProtectionProvider.Create("SomaCore.Tests");
        return new WhoopStateProtector(provider);
    }

    [Fact]
    public void Should_round_trip_a_freshly_minted_state()
    {
        var protector = NewProtector();
        var state = new WhoopOAuthState(Guid.CreateVersion7(), WhoopStateProtector.NewNonce(), DateTimeOffset.UtcNow);

        var token = protector.Protect(state);
        var roundTripped = protector.Unprotect(token);

        roundTripped.Should().NotBeNull();
        roundTripped!.SomaCoreUserId.Should().Be(state.SomaCoreUserId);
        roundTripped.Nonce.Should().Be(state.Nonce);
    }

    [Fact]
    public void Should_reject_a_tampered_state_token()
    {
        var protector = NewProtector();
        var state = new WhoopOAuthState(Guid.CreateVersion7(), WhoopStateProtector.NewNonce(), DateTimeOffset.UtcNow);

        var token = protector.Protect(state);
        // Flip a character somewhere in the middle to corrupt the AEAD tag.
        var tampered = token.Substring(0, token.Length / 2) + "x" + token.Substring(token.Length / 2 + 1);

        protector.Unprotect(tampered).Should().BeNull();
    }

    [Fact]
    public void Should_reject_a_stale_state_token()
    {
        var protector = NewProtector();
        var stale = new WhoopOAuthState(
            Guid.CreateVersion7(),
            WhoopStateProtector.NewNonce(),
            DateTimeOffset.UtcNow.AddMinutes(-15));

        var token = protector.Protect(stale);
        protector.Unprotect(token).Should().BeNull();
    }

    [Fact]
    public void Should_emit_distinct_nonces()
    {
        var a = WhoopStateProtector.NewNonce();
        var b = WhoopStateProtector.NewNonce();
        a.Should().NotBe(b);
        a.Length.Should().Be(32); // 16 random bytes hex-encoded
    }
}
