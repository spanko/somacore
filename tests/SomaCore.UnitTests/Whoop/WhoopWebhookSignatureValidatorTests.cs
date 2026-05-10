using System.Security.Cryptography;
using System.Text;

using FluentAssertions;

using Microsoft.Extensions.Options;

using SomaCore.Infrastructure.Whoop;

namespace SomaCore.UnitTests.Whoop;

public class WhoopWebhookSignatureValidatorTests
{
    private const string Secret = "test-client-secret";

    private static IWhoopWebhookSignatureValidator NewValidator() =>
        new WhoopWebhookSignatureValidator(
            Options.Create(new WhoopOptions
            {
                ClientId = "test-id",
                ClientSecret = Secret,
                RedirectUri = "https://example.test/cb",
            }));

    private static string ValidSignatureFor(string timestamp, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        var input = Encoding.UTF8.GetBytes(timestamp).Concat(body).ToArray();
        return Convert.ToBase64String(hmac.ComputeHash(input));
    }

    [Fact]
    public void Should_accept_a_correctly_signed_request()
    {
        var v = NewValidator();
        var body = "{\"id\":\"abc\"}"u8.ToArray();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var sig = ValidSignatureFor(ts, body);

        var result = v.Validate(sig, ts, body);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Should_reject_a_tampered_body()
    {
        var v = NewValidator();
        var body = "{\"id\":\"abc\"}"u8.ToArray();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var sig = ValidSignatureFor(ts, body);

        var tampered = "{\"id\":\"xyz\"}"u8.ToArray();
        var result = v.Validate(sig, ts, tampered);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("did not match");
    }

    [Fact]
    public void Should_reject_a_signature_signed_with_a_different_secret()
    {
        var v = NewValidator();
        var body = "{\"id\":\"abc\"}"u8.ToArray();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("a-different-secret"));
        var input = Encoding.UTF8.GetBytes(ts).Concat(body).ToArray();
        var sig = Convert.ToBase64String(hmac.ComputeHash(input));

        v.Validate(sig, ts, body).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Should_reject_a_stale_timestamp()
    {
        var v = NewValidator();
        var body = "{\"id\":\"abc\"}"u8.ToArray();
        var stale = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds().ToString();
        var sig = ValidSignatureFor(stale, body);

        v.Validate(sig, stale, body).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Should_reject_missing_or_malformed_headers()
    {
        var v = NewValidator();
        var body = "{}"u8.ToArray();

        v.Validate(null,        "1700000000000", body).IsSuccess.Should().BeFalse();
        v.Validate("sig",       null,            body).IsSuccess.Should().BeFalse();
        v.Validate("sig",       "not-a-number",  body).IsSuccess.Should().BeFalse();
    }
}
