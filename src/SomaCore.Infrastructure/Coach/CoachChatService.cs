using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SomaCore.Domain.Agent;
using SomaCore.Domain.CoachThreads;
using SomaCore.Domain.Common;
using SomaCore.Infrastructure.Agent;
using SomaCore.Infrastructure.Persistence;

namespace SomaCore.Infrastructure.Coach;

public interface ICoachChatService
{
    /// <summary>Creates a thread anchored to a subject (or 'general'). Returns the existing open thread for the same subject if one exists.</summary>
    Task<Result<CoachThread>> StartThreadAsync(
        Guid userId, string subjectType, Guid? subjectId, CancellationToken ct);

    /// <summary>One user turn: persists the user message, runs the model with the subject + snapshot context, persists + returns the coach's reply.</summary>
    Task<Result<CoachMessage>> SendAsync(
        Guid userId, Guid threadId, string userText, CancellationToken ct);
}

/// <summary>
/// The conversation surface (session-quick-log research §3, pulled forward
/// 2026-07-05 on Adam's direction). Same mechanical-guard philosophy as the
/// card: the model answers through a forced tool with a refusal flag; the
/// validator enforces shape; out-of-bounds pushes surface as refusals in
/// the coach's voice, not silent drift. Confirmation is never conversational —
/// this service persists MESSAGES only; data writes still go through the
/// structured input's explicit forms.
/// </summary>
public sealed class CoachChatService : ICoachChatService
{
    internal const string ToolName = "submit_coach_reply";

    private readonly SomaCoreDbContext _db;
    private readonly CoachChatOptions _options;
    private readonly AnthropicOptions _anthropicOptions;
    private readonly ILogger<CoachChatService> _logger;
    private readonly AnthropicMessagesClient? _client;

    public CoachChatService(
        SomaCoreDbContext db,
        IOptions<CoachChatOptions> options,
        IOptions<AnthropicOptions> anthropicOptions,
        ILogger<CoachChatService> logger,
        AnthropicMessagesClient? client = null)
    {
        _db = db;
        _options = options.Value;
        _anthropicOptions = anthropicOptions.Value;
        _logger = logger;
        _client = client;
    }

    public async Task<Result<CoachThread>> StartThreadAsync(
        Guid userId, string subjectType, Guid? subjectId, CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            return Result<CoachThread>.Failure("Coach conversations are not enabled.");
        }
        if (!CoachThreadSubjectType.All.Contains(subjectType))
        {
            return Result<CoachThread>.Failure($"Unknown subject type '{subjectType}'.");
        }
        if (subjectType != CoachThreadSubjectType.General && subjectId is null)
        {
            return Result<CoachThread>.Failure("A subject id is required for this thread type.");
        }

        // Reuse an existing thread on the same subject — one conversation
        // per piece of evidence keeps the list navigable.
        var existing = await _db.CoachThreads
            .FirstOrDefaultAsync(t => t.UserId == userId
                                   && t.SubjectType == subjectType
                                   && t.SubjectId == subjectId, ct);
        if (existing is not null)
        {
            return Result<CoachThread>.Success(existing);
        }

        var title = await BuildTitleAsync(userId, subjectType, subjectId, ct);
        if (title is null)
        {
            return Result<CoachThread>.Failure("That item wasn't found (it may have been deleted).");
        }

        var thread = new CoachThread
        {
            UserId = userId,
            SubjectType = subjectType,
            SubjectId = subjectId,
            Title = title,
            LastMessageAt = DateTimeOffset.UtcNow,
        };
        _db.CoachThreads.Add(thread);
        await _db.SaveChangesAsync(ct);
        return Result<CoachThread>.Success(thread);
    }

    public async Task<Result<CoachMessage>> SendAsync(
        Guid userId, Guid threadId, string userText, CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            return Result<CoachMessage>.Failure("Coach conversations are not enabled.");
        }
        if (_client is null || !_anthropicOptions.Enabled)
        {
            return Result<CoachMessage>.Failure("Coach conversations require the Anthropic client.");
        }

        userText = (userText ?? "").Trim();
        if (userText.Length == 0)
        {
            return Result<CoachMessage>.Failure("Nothing to send.");
        }
        if (userText.Length > _options.MaxTurnChars)
        {
            return Result<CoachMessage>.Failure($"Keep it under {_options.MaxTurnChars} characters.");
        }

        var thread = await _db.CoachThreads
            .Include(t => t.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(t => t.Id == threadId && t.UserId == userId, ct);
        if (thread is null)
        {
            return Result<CoachMessage>.Failure("Conversation not found.");
        }

        var userTurns = thread.Messages.Count(m => m.Role == "user");
        if (userTurns >= _options.MaxUserTurnsPerThread)
        {
            return Result<CoachMessage>.Failure(
                "That's it for this conversation — start a new one from your coach page anytime.");
        }

        var utcDayStart = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var todayCount = await _db.CoachMessages
            .CountAsync(m => m.UserId == userId && m.Role == "coach" && m.CreatedAt >= utcDayStart, ct);
        if (todayCount >= _options.DailyMessageCap)
        {
            return Result<CoachMessage>.Failure("Daily conversation limit reached — more room tomorrow.");
        }

        var invocationId = Guid7Generator.NewId();
        var stopwatch = Stopwatch.StartNew();

        // Context: the subject (what this thread is ABOUT), the latest card,
        // and the same data snapshot the card sees. Built fresh per turn so
        // a meal logged mid-conversation is visible on the next question.
        var subjectContext = await BuildSubjectContextAsync(userId, thread, ct);
        var snapshot = await AgentInputSnapshotBuilder.BuildAsync(db: _db, userId, DateTimeOffset.UtcNow, ct);
        var latestCard = await _db.AgentInvocations
            .AsNoTracking()
            .Where(a => a.UserId == userId && a.Kind == AgentInvocationKinds.DailyCard && a.TodaysRead != "")
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => a.TodaysRead)
            .FirstOrDefaultAsync(ct);

        var messages = new List<AnthropicMessage>();
        foreach (var m in thread.Messages)
        {
            messages.Add(new AnthropicMessage(m.Role == "coach" ? "assistant" : "user", m.Content));
        }
        messages.Add(new AnthropicMessage("user", userText));

        var request = new AnthropicMessageRequest(
            Model: _anthropicOptions.ModelId,
            MaxTokens: 1024,
            System: BuildSystemPrompt(subjectContext, latestCard, snapshot.Json),
            Messages: messages,
            Tools: new[] { BuildTool() },
            ToolChoice: new AnthropicToolChoice("tool", ToolName),
            Metadata: new AnthropicMessageMetadata(invocationId.ToString("N")),
            Temperature: _anthropicOptions.Temperature);

        AnthropicMessageResponse response;
        try
        {
            response = await _client.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await LogInvocationAsync(invocationId, userId, thread.Id, userText, null, stopwatch, ex.Message, ct);
            _logger.LogWarning(ex, "Coach chat call failed for thread {ThreadId}", threadId);
            return Result<CoachMessage>.Failure("The coach didn't answer — try again.");
        }
        stopwatch.Stop();

        var validated = CoachReplyValidator.Validate(response);
        if (!validated.IsSuccess)
        {
            await LogInvocationAsync(invocationId, userId, thread.Id, userText, null, stopwatch, validated.Error, ct);
            _logger.LogWarning("Coach chat reply failed validation for thread {ThreadId}: {Error}",
                threadId, validated.Error);
            return Result<CoachMessage>.Failure("The coach didn't answer — try again.");
        }

        var reply = validated.Value!;

        var userMessage = new CoachMessage
        {
            ThreadId = thread.Id,
            UserId = userId,
            Role = "user",
            Content = userText,
        };
        var coachMessage = new CoachMessage
        {
            ThreadId = thread.Id,
            UserId = userId,
            Role = "coach",
            Content = reply.Reply,
            Refusal = reply.Refusal,
            InvocationId = invocationId,
        };
        thread.LastMessageAt = DateTimeOffset.UtcNow;
        _db.CoachMessages.AddRange(userMessage, coachMessage);
        await _db.SaveChangesAsync(ct);

        await LogInvocationAsync(invocationId, userId, thread.Id, userText, response, stopwatch, null, ct);

        return Result<CoachMessage>.Success(coachMessage);
    }

    // ------------------------------------------------------------------
    // Context assembly
    // ------------------------------------------------------------------

    private async Task<string> BuildSubjectContextAsync(Guid userId, CoachThread thread, CancellationToken ct)
    {
        switch (thread.SubjectType)
        {
            case CoachThreadSubjectType.Document:
                var doc = await _db.UserDocuments
                    .AsNoTracking()
                    .Where(d => d.Id == thread.SubjectId && d.UserId == userId)
                    .Select(d => new { d.FileName, d.Summary, d.ExtractedText, d.UploadedAt })
                    .FirstOrDefaultAsync(ct);
                if (doc is null)
                {
                    return "The document this conversation was about has been deleted.";
                }
                return $"DOCUMENT the user uploaded ({doc.FileName}, {doc.UploadedAt:yyyy-MM-dd}): {doc.Summary}\n\n---\n{doc.ExtractedText ?? "(no readable content)"}\n---";

            case CoachThreadSubjectType.Meal:
                var meal = await _db.FoodEntries
                    .AsNoTracking()
                    .Where(f => f.Id == thread.SubjectId && f.UserId == userId)
                    .Select(f => new { f.MealDate, f.MealSlot, f.Calories, f.ProteinG, f.CarbsG, f.FatG, f.FiberG })
                    .FirstOrDefaultAsync(ct);
                return meal is null
                    ? "The meal this conversation was about has been deleted."
                    : $"MEAL under discussion: {meal.MealDate:yyyy-MM-dd} {meal.MealSlot} — calories {meal.Calories?.ToString() ?? "?"}, protein {meal.ProteinG?.ToString() ?? "?"}g, carbs {meal.CarbsG?.ToString() ?? "?"}g, fat {meal.FatG?.ToString() ?? "?"}g, fiber {meal.FiberG?.ToString() ?? "?"}g.";

            case CoachThreadSubjectType.Workout:
                var workout = await _db.HealthKitWorkouts
                    .AsNoTracking()
                    .Where(w => w.Id == thread.SubjectId && w.UserId == userId)
                    .Select(w => new { w.WorkoutType, w.StartedAt, w.ElapsedSeconds, w.AverageHr })
                    .FirstOrDefaultAsync(ct);
                return workout is null
                    ? "The workout this conversation was about has been deleted."
                    : $"WORKOUT under discussion: {workout.WorkoutType}, started {workout.StartedAt:yyyy-MM-dd HH:mm}, {workout.ElapsedSeconds / 60} min, avg HR {workout.AverageHr?.ToString() ?? "?"}.";

            case CoachThreadSubjectType.Note:
                var note = await _db.UserNotes
                    .AsNoTracking()
                    .Where(n => n.Id == thread.SubjectId && n.UserId == userId)
                    .Select(n => new { n.Note, n.Category, n.ActiveUntil })
                    .FirstOrDefaultAsync(ct);
                return note is null
                    ? "The note this conversation was about has been deleted."
                    : $"NOTE under discussion ({note.Category ?? "context"}): \"{note.Note}\"{(note.ActiveUntil is { } u ? $" (active until {u:yyyy-MM-dd})" : "")}.";

            default:
                return "General conversation — no specific subject; use the data snapshot.";
        }
    }

    private async Task<string?> BuildTitleAsync(
        Guid userId, string subjectType, Guid? subjectId, CancellationToken ct)
    {
        switch (subjectType)
        {
            case CoachThreadSubjectType.General:
                return "General";
            case CoachThreadSubjectType.Document:
                var doc = await _db.UserDocuments.AsNoTracking()
                    .Where(d => d.Id == subjectId && d.UserId == userId)
                    .Select(d => d.FileName).FirstOrDefaultAsync(ct);
                return doc is null ? null : Truncate(doc, 200);
            case CoachThreadSubjectType.Meal:
                var meal = await _db.FoodEntries.AsNoTracking()
                    .Where(f => f.Id == subjectId && f.UserId == userId)
                    .Select(f => new { f.MealDate, f.MealSlot }).FirstOrDefaultAsync(ct);
                return meal is null ? null : $"{meal.MealDate:MMM d} {meal.MealSlot}";
            case CoachThreadSubjectType.Workout:
                var workout = await _db.HealthKitWorkouts.AsNoTracking()
                    .Where(w => w.Id == subjectId && w.UserId == userId)
                    .Select(w => new { w.WorkoutType, w.StartedAt }).FirstOrDefaultAsync(ct);
                return workout is null ? null : $"{workout.StartedAt:MMM d} {workout.WorkoutType}";
            case CoachThreadSubjectType.Note:
                var note = await _db.UserNotes.AsNoTracking()
                    .Where(n => n.Id == subjectId && n.UserId == userId)
                    .Select(n => n.Note).FirstOrDefaultAsync(ct);
                return note is null ? null : Truncate(note, 200);
            default:
                return null;
        }
    }

    private static string BuildSystemPrompt(string subjectContext, string? latestCard, string snapshotJson)
    {
        var (voice, bounds) = AgentDocs.Load();
        var sb = new StringBuilder();
        sb.AppendLine("You are the SomaCore AI, a daily performance coach, in a short conversation with your user about their data. Your voice and constraints follow, verbatim.");
        sb.AppendLine("\n# Voice and persona\n");
        sb.AppendLine(voice);
        sb.AppendLine("\n# Bounds\n");
        sb.AppendLine(bounds);
        sb.AppendLine($"""

# Conversation rules

- You MUST call the `{ToolName}` tool exactly once per turn. Never respond in plain text.
- reply: your answer, in the coach's voice, at most 150 words. Reference only the data provided below — never invent values.
- refusal: set true when the user's ask is OUT OF BOUNDS (medical diagnosis, clinical interpretation, appearance/weight commentary, second-guessing clinicians). When refusing, the reply briefly says what you can't do and redirects to what you can — one sentence each, no lecture.
- You cannot log, change, or delete any data from conversation. If the user states new data ("I also ran 5k"), tell them to log it from the coach page so it counts.
- No recommendations that need a lab source unless lab data appears in the context below.
- The snapshot's `documents_on_file` lists everything the user has uploaded, by name and summary. You may reference any of them. If the user asks about a document's DETAILS and its full contents are not in "What this conversation is about" below, say what you know from the summary and tell them to open that document's "Ask about this" on the coach page for a conversation with its full contents.

# What this conversation is about

{subjectContext}

# The user's latest daily card

{latestCard ?? "(no card yet today)"}

# The user's data snapshot (same window the daily card sees)

```json
{snapshotJson}
```
""");
        return sb.ToString();
    }

    private static AnthropicTool BuildTool()
    {
        var inputSchema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "reply":   { "type": "string", "description": "The coach's answer, max 150 words." },
                "refusal": { "type": "boolean", "description": "True when declining an out-of-bounds ask." }
              },
              "required": ["reply", "refusal"]
            }
            """).RootElement.Clone();
        return new AnthropicTool(
            ToolName,
            "Submit the coach's conversational reply. Call exactly once.",
            inputSchema);
    }

    private async Task LogInvocationAsync(
        Guid invocationId, Guid userId, Guid threadId, string userText,
        AnthropicMessageResponse? response, Stopwatch stopwatch, string? error, CancellationToken ct)
    {
        _db.AgentInvocations.Add(new AgentInvocation
        {
            Id = invocationId,
            UserId = userId,
            Kind = AgentInvocationKinds.Conversation,
            InputSnapshot = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                thread_id = threadId,
                user_text = userText,
            })),
            TodaysRead = "",
            ActionsJson = JsonDocument.Parse("[]"),
            ModelId = response?.Model ?? "",
            InputTokens = response?.Usage?.InputTokens,
            CachedInputTokens = response?.Usage?.CacheReadInputTokens ?? response?.Usage?.CacheCreationInputTokens,
            OutputTokens = response?.Usage?.OutputTokens,
            DurationMs = (int)stopwatch.Elapsed.TotalMilliseconds,
            ErrorMessage = error is { Length: > 2000 } e ? e[..2000] : error,
            TraceId = Activity.Current?.TraceId.ToString(),
        });
        await _db.SaveChangesAsync(ct);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>The coach's validated conversational reply.</summary>
public sealed record CoachReply(string Reply, bool Refusal);

/// <summary>Mechanical guard on the model's conversational tool response.</summary>
internal static class CoachReplyValidator
{
    public static Result<CoachReply> Validate(AnthropicMessageResponse response)
    {
        var block = response.Content.FirstOrDefault(
            c => c.Type == "tool_use" && c.Name == CoachChatService.ToolName);
        if (block?.Input is not JsonElement input)
        {
            return Result<CoachReply>.Failure($"Model response contained no {CoachChatService.ToolName} tool call.");
        }
        if (!input.TryGetProperty("reply", out var replyEl) || replyEl.ValueKind != JsonValueKind.String)
        {
            return Result<CoachReply>.Failure("Tool input missing reply string.");
        }
        var reply = replyEl.GetString()!.Trim();
        if (reply.Length is 0 or > 4000)
        {
            return Result<CoachReply>.Failure("Reply empty or over 4000 chars.");
        }
        var refusal = input.TryGetProperty("refusal", out var refusalEl)
            && refusalEl.ValueKind == JsonValueKind.True;
        return Result<CoachReply>.Success(new CoachReply(reply, refusal));
    }
}
