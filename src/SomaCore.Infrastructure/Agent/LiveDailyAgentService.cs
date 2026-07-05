using System.Diagnostics;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SomaCore.Domain.Agent;
using SomaCore.Domain.Common;
using SomaCore.Infrastructure.Persistence;

namespace SomaCore.Infrastructure.Agent;

/// <summary>
/// Network-backed daily-card agent per ADR 0012. Reads
/// <c>docs/agent-voice-and-persona.md</c> + <c>docs/agent-bounds.md</c>
/// at startup (singleton lifetime) and uses them verbatim as the system
/// prompt and refusal-guard vocabulary. For each call: assembles the
/// user's 7-day WHOOP window per privacy doc Section D.1, hits the
/// Anthropic Messages API with a forced tool-use response, validates the
/// returned actions against <see cref="AgentActionCategory.All"/>, and
/// persists one <c>agent_invocations</c> row.
///
/// What this service does NOT send (per privacy D.2): user name, email,
/// Entra OID, SomaCore user_id, WHOOP user_id, OAuth tokens, raw payloads.
/// The Metadata.user_id field on the Anthropic request carries the
/// invocation's anonymous Guid7 reference — not the user's identifiers.
/// </summary>
public sealed class LiveDailyAgentService : IDailyAgentService
{
    private const string ToolName = "submit_daily_card";

    private readonly AnthropicMessagesClient _client;
    private readonly AnthropicOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LiveDailyAgentService> _logger;
    private readonly string _systemPrompt;
    private readonly AnthropicTool _submitCardTool;

    public LiveDailyAgentService(
        AnthropicMessagesClient client,
        IOptions<AnthropicOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<LiveDailyAgentService> logger)
    {
        _client = client;
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;

        (_systemPrompt, _submitCardTool) = BuildSystemPromptAndTool();
    }

    public async Task<Result<DailyAgentResponse>> GenerateAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return Result<DailyAgentResponse>.Failure("Anthropic disabled.");
        }
        if (string.IsNullOrWhiteSpace(_options.ModelId))
        {
            return Result<DailyAgentResponse>.Failure("Anthropic:ModelId not configured.");
        }

        var invocationId = Guid7Generator.NewId();
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SomaCoreDbContext>();

        var snapshot = await AgentInputSnapshotBuilder.BuildAsync(db, userId, startedAt, cancellationToken);

        var request = new AnthropicMessageRequest(
            Model: _options.ModelId,
            MaxTokens: _options.MaxOutputTokens,
            System: _systemPrompt,
            Messages: new[]
            {
                new AnthropicMessage(
                    Role: "user",
                    Content: $"Generate today's plan from this signal:\n\n```json\n{snapshot.Json}\n```"),
            },
            Tools: new[] { _submitCardTool },
            ToolChoice: new AnthropicToolChoice(Type: "tool", Name: ToolName),
            Metadata: new AnthropicMessageMetadata(UserId: invocationId.ToString("N")),
            Temperature: _options.Temperature);

        AnthropicMessageResponse response;
        try
        {
            response = await _client.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await PersistFailureAsync(db, invocationId, userId, snapshot, stopwatch, ex.Message, cancellationToken);
            _logger.LogWarning(ex,
                "Live agent call failed for user {UserId} invocation {InvocationId}",
                userId, invocationId);
            return Result<DailyAgentResponse>.Failure($"Anthropic call failed: {ex.Message}");
        }

        var validation = AgentResponseValidator.Validate(response);
        if (!validation.IsSuccess)
        {
            stopwatch.Stop();
            await PersistFailureAsync(db, invocationId, userId, snapshot, stopwatch, validation.Error!, cancellationToken);
            _logger.LogWarning(
                "Live agent response failed validation for user {UserId} invocation {InvocationId}: {Error}",
                userId, invocationId, validation.Error);
            return Result<DailyAgentResponse>.Failure(validation.Error!);
        }

        stopwatch.Stop();
        var card = validation.Value!;

        await PersistSuccessAsync(db, invocationId, userId, snapshot, response, card, stopwatch, cancellationToken);

        return Result<DailyAgentResponse>.Success(new DailyAgentResponse(
            TodaysRead: card.TodaysRead,
            Actions: card.Actions,
            GeneratedAt: startedAt,
            ModelId: response.Model ?? _options.ModelId,
            IsStub: false,
            CostEstimateCents: null));
    }

    public async Task<DailyAgentResponse?> GetLatestAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SomaCoreDbContext>();

        var latest = await db.AgentInvocations
            .AsNoTracking()
            .Where(a => a.UserId == userId && a.Kind == AgentInvocationKinds.DailyCard)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (latest is null || string.IsNullOrEmpty(latest.TodaysRead))
        {
            return null;
        }

        var actions = JsonSerializer.Deserialize<List<AgentAction>>(latest.ActionsJson.RootElement.GetRawText())
            ?? new List<AgentAction>();

        return new DailyAgentResponse(
            TodaysRead: latest.TodaysRead,
            Actions: actions,
            GeneratedAt: latest.CreatedAt,
            ModelId: latest.ModelId,
            // Compute IsStub from the stored ModelId rather than hardcoding
            // false: an opted-in user can still have an old stub row sitting
            // in their history from before the flip, and the view needs to
            // know to render the scaffolding banner over it (and the router
            // needs to know to treat it as stale).
            IsStub: AgentInvocationKind.IsStub(latest.ModelId),
            CostEstimateCents: latest.CostEstimateUsd is null
                ? null
                : (int)(latest.CostEstimateUsd.Value * 100m));
    }

    // ------------------------------------------------------------------
    // Construction helpers
    // ------------------------------------------------------------------

    private static (string SystemPrompt, AnthropicTool Tool) BuildSystemPromptAndTool()
    {
        var (voice, bounds) = AgentDocs.Load();

        var categories = string.Join(", ", AgentActionCategory.All);
        var sources = $"{AgentActionSource.ProtocolBased}, {AgentActionSource.UserDataInformed}";

        var systemPrompt =
$@"You are the SomaCore AI, a daily performance coach. The user-facing brief
that defines your voice and constraints follows below, verbatim. Read it
as the operating definition of who you are.

# Voice and persona

{voice}

# Bounds

{bounds}

# How to respond

When the user submits today's signal, you MUST call the `{ToolName}` tool
exactly once with a JSON payload describing the daily card. Never respond
in plain text. The payload must conform to this shape:

{{
  ""todays_read"": ""<one paragraph, in the coach's voice>"",
  ""actions"": [
    {{
      ""title"": ""<one-line action, presented-plan format>"",
      ""why"":   ""<1-2 sentence rationale>"",
      ""category"": ""<one of: {categories}>"",
      ""rank"": <integer; 1 = most important>,
      ""source"": ""<one of: {sources}>""
    }},
    ... three actions total ...
  ]
}}

Exactly three actions. Ranks 1, 2, 3 (no ties on this first pass).
`category` must be one of the listed values verbatim. `source` must be one
of {sources}. Do not invent new categories or sources. Do not generate
anything outside the IN BOUNDS list. Do not output free-form text outside
the tool call.";

        // Tool schema: single string field carrying the JSON-serialized card.
        // We keep server-side parsing + validation rather than fight JSON-schema
        // shapes for nested arrays of objects. The system prompt describes the
        // exact shape; the validator rejects drift.
        var inputSchema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "card_json": {
                  "type": "string",
                  "description": "JSON object with fields todays_read (string) and actions (array of 3 objects with title, why, category, rank, source). See the system prompt for the exact shape and allowed enum values."
                }
              },
              "required": ["card_json"]
            }
            """).RootElement.Clone();

        var tool = new AnthropicTool(
            Name: ToolName,
            Description: "Submit the daily plan card. Call exactly once per request.",
            InputSchema: inputSchema);

        return (systemPrompt, tool);
    }

    // ------------------------------------------------------------------
    // Persistence
    // ------------------------------------------------------------------

    private static async Task PersistSuccessAsync(
        SomaCoreDbContext db,
        Guid invocationId,
        Guid userId,
        AgentInputSnapshot snapshot,
        AnthropicMessageResponse response,
        AgentCardPayload card,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        var invocation = new AgentInvocation
        {
            Id = invocationId,
            UserId = userId,
            Kind = AgentInvocationKinds.DailyCard,
            InputSnapshot = JsonDocument.Parse(snapshot.Json),
            TodaysRead = card.TodaysRead,
            ActionsJson = JsonDocument.Parse(JsonSerializer.Serialize(card.Actions)),
            ModelId = response.Model ?? "",
            InputTokens = response.Usage?.InputTokens,
            CachedInputTokens = response.Usage?.CacheReadInputTokens ?? response.Usage?.CacheCreationInputTokens,
            OutputTokens = response.Usage?.OutputTokens,
            DurationMs = (int)stopwatch.Elapsed.TotalMilliseconds,
            ErrorMessage = null,
            TraceId = Activity.Current?.TraceId.ToString(),
        };
        db.AgentInvocations.Add(invocation);
        await db.SaveChangesAsync(ct);
    }

    private static async Task PersistFailureAsync(
        SomaCoreDbContext db,
        Guid invocationId,
        Guid userId,
        AgentInputSnapshot snapshot,
        Stopwatch stopwatch,
        string errorMessage,
        CancellationToken ct)
    {
        var invocation = new AgentInvocation
        {
            Id = invocationId,
            UserId = userId,
            Kind = AgentInvocationKinds.DailyCard,
            InputSnapshot = JsonDocument.Parse(snapshot.Json),
            TodaysRead = "",
            ActionsJson = JsonDocument.Parse("[]"),
            ModelId = "",
            DurationMs = (int)stopwatch.Elapsed.TotalMilliseconds,
            ErrorMessage = errorMessage.Length > 2000 ? errorMessage[..2000] : errorMessage,
            TraceId = Activity.Current?.TraceId.ToString(),
        };
        db.AgentInvocations.Add(invocation);
        await db.SaveChangesAsync(ct);
    }
}
