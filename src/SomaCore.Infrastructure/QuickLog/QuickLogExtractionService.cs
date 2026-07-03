using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SomaCore.Domain.Agent;
using SomaCore.Domain.Common;
using SomaCore.Infrastructure.Agent;
using SomaCore.Infrastructure.Persistence;

namespace SomaCore.Infrastructure.QuickLog;

public interface IQuickLogExtractionService
{
    /// <summary>
    /// Extracts a structured quick-log entry from one user-typed line.
    /// Enforces the daily cap, persists an <c>agent_invocations</c> row
    /// (kind=quick_log_extraction) win or lose, and returns the draft for
    /// the confirm card. Nothing here writes a food/workout/note row —
    /// persistence happens only on the user's explicit Confirm, via
    /// <see cref="QuickLogEntryService"/>.
    /// </summary>
    Task<Result<QuickLogExtraction>> ExtractAsync(
        Guid userId, string userText, CancellationToken cancellationToken);
}

/// <summary>
/// Anthropic-backed extraction per session-quick-log.md. Mirrors
/// <see cref="LiveDailyAgentService"/>'s shape: forced tool call, server-side
/// validation, one invocation row per call. Gated twice: QuickLog:Enabled
/// (privacy Part 4 — user free text is a new surface) AND the Anthropic
/// client being registered (Anthropic:Enabled + ApiKey).
/// </summary>
public sealed class QuickLogExtractionService : IQuickLogExtractionService
{
    private readonly AnthropicMessagesClient? _client;
    private readonly QuickLogOptions _quickLogOptions;
    private readonly AnthropicOptions _anthropicOptions;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<QuickLogExtractionService> _logger;
    private readonly AnthropicTool _tool;

    public QuickLogExtractionService(
        IOptions<QuickLogOptions> quickLogOptions,
        IOptions<AnthropicOptions> anthropicOptions,
        IServiceScopeFactory scopeFactory,
        ILogger<QuickLogExtractionService> logger,
        AnthropicMessagesClient? client = null)
    {
        _client = client;
        _quickLogOptions = quickLogOptions.Value;
        _anthropicOptions = anthropicOptions.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _tool = BuildTool();
    }

    public async Task<Result<QuickLogExtraction>> ExtractAsync(
        Guid userId, string userText, CancellationToken cancellationToken)
    {
        if (!_quickLogOptions.Enabled)
        {
            return Result<QuickLogExtraction>.Failure("Quick-log is not enabled.");
        }
        if (_client is null || !_anthropicOptions.Enabled)
        {
            return Result<QuickLogExtraction>.Failure("Quick-log requires the Anthropic client.");
        }
        if (string.IsNullOrWhiteSpace(userText))
        {
            return Result<QuickLogExtraction>.Failure("Nothing to log.");
        }
        if (userText.Length > _quickLogOptions.MaxInputChars)
        {
            return Result<QuickLogExtraction>.Failure(
                $"Keep it under {_quickLogOptions.MaxInputChars} characters.");
        }

        var invocationId = Guid7Generator.NewId();
        var stopwatch = Stopwatch.StartNew();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SomaCoreDbContext>();

        // Daily cap on extraction invocations, per UTC day.
        var utcDayStart = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var todayCount = await db.AgentInvocations
            .AsNoTracking()
            .CountAsync(a => a.UserId == userId
                          && a.Kind == AgentInvocationKinds.QuickLogExtraction
                          && a.CreatedAt >= utcDayStart,
                cancellationToken);
        if (todayCount >= _quickLogOptions.DailyCap)
        {
            return Result<QuickLogExtraction>.Failure(
                "Daily quick-log limit reached — more room tomorrow.");
        }

        // Local time context so "this morning" resolves to the right slot/date.
        // Same source of truth as the snapshot builder: the most recent
        // sleep's timezone offset, falling back to UTC.
        var tzOffset = await db.WhoopSleeps
            .AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.EndAt)
            .Select(s => s.TimezoneOffset)
            .FirstOrDefaultAsync(cancellationToken) ?? "+00:00";
        var localNow = DateTimeOffset.UtcNow.ToOffset(ParseOffset(tzOffset));
        var localToday = DateOnly.FromDateTime(localNow.DateTime);

        var inputSnapshotJson = JsonSerializer.Serialize(new
        {
            user_text = userText,
            local_now = localNow,
        });

        var request = new AnthropicMessageRequest(
            Model: _anthropicOptions.ModelId,
            MaxTokens: 1024,
            System: BuildSystemPrompt(localNow),
            Messages: new[] { new AnthropicMessage(Role: "user", Content: userText) },
            Tools: new[] { _tool },
            ToolChoice: new AnthropicToolChoice(Type: "tool", Name: QuickLogExtractionValidator.ToolName),
            Metadata: new AnthropicMessageMetadata(UserId: invocationId.ToString("N")),
            Temperature: 0);

        AnthropicMessageResponse response;
        try
        {
            response = await _client.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await PersistAsync(db, invocationId, userId, inputSnapshotJson, "[]", "",
                null, stopwatch, ex.Message, cancellationToken);
            _logger.LogWarning(ex,
                "Quick-log extraction call failed for user {UserId} invocation {InvocationId}",
                userId, invocationId);
            return Result<QuickLogExtraction>.Failure("Extraction failed — try again.");
        }

        var validation = QuickLogExtractionValidator.Validate(response, localToday);
        stopwatch.Stop();

        var extractionJson = validation.IsSuccess
            ? JsonSerializer.Serialize(validation.Value)
            : "[]";
        await PersistAsync(db, invocationId, userId, inputSnapshotJson, extractionJson,
            response.Model ?? "", response.Usage, stopwatch,
            validation.IsSuccess ? null : validation.Error, cancellationToken);

        if (!validation.IsSuccess)
        {
            _logger.LogWarning(
                "Quick-log extraction failed validation for user {UserId} invocation {InvocationId}: {Error}",
                userId, invocationId, validation.Error);
            return Result<QuickLogExtraction>.Failure(
                "I couldn't turn that into an entry — try rephrasing with what/when.");
        }

        return validation;
    }

    private static string BuildSystemPrompt(DateTimeOffset localNow) =>
$@"You extract ONE structured entry from a single line a user typed into their
health app. The user's local date-time is {localNow:yyyy-MM-dd HH:mm} ({localNow:dddd}).

Call the `{QuickLogExtractionValidator.ToolName}` tool exactly once. entry_json
must be a JSON object with this shape:

{{
  ""entry_type"": ""meal"" | ""workout"" | ""note"" | ""unclassified"",
  ""meal"": {{ ""meal_slot"": ""breakfast|lunch|dinner|snack|other"", ""meal_date"": ""YYYY-MM-DD"",
             ""calories"": n?, ""protein_g"": n?, ""carbs_g"": n?, ""fat_g"": n?,
             ""fiber_g"": n?, ""sugar_g"": n?, ""sodium_mg"": n?,
             ""food_items"": [{{""name"": ""..."", ""amount"": ""...""?}}] }},
  ""workout"": {{ ""workout_type"": ""run|ride|strength|swim|yoga|walk|other-short-word"",
                ""started_at"": ""ISO-8601 with the user's offset"", ""elapsed_seconds"": n,
                ""intensity"": ""easy|moderate|hard""?, ""total_energy_kcal"": n?,
                ""total_distance_m"": n?, ""average_hr"": n? }},
  ""note"": {{ ""category"": ""symptom|schedule|context""?, ""note"": ""the user's meaning, their words"",
             ""active_until"": ""YYYY-MM-DD""? }},
  ""message"": ""only for unclassified: one short question asking what they meant""
}}

Populate exactly one of meal/workout/note matching entry_type; omit the others.
Rules:
- Only include values the user stated or that are direct arithmetic on them.
  NEVER estimate calories or macros from food names. ""a burrito"" → food_items
  only, no macros. ""~50g protein"" → protein_g: 50.
- Resolve relative times against the local date-time above. ""this morning"" →
  today; no meal slot stated → infer from time of day; workout with no start
  time → assume it just ended.
- ""until Friday"" style phrases → active_until on the coming Friday.
- If the line is not a meal, workout, or personal context note (e.g. it's a
  question or chit-chat), use entry_type unclassified with a short message.";

    private static AnthropicTool BuildTool()
    {
        var inputSchema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "entry_json": {
                  "type": "string",
                  "description": "JSON object with entry_type and exactly one of meal/workout/note (or message for unclassified). See the system prompt for the exact shape."
                }
              },
              "required": ["entry_json"]
            }
            """).RootElement.Clone();

        return new AnthropicTool(
            Name: QuickLogExtractionValidator.ToolName,
            Description: "Submit the structured entry extracted from the user's line. Call exactly once.",
            InputSchema: inputSchema);
    }

    private static async Task PersistAsync(
        SomaCoreDbContext db,
        Guid invocationId,
        Guid userId,
        string inputSnapshotJson,
        string extractionJson,
        string modelId,
        AnthropicUsage? usage,
        Stopwatch stopwatch,
        string? errorMessage,
        CancellationToken ct)
    {
        var invocation = new AgentInvocation
        {
            Id = invocationId,
            UserId = userId,
            Kind = AgentInvocationKinds.QuickLogExtraction,
            InputSnapshot = JsonDocument.Parse(inputSnapshotJson),
            TodaysRead = "",
            ActionsJson = JsonDocument.Parse(extractionJson),
            ModelId = modelId,
            InputTokens = usage?.InputTokens,
            CachedInputTokens = usage?.CacheReadInputTokens ?? usage?.CacheCreationInputTokens,
            OutputTokens = usage?.OutputTokens,
            DurationMs = (int)stopwatch.Elapsed.TotalMilliseconds,
            ErrorMessage = errorMessage is { Length: > 2000 } long_ ? long_[..2000] : errorMessage,
            TraceId = Activity.Current?.TraceId.ToString(),
        };
        db.AgentInvocations.Add(invocation);
        await db.SaveChangesAsync(ct);
    }

    private static TimeSpan ParseOffset(string offset)
    {
        if (TimeSpan.TryParseExact(offset.TrimStart('+'), @"hh\:mm",
                CultureInfo.InvariantCulture, out var positive))
        {
            return offset.StartsWith('-') ? -positive : positive;
        }
        return TimeSpan.Zero;
    }
}
