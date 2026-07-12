using System.IO.Compression;
using System.Security.Claims;
using System.Text;

using FluentAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using SomaCore.Api.Pages;
using SomaCore.Domain.Users;
using SomaCore.Infrastructure.Agent;
using SomaCore.Infrastructure.Mfp;
using SomaCore.Infrastructure.Persistence;

using Testcontainers.PostgreSql;

namespace SomaCore.IntegrationTests;

/// <summary>
/// M1 coverage: the MFP data-export ZIP path into mfp_food_entries. The
/// fixture is a synthetic MFP-export-shaped ZIP built from the help-center
/// description (meal-level nutrition CSV: Date + Meal + macros + food notes);
/// Adam's REAL export is the post-loop acceptance gate (integrations-INBOX).
/// </summary>
public class MfpCsvUploadTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("somacore_mfp_csv")
        .WithUsername("somacore")
        .WithPassword("devonly")
        .Build();

    private SomaCoreDbContext _db = null!;
    private Guid _userId;
    private Guid _entraOid;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var options = new DbContextOptionsBuilder<SomaCoreDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .Options;
        _db = new SomaCoreDbContext(options);
        await _db.Database.MigrateAsync();

        _entraOid = Guid.NewGuid();
        var user = new User
        {
            EntraOid = _entraOid,
            EntraTenantId = Guid.NewGuid(),
            Email = "mfp-csv-test@example.com",
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        _userId = user.Id;
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private MfpCsvUploadService BuildService()
        => new(_db, Options.Create(new MfpOptions { CsvUploadEnabled = true }),
            NullLogger<MfpCsvUploadService>.Instance);

    private const string GoldenCsv =
        """
        Date,Meal,Calories,Fat (g),Sodium (mg),Carbohydrates (g),Fiber,Sugar,Protein (g),Note
        2026-07-08,Breakfast,420,12.5,300,45.2,6.1,12.0,32.5,"Greek Yogurt; Blueberries"
        2026-07-08,Lunch,650,22.0,850,55.0,8.0,9.5,45.0,"Chicken Teriyaki; Rice"
        2026-07-08,Dinner,780,30.1,900,60.3,10.2,14.8,52.0,"Salmon; Quinoa"
        2026-07-09,Breakfast,390,10.0,250,40.0,5.5,11.0,30.0,Oatmeal
        2026-07-09,Snacks,180,8.0,120,20.0,3.0,10.0,6.0,Almonds
        """;

    private static MemoryStream BuildZip(params (string EntryName, string Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }
        ms.Position = 0;
        return ms;
    }

    private static MemoryStream GoldenZip() => BuildZip(
        ("Nutrition-Summary.csv", GoldenCsv),
        ("Exercise-Summary.csv", "Date,Exercise,Calories Burned\n2026-07-08,Running,450"),
        ("Measurement-Summary.csv", "Date,Weight\n2026-07-08,82.5"));

    [Fact]
    public async Task Golden_zip_parses_into_expected_rows()
    {
        using var zip = GoldenZip();
        var result = await BuildService().IngestExportAsync(_userId, zip, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.CsvEntryName.Should().Be("Nutrition-Summary.csv");
        result.Value.RowsInserted.Should().Be(5);
        result.Value.RowsReplaced.Should().Be(0);
        result.Value.DaysCovered.Should().Be(2);

        var rows = await _db.FoodEntries.AsNoTracking()
            .Where(f => f.UserId == _userId && f.Source == "csv_upload")
            .OrderBy(f => f.MealDate).ThenBy(f => f.MealSlot)
            .ToListAsync();
        rows.Should().HaveCount(5);
        rows.Should().OnlyContain(f => f.IngestedVia == "csv_upload");

        var lunch = rows.Single(f => f.MealDate == new DateOnly(2026, 7, 8) && f.MealSlot == "lunch");
        lunch.Calories.Should().Be(650m);
        lunch.ProteinG.Should().Be(45.0m);
        lunch.CarbsG.Should().Be(55.0m);
        lunch.FatG.Should().Be(22.0m);
        lunch.FiberG.Should().Be(8.0m);
        lunch.SugarG.Should().Be(9.5m);
        lunch.SodiumMg.Should().Be(850m);
        lunch.LoggedAt.Should().BeNull("the export row is a slot-level rollup");
        lunch.FoodItems.RootElement.GetArrayLength().Should().Be(2);
        lunch.FoodItems.RootElement[0].GetProperty("name").GetString().Should().Be("Chicken Teriyaki");

        // MFP's "Snacks" maps onto the schema's singular 'snack' slot.
        rows.Should().ContainSingle(f => f.MealDate == new DateOnly(2026, 7, 9) && f.MealSlot == "snack");
    }

    [Fact]
    public async Task Reuploading_the_same_zip_replaces_rather_than_duplicates()
    {
        using (var first = GoldenZip())
        {
            await BuildService().IngestExportAsync(_userId, first, CancellationToken.None);
        }

        using var second = GoldenZip();
        var result = await BuildService().IngestExportAsync(_userId, second, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.RowsInserted.Should().Be(0);
        result.Value.RowsReplaced.Should().Be(5);

        var rows = await _db.FoodEntries.AsNoTracking()
            .Where(f => f.UserId == _userId && f.Source == "csv_upload")
            .ToListAsync();
        rows.Should().HaveCount(5, "re-upload must not duplicate");

        // REPLACE semantics: values identical, never doubled (a merge would
        // have made lunch protein 90).
        var lunch = rows.Single(f => f.MealDate == new DateOnly(2026, 7, 8) && f.MealSlot == "lunch");
        lunch.ProteinG.Should().Be(45.0m);
        lunch.Calories.Should().Be(650m);
    }

    [Fact]
    public async Task Reuploading_corrected_values_wins_over_the_old_row()
    {
        using (var first = GoldenZip())
        {
            await BuildService().IngestExportAsync(_userId, first, CancellationToken.None);
        }

        // The user fixed their 7/8 lunch in MFP and re-exported.
        var corrected = GoldenCsv.Replace(
            "2026-07-08,Lunch,650,22.0,850,55.0,8.0,9.5,45.0",
            "2026-07-08,Lunch,700,25.0,900,58.0,8.0,9.5,48.0");
        using var zip = BuildZip(("Nutrition-Summary.csv", corrected));
        var result = await BuildService().IngestExportAsync(_userId, zip, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var lunch = await _db.FoodEntries.AsNoTracking()
            .SingleAsync(f => f.UserId == _userId && f.MealDate == new DateOnly(2026, 7, 8) && f.MealSlot == "lunch");
        lunch.Calories.Should().Be(700m, "the re-uploaded export is a full restatement — replace, not merge");
        lunch.ProteinG.Should().Be(48.0m);
    }

    [Fact]
    public async Task Zip_slip_entry_rejects_the_whole_upload()
    {
        using var zip = BuildZip(
            ("../evil.csv", "Date,Meal\n2026-07-08,Breakfast"),
            ("Nutrition-Summary.csv", GoldenCsv));

        var result = await BuildService().IngestExportAsync(_userId, zip, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("invalid entry path");
        (await _db.FoodEntries.AsNoTracking().CountAsync(f => f.UserId == _userId))
            .Should().Be(0, "a hostile archive must contribute nothing");
    }

    [Fact]
    public async Task Food_names_stay_server_side_and_out_of_the_agent_snapshot()
    {
        // Recent dates so the snapshot's 7-day food window picks the rows up.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var csv =
            $"""
            Date,Meal,Calories,Fat (g),Sodium (mg),Carbohydrates (g),Fiber,Sugar,Protein (g),Note
            {today.AddDays(-1):yyyy-MM-dd},Lunch,650,22.0,850,55.0,8.0,9.5,45.0,"Chicken Teriyaki; Rice"
            """;
        using var zip = BuildZip(("Nutrition-Summary.csv", csv));
        (await BuildService().IngestExportAsync(_userId, zip, CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        // Server-side: the names ARE on the row.
        var row = await _db.FoodEntries.AsNoTracking()
            .SingleAsync(f => f.UserId == _userId && f.Source == "csv_upload");
        row.FoodItems.RootElement.GetArrayLength().Should().Be(2);

        // Coach-side: the snapshot carries totals + timing, never names
        // (privacy draft Part 4 / Section D commitment — extends the existing
        // quick-log behavior to a csv_upload-sourced row).
        var snapshot = await AgentInputSnapshotBuilder.BuildAsync(
            _db, _userId, DateTimeOffset.UtcNow, CancellationToken.None);
        snapshot.Json.Should().Contain("\"protein_g\":45.0");
        snapshot.Json.Should().Contain("csv_upload", "source provenance is coach-visible");
        snapshot.Json.Should().NotContain("Chicken Teriyaki");
        snapshot.Json.Should().NotContain("Rice");
        snapshot.Json.Should().NotContain("food_items");
    }

    [Fact]
    public async Task Food_page_404s_when_flag_off_and_serves_when_on()
    {
        var offModel = BuildFoodModel(enabled: false);
        (await offModel.OnGetAsync(CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await offModel.OnPostUploadAsync(null, CancellationToken.None)).Should().BeOfType<NotFoundResult>(
            "the POST handler is gated too — the flag guards the surface, not just the link");

        var onModel = BuildFoodModel(enabled: true);
        (await onModel.OnGetAsync(CancellationToken.None)).Should().BeOfType<PageResult>();
    }

    private FoodModel BuildFoodModel(bool enabled)
    {
        var model = new FoodModel(
            _db,
            BuildService(),
            Options.Create(new MfpOptions { CsvUploadEnabled = enabled }));

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(Microsoft.Identity.Web.ClaimConstants.ObjectId, _entraOid.ToString()),
            }, "test")),
        };
        var actionContext = new ActionContext(httpContext, new RouteData(), new PageActionDescriptor(), new ModelStateDictionary());
        model.PageContext = new PageContext(actionContext)
        {
            ViewData = new ViewDataDictionary<FoodModel>(
                new EmptyModelMetadataProvider(), new ModelStateDictionary()),
        };
        model.TempData = new TempDataDictionary(httpContext, Substitute.For<ITempDataProvider>());
        return model;
    }
}
