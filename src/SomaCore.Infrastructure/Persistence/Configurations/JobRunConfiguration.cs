using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using SomaCore.Domain.JobRuns;

namespace SomaCore.Infrastructure.Persistence.Configurations;

public sealed class JobRunConfiguration : IEntityTypeConfiguration<JobRun>
{
    public void Configure(EntityTypeBuilder<JobRun> builder)
    {
        builder.ToTable("job_runs", t =>
        {
            t.HasCheckConstraint(
                "chk_job_runs_job_name",
                "job_name IN ('reconciliation-poller', 'token-refresh-sweeper')");
        });

        builder.HasKey(j => j.Id);

        builder.Property(j => j.Id)
            .ValueGeneratedOnAdd()
            .HasValueGenerator<Guid7Generator>();

        builder.Property(j => j.JobName)
            .IsRequired();

        builder.Property(j => j.StartedAt)
            .HasDefaultValueSql("now()");

        builder.Property(j => j.EndedAt);
        builder.Property(j => j.Success);
        builder.Property(j => j.ErrorMessage);

        builder.Property(j => j.Summary)
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb")
            .IsRequired();

        // Hot path: "give me the most recent run(s) for this job".
        builder.HasIndex(j => new { j.JobName, j.StartedAt })
            .IsDescending(false, true)
            .HasDatabaseName("idx_job_runs_job_started");
    }
}
