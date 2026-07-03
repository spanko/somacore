using System.Net;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using SomaCore.Domain.Agent;
using SomaCore.Domain.Users;
using SomaCore.Infrastructure.Agent;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.QuickLog;

using Testcontainers.PostgreSql;

namespace SomaCore.IntegrationTests;

/// <summary>
/// Quick-log (session-quick-log.md) end-to-end coverage against real
/// Postgres: confirm-persist including the meal merge rule, ownership on
/// delete, the daily extraction cap, the user-deletion cascade, and the
/// snapshot builder's new sections. The Anthropic side is a stub HTTP
/// handler returning canned tool responses — extraction PROMPT quality is
/// validated with real fixtures at flag-flip time, not here.
/// </summary>
public class QuickLogTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("somacore_quicklog")
        .WithUsername("somacore")
        .WithPassword("devonly")
        .Build();

    private ServiceProvider _services = null!;
    private SomaCoreDbContext _db = null!;
    private Guid _userId;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var collection = new ServiceCollection();
        collection.AddDbContext<SomaCoreDbContext>(o => o
            .UseNpgsql(_postgres.GetConnectionString())
            .UseSnakeCaseNamingConvention());
        _services = collection.BuildServiceProvider();

        _db = _services.GetRequiredService<SomaCoreDbContext>();
        await _db.Database.MigrateAsync();

        var user = new User
        {
            EntraOid = Guid.NewGuid(),
            EntraTenantId = Guid.NewGuid(),
            Email = "quicklog-test@example.com",
            DisplayName = "Quick Log Test",
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        _userId = user.Id;
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private IQuickLogEntryService EntryService()
        => new QuickLogEntryService(_db, NullLogger<QuickLogEntryService>.Instance);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.Date);

    // ------------------------------------------------------------------
    // Confirm-persist
    // ------------------------------------------------------------------

    [Fact]
    public async Task Confirmed_meal_persists_with_manual_source()
    {
        var draft = MealExtraction(Today, "breakfast", proteinG: 30, calories: 400);

        var result = await EntryService().ConfirmAsync(_userId, draft, "trace-1", CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Error);
        var row = await _db.FoodEntries.SingleAsync(
            f => f.UserId == _userId && f.MealSlot == "breakfast");
        row.Source.Should().Be("manual");
        row.IngestedVia.Should().Be("quick_log");
        row.ProteinG.Should().Be(30);
    }

    [Fact]
    public async Task Second_confirm_into_same_slot_merges_instead_of_duplicating()
    {
        var svc = EntryService();
        await svc.ConfirmAsync(_userId,
            MealExtraction(Today, "lunch", proteinG: 50, calories: 600, foodName: "chicken bowl"),
            null, CancellationToken.None);

        var merge = await svc.ConfirmAsync(_userId,
            MealExtraction(Today, "lunch", proteinG: 10, calories: null, foodName: "cookie"),
            null, CancellationToken.None);

        merge.IsSuccess.Should().BeTrue(merge.Error);
        var rows = await _db.FoodEntries
            .Where(f => f.UserId == _userId && f.MealSlot == "lunch" && f.MealDate == Today)
            .ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].ProteinG.Should().Be(60);              // 50 + 10
        rows[0].Calories.Should().Be(600);             // null merges as no-op
        rows[0].FoodItems.RootElement.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Confirmed_workout_persists_with_manual_bundle_id()
    {
        var draft = new QuickLogExtraction(
            QuickLogEntryType.Workout, null,
            new WorkoutDraft("ride", DateTimeOffset.UtcNow.AddHours(-2), 2700, "hard", null, null, null),
            null, null);

        var result = await EntryService().ConfirmAsync(_userId, draft, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Error);
        var row = await _db.HealthKitWorkouts.SingleAsync(w => w.UserId == _userId);
        row.SourceBundleId.Should().Be("manual");
        row.ElapsedSeconds.Should().Be(2700);
    }

    [Fact]
    public async Task Confirmed_note_persists_and_expired_note_leaves_snapshot()
    {
        var svc = EntryService();
        await svc.ConfirmAsync(_userId, NoteExtraction("knee is sore", "symptom", null),
            null, CancellationToken.None);
        await svc.ConfirmAsync(_userId, NoteExtraction("traveling", "schedule", Today.AddDays(2)),
            null, CancellationToken.None);

        // Manufacture an already-expired note directly (the validator
        // rejects past dates on the way in, which is exactly why the
        // snapshot filter needs independent verification).
        _db.UserNotes.Add(new SomaCore.Domain.UserNotes.UserNote
        {
            UserId = _userId,
            Source = "quick_log",
            Note = "old context",
            ActiveUntil = Today.AddDays(-1),
        });
        await _db.SaveChangesAsync();

        var snapshot = await AgentInputSnapshotBuilder.BuildAsync(
            _db, _userId, DateTimeOffset.UtcNow, CancellationToken.None);

        snapshot.Json.Should().Contain("knee is sore");
        snapshot.Json.Should().Contain("traveling");
        snapshot.Json.Should().NotContain("old context");
    }

    [Fact]
    public async Task Confirm_revalidates_ranges_so_a_tampered_draft_is_rejected()
    {
        // The confirm POST round-trips the draft through a hidden form field;
        // a tampered protein value must hit the same wall as a bad extraction.
        var draft = MealExtraction(Today, "dinner", proteinG: 5000, calories: null);

        var result = await EntryService().ConfirmAsync(_userId, draft, null, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        (await _db.FoodEntries.CountAsync(f => f.UserId == _userId && f.MealSlot == "dinner"))
            .Should().Be(0);
    }

    // ------------------------------------------------------------------
    // Delete + ownership
    // ------------------------------------------------------------------

    [Fact]
    public async Task Delete_enforces_ownership()
    {
        var svc = EntryService();
        await svc.ConfirmAsync(_userId, NoteExtraction("mine", null, null), null, CancellationToken.None);
        var note = await _db.UserNotes.SingleAsync(n => n.UserId == _userId && n.Note == "mine");

        var stranger = Guid.NewGuid();
        (await svc.DeleteAsync(stranger, "note", note.Id, CancellationToken.None))
            .IsSuccess.Should().BeFalse();

        (await svc.DeleteAsync(_userId, "note", note.Id, CancellationToken.None))
            .IsSuccess.Should().BeTrue();
        (await _db.UserNotes.AnyAsync(n => n.Id == note.Id)).Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // Extraction service: cap + invocation logging (stubbed Anthropic)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Extraction_persists_invocation_row_and_daily_cap_blocks()
    {
        var service = ExtractionService(cap: 2, cannedEntryJson:
            """{"entry_type":"note","note":{"note":"test note"}}""");

        (await service.ExtractAsync(_userId, "note one", CancellationToken.None))
            .IsSuccess.Should().BeTrue();
        (await service.ExtractAsync(_userId, "note two", CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        var third = await service.ExtractAsync(_userId, "note three", CancellationToken.None);
        third.IsSuccess.Should().BeFalse();
        third.Error.Should().Contain("limit");

        var rows = await _db.AgentInvocations
            .Where(a => a.UserId == _userId && a.Kind == AgentInvocationKinds.QuickLogExtraction)
            .ToListAsync();
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.TodaysRead == "");
    }

    [Fact]
    public async Task Extraction_rows_do_not_shadow_the_daily_card()
    {
        // The regression this guards: GetLatestAsync used to take the newest
        // agent_invocations row unfiltered — an extraction row with empty
        // TodaysRead would blank the user's card and force regeneration.
        _db.AgentInvocations.Add(new AgentInvocation
        {
            UserId = _userId,
            Kind = AgentInvocationKinds.DailyCard,
            InputSnapshot = JsonDocument.Parse("{}"),
            TodaysRead = "the card",
            ActionsJson = JsonDocument.Parse("[]"),
            ModelId = "test-model",
            DurationMs = 1,
        });
        await _db.SaveChangesAsync();

        var service = ExtractionService(cap: 20, cannedEntryJson:
            """{"entry_type":"note","note":{"note":"newer than the card"}}""");
        (await service.ExtractAsync(_userId, "context", CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        var stub = new StubDailyAgentService(_db, NullLogger<StubDailyAgentService>.Instance);
        var latest = await stub.GetLatestAsync(_userId, CancellationToken.None);

        latest.Should().NotBeNull();
        latest!.TodaysRead.Should().Be("the card");
    }

    // ------------------------------------------------------------------
    // Snapshot sections
    // ------------------------------------------------------------------

    [Fact]
    public async Task Snapshot_contains_food_rollups_and_manual_workout_and_omits_empty_sections()
    {
        // No data yet: the sections must be ABSENT, not empty arrays.
        var empty = await AgentInputSnapshotBuilder.BuildAsync(
            _db, _userId, DateTimeOffset.UtcNow, CancellationToken.None);
        empty.Json.Should().NotContain("latest_food_entries");
        empty.Json.Should().NotContain("daily_macro_rollups");
        empty.Json.Should().NotContain("user_notes");

        var svc = EntryService();
        await svc.ConfirmAsync(_userId,
            MealExtraction(Today, "breakfast", proteinG: 30, calories: 400),
            null, CancellationToken.None);
        await svc.ConfirmAsync(_userId,
            MealExtraction(Today, "lunch", proteinG: 50, calories: 700),
            null, CancellationToken.None);
        await svc.ConfirmAsync(_userId,
            new QuickLogExtraction(QuickLogEntryType.Workout, null,
                new WorkoutDraft("run", DateTimeOffset.UtcNow.AddHours(-3), 1800, "easy", null, null, 150),
                null, null),
            null, CancellationToken.None);

        var snapshot = await AgentInputSnapshotBuilder.BuildAsync(
            _db, _userId, DateTimeOffset.UtcNow, CancellationToken.None);
        using var doc = JsonDocument.Parse(snapshot.Json);
        var root = doc.RootElement;

        root.GetProperty("latest_food_entries").GetArrayLength().Should().Be(2);

        var rollup = root.GetProperty("daily_macro_rollups").EnumerateArray().First();
        rollup.GetProperty("protein_g").GetDecimal().Should().Be(80);
        rollup.GetProperty("meals_logged").GetInt32().Should().Be(2);

        var workouts = root.GetProperty("workouts").EnumerateArray().ToList();
        workouts.Should().Contain(w => w.GetProperty("source").GetString() == "manual");

        // Food NAMES never enter the snapshot — only totals + timing
        // (privacy draft Part 4 / Section D commitment).
        snapshot.Json.Should().NotContain("food_items");
    }

    // ------------------------------------------------------------------
    // Cascade
    // ------------------------------------------------------------------

    [Fact]
    public async Task User_deletion_cascades_all_quick_log_rows()
    {
        var svc = EntryService();
        await svc.ConfirmAsync(_userId,
            MealExtraction(Today, "snack", proteinG: 10, calories: 150), null, CancellationToken.None);
        await svc.ConfirmAsync(_userId,
            new QuickLogExtraction(QuickLogEntryType.Workout, null,
                new WorkoutDraft("yoga", DateTimeOffset.UtcNow.AddHours(-1), 1800, null, null, null, null),
                null, null),
            null, CancellationToken.None);
        await svc.ConfirmAsync(_userId, NoteExtraction("context", null, null), null, CancellationToken.None);

        await _db.Users.Where(u => u.Id == _userId).ExecuteDeleteAsync();

        (await _db.FoodEntries.CountAsync(f => f.UserId == _userId)).Should().Be(0);
        (await _db.HealthKitWorkouts.CountAsync(w => w.UserId == _userId)).Should().Be(0);
        (await _db.UserNotes.CountAsync(n => n.UserId == _userId)).Should().Be(0);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static QuickLogExtraction MealExtraction(
        DateOnly date, string slot, decimal? proteinG, decimal? calories, string? foodName = null)
        => new(
            QuickLogEntryType.Meal,
            new MealDraft(slot, date, calories, proteinG, null, null, null, null, null,
                foodName is null
                    ? Array.Empty<FoodItemDraft>()
                    : new[] { new FoodItemDraft(foodName, null) }),
            null, null, null);

    private static QuickLogExtraction NoteExtraction(string note, string? category, DateOnly? until)
        => new(QuickLogEntryType.Note, null, null, new NoteDraft(category, note, until), null);

    private IQuickLogExtractionService ExtractionService(int cap, string cannedEntryJson)
    {
        var toolInput = JsonSerializer.Serialize(new { entry_json = cannedEntryJson });
        var body = $$$"""
            {"id":"msg_1","model":"test-model","stop_reason":"tool_use",
             "content":[{"type":"tool_use","id":"tu_1","name":"submit_quick_log_entry",
                         "input":{{{toolInput}}}}],
             "usage":{"input_tokens":10,"output_tokens":20}}
            """;
        var http = new HttpClient(new CannedResponseHandler(body))
        {
            BaseAddress = new Uri("https://anthropic.test"),
        };

        return new QuickLogExtractionService(
            Options.Create(new QuickLogOptions { Enabled = true, DailyCap = cap }),
            Options.Create(new AnthropicOptions { Enabled = true, ApiKey = "test", ModelId = "test-model" }),
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<QuickLogExtractionService>.Instance,
            new AnthropicMessagesClient(http));
    }

    private sealed class CannedResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
