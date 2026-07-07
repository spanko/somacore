using System.Net;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using SomaCore.Domain.Agent;
using SomaCore.Domain.Users;
using SomaCore.Infrastructure.Agent;
using SomaCore.Infrastructure.Labs;
using SomaCore.Infrastructure.Persistence;

using Testcontainers.PostgreSql;

namespace SomaCore.IntegrationTests;

/// <summary>
/// Lab upload flow against real Postgres with a stubbed Anthropic handler:
/// parse → rows + computed flags, the confirm gate against the snapshot,
/// re-upload replacement, taxonomy failure, ownership, cascade. Real
/// Function PDF fixtures are the flag-flip acceptance gate, not here.
/// </summary>
public class LabsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("somacore_labs")
        .WithUsername("somacore")
        .WithPassword("devonly")
        .Build();

    private ServiceProvider _services = null!;
    private SomaCoreDbContext _db = null!;
    private Guid _userId;

    // %PDF- magic bytes + filler; the service checks the header, not full validity.
    private static byte[] FakePdf() => Encoding.ASCII.GetBytes("%PDF-1.4 test panel content");

    private static readonly string ValidPanelJson = """
        {"collected_at":"2026-03-14","biomarkers":[
          {"biomarker_name":"vitamin_d_25_hydroxy","display_name":"Vitamin D, 25-OH","category":"nutrients",
           "numeric_value":22,"unit":"ng/mL","reference_low":30,"reference_high":100},
          {"biomarker_name":"ferritin","display_name":"Ferritin","category":"nutrients",
           "numeric_value":95,"unit":"ng/mL","reference_low":30,"reference_high":300}
        ]}
        """;

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
            Email = "labs-test@example.com",
            DisplayName = "Labs Test",
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

    [Fact]
    public async Task Upload_parses_rows_with_computed_flags_and_logs_invocation()
    {
        var service = Service(ValidPanelJson);

        var result = await service.UploadAsync(_userId, "function-panel.pdf", FakePdf(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.ParseStatus.Should().Be("parsed");
        result.Value.CollectedAt.Should().Be(new DateOnly(2026, 3, 14));

        var markers = await _db.LabBiomarkers.Where(b => b.LabUploadId == result.Value.Id).ToListAsync();
        markers.Should().HaveCount(2);
        markers.Single(m => m.BiomarkerName == "vitamin_d_25_hydroxy").Flagged.Should().Be("low");
        markers.Single(m => m.BiomarkerName == "ferritin").Flagged.Should().Be("in_range");

        (await _db.AgentInvocations.CountAsync(
            a => a.UserId == _userId && a.Kind == AgentInvocationKinds.LabExtraction))
            .Should().Be(1);
    }

    [Fact]
    public async Task Snapshot_carries_biomarkers_only_after_confirm()
    {
        var service = Service(ValidPanelJson);
        var upload = await service.UploadAsync(_userId, "panel.pdf", FakePdf(), CancellationToken.None);

        // Parsed but NOT confirmed: invisible to the coach.
        var before = await AgentInputSnapshotBuilder.BuildAsync(
            _db, _userId, DateTimeOffset.UtcNow, CancellationToken.None);
        before.Json.Should().NotContain("latest_biomarkers");

        (await service.ConfirmAsync(_userId, upload.Value!.Id, CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        var after = await AgentInputSnapshotBuilder.BuildAsync(
            _db, _userId, DateTimeOffset.UtcNow, CancellationToken.None);
        after.Json.Should().Contain("latest_biomarkers");
        after.Json.Should().Contain("vitamin_d_25_hydroxy");
        after.Json.Should().Contain(upload.Value.Id.ToString());
    }

    [Fact]
    public async Task Reuploading_the_same_collection_date_replaces_the_panel()
    {
        var service = Service(ValidPanelJson);
        var first = await service.UploadAsync(_userId, "panel-v1.pdf", FakePdf(), CancellationToken.None);
        var second = await service.UploadAsync(_userId, "panel-v2.pdf", FakePdf(), CancellationToken.None);

        second.IsSuccess.Should().BeTrue(second.Error);
        (await _db.LabUploads.CountAsync(u => u.UserId == _userId)).Should().Be(1);
        (await _db.LabUploads.AnyAsync(u => u.Id == first.Value!.Id)).Should().BeFalse();
        // Replacement resets the confirmation gate — new extraction, new review.
        (await _db.LabUploads.SingleAsync(u => u.Id == second.Value!.Id)).ParseStatus.Should().Be("parsed");
    }

    [Fact]
    public async Task Hallucinated_biomarker_fails_the_upload_and_persists_nothing_readable()
    {
        var service = Service("""
            {"collected_at":"2026-03-14","biomarkers":[
              {"biomarker_name":"midichlorian_count","display_name":"Midichlorians","category":"blood","numeric_value":9000}]}
            """);

        var result = await service.UploadAsync(_userId, "bad-panel.pdf", FakePdf(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        var upload = await _db.LabUploads.SingleAsync(u => u.UserId == _userId);
        upload.ParseStatus.Should().Be("failed");
        upload.ParseError.Should().Contain("midichlorian_count");
        (await _db.LabBiomarkers.CountAsync(b => b.UserId == _userId)).Should().Be(0);

        // A failed upload can't be confirmed.
        (await service.ConfirmAsync(_userId, upload.Id, CancellationToken.None))
            .IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Non_pdf_bytes_are_rejected_before_any_network_call()
    {
        var service = Service(ValidPanelJson);
        var result = await service.UploadAsync(
            _userId, "sneaky.pdf", Encoding.ASCII.GetBytes("not a pdf at all"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        (await _db.LabUploads.CountAsync(u => u.UserId == _userId)).Should().Be(0);
    }

    [Fact]
    public async Task Confirm_and_delete_enforce_ownership()
    {
        var service = Service(ValidPanelJson);
        var upload = await service.UploadAsync(_userId, "panel.pdf", FakePdf(), CancellationToken.None);
        var stranger = Guid.NewGuid();

        (await service.ConfirmAsync(stranger, upload.Value!.Id, CancellationToken.None))
            .IsSuccess.Should().BeFalse();
        (await service.DeleteAsync(stranger, upload.Value.Id, CancellationToken.None))
            .IsSuccess.Should().BeFalse();

        (await service.DeleteAsync(_userId, upload.Value.Id, CancellationToken.None))
            .IsSuccess.Should().BeTrue();
        (await _db.LabBiomarkers.CountAsync(b => b.UserId == _userId)).Should().Be(0);
    }

    [Fact]
    public async Task User_deletion_cascades_uploads_and_biomarkers()
    {
        var service = Service(ValidPanelJson);
        await service.UploadAsync(_userId, "panel.pdf", FakePdf(), CancellationToken.None);

        await _db.Users.Where(u => u.Id == _userId).ExecuteDeleteAsync();

        (await _db.LabUploads.CountAsync(u => u.UserId == _userId)).Should().Be(0);
        (await _db.LabBiomarkers.CountAsync(b => b.UserId == _userId)).Should().Be(0);
    }

    // ------------------------------------------------------------------

    private ILabUploadService Service(string cannedPanelJson)
    {
        var toolInput = JsonSerializer.Serialize(new { panel_json = cannedPanelJson });
        var body = $$$"""
            {"id":"msg_1","model":"test-model","stop_reason":"tool_use",
             "content":[{"type":"tool_use","id":"tu_1","name":"submit_lab_panel",
                         "input":{{{toolInput}}}}],
             "usage":{"input_tokens":10,"output_tokens":20}}
            """;
        var http = new HttpClient(new CannedResponseHandler(body))
        {
            BaseAddress = new Uri("https://anthropic.test"),
        };

        return new LabUploadService(
            _db,
            Options.Create(new LabsOptions { Enabled = true }),
            Options.Create(new AnthropicOptions { Enabled = true, ApiKey = "test", ModelId = "test-model" }),
            NullLogger<LabUploadService>.Instance,
            new AnthropicMessagesClient(http));
    }

    private sealed class CannedResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
