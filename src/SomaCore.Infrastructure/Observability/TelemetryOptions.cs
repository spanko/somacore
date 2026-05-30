using System.ComponentModel.DataAnnotations;

namespace SomaCore.Infrastructure.Observability;

/// <summary>
/// Telemetry configuration. The Application Insights exporter is opt-in via
/// <see cref="ApplicationInsightsConnectionString"/>; when empty (the default
/// for local dev), spans still emit through the <see cref="IngestionTracing.SourceName"/>
/// ActivitySource but nothing is exported to Azure. Tests rely on this — they
/// install their own <see cref="System.Diagnostics.ActivityListener"/>.
/// </summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>
    /// Application Insights connection string. Read from configuration
    /// (typically a Key Vault reference in production). Empty in local dev.
    /// </summary>
    public string? ApplicationInsightsConnectionString { get; init; }

    /// <summary>
    /// Logical service name used in the OTel <c>service.name</c> resource attribute.
    /// Defaults to <c>somacore-api</c>.
    /// </summary>
    [Required]
    public string ServiceName { get; init; } = "somacore-api";
}
