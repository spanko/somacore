using System.Text.Json;

using FluentAssertions;

using SomaCore.Domain.Agent;
using SomaCore.Domain.Common;
using SomaCore.Infrastructure.Agent;

namespace SomaCore.UnitTests.Agent;

/// <summary>
/// The lab-provenance acceptance rules (session-function-health §1.5):
/// supplements_from_labs is accepted ONLY as user_uploaded_lab + a
/// lab_upload_id that references one of the user's real confirmed uploads.
/// Everything else about the combination rejects the whole card.
/// </summary>
public class AgentResponseValidatorLabTests
{
    private static readonly Guid ConfirmedUpload = Guid.Parse("0197f000-0000-7000-8000-000000000001");

    [Fact]
    public void Lab_action_with_confirmed_upload_id_is_accepted()
    {
        var result = Validate(LabAction(ConfirmedUpload), new[] { ConfirmedUpload });

        result.IsSuccess.Should().BeTrue(result.Error);
        var action = result.Value!.Actions.Single(a => a.Category == AgentActionCategory.SupplementsFromLabs);
        action.Source.Should().Be(AgentActionSource.UserUploadedLab);
        action.LabUploadId.Should().Be(ConfirmedUpload);
    }

    [Fact]
    public void Lab_action_citing_an_unknown_upload_id_rejects_the_card()
    {
        var hallucinated = Guid.NewGuid();
        var result = Validate(LabAction(hallucinated), new[] { ConfirmedUpload });

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("confirmed uploads");
    }

    [Fact]
    public void Lab_action_with_no_confirmed_uploads_rejects_the_card()
    {
        var result = Validate(LabAction(ConfirmedUpload), Array.Empty<Guid>());
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Supplements_category_without_lab_source_rejects()
    {
        var card = Card($$"""
            {"title":"Take vitamin D","why":"You're low.","category":"supplements_from_labs","rank":1,"source":"protocol_based"}
            """);
        var result = Validate(card, new[] { ConfirmedUpload });

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("requires source");
    }

    [Fact]
    public void Lab_source_without_upload_id_rejects()
    {
        var card = Card($$"""
            {"title":"Take vitamin D","why":"Panel says low.","category":"supplements_from_labs","rank":1,"source":"user_uploaded_lab"}
            """);
        Validate(card, new[] { ConfirmedUpload }).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Upload_id_on_a_non_lab_source_rejects()
    {
        var card = Card($$"""
            {"title":"Zone 2 today","why":"Recovery is low.","category":"training_intensity","rank":1,"source":"protocol_based","lab_upload_id":"{{ConfirmedUpload}}"}
            """);
        Validate(card, new[] { ConfirmedUpload }).IsSuccess.Should().BeFalse();
    }

    // ------------------------------------------------------------------

    private static string LabAction(Guid uploadId) => Card($$"""
        {"title":"Take 2000 IU vitamin D with your first meal","why":"Vitamin D is 22 ng/mL against a 30-100 reference on your panel from 2026-03-14.","category":"supplements_from_labs","rank":1,"source":"user_uploaded_lab","lab_upload_id":"{{uploadId}}"}
        """);

    private static string Card(string firstAction) => $$"""
        {"todays_read":"Solid recovery — push today.","actions":[
          {{firstAction}},
          {"title":"Zone 2 for 45","why":"Consolidate the trend.","category":"training_intensity","rank":2,"source":"user_data_informed"},
          {"title":"Caffeine off at 1pm","why":"Protect tonight.","category":"caffeine_timing","rank":3,"source":"protocol_based"}
        ]}
        """;

    private static Result<AgentCardPayload> Validate(string cardJson, IReadOnlyCollection<Guid> confirmedIds)
    {
        var input = JsonSerializer.SerializeToElement(new { card_json = cardJson });
        var response = new AnthropicMessageResponse(
            "msg_1", "test-model", "tool_use",
            new[]
            {
                new AnthropicContentBlock(Type: "tool_use", Id: "tu_1", Name: "submit_daily_card", Input: input),
            },
            new AnthropicUsage(10, 20, null, null));
        return AgentResponseValidator.Validate(response, confirmedIds);
    }
}
