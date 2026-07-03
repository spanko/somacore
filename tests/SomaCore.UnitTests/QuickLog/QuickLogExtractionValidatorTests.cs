using System.Text.Json;

using FluentAssertions;

using SomaCore.Infrastructure.Agent;
using SomaCore.Infrastructure.QuickLog;

namespace SomaCore.UnitTests.QuickLog;

/// <summary>
/// The mechanical guard for quick-log extraction (session-quick-log.md).
/// Same testing posture as the webhook signature validator: the model is
/// untrusted input; every malformed or out-of-range shape must be rejected
/// before it can reach a confirm card.
/// </summary>
public class QuickLogExtractionValidatorTests
{
    private static readonly DateOnly Today = new(2026, 7, 2);

    // ------------------------------------------------------------------
    // Happy paths
    // ------------------------------------------------------------------

    [Fact]
    public void Meal_with_slot_macros_and_items_validates()
    {
        var result = Validate("""
            {"entry_type":"meal","meal":{"meal_slot":"lunch","meal_date":"2026-07-02",
             "protein_g":50,"calories":650,
             "food_items":[{"name":"chicken bowl"}]}}
            """);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.EntryType.Should().Be("meal");
        result.Value.Meal!.ProteinG.Should().Be(50);
        result.Value.Meal.FoodItems.Should().ContainSingle(i => i.Name == "chicken bowl");
    }

    [Fact]
    public void Meal_without_food_items_normalizes_to_empty_list()
    {
        var result = Validate("""
            {"entry_type":"meal","meal":{"meal_slot":"snack","meal_date":"2026-07-02","protein_g":20}}
            """);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Meal!.FoodItems.Should().BeEmpty();
    }

    [Fact]
    public void Workout_with_duration_and_intensity_validates()
    {
        var result = Validate("""
            {"entry_type":"workout","workout":{"workout_type":"ride",
             "started_at":"2026-07-02T06:30:00-06:00","elapsed_seconds":2700,"intensity":"hard"}}
            """);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Workout!.ElapsedSeconds.Should().Be(2700);
    }

    [Fact]
    public void Note_with_category_and_expiry_validates()
    {
        var result = Validate("""
            {"entry_type":"note","note":{"category":"schedule",
             "note":"traveling until Friday","active_until":"2026-07-04"}}
            """);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Note!.ActiveUntil.Should().Be(new DateOnly(2026, 7, 4));
    }

    [Fact]
    public void Unclassified_with_message_validates()
    {
        var result = Validate("""
            {"entry_type":"unclassified","message":"Was that a meal or a workout?"}
            """);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Message.Should().NotBeNullOrEmpty();
    }

    // ------------------------------------------------------------------
    // Envelope failures
    // ------------------------------------------------------------------

    [Fact]
    public void Missing_tool_block_fails()
    {
        var response = new AnthropicMessageResponse(
            Id: "msg_1", Model: "test-model", StopReason: "end_turn",
            Content: new[] { new AnthropicContentBlock(Type: "text", Text: "hello") },
            Usage: null);

        var result = QuickLogExtractionValidator.Validate(response, Today);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("no submit_quick_log_entry");
    }

    [Fact]
    public void Malformed_entry_json_fails()
    {
        var result = Validate("{not json");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("failed to parse");
    }

    [Fact]
    public void Unknown_entry_type_fails()
    {
        Validate("""{"entry_type":"selfie"}""").IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Entry_type_without_matching_payload_fails()
    {
        Validate("""{"entry_type":"meal"}""").IsSuccess.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // Range / enum failures
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("brunch")]
    [InlineData("")]
    public void Meal_with_unknown_slot_fails(string slot)
    {
        var result = Validate($$$"""
            {"entry_type":"meal","meal":{"meal_slot":"{{{slot}}}","meal_date":"2026-07-02"}}
            """);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Meal_older_than_seven_days_fails()
    {
        var result = Validate("""
            {"entry_type":"meal","meal":{"meal_slot":"lunch","meal_date":"2026-06-01"}}
            """);
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("last 7 days");
    }

    [Fact]
    public void Meal_with_implausible_protein_fails()
    {
        var result = Validate("""
            {"entry_type":"meal","meal":{"meal_slot":"lunch","meal_date":"2026-07-02","protein_g":5000}}
            """);
        result.IsSuccess.Should().BeFalse();
    }

    [Theory]
    [InlineData(30)]      // under a minute
    [InlineData(100_000)] // over a day
    public void Workout_with_implausible_duration_fails(int elapsedSeconds)
    {
        var result = Validate($$$"""
            {"entry_type":"workout","workout":{"workout_type":"run",
             "started_at":"2026-07-02T06:30:00-06:00","elapsed_seconds":{{{elapsedSeconds}}}}}
            """);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Note_over_2000_chars_fails()
    {
        var longNote = new string('x', 2001);
        var result = Validate($$$"""
            {"entry_type":"note","note":{"note":"{{{longNote}}}"}}
            """);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Note_with_unknown_category_fails()
    {
        var result = Validate("""
            {"entry_type":"note","note":{"category":"mood","note":"feeling great"}}
            """);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Note_expiring_in_the_past_fails()
    {
        var result = Validate("""
            {"entry_type":"note","note":{"note":"stale","active_until":"2026-06-01"}}
            """);
        result.IsSuccess.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // Helper: wrap entry_json in a canned Anthropic tool-use response.
    // ------------------------------------------------------------------

    private static SomaCore.Domain.Common.Result<QuickLogExtraction> Validate(string entryJson)
    {
        var input = JsonSerializer.SerializeToElement(new { entry_json = entryJson });
        var response = new AnthropicMessageResponse(
            Id: "msg_1",
            Model: "test-model",
            StopReason: "tool_use",
            Content: new[]
            {
                new AnthropicContentBlock(
                    Type: "tool_use",
                    Id: "tu_1",
                    Name: QuickLogExtractionValidator.ToolName,
                    Input: input),
            },
            Usage: new AnthropicUsage(10, 20, null, null));

        return QuickLogExtractionValidator.Validate(response, Today);
    }
}
