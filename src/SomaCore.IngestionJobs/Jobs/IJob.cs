namespace SomaCore.IngestionJobs.Jobs;

/// <summary>
/// One-shot scheduled job. Runner instantiates, calls <see cref="RunAsync"/>,
/// then the process exits. Concrete implementations are registered in DI
/// keyed by their <see cref="Name"/>.
/// </summary>
public interface IJob
{
    /// <summary>Matches <c>SomaCore.Domain.JobRuns.JobName.*</c> values.</summary>
    string Name { get; }

    Task<JobOutcome> RunAsync(CancellationToken cancellationToken);
}

/// <summary>Structured result the dispatcher writes to <c>job_runs</c>.</summary>
public sealed record JobOutcome(bool Success, string? Error, object Summary);
