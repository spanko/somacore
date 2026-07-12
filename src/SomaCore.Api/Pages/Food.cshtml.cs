using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

using SomaCore.Infrastructure.Mfp;
using SomaCore.Infrastructure.Persistence;

namespace SomaCore.Api.Pages;

/// <summary>
/// /me/food — upload an MFP data-export ZIP (CSV path, MFP session brief
/// §1.3). No confirm gate: the export is a first-party MFP artifact, not an
/// LLM-parsed one (contrast /me/labs). The whole surface 404s while
/// Mfp:CsvUploadEnabled is false — the default, and dev's value until the
/// real-export acceptance gate passes.
/// </summary>
[Authorize]
public sealed class FoodModel(
    SomaCoreDbContext dbContext,
    IMfpCsvUploadService csvUpload,
    IOptions<MfpOptions> options) : PageModel
{
    private readonly MfpOptions _options = options.Value;

    public bool CsvUploadEnabled => _options.CsvUploadEnabled;
    public long MaxUploadBytes => _options.MaxUploadBytes;

    public string? Banner { get; private set; }
    public string? Error { get; private set; }

    public IReadOnlyList<CsvRow> RecentRows { get; private set; } = Array.Empty<CsvRow>();

    public sealed record CsvRow(
        DateOnly MealDate, string MealSlot, decimal? Calories, decimal? ProteinG,
        decimal? CarbsG, decimal? FatG, DateTimeOffset IngestedAt);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!CsvUploadEnabled)
        {
            return NotFound();
        }

        var userId = await ResolveUserIdAsync(ct);
        if (userId is null)
        {
            return Page();
        }

        Banner = TempData["FoodBanner"] as string;
        Error = TempData["FoodError"] as string;

        var since = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-14);
        RecentRows = await dbContext.FoodEntries
            .AsNoTracking()
            .Where(f => f.UserId == userId.Value
                     && f.Source == "csv_upload"
                     && f.MealDate >= since)
            .OrderByDescending(f => f.MealDate)
            .ThenBy(f => f.MealSlot)
            .Select(f => new CsvRow(
                f.MealDate, f.MealSlot, f.Calories, f.ProteinG, f.CarbsG, f.FatG, f.IngestedAt))
            .ToListAsync(ct);

        return Page();
    }

    public async Task<IActionResult> OnPostUploadAsync(IFormFile? file, CancellationToken ct)
    {
        if (!CsvUploadEnabled)
        {
            return NotFound();
        }

        var userId = await ResolveUserIdAsync(ct);
        if (userId is null)
        {
            return RedirectToPage();
        }
        if (file is null || file.Length == 0)
        {
            TempData["FoodError"] = "Choose your MyFitnessPal export ZIP first.";
            return RedirectToPage();
        }
        if (file.Length > MaxUploadBytes)
        {
            TempData["FoodError"] = "That file is larger than the 50 MB limit.";
            return RedirectToPage();
        }
        if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            TempData["FoodError"] = "Upload the ZIP exactly as MFP emailed it (a .zip file).";
            return RedirectToPage();
        }

        await using var stream = file.OpenReadStream();
        var result = await csvUpload.IngestExportAsync(userId.Value, stream, ct);
        if (result.IsSuccess)
        {
            var o = result.Value!;
            TempData["FoodBanner"] =
                $"Parsed {o.CsvEntryName}: {o.RowsInserted} new, {o.RowsReplaced} replaced across {o.DaysCovered} day(s)."
                + (o.RowsSkipped > 0 ? $" {o.RowsSkipped} unparseable row(s) skipped." : string.Empty);
        }
        else
        {
            TempData["FoodError"] = result.Error;
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
