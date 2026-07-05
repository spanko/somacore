using System.Globalization;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

using SomaCore.Domain.CoachThreads;
using SomaCore.Domain.FoodEntries;
using SomaCore.Domain.UserNotes;
using SomaCore.Infrastructure.Coach;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.QuickLog;

namespace SomaCore.Api.Pages;

/// <summary>
/// The coach page: structured input (meal / workout / note / document),
/// the evidence on file, and conversations about it. Structured forms are
/// explicit user input, so there's no extraction read-back here — the form
/// IS the confirmation. The free-text quick line (with read-back) stays
/// on /me.
/// </summary>
[Authorize]
public sealed class CoachModel(
    SomaCoreDbContext dbContext,
    IQuickLogEntryService quickLogEntries,
    IUserDocumentService documents,
    ICoachChatService chat,
    IOptions<QuickLogOptions> quickLogOptions,
    IOptions<CoachChatOptions> coachChatOptions) : PageModel
{
    private readonly QuickLogOptions _quickLogOptions = quickLogOptions.Value;
    private readonly CoachChatOptions _coachChatOptions = coachChatOptions.Value;

    public bool LoggingEnabled => _quickLogOptions.Enabled;
    public bool ChatEnabled => _coachChatOptions.Enabled;

    public string? Banner { get; private set; }
    public string? Error { get; private set; }

    public IReadOnlyList<LoggedItem> LoggedItems { get; private set; } = Array.Empty<LoggedItem>();
    public IReadOnlyList<DocumentRow> Documents { get; private set; } = Array.Empty<DocumentRow>();
    public IReadOnlyList<ThreadRow> Threads { get; private set; } = Array.Empty<ThreadRow>();

    public sealed record DocumentRow(Guid Id, string FileName, string? Summary, string ParseStatus, DateTimeOffset UploadedAt);
    public sealed record ThreadRow(Guid Id, string SubjectType, string Title, DateTimeOffset LastMessageAt, int MessageCount);

    public async Task OnGetAsync(CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(ct);
        if (userId is null)
        {
            return;
        }

        Banner = TempData["CoachBanner"] as string;
        Error = TempData["CoachError"] as string;

        LoggedItems = await quickLogEntries.GetLoggedItemsAsync(userId.Value, ct);

        Documents = await dbContext.UserDocuments
            .AsNoTracking()
            .Where(d => d.UserId == userId.Value)
            .OrderByDescending(d => d.UploadedAt)
            .Take(20)
            .Select(d => new DocumentRow(d.Id, d.FileName, d.Summary, d.ParseStatus, d.UploadedAt))
            .ToListAsync(ct);

        Threads = await dbContext.CoachThreads
            .AsNoTracking()
            .Where(t => t.UserId == userId.Value)
            .OrderByDescending(t => t.LastMessageAt)
            .Take(20)
            .Select(t => new ThreadRow(
                t.Id, t.SubjectType, t.Title, t.LastMessageAt,
                dbContext.CoachMessages.Count(m => m.ThreadId == t.Id)))
            .ToListAsync(ct);
    }

    // ------------------------------------------------------------------
    // Structured logging — the form is the confirmation; values are still
    // range-checked server-side by the same validator extraction uses.
    // ------------------------------------------------------------------

    public async Task<IActionResult> OnPostLogMealAsync(
        DateOnly? mealDate, string? mealSlot, decimal? calories, decimal? protein,
        decimal? carbs, decimal? fat, string? items, CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(ct);
        if (userId is null || !LoggingEnabled)
        {
            return RedirectToPage();
        }

        var foodItems = (items ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => new FoodItemDraft(n, null))
            .ToArray();

        var draft = new QuickLogExtraction(
            QuickLogEntryType.Meal,
            new MealDraft(
                mealSlot ?? MealSlot.Other,
                mealDate ?? DateOnly.FromDateTime(DateTime.UtcNow.Date),
                calories, protein, carbs, fat, null, null, null,
                foodItems),
            null, null, null);

        var result = await quickLogEntries.ConfirmAsync(userId.Value, draft, HttpContext.TraceIdentifier, ct);
        SetBanner(result.IsSuccess, result.IsSuccess ? result.Value : result.Error);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostLogWorkoutAsync(
        string? workoutType, string? startedAt, int? minutes, string? intensity,
        int? avgHr, CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(ct);
        if (userId is null || !LoggingEnabled)
        {
            return RedirectToPage();
        }

        var started = await ParseLocalAsync(userId.Value, startedAt, ct);
        if (started is null)
        {
            SetBanner(false, "Enter when the workout started.");
            return RedirectToPage();
        }

        var draft = new QuickLogExtraction(
            QuickLogEntryType.Workout, null,
            new WorkoutDraft(
                (workoutType ?? "").Trim().ToLowerInvariant(),
                started.Value,
                (minutes ?? 0) * 60,
                string.IsNullOrWhiteSpace(intensity) ? null : intensity,
                null, null, avgHr),
            null, null);

        var result = await quickLogEntries.ConfirmAsync(userId.Value, draft, HttpContext.TraceIdentifier, ct);
        SetBanner(result.IsSuccess, result.IsSuccess ? result.Value : result.Error);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostLogNoteAsync(
        string? note, string? category, DateOnly? activeUntil, CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(ct);
        if (userId is null || !LoggingEnabled)
        {
            return RedirectToPage();
        }

        var draft = new QuickLogExtraction(
            QuickLogEntryType.Note, null, null,
            new NoteDraft(
                string.IsNullOrWhiteSpace(category) ? null : category,
                (note ?? "").Trim(),
                activeUntil),
            null);

        var result = await quickLogEntries.ConfirmAsync(userId.Value, draft, HttpContext.TraceIdentifier, ct);
        SetBanner(result.IsSuccess, result.IsSuccess ? result.Value : result.Error);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUploadDocumentAsync(IFormFile? file, CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(ct);
        if (userId is null || !ChatEnabled)
        {
            return RedirectToPage();
        }
        if (file is null || file.Length == 0)
        {
            SetBanner(false, "Choose a file first.");
            return RedirectToPage();
        }
        if (file.Length > _coachChatOptions.MaxDocumentBytes)
        {
            SetBanner(false, "Files up to 10 MB are supported.");
            return RedirectToPage();
        }

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);

        var result = await documents.UploadAsync(
            userId.Value, file.FileName, file.ContentType ?? "", ms.ToArray(), ct);
        SetBanner(result.IsSuccess,
            result.IsSuccess ? $"\"{result.Value!.FileName}\" is on file — ask the coach about it." : result.Error);
        return RedirectToPage();
    }

    // ------------------------------------------------------------------
    // Conversations + deletes
    // ------------------------------------------------------------------

    public async Task<IActionResult> OnPostAskAsync(
        string subjectType, Guid? subjectId, CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(ct);
        if (userId is null || !ChatEnabled)
        {
            return RedirectToPage();
        }

        var thread = await chat.StartThreadAsync(userId.Value, subjectType, subjectId, ct);
        if (!thread.IsSuccess)
        {
            SetBanner(false, thread.Error);
            return RedirectToPage();
        }
        return RedirectToPage("/CoachThread", new { id = thread.Value!.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(
        string itemType, Guid itemId, CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(ct);
        if (userId is null)
        {
            return RedirectToPage();
        }

        var result = itemType == "document"
            ? await documents.DeleteAsync(userId.Value, itemId, ct)
            : await quickLogEntries.DeleteAsync(userId.Value, itemType, itemId, ct);
        SetBanner(result.IsSuccess, result.IsSuccess ? "Deleted." : result.Error);
        return RedirectToPage();
    }

    // ------------------------------------------------------------------

    private void SetBanner(bool success, string? message)
    {
        if (success)
        {
            TempData["CoachBanner"] = message;
        }
        else
        {
            TempData["CoachError"] = message;
        }
    }

    /// <summary>
    /// datetime-local inputs arrive without an offset. Interpret them in the
    /// user's timezone, best known from their most recent sleep — same
    /// source of truth as the snapshot builder.
    /// </summary>
    private async Task<DateTimeOffset?> ParseLocalAsync(Guid userId, string? value, CancellationToken ct)
    {
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var naive))
        {
            return null;
        }
        var tz = await dbContext.WhoopSleeps
            .AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.EndAt)
            .Select(s => s.TimezoneOffset)
            .FirstOrDefaultAsync(ct) ?? "+00:00";
        var offset = TimeSpan.TryParseExact(tz.TrimStart('+'), @"hh\:mm",
            CultureInfo.InvariantCulture, out var parsed)
            ? (tz.StartsWith('-') ? -parsed : parsed)
            : TimeSpan.Zero;
        return new DateTimeOffset(naive, offset);
    }

    private async Task<Guid?> ResolveUserIdAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(User.GetObjectId(), out var entraOid))
        {
            return null;
        }
        return await dbContext.Users
            .AsNoTracking()
            .Where(u => u.EntraOid == entraOid)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct);
    }

    public static string ChipTag(string itemType) => itemType switch
    {
        "meal" => "MEAL",
        "workout" => "WORK",
        "note" => "NOTE",
        "document" or CoachThreadSubjectType.Document => "DOC",
        CoachThreadSubjectType.General => "ALL",
        _ => itemType.ToUpperInvariant() is { Length: > 4 } s ? s[..4] : itemType.ToUpperInvariant(),
    };

    public static string ChipTagClass(string itemType) => itemType switch
    {
        "meal" => "chip-tag-meal",
        "workout" => "chip-tag-work",
        "note" => "chip-tag-note",
        _ => "chip-tag-doc",
    };
}
