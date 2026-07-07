using System.Text.Json;

using FluentAssertions;

using SomaCore.Domain.Common;
using SomaCore.Infrastructure.Agent;
using SomaCore.Infrastructure.Labs;

namespace SomaCore.UnitTests.Labs;

/// <summary>
/// The taxonomy checksum + shape guard for lab extraction. A hallucinated
/// biomarker fails the WHOLE panel — the fixture-set acceptance testing
/// with real Function PDFs happens at flag-flip time; these tests pin the
/// mechanical layer.
/// </summary>
public class LabPanelResponseValidatorTests
{
    private static readonly DateOnly Today = new(2026, 7, 7);

    [Fact]
    public void Valid_panel_passes_and_flags_compute_server_side()
    {
        var result = Validate("""
            {"collected_at":"2026-03-14","biomarkers":[
              {"biomarker_name":"vitamin_d_25_hydroxy","display_name":"Vitamin D, 25-OH","category":"nutrients",
               "numeric_value":22,"unit":"ng/mL","reference_low":30,"reference_high":100},
              {"biomarker_name":"ferritin","display_name":"Ferritin","category":"nutrients",
               "numeric_value":95,"unit":"ng/mL","reference_low":30,"reference_high":300},
              {"biomarker_name":"tpo_antibodies","display_name":"TPO Ab","category":"thyroid",
               "string_value":"Negative","reference_string":"Negative"}
            ]}
            """);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.CollectedAt.Should().Be(new DateOnly(2026, 3, 14));
        result.Value.Biomarkers.Should().HaveCount(3);

        LabPanelResponseValidator.ComputeFlag(result.Value.Biomarkers[0]).Should().Be("low");
        LabPanelResponseValidator.ComputeFlag(result.Value.Biomarkers[1]).Should().Be("in_range");
        LabPanelResponseValidator.ComputeFlag(result.Value.Biomarkers[2]).Should().Be("unknown");
    }

    [Fact]
    public void Unknown_biomarker_name_fails_the_whole_panel()
    {
        var result = Validate("""
            {"collected_at":"2026-03-14","biomarkers":[
              {"biomarker_name":"ferritin","display_name":"Ferritin","category":"nutrients","numeric_value":95},
              {"biomarker_name":"midichlorian_count","display_name":"Midichlorians","category":"blood","numeric_value":9000}
            ]}
            """);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("midichlorian_count");
        result.Error.Should().Contain("held for review");
    }

    [Fact]
    public void Future_collection_date_fails()
    {
        Validate("""
            {"collected_at":"2027-01-01","biomarkers":[
              {"biomarker_name":"ferritin","display_name":"Ferritin","category":"nutrients","numeric_value":95}]}
            """).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Missing_collection_date_fails()
    {
        Validate("""
            {"biomarkers":[
              {"biomarker_name":"ferritin","display_name":"Ferritin","category":"nutrients","numeric_value":95}]}
            """).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Biomarker_with_no_value_at_all_fails()
    {
        Validate("""
            {"collected_at":"2026-03-14","biomarkers":[
              {"biomarker_name":"ferritin","display_name":"Ferritin","category":"nutrients"}]}
            """).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Inverted_reference_range_fails()
    {
        Validate("""
            {"collected_at":"2026-03-14","biomarkers":[
              {"biomarker_name":"ferritin","display_name":"Ferritin","category":"nutrients",
               "numeric_value":95,"reference_low":300,"reference_high":30}]}
            """).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Duplicate_biomarkers_in_one_panel_fail()
    {
        Validate("""
            {"collected_at":"2026-03-14","biomarkers":[
              {"biomarker_name":"ferritin","display_name":"Ferritin","category":"nutrients","numeric_value":95},
              {"biomarker_name":"ferritin","display_name":"Ferritin (repeat)","category":"nutrients","numeric_value":96}]}
            """).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Unknown_category_fails()
    {
        Validate("""
            {"collected_at":"2026-03-14","biomarkers":[
              {"biomarker_name":"ferritin","display_name":"Ferritin","category":"minerals","numeric_value":95}]}
            """).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void High_value_flags_high()
    {
        var draft = new BiomarkerDraft("ldl_cholesterol", "LDL-C", "heart", 190, null, "mg/dL", 0, 130, null);
        LabPanelResponseValidator.ComputeFlag(draft).Should().Be("high");
    }

    private static Result<LabPanelDraft> Validate(string panelJson)
    {
        var input = JsonSerializer.SerializeToElement(new { panel_json = panelJson });
        var response = new AnthropicMessageResponse(
            "msg_1", "test-model", "tool_use",
            new[]
            {
                new AnthropicContentBlock(
                    Type: "tool_use", Id: "tu_1",
                    Name: LabPanelResponseValidator.ToolName,
                    Input: input),
            },
            new AnthropicUsage(10, 20, null, null));
        return LabPanelResponseValidator.Validate(response, Today);
    }
}
