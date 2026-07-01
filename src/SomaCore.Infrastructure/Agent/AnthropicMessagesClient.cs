using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SomaCore.Infrastructure.Agent;

/// <summary>
/// Thin HttpClient wrapper for the Anthropic Messages API. Models only the
/// subset of the surface <see cref="LiveDailyAgentService"/> uses: one POST
/// to /v1/messages with a system prompt, one tool, forced tool_choice, and
/// the user message. No streaming, no batches, no extras. Returns the
/// minimal shape the validator needs.
///
/// Registered as a typed HttpClient so the message handler chain
/// (retry/timeout/logging) hooks in naturally.
/// </summary>
public sealed class AnthropicMessagesClient
{
    private const string AnthropicVersion = "2023-06-01";
    private const string MessagesEndpoint = "/v1/messages";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;

    public AnthropicMessagesClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<AnthropicMessageResponse> SendAsync(
        AnthropicMessageRequest request,
        CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(request, options: JsonOptions);
        using var response = await _http.PostAsync(MessagesEndpoint, content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new AnthropicApiException(
                $"Anthropic returned {(int)response.StatusCode}: {Truncate(errorBody, 800)}");
        }

        var parsed = await response.Content.ReadFromJsonAsync<AnthropicMessageResponse>(
            JsonOptions, cancellationToken);

        if (parsed is null)
        {
            throw new AnthropicApiException("Anthropic returned an empty body.");
        }
        return parsed;
    }

    public static HttpClient ConfigureHttp(HttpClient http, string apiKey, TimeSpan timeout)
    {
        http.BaseAddress = new Uri("https://api.anthropic.com");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        http.DefaultRequestHeaders.Add("anthropic-version", AnthropicVersion);
        http.Timeout = timeout;
        return http;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>
/// Request payload for POST /v1/messages. Only the fields LiveDailyAgentService
/// uses are modeled. JsonIgnoreCondition.WhenWritingNull elides anything left
/// at default.
/// </summary>
public sealed record AnthropicMessageRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("system")] string System,
    [property: JsonPropertyName("messages")] IReadOnlyList<AnthropicMessage> Messages,
    [property: JsonPropertyName("tools")] IReadOnlyList<AnthropicTool> Tools,
    [property: JsonPropertyName("tool_choice")] AnthropicToolChoice ToolChoice,
    [property: JsonPropertyName("metadata")] AnthropicMessageMetadata? Metadata = null,
    [property: JsonPropertyName("temperature")] double? Temperature = null);

public sealed record AnthropicMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

public sealed record AnthropicTool(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("input_schema")] JsonElement InputSchema);

public sealed record AnthropicToolChoice(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string? Name = null);

public sealed record AnthropicMessageMetadata(
    [property: JsonPropertyName("user_id")] string UserId);

public sealed record AnthropicMessageResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("stop_reason")] string? StopReason,
    [property: JsonPropertyName("content")] IReadOnlyList<AnthropicContentBlock> Content,
    [property: JsonPropertyName("usage")] AnthropicUsage? Usage);

public sealed record AnthropicContentBlock(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("input")] JsonElement? Input = null,
    [property: JsonPropertyName("text")] string? Text = null);

public sealed record AnthropicUsage(
    [property: JsonPropertyName("input_tokens")] int? InputTokens,
    [property: JsonPropertyName("output_tokens")] int? OutputTokens,
    [property: JsonPropertyName("cache_creation_input_tokens")] int? CacheCreationInputTokens,
    [property: JsonPropertyName("cache_read_input_tokens")] int? CacheReadInputTokens);

public sealed class AnthropicApiException : Exception
{
    public AnthropicApiException(string message) : base(message) { }
}
