using System.Text.Json.Serialization;

namespace SomaCore.Infrastructure.Whoop;

public sealed record WhoopTokenResponse(
    [property: JsonPropertyName("access_token")]  string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_in")]    int ExpiresInSeconds,
    [property: JsonPropertyName("scope")]         string Scope,
    [property: JsonPropertyName("token_type")]    string TokenType);

public sealed record WhoopBasicProfile(
    [property: JsonPropertyName("user_id")]    long UserId,
    [property: JsonPropertyName("email")]      string Email,
    [property: JsonPropertyName("first_name")] string? FirstName,
    [property: JsonPropertyName("last_name")]  string? LastName);
