using FluentAssertions;

using SomaCore.Domain.ExternalConnections;

namespace SomaCore.UnitTests.Domain;

public class ExternalConnectionsTests
{
    [Fact]
    public void Should_define_connection_source_constants_and_construct_entity()
    {
        ConnectionSource.All.Should().BeEquivalentTo(new[]
        {
            "whoop", "oura", "strava", "apple_health", "manual",
        });

        ConnectionStatus.All.Should().BeEquivalentTo(new[]
        {
            "active", "revoked", "refresh_failed", "pending_authorization",
        });

        var connection = new ExternalConnection
        {
            UserId = Guid.NewGuid(),
            Source = ConnectionSource.Whoop,
            Status = ConnectionStatus.PendingAuthorization,
            KeyVaultSecretName = "whoop-refresh-test",
        };

        connection.Source.Should().Be("whoop");
        connection.Status.Should().Be("pending_authorization");
        connection.Scopes.Should().BeEmpty();
    }
}
