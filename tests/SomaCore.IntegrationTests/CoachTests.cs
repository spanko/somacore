using System.Net;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using SomaCore.Domain.Agent;
using SomaCore.Domain.CoachThreads;
using SomaCore.Domain.Users;
using SomaCore.Infrastructure.Agent;
using SomaCore.Infrastructure.Coach;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.QuickLog;

using Testcontainers.PostgreSql;

namespace SomaCore.IntegrationTests;

/// <summary>
/// Coach surfaces (/me/coach): document upload + extraction, threads,
/// conversation turns against a stubbed Anthropic handler, caps,
/// ownership, and cascade. Model-quality concerns (does the coach answer
/// well) are validated live, not here — these tests pin the machinery.
/// </summary>
public class CoachTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("somacore_coach")
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
            Email = "coach-test@example.com",
            DisplayName = "Coach Test",
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

    // ------------------------------------------------------------------
    // Documents
    // ------------------------------------------------------------------

    [Fact]
    public async Task Text_document_upload_extracts_inline_and_persists()
    {
        var service = DocumentService();
        var csv = Encoding.UTF8.GetBytes("date,protein_g\n2026-07-01,140\n2026-07-02,155\n");

        var result = await service.UploadAsync(_userId, "protein-log.csv", "text/csv", csv, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Error);
        var doc = await _db.UserDocuments.SingleAsync(d => d.UserId == _userId);
        doc.ParseStatus.Should().Be("parsed");
        doc.ExtractedText.Should().Contain("155");
        doc.Summary.Should().Contain("protein-log.csv");
    }

    [Fact]
    public async Task Unsupported_format_is_rejected_and_nothing_persists()
    {
        var service = DocumentService();

        var result = await service.UploadAsync(
            _userId, "photo.jpg", "image/jpeg", new byte[] { 1, 2, 3 }, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        (await _db.UserDocuments.CountAsync(d => d.UserId == _userId && d.FileName == "photo.jpg"))
            .Should().Be(0);
    }

    [Fact]
    public async Task Parsed_documents_appear_as_summaries_in_the_snapshot()
    {
        // Documents must be visible to the coach EVERYWHERE (daily card +
        // general conversations) as name+summary — not only inside a thread
        // anchored to them. Full text stays out of the snapshot.
        var empty = await AgentInputSnapshotBuilder.BuildAsync(
            _db, _userId, DateTimeOffset.UtcNow, CancellationToken.None);
        empty.Json.Should().NotContain("documents_on_file");

        var service = DocumentService();
        await service.UploadAsync(_userId, "training-plan.txt", "text/plain",
            Encoding.UTF8.GetBytes("Week 1: 5.5 hours all Z2. Week 3 peak: threshold intervals."),
            CancellationToken.None);

        var snapshot = await AgentInputSnapshotBuilder.BuildAsync(
            _db, _userId, DateTimeOffset.UtcNow, CancellationToken.None);

        snapshot.Json.Should().Contain("documents_on_file");
        snapshot.Json.Should().Contain("training-plan.txt");
        // Summary only — the document's full text must NOT ride in the snapshot.
        snapshot.Json.Should().NotContain("threshold intervals");
    }

    [Fact]
    public async Task Document_delete_enforces_ownership()
    {
        var service = DocumentService();
        await service.UploadAsync(_userId, "mine.txt", "text/plain",
            Encoding.UTF8.GetBytes("my notes"), CancellationToken.None);
        var doc = await _db.UserDocuments.SingleAsync(d => d.FileName == "mine.txt");

        (await service.DeleteAsync(Guid.NewGuid(), doc.Id, CancellationToken.None))
            .IsSuccess.Should().BeFalse();
        (await service.DeleteAsync(_userId, doc.Id, CancellationToken.None))
            .IsSuccess.Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // Threads + conversation
    // ------------------------------------------------------------------

    [Fact]
    public async Task Thread_about_a_document_sends_and_persists_both_turns()
    {
        var docService = DocumentService();
        var upload = await docService.UploadAsync(_userId, "plan.txt", "text/plain",
            Encoding.UTF8.GetBytes("Week 3: five sessions, two hard."), CancellationToken.None);

        var chat = ChatService(cannedReply: "Two hard sessions is the right dose for week 3.");
        var thread = await chat.StartThreadAsync(
            _userId, CoachThreadSubjectType.Document, upload.Value!.Id, CancellationToken.None);
        thread.IsSuccess.Should().BeTrue(thread.Error);
        thread.Value!.Title.Should().Be("plan.txt");

        var reply = await chat.SendAsync(_userId, thread.Value.Id, "Is week 3 too much?", CancellationToken.None);

        reply.IsSuccess.Should().BeTrue(reply.Error);
        reply.Value!.Role.Should().Be("coach");
        reply.Value.Content.Should().Contain("right dose");

        var messages = await _db.CoachMessages.Where(m => m.ThreadId == thread.Value.Id).ToListAsync();
        messages.Should().HaveCount(2);
        messages.Should().ContainSingle(m => m.Role == "user" && m.Content == "Is week 3 too much?");

        // Every coach turn carries its invocation for cost/audit.
        var invocation = await _db.AgentInvocations
            .SingleAsync(a => a.Id == reply.Value.InvocationId);
        invocation.Kind.Should().Be(AgentInvocationKinds.Conversation);
    }

    [Fact]
    public async Task Starting_a_thread_on_the_same_subject_reuses_it()
    {
        var chat = ChatService("ok");
        var first = await chat.StartThreadAsync(_userId, CoachThreadSubjectType.General, null, CancellationToken.None);
        var second = await chat.StartThreadAsync(_userId, CoachThreadSubjectType.General, null, CancellationToken.None);

        second.Value!.Id.Should().Be(first.Value!.Id);
    }

    [Fact]
    public async Task Turn_cap_ends_the_thread()
    {
        var chat = ChatService("noted", maxTurns: 2);
        var thread = await chat.StartThreadAsync(_userId, CoachThreadSubjectType.General, null, CancellationToken.None);

        (await chat.SendAsync(_userId, thread.Value!.Id, "one", CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await chat.SendAsync(_userId, thread.Value.Id, "two", CancellationToken.None)).IsSuccess.Should().BeTrue();

        var third = await chat.SendAsync(_userId, thread.Value.Id, "three", CancellationToken.None);
        third.IsSuccess.Should().BeFalse();
        // The remedy is a NEW thread, not waiting — the per-thread cap never
        // resets, and the copy must say so (Tai hit the old "tomorrow"
        // wording and read it as a daily lockout).
        third.Error.Should().Contain("start a new one");
    }

    [Fact]
    public async Task Refusal_flag_persists_on_the_message()
    {
        var chat = ChatService("That's a clinician question.", refusal: true);
        var thread = await chat.StartThreadAsync(_userId, CoachThreadSubjectType.General, null, CancellationToken.None);

        var reply = await chat.SendAsync(_userId, thread.Value!.Id, "Do I have a thyroid problem?", CancellationToken.None);

        reply.IsSuccess.Should().BeTrue(reply.Error);
        reply.Value!.Refusal.Should().BeTrue();
    }

    [Fact]
    public async Task Thread_access_enforces_ownership()
    {
        var chat = ChatService("ok");
        var thread = await chat.StartThreadAsync(_userId, CoachThreadSubjectType.General, null, CancellationToken.None);

        var stranger = await chat.SendAsync(Guid.NewGuid(), thread.Value!.Id, "hi", CancellationToken.None);
        stranger.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task User_deletion_cascades_documents_threads_and_messages()
    {
        var docService = DocumentService();
        await docService.UploadAsync(_userId, "gone.txt", "text/plain",
            Encoding.UTF8.GetBytes("x"), CancellationToken.None);
        var chat = ChatService("ok");
        var thread = await chat.StartThreadAsync(_userId, CoachThreadSubjectType.General, null, CancellationToken.None);
        await chat.SendAsync(_userId, thread.Value!.Id, "hello", CancellationToken.None);

        await _db.Users.Where(u => u.Id == _userId).ExecuteDeleteAsync();

        (await _db.UserDocuments.CountAsync(d => d.UserId == _userId)).Should().Be(0);
        (await _db.CoachThreads.CountAsync(t => t.UserId == _userId)).Should().Be(0);
        (await _db.CoachMessages.CountAsync(m => m.UserId == _userId)).Should().Be(0);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private IUserDocumentService DocumentService()
        => new UserDocumentService(
            _db,
            Options.Create(new CoachChatOptions { Enabled = true }),
            Options.Create(new AnthropicOptions { Enabled = true, ApiKey = "test", ModelId = "test-model" }),
            NullLogger<UserDocumentService>.Instance,
            client: null); // text formats never hit Anthropic

    private ICoachChatService ChatService(string cannedReply, bool refusal = false, int maxTurns = 10)
    {
        var toolInput = JsonSerializer.Serialize(new
        {
            reply = cannedReply,
            refusal,
        });
        var body = $$$"""
            {"id":"msg_1","model":"test-model","stop_reason":"tool_use",
             "content":[{"type":"tool_use","id":"tu_1","name":"submit_coach_reply",
                         "input":{{{toolInput}}}}],
             "usage":{"input_tokens":10,"output_tokens":20}}
            """;
        var http = new HttpClient(new CannedResponseHandler(body))
        {
            BaseAddress = new Uri("https://anthropic.test"),
        };

        return new CoachChatService(
            _db,
            Options.Create(new CoachChatOptions { Enabled = true, MaxUserTurnsPerThread = maxTurns }),
            Options.Create(new AnthropicOptions { Enabled = true, ApiKey = "test", ModelId = "test-model" }),
            NullLogger<CoachChatService>.Instance,
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
