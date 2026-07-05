using System.Text.Json;

using FluentAssertions;

using SomaCore.Infrastructure.Agent;
using SomaCore.Infrastructure.Coach;

namespace SomaCore.UnitTests.Coach;

public class CoachReplyValidatorTests
{
    [Fact]
    public void Valid_reply_passes()
    {
        var result = CoachReplyValidator.Validate(Response(
            """{"reply":"Zone 2 today because yesterday stacked strain.","refusal":false}"""));

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Reply.Should().Contain("Zone 2");
        result.Value.Refusal.Should().BeFalse();
    }

    [Fact]
    public void Refusal_flag_carries_through()
    {
        var result = CoachReplyValidator.Validate(Response(
            """{"reply":"That's a clinician question — I can talk training load.","refusal":true}"""));

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Refusal.Should().BeTrue();
    }

    [Fact]
    public void Missing_tool_call_fails()
    {
        var response = new AnthropicMessageResponse(
            "msg_1", "test-model", "end_turn",
            new[] { new AnthropicContentBlock(Type: "text", Text: "plain text escape attempt") },
            null);

        CoachReplyValidator.Validate(response).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Empty_reply_fails()
    {
        CoachReplyValidator.Validate(Response("""{"reply":"  ","refusal":false}"""))
            .IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Missing_refusal_defaults_false()
    {
        var result = CoachReplyValidator.Validate(Response("""{"reply":"ok"}"""));
        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Refusal.Should().BeFalse();
    }

    private static AnthropicMessageResponse Response(string inputJson)
        => new(
            "msg_1", "test-model", "tool_use",
            new[]
            {
                new AnthropicContentBlock(
                    Type: "tool_use",
                    Id: "tu_1",
                    Name: CoachChatService.ToolName,
                    Input: JsonDocument.Parse(inputJson).RootElement.Clone()),
            },
            new AnthropicUsage(10, 20, null, null));
}
