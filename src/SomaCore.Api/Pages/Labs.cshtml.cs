using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

using SomaCore.Infrastructure.Labs;
using SomaCore.Infrastructure.Persistence;

namespace SomaCore.Api.Pages;

/// <summary>
/// Lab results home: upload a Function Health PDF, see what's on file and
/// its confirmation state. The review-and-confirm surface is
/// <see cref="LabReviewModel"/> — nothing reaches the coach from here.
/// </summary>
[Authorize]
public sealed class LabsModel(
    SomaCoreDbContext dbContext,
    ILabUploadService labs,
    IOptions<LabsOptions> options) : PageModel
{
    private readonly LabsOptions _options = options.Value;

    public bool LabsEnabled => _options.Enabled;

    public string? Banner { get; private set; }
    public string? Error { get; private set; }

    public IReadOnlyList<UploadRow> Uploads { get; private set; } = Array.Empty<UploadRow>();

    public sealed record UploadRow(
        Guid Id, string FileName, DateOnly? CollectedAt, string ParseStatus,
        int BiomarkerCount, int FlaggedCount, DateTimeOffset UploadedAt);

    public async Task OnGetAsync(CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(ct);
        if (userId is null)
        {
            return;
        }

        Banner = TempData["LabsBanner"] as string;
        Error = TempData["LabsError"] as string;

        Uploads = await dbContext.LabUploads
            .AsNoTracking()
            .Where(u => u.UserId == userId.Value)
            .OrderByDescending(u => u.UploadedAt)
            .Select(u => new UploadRow(
                u.Id, u.FileName, u.CollectedAt, u.ParseStatus,
                u.Biomarkers.Count,
                u.Biomarkers.Count(b => b.Flagged == "low" || b.Flagged == "high"),
                u.UploadedAt))
            .ToListAsync(ct);
    }

    public async Task<IActionResult> OnPostUploadAsync(IFormFile? file, CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(ct);
        if (userId is null || !LabsEnabled)
        {
            return RedirectToPage();
        }
        if (file is null || file.Length == 0)
        {
            TempData["LabsError"] = "Choose your Function Health results PDF first.";
            return RedirectToPage();
        }

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);

        var result = await labs.UploadAsync(userId.Value, file.FileName, ms.ToArray(), ct);
        if (result.IsSuccess)
        {
            // Straight to review — the whole point is reading back what was
            // extracted before it counts.
            return RedirectToPage("/LabReview", new { id = result.Value!.Id });
        }
        TempData["LabsError"] = result.Error;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid uploadId, CancellationToken ct)
    {
        var userId = await ResolveUserIdAsync(ct);
        if (userId is not null)
        {
            var result = await labs.DeleteAsync(userId.Value, uploadId, ct);
            TempData["LabsBanner"] = result.IsSuccess ? "Deleted." : result.Error;
        }
        return RedirectToPage();
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
