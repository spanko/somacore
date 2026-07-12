using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SomaCore.Domain.Common;
using SomaCore.Domain.FoodEntries;
using SomaCore.Infrastructure.Persistence;

namespace SomaCore.Infrastructure.Mfp;

public interface IMfpCsvUploadService
{
    /// <summary>
    /// Unpack an MFP data-export ZIP, locate the meal-nutrition CSV, and
    /// upsert per-(date, meal-slot) rows into mfp_food_entries with
    /// source='csv_upload'. Idempotent: re-uploading overlapping history
    /// REPLACES each slot's values rather than merging.
    /// </summary>
    Task<Result<MfpCsvUploadOutcome>> IngestExportAsync(
        Guid userId,
        Stream zipStream,
        CancellationToken cancellationToken);
}

public sealed record MfpCsvUploadOutcome(
    string CsvEntryName,
    int RowsInserted,
    int RowsReplaced,
    int RowsSkipped,
    int DaysCovered);

/// <summary>
/// The CSV half of the MFP session (brief §1.3; iOS is out of loop scope).
/// The export's meal-nutrition CSV is a first-party MFP artifact — values
/// are trusted as-shipped, no confirm gate (unlike lab PDFs, where LLM
/// extraction can hallucinate). Column names follow MFP's help-center
/// description (Date + Meal + per-meal nutrition, matched case-insensitively
/// with unit-suffix tolerance); Adam's REAL export is the acceptance gate
/// before the flag ever flips (integrations-INBOX note).
/// </summary>
public sealed class MfpCsvUploadService(
    SomaCoreDbContext dbContext,
    IOptions<MfpOptions> options,
    ILogger<MfpCsvUploadService> logger)
    : IMfpCsvUploadService
{
    /// <summary>Uncompressed-total cap — a 50 MB ZIP of CSV text must not inflate without bound.</summary>
    private const long MaxUncompressedBytes = 256L * 1024 * 1024;

    public async Task<Result<MfpCsvUploadOutcome>> IngestExportAsync(
        Guid userId,
        Stream zipStream,
        CancellationToken cancellationToken)
    {
        // The page enforces the cap on the uploaded file; re-check here so
        // the service is safe for any future caller.
        if (zipStream.CanSeek && zipStream.Length > options.Value.MaxUploadBytes)
        {
            return Result<MfpCsvUploadOutcome>.Failure(
                "That file is larger than the upload limit.");
        }

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            return Result<MfpCsvUploadOutcome>.Failure("That file isn't a readable ZIP archive.");
        }

        using (archive)
        {
            // Zip-slip guard: a hostile entry path rejects the WHOLE upload —
            // a legitimate MFP export never contains traversal paths, so any
            // occurrence means the archive is not what it claims to be.
            long totalUncompressed = 0;
            foreach (var entry in archive.Entries)
            {
                if (IsHostileEntryPath(entry.FullName))
                {
                    logger.LogWarning("Rejecting MFP upload: hostile ZIP entry path");
                    return Result<MfpCsvUploadOutcome>.Failure(
                        "The archive contains an invalid entry path and was rejected.");
                }
                totalUncompressed += entry.Length;
            }
            if (totalUncompressed > MaxUncompressedBytes)
            {
                return Result<MfpCsvUploadOutcome>.Failure(
                    "The archive expands beyond the allowed size and was rejected.");
            }

            var csvEntry = LocateMealNutritionCsv(archive);
            if (csvEntry is null)
            {
                return Result<MfpCsvUploadOutcome>.Failure(
                    "No meal-nutrition CSV found in the archive. Export your data from myfitnesspal.com (desktop) and upload the ZIP it emails you.");
            }

            List<ParsedRow> rows;
            int skippedRows;
            using (var reader = new StreamReader(csvEntry.Open()))
            {
                var text = await reader.ReadToEndAsync(cancellationToken);
                var parse = ParseMealNutritionCsv(text);
                if (!parse.IsSuccess)
                {
                    return Result<MfpCsvUploadOutcome>.Failure(parse.Error!);
                }
                (rows, skippedRows) = parse.Value!;
            }

            var outcome = await UpsertAsync(userId, csvEntry.FullName, rows, skippedRows, cancellationToken);
            logger.LogInformation(
                "MFP CSV upload for user {UserId}: {Inserted} inserted, {Replaced} replaced, {Skipped} skipped across {Days} days from {CsvEntry}",
                userId, outcome.RowsInserted, outcome.RowsReplaced, outcome.RowsSkipped, outcome.DaysCovered, csvEntry.FullName);
            return Result<MfpCsvUploadOutcome>.Success(outcome);
        }
    }

    private static bool IsHostileEntryPath(string fullName)
        => fullName.Contains("..", StringComparison.Ordinal)
        || fullName.StartsWith('/')
        || fullName.StartsWith('\\')
        || fullName.Contains(':', StringComparison.Ordinal);

    private static ZipArchiveEntry? LocateMealNutritionCsv(ZipArchive archive)
    {
        var csvs = archive.Entries
            .Where(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // MFP names the meal-level file "Nutrition-Summary" per its export
        // convention; fall back to any CSV whose header carries Date + Meal
        // so a renamed variant still parses.
        return csvs.FirstOrDefault(e => e.Name.Contains("nutrition", StringComparison.OrdinalIgnoreCase))
            ?? csvs.FirstOrDefault(HeaderLooksLikeMealNutrition);
    }

    private static bool HeaderLooksLikeMealNutrition(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        var header = reader.ReadLine();
        if (header is null) return false;
        return header.Contains("date", StringComparison.OrdinalIgnoreCase)
            && header.Contains("meal", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ParsedRow(
        DateOnly Date,
        string MealSlot,
        decimal? Calories,
        decimal? ProteinG,
        decimal? CarbsG,
        decimal? FatG,
        decimal? FiberG,
        decimal? SugarG,
        decimal? SodiumMg,
        List<string> FoodNames,
        Dictionary<string, string> Raw);

    private static Result<(List<ParsedRow> Rows, int Skipped)> ParseMealNutritionCsv(string text)
    {
        var records = CsvReader.Parse(text);
        if (records.Count == 0)
        {
            return Result<(List<ParsedRow>, int)>.Failure("The meal-nutrition CSV is empty.");
        }

        var header = records[0].Select(NormalizeHeader).ToArray();
        int Col(params string[] names) => Array.FindIndex(header, h => names.Contains(h));

        var dateCol = Col("date");
        var mealCol = Col("meal");
        if (dateCol < 0 || mealCol < 0)
        {
            return Result<(List<ParsedRow>, int)>.Failure(
                "The meal-nutrition CSV is missing the Date/Meal columns.");
        }

        var caloriesCol = Col("calories");
        var proteinCol = Col("protein");
        var carbsCol = Col("carbohydrates", "total carbohydrates", "carbs");
        var fatCol = Col("fat", "total fat");
        var fiberCol = Col("fiber", "dietary fiber");
        var sugarCol = Col("sugar", "sugars");
        var sodiumCol = Col("sodium");
        var foodsCol = Col("foods", "food", "note", "notes");

        // Group by (date, slot): the export is already per-meal, but summing
        // guards against variants that emit one row per food.
        var skipped = 0;
        var grouped = new Dictionary<(DateOnly, string), ParsedRow>();
        for (var i = 1; i < records.Count; i++)
        {
            var record = records[i];
            if (record.Count <= Math.Max(dateCol, mealCol)) { skipped++; continue; }

            if (!TryParseDate(record[dateCol], out var date)) { skipped++; continue; }
            var slot = NormalizeMealSlot(record[mealCol]);

            decimal? Num(int col) =>
                col >= 0 && col < record.Count
                    && decimal.TryParse(record[col], NumberStyles.Number, CultureInfo.InvariantCulture, out var v)
                    ? v : null;

            var foods = new List<string>();
            if (foodsCol >= 0 && foodsCol < record.Count && !string.IsNullOrWhiteSpace(record[foodsCol]))
            {
                foods.AddRange(record[foodsCol]
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            var raw = new Dictionary<string, string>();
            for (var c = 0; c < Math.Min(header.Length, record.Count); c++)
            {
                raw[header[c]] = record[c];
            }

            var row = new ParsedRow(
                date, slot,
                Num(caloriesCol), Num(proteinCol), Num(carbsCol), Num(fatCol),
                Num(fiberCol), Num(sugarCol), Num(sodiumCol),
                foods, raw);

            if (grouped.TryGetValue((date, slot), out var existing))
            {
                grouped[(date, slot)] = existing with
                {
                    Calories = SumN(existing.Calories, row.Calories),
                    ProteinG = SumN(existing.ProteinG, row.ProteinG),
                    CarbsG = SumN(existing.CarbsG, row.CarbsG),
                    FatG = SumN(existing.FatG, row.FatG),
                    FiberG = SumN(existing.FiberG, row.FiberG),
                    SugarG = SumN(existing.SugarG, row.SugarG),
                    SodiumMg = SumN(existing.SodiumMg, row.SodiumMg),
                    FoodNames = existing.FoodNames.Concat(row.FoodNames).ToList(),
                };
            }
            else
            {
                grouped[(date, slot)] = row;
            }
        }

        return Result<(List<ParsedRow>, int)>.Success((grouped.Values.ToList(), skipped));
    }

    private async Task<MfpCsvUploadOutcome> UpsertAsync(
        Guid userId,
        string csvEntryName,
        List<ParsedRow> rows,
        int skipped,
        CancellationToken cancellationToken)
    {
        int inserted = 0, replaced = 0;

        var dates = rows.Select(r => r.Date).Distinct().ToArray();
        var existingRows = await dbContext.FoodEntries
            .Where(f => f.UserId == userId
                     && f.Source == "csv_upload"
                     && dates.Contains(f.MealDate))
            .ToListAsync(cancellationToken);
        var existingBySlot = existingRows.ToDictionary(f => (f.MealDate, f.MealSlot));

        var now = DateTimeOffset.UtcNow;
        foreach (var row in rows)
        {
            var foodItems = JsonDocument.Parse(JsonSerializer.Serialize(
                row.FoodNames.Select(n => new { name = n })));
            var rawPayload = JsonDocument.Parse(JsonSerializer.Serialize(row.Raw));

            if (existingBySlot.TryGetValue((row.Date, row.MealSlot), out var entry))
            {
                // REPLACE, never merge: a CSV re-upload is a full restatement
                // of the slot from MFP, the source of truth — merging would
                // double-count every overlapping day on re-upload. Quick-log
                // merges because each manual line is an increment; an export
                // row is the whole slot.
                entry.Calories = row.Calories;
                entry.ProteinG = row.ProteinG;
                entry.CarbsG = row.CarbsG;
                entry.FatG = row.FatG;
                entry.FiberG = row.FiberG;
                entry.SugarG = row.SugarG;
                entry.SodiumMg = row.SodiumMg;
                entry.FoodItems = foodItems;
                entry.RawPayload = rawPayload;
                entry.IngestedVia = "csv_upload";
                entry.IngestedAt = now;
                replaced++;
            }
            else
            {
                dbContext.FoodEntries.Add(new FoodEntry
                {
                    UserId = userId,
                    Source = "csv_upload",
                    MealDate = row.Date,
                    MealSlot = row.MealSlot,
                    LoggedAt = null, // slot-level rollup — no per-item timestamp
                    Calories = row.Calories,
                    ProteinG = row.ProteinG,
                    CarbsG = row.CarbsG,
                    FatG = row.FatG,
                    FiberG = row.FiberG,
                    SugarG = row.SugarG,
                    SodiumMg = row.SodiumMg,
                    FoodItems = foodItems,
                    RawPayload = rawPayload,
                    IngestedVia = "csv_upload",
                    IngestedAt = now,
                });
                inserted++;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new MfpCsvUploadOutcome(csvEntryName, inserted, replaced, skipped, dates.Length);
    }

    private static string NormalizeHeader(string raw)
    {
        var h = raw.Trim().ToLowerInvariant();
        // Strip unit suffixes: "fat (g)" → "fat", "sodium (mg)" → "sodium".
        var paren = h.IndexOf('(');
        if (paren > 0)
        {
            h = h[..paren].Trim();
        }
        return h;
    }

    private static string NormalizeMealSlot(string raw)
        => raw.Trim().ToLowerInvariant() switch
        {
            "breakfast" => MealSlot.Breakfast,
            "lunch" => MealSlot.Lunch,
            "dinner" => MealSlot.Dinner,
            "snack" or "snacks" => MealSlot.Snack,
            _ => MealSlot.Other,
        };

    private static bool TryParseDate(string raw, out DateOnly date)
        => DateOnly.TryParseExact(raw.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
        || DateOnly.TryParseExact(raw.Trim(), "M/d/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static decimal? SumN(decimal? a, decimal? b)
        => a is null && b is null ? null : (a ?? 0) + (b ?? 0);

    /// <summary>
    /// Minimal RFC 4180 CSV reader (quoted fields, escaped quotes, CR/LF).
    /// Deliberately in-repo: a CSV NuGet package would be a new dependency
    /// (a decision per CLAUDE.md) for ~40 lines of parsing.
    /// </summary>
    internal static class CsvReader
    {
        public static List<List<string>> Parse(string text)
        {
            var records = new List<List<string>>();
            var current = new List<string>();
            var field = new System.Text.StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        field.Append(c);
                    }
                }
                else if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    current.Add(field.ToString());
                    field.Clear();
                }
                else if (c is '\r' or '\n')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }
                    current.Add(field.ToString());
                    field.Clear();
                    if (current.Count > 1 || current[0].Length > 0)
                    {
                        records.Add(current);
                    }
                    current = new List<string>();
                }
                else
                {
                    field.Append(c);
                }
            }

            if (field.Length > 0 || current.Count > 0)
            {
                current.Add(field.ToString());
                if (current.Count > 1 || current[0].Length > 0)
                {
                    records.Add(current);
                }
            }

            return records;
        }
    }
}
