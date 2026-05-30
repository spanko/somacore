using System.Text.Json;

using SomaCore.Domain.Common;
using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.Users;

namespace SomaCore.Domain.WhoopWorkouts;

public class WhoopWorkout : IHasTimestamps
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>
    /// FK to <see cref="ExternalConnection"/>. Nullable so that disconnecting
    /// WHOOP (deleting the connection row) preserves the workout as historical
    /// data tied to the user, not the connection. Same contract as
    /// <see cref="SomaCore.Domain.WhoopRecoveries.WhoopRecovery.ExternalConnectionId"/>.
    /// See <c>docs/schema/SCHEMA-NOTES.md</c> — "Cascade rules".
    /// </summary>
    public Guid? ExternalConnectionId { get; set; }

    public Guid WhoopWorkoutId { get; set; }

    public DateTimeOffset StartAt { get; set; }

    public DateTimeOffset EndAt { get; set; }

    public string TimezoneOffset { get; set; } = string.Empty;

    /// <summary>
    /// WHOOP v2 identifies the sport by name (string) rather than the legacy
    /// integer sport_id. Stored verbatim from the WHOOP payload.
    /// </summary>
    public string SportName { get; set; } = string.Empty;

    public string ScoreState { get; set; } = string.Empty;

    public decimal? Strain { get; set; }

    public int? AverageHeartRate { get; set; }

    public int? MaxHeartRate { get; set; }

    public decimal? Kilojoule { get; set; }

    public JsonDocument? Score { get; set; }

    public string IngestedVia { get; set; } = string.Empty;

    public DateTimeOffset IngestedAt { get; set; }

    public JsonDocument RawPayload { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public User? User { get; set; }

    public ExternalConnection? ExternalConnection { get; set; }
}
