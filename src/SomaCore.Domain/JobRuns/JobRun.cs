using System.Text.Json;

namespace SomaCore.Domain.JobRuns;

/// <summary>
/// Audit row per execution of a scheduled job (reconciliation poller, token
/// refresh sweeper, etc.). Written on start, updated on completion. Drives
/// the /admin/health page's "last poller run" / "last sweeper run" cards.
/// </summary>
public class JobRun
{
    public Guid Id { get; set; }

    public string JobName { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public bool? Success { get; set; }

    public string? ErrorMessage { get; set; }

    public JsonDocument Summary { get; set; } = JsonDocument.Parse("{}");
}
