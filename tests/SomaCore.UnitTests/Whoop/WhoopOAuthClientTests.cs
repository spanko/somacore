using System.Net;
using System.Text;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using SomaCore.Infrastructure.Whoop;

namespace SomaCore.UnitTests.Whoop;

public class WhoopOAuthClientTests
{
    private static (WhoopOAuthClient Client, StubHandler Handler) NewClient()
    {
        var options = Options.Create(new WhoopOptions
        {
            ClientId = "test-id",
            ClientSecret = "test-secret",
            RedirectUri = "https://example.test/auth/whoop/callback",
            AuthorizeUri = "https://api.prod.whoop.com/oauth/oauth2/auth",
            TokenUri = "https://api.prod.whoop.com/oauth/oauth2/token",
            ProfileUri = "https://api.prod.whoop.com/developer/v2/user/profile/basic",
            Scopes = "read:recovery offline",
        });

        var handler = new StubHandler();
        var http = new HttpClient(handler);
        var client = new WhoopOAuthClient(http, options, NullLogger<WhoopOAuthClient>.Instance);
        return (client, handler);
    }

    [Fact]
    public void Should_build_authorize_url_with_state_and_required_params()
    {
        var (client, _) = NewClient();

        var url = client.BuildAuthorizeUrl("the-state");

        url.Should().StartWith("https://api.prod.whoop.com/oauth/oauth2/auth?");
        url.Should().Contain("client_id=test-id");
        url.Should().Contain("response_type=code");
        url.Should().Contain("state=the-state");
        url.Should().Contain("scope=read%3arecovery+offline");
        url.Should().Contain("redirect_uri=https%3a%2f%2fexample.test%2fauth%2fwhoop%2fcallback");
    }

    [Fact]
    public async Task Should_exchange_code_for_token_on_2xx()
    {
        var (client, handler) = NewClient();
        handler.Reply(HttpStatusCode.OK, """
            {
              "access_token":"a",
              "refresh_token":"r",
              "expires_in":3600,
              "scope":"read:recovery offline",
              "token_type":"Bearer"
            }
            """);

        var result = await client.ExchangeCodeAsync("the-code", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.AccessToken.Should().Be("a");
        result.Value.RefreshToken.Should().Be("r");
        result.Value.ExpiresInSeconds.Should().Be(3600);

        var posted = handler.LastRequestBody!;
        posted.Should().Contain("grant_type=authorization_code");
        posted.Should().Contain("code=the-code");
        posted.Should().Contain("client_id=test-id");
        posted.Should().Contain("client_secret=test-secret");
    }

    [Fact]
    public async Task Should_return_failure_result_on_4xx_response()
    {
        var (client, handler) = NewClient();
        handler.Reply(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");

        var result = await client.ExchangeCodeAsync("bad", CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("400");
    }

    [Fact]
    public async Task Should_throw_on_5xx_so_callers_see_an_unexpected_failure()
    {
        var (client, handler) = NewClient();
        handler.Reply(HttpStatusCode.ServiceUnavailable, "");

        var act = () => client.ExchangeCodeAsync("anything", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _body = "";

        public string? LastRequestBody { get; private set; }

        public void Reply(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
