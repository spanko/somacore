using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;

using SomaCore.Infrastructure.Labs;
using SomaCore.Infrastructure.Persistence;

namespace SomaCore.Api.Pages;

/// <summary>
/// The review-and-confirm surface: every extracted biomarker, next to what
/// the user can check against their PDF. Confirm is the gate — the coach
/// reads nothing from this panel until the user clicks it
/// (session-function-health §1.2, the non-negotiable safeguard).
/// </summary>
[Authorize]
public sealed class LabReviewModel(
    SomaCoreDbContext dbContext,
    ILabUploadService labs) : PageModel
{
    public Guid UploadId { get; private set; }
    public string FileName { get; private set; } = "";
    public DateOnly? CollectedAt { get; private set; }
    public string ParseStatus { get; private set; } = "";
    public string? ParseError { get; private set; }
    public string? Banner { get; private set; }

    public IReadOnlyList<BiomarkerRow> Biomarkers { get; private set; } = Array.Empty<BiomarkerRow>();

    public sealed record BiomarkerRow(
        string DisplayName, string Category, decimal? NumericValue, string? StringValue,
        string? Unit, decimal? ReferenceLow, decimal? ReferenceHigh, string? ReferenceString,
        string Flagged);

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(ct);
        if (userId is null)
        {
            return RedirectToPage("/Labs");
        }

        var upload = await dbContext.LabUploads
            .AsNoTracking()
            .Where(u => u.Id == id && u.UserId == userId.Value)
            .Select(u => new { u.Id, u.FileName, u.CollectedAt, u.ParseStatus, u.ParseError })
            .FirstOrDefaultAsync(ct);
        if (upload is null)
        {
            return RedirectToPage("/Labs");
        }

        UploadId = upload.Id;
        FileName = upload.FileName;
        CollectedAt = upload.CollectedAt;
        ParseStatus = upload.ParseStatus;
        ParseError = upload.ParseError;
        Banner = TempData["ReviewBanner"] as string;

        Biomarkers = await dbContext.LabBiomarkers
            .AsNoTracking()
            .Where(b => b.LabUploadId == id)
            .OrderBy(b => b.Flagged == "in_range" ? 1 : 0)
            .ThenBy(b => b.Category)
            .ThenBy(b => b.DisplayName)
            .Select(b => new BiomarkerRow(
                b.DisplayName, b.Category, b.NumericValue, b.StringValue,
                b.Unit, b.ReferenceLow, b.ReferenceHigh, b.ReferenceString, b.Flagged))
            .ToListAsync(ct);

        return Page();
    }

    public async Task<IActionResult> OnPostConfirmAsync(Guid id, CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(ct);
        if (userId is null)
        {
            return RedirectToPage("/Labs");
        }

        var result = await labs.ConfirmAsync(userId.Value, id, ct);
        if (result.IsSuccess)
        {
            TempData["LabsBanner"] = "Panel confirmed — the coach can reference it from your next card.";
            return RedirectToPage("/Labs");
        }
        TempData["ReviewBanner"] = result.Error;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(ct);
        if (userId is not null)
        {
            await labs.DeleteAsync(userId.Value, id, ct);
            TempData["LabsBanner"] = "Deleted.";
        }
        return RedirectToPage("/Labs");
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
