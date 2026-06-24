using System.Text.Json;

using SomaCore.Domain.Agent;
using SomaCore.Domain.Common;

namespace SomaCore.Infrastructure.Agent;

/// <summary>
/// Mechanical refusal guard for live agent responses per ADR 0012 and
/// <c>docs/agent-bounds.md</c>. The model is told what IN BOUNDS means;
/// this code enforces it. Any drift — wrong shape, missing fields,
/// out-of-bounds category, unknown source — rejects the whole response.
/// </summary>
public sealed record AgentCardPayload(string TodaysRead, IReadOnlyList<AgentAction> Actions);

internal static class AgentResponseValidator
{
    public static Result<AgentCardPayload> Validate(AnthropicMessageResponse response)
    {
        if (response.Content is null || response.Content.Count == 0)
        {
            return Result<AgentCardPayload>.Failure("Model returned no content blocks.");
        }

        var toolUse = response.Content.FirstOrDefault(c => c.Type == "tool_use");
        if (toolUse is null)
        {
            return Result<AgentCardPayload>.Failure(
                "Model did not emit a tool_use block — refusal-guard reject.");
        }

        if (toolUse.Input is not JsonElement input
            || input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("card_json", out var cardJsonElement)
            || cardJsonElement.ValueKind != JsonValueKind.String)
        {
            return Result<AgentCardPayload>.Failure("Tool input missing or non-string 'card_json'.");
        }

        var cardJson = cardJsonElement.GetString();
        if (string.IsNullOrWhiteSpace(cardJson))
        {
            return Result<AgentCardPayload>.Failure("card_json is empty.");
        }

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(cardJson);
        }
        catch (JsonException ex)
        {
            return Result<AgentCardPayload>.Failure($"card_json is not valid JSON: {ex.Message}");
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Result<AgentCardPayload>.Failure("card_json root is not an object.");
            }

            if (!root.TryGetProperty("todays_read", out var todaysReadEl)
                || todaysReadEl.ValueKind != JsonValueKind.String)
            {
                return Result<AgentCardPayload>.Failure("Missing or non-string todays_read.");
            }
            var todaysRead = todaysReadEl.GetString()!.Trim();
            if (todaysRead.Length == 0)
            {
                return Result<AgentCardPayload>.Failure("todays_read is empty.");
            }

            if (!root.TryGetProperty("actions", out var actionsEl)
                || actionsEl.ValueKind != JsonValueKind.Array)
            {
                return Result<AgentCardPayload>.Failure("Missing or non-array actions.");
            }

            var actions = new List<AgentAction>();
            int index = 0;
            foreach (var actionEl in actionsEl.EnumerateArray())
            {
                index++;
                if (actionEl.ValueKind != JsonValueKind.Object)
                {
                    return Result<AgentCardPayload>.Failure($"actions[{index - 1}] is not an object.");
                }

                if (!TryGetNonEmptyString(actionEl, "title", out var title))
                {
                    return Result<AgentCardPayload>.Failure($"actions[{index - 1}].title missing or empty.");
                }
                if (!TryGetNonEmptyString(actionEl, "why", out var why))
                {
                    return Result<AgentCardPayload>.Failure($"actions[{index - 1}].why missing or empty.");
                }
                if (!TryGetNonEmptyString(actionEl, "category", out var category))
                {
                    return Result<AgentCardPayload>.Failure($"actions[{index - 1}].category missing or empty.");
                }
                if (!AgentActionCategory.All.Contains(category))
                {
                    return Result<AgentCardPayload>.Failure(
                        $"actions[{index - 1}].category '{category}' is not in the IN BOUNDS list.");
                }

                if (!actionEl.TryGetProperty("rank", out var rankEl)
                    || rankEl.ValueKind != JsonValueKind.Number
                    || !rankEl.TryGetInt32(out var rank))
                {
                    return Result<AgentCardPayload>.Failure($"actions[{index - 1}].rank missing or non-integer.");
                }

                if (!TryGetNonEmptyString(actionEl, "source", out var source))
                {
                    return Result<AgentCardPayload>.Failure($"actions[{index - 1}].source missing or empty.");
                }
                if (source != AgentActionSource.ProtocolBased
                    && source != AgentActionSource.UserDataInformed)
                {
                    // We don't yet ingest lab documents, so lab-sourced
                    // citations can't be honored. Reject for now and revisit
                    // when lab ingestion lands.
                    return Result<AgentCardPayload>.Failure(
                        $"actions[{index - 1}].source '{source}' is not a recognized provenance value.");
                }

                actions.Add(new AgentAction(title, why, category, rank, source));
            }

            if (actions.Count != 3)
            {
                return Result<AgentCardPayload>.Failure(
                    $"Expected exactly 3 actions, got {actions.Count}.");
            }

            return Result<AgentCardPayload>.Success(new AgentCardPayload(todaysRead, actions));
        }
    }

    private static bool TryGetNonEmptyString(JsonElement parent, string property, out string value)
    {
        if (parent.TryGetProperty(property, out var el)
            && el.ValueKind == JsonValueKind.String
            && el.GetString() is { Length: > 0 } s
            && s.Trim().Length > 0)
        {
            value = s.Trim();
            return true;
        }
        value = string.Empty;
        return false;
    }
}
