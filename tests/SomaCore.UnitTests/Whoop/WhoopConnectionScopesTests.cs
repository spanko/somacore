using FluentAssertions;

using SomaCore.Infrastructure.Whoop;

namespace SomaCore.UnitTests.Whoop;

public class WhoopConnectionScopesTests
{
    private static readonly string[] DefaultRequired = new[]
    {
        "read:recovery", "read:cycles", "read:sleep", "read:workout", "read:profile", "offline",
    };

    [Fact]
    public void Returns_true_for_exact_match()
    {
        var stored = new[] { "read:recovery", "read:cycles", "read:sleep", "read:workout", "read:profile", "offline" };
        WhoopConnectionScopes.HasRequiredScopes(stored, DefaultRequired).Should().BeTrue();
    }

    [Fact]
    public void Returns_true_for_full_superset()
    {
        var stored = new[]
        {
            "read:recovery", "read:cycles", "read:sleep", "read:workout",
            "read:profile", "offline", "read:body_measurement",
        };
        WhoopConnectionScopes.HasRequiredScopes(stored, DefaultRequired).Should().BeTrue();
    }

    [Fact]
    public void Returns_false_when_missing_one_required_scope()
    {
        var stored = new[] { "read:recovery", "read:cycles", "read:profile", "offline" };
        WhoopConnectionScopes.HasRequiredScopes(stored, DefaultRequired).Should().BeFalse();
    }

    [Fact]
    public void Returns_false_for_null_stored()
    {
        WhoopConnectionScopes.HasRequiredScopes(null, DefaultRequired).Should().BeFalse();
    }

    [Fact]
    public void Returns_false_for_empty_stored()
    {
        WhoopConnectionScopes.HasRequiredScopes(Array.Empty<string>(), DefaultRequired).Should().BeFalse();
    }

    [Fact]
    public void Returns_false_when_stored_is_only_whitespace()
    {
        WhoopConnectionScopes.HasRequiredScopes(new[] { "", "  " }, DefaultRequired).Should().BeFalse();
    }

    [Fact]
    public void Is_order_insensitive()
    {
        var stored = DefaultRequired.Reverse().ToArray();
        WhoopConnectionScopes.HasRequiredScopes(stored, DefaultRequired).Should().BeTrue();
    }

    [Fact]
    public void Trims_whitespace_on_stored_entries()
    {
        var stored = new[] { "  read:recovery", "read:cycles  ", " read:sleep ", "read:workout", "read:profile", "offline" };
        WhoopConnectionScopes.HasRequiredScopes(stored, DefaultRequired).Should().BeTrue();
    }

    [Fact]
    public void Empty_required_set_returns_true_for_any_stored()
    {
        // Edge case: if config somehow had no required scopes, anything is sufficient.
        WhoopConnectionScopes.HasRequiredScopes(new[] { "offline" }, Array.Empty<string>()).Should().BeTrue();
    }

    [Fact]
    public void Empty_required_returns_false_for_empty_stored()
    {
        // Empty-stored returns false even when required is empty — null/empty
        // stored is the "no recorded scopes" case, not a valid grant.
        WhoopConnectionScopes.HasRequiredScopes(Array.Empty<string>(), Array.Empty<string>()).Should().BeFalse();
    }

    [Fact]
    public void Throws_when_required_is_null()
    {
        Action act = () => WhoopConnectionScopes.HasRequiredScopes(new[] { "x" }, required: null!);
        act.Should().Throw<ArgumentNullException>();
    }
}

public class WhoopOptionsScopeParsingTests
{
    [Fact]
    public void GetRequiredScopes_splits_on_whitespace_and_trims_empties()
    {
        var options = new WhoopOptions
        {
            ClientId = "x",
            ClientSecret = "y",
            RedirectUri = "https://example.com/cb",
            Scopes = "read:recovery  read:cycles   read:sleep read:workout read:profile offline",
        };

        options.GetRequiredScopes().Should().BeEquivalentTo(new[]
        {
            "read:recovery", "read:cycles", "read:sleep", "read:workout", "read:profile", "offline",
        });
    }

    [Fact]
    public void GetRequiredScopes_default_value_matches_expected_set()
    {
        // Guards the commit c96f62c scope expansion — if someone reverts or
        // narrows the default in WhoopOptions.Scopes, this test fails loudly.
        var options = new WhoopOptions
        {
            ClientId = "x",
            ClientSecret = "y",
            RedirectUri = "https://example.com/cb",
        };

        options.GetRequiredScopes().Should().BeEquivalentTo(new[]
        {
            "read:recovery", "read:cycles", "read:sleep", "read:workout", "read:profile", "offline",
        });
    }
}
