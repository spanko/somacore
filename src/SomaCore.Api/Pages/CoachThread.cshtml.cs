using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

using SomaCore.Infrastructure.Coach;
using SomaCore.Infrastructure.Persistence;

namespace SomaCore.Api.Pages;

/// <summary>
/// One conversation with the coach. The subject rides pinned at the top;
/// the coach's turns render as margin annotations. Turns are capped so
/// this stays a conversation about the plan, not a chatbot.
/// </summary>
[Authorize]
public sealed class CoachThreadModel(
    SomaCoreDbContext dbContext,
    ICoachChatService chat,
    IOptions<CoachChatOptions> options) : PageModel
{
    private readonly CoachChatOptions _options = options.Value;

    public bool ChatEnabled => _options.Enabled;
    public int MaxTurnChars => _options.MaxTurnChars;
    public int MaxUserTurns => _options.MaxUserTurnsPerThread;

    public Guid ThreadId { get; private set; }
    public string SubjectType { get; private set; } = "general";
    public string Title { get; private set; } = "";
    public int TurnsLeft { get; private set; }
    public string? Error { get; private set; }

    public IReadOnlyList<MessageRow> Messages { get; private set; } = Array.Empty<MessageRow>();

    public sealed record MessageRow(string Role, string Content, bool Refusal, DateTimeOffset CreatedAt);

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(ct);
        if (userId is null || !ChatEnabled)
        {
            return RedirectToPage("/Coach");
        }

        var thread = await dbContext.CoachThreads
            .AsNoTracking()
            .Where(t => t.Id == id && t.UserId == userId.Value)
            .Select(t => new { t.Id, t.SubjectType, t.Title })
            .FirstOrDefaultAsync(ct);
        if (thread is null)
        {
            return RedirectToPage("/Coach");
        }

        ThreadId = thread.Id;
        SubjectType = thread.SubjectType;
        Title = thread.Title;
        Error = TempData["ThreadError"] as string;

        var messages = await dbContext.CoachMessages
            .AsNoTracking()
            .Where(m => m.ThreadId == id)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new MessageRow(m.Role, m.Content, m.Refusal, m.CreatedAt))
            .ToListAsync(ct);
        Messages = messages;
        TurnsLeft = Math.Max(0, _options.MaxUserTurnsPerThread - messages.Count(m => m.Role == "user"));

        return Page();
    }

    public async Task<IActionResult> OnPostSendAsync(Guid id, string? text, CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(ct);
        if (userId is null || !ChatEnabled)
        {
            return RedirectToPage("/Coach");
        }

        var result = await chat.SendAsync(userId.Value, id, text ?? "", ct);
        if (!result.IsSuccess)
        {
            TempData["ThreadError"] = result.Error;
        }
        return RedirectToPage(new { id });
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
}
