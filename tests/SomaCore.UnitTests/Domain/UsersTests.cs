using FluentAssertions;

using SomaCore.Domain.Users;

namespace SomaCore.UnitTests.Domain;

public class UsersTests
{
    [Fact]
    public void Should_construct_user_with_required_fields()
    {
        var user = new User
        {
            EntraOid = Guid.NewGuid(),
            EntraTenantId = Guid.NewGuid(),
            Email = "adam@tento100.com",
        };

        user.Email.Should().Be("adam@tento100.com");
        user.ExternalConnections.Should().BeEmpty();
        user.WhoopRecoveries.Should().BeEmpty();
    }
}
