using System.Text.Json;

using Microsoft.Extensions.Logging;

using SomaCore.Domain.JobRuns;
using SomaCore.Infrastructure.Persistence;

namespace SomaCore.IngestionJobs.Jobs;

/// <summary>
/// Resolves the requested <see cref="IJob"/> by name from the registered set,
/// records a row in <c>job_runs</c> wrapping the run, and converts the outcome
/// into a process exit code. Scoped — shares the DbContext + scope lifetime
/// with whatever the dispatched job needs.
/// </summary>
public sealed class JobDispatcher(
    IEnumerable<IJob> jobs,
    SomaCoreDbContext dbContext,
    ILogger<JobDispatcher> logger)
{
    public async Task<int> DispatchAsync(string jobName, CancellationToken cancellationToken)
    {
        var job = jobs.FirstOrDefault(j => j.Name.Equals(jobName, StringComparison.OrdinalIgnoreCase));
        if (job is null)
        {
            logger.LogError("Unknown job name '{JobName}'. Registered: {Registered}",
                jobName, string.Join(", ", jobs.Select(j => j.Name)));
            return 64; // EX_USAGE
        }

        var jobRun = new JobRun { JobName = job.Name, StartedAt = DateTimeOffset.UtcNow };
        dbContext.JobRuns.Add(jobRun);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Job {JobName} started ({JobRunId})", job.Name, jobRun.Id);

        JobOutcome outcome;
        try
        {
            outcome = await job.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = new JobOutcome(false, "cancelled", new { cancelled = true });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobName} threw", job.Name);
            outcome = new JobOutcome(false, $"{ex.GetType().Name}: {ex.Message}", new { });
        }

        jobRun.EndedAt = DateTimeOffset.UtcNow;
        jobRun.Success = outcome.Success;
        jobRun.ErrorMessage = outcome.Error?.Length > 1000 ? outcome.Error[..1000] : outcome.Error;
        jobRun.Summary = JsonDocument.Parse(JsonSerializer.Serialize(outcome.Summary));
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Job {JobName} ended success={Success} duration={DurationSec}s",
            job.Name,
            outcome.Success,
            Math.Round((jobRun.EndedAt.Value - jobRun.StartedAt).TotalSeconds, 1));

        return outcome.Success ? 0 : 1;
    }
}
