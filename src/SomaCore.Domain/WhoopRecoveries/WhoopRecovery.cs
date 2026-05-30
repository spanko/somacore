using System.Text.Json;

using SomaCore.Domain.Common;
using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.Users;

namespace SomaCore.Domain.WhoopRecoveries;

public class WhoopRecovery : IHasTimestamps
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>
    /// FK to <see cref="ExternalConnection"/>. Nullable so that disconnecting
    /// WHOOP (deleting the connection row) preserves the recovery as
    /// historical data tied to the user, not the connection.
    /// See <c>docs/schema/SCHEMA-NOTES.md</c> — "Cascade rules".
    /// </summary>
    public Guid? ExternalConnectionId { get; set; }

    public long WhoopCycleId { get; set; }

    public Guid? WhoopSleepId { get; set; }

    public string ScoreState { get; set; } = string.Empty;

    public int? RecoveryScore { get; set; }

    public decimal? HrvRmssdMilli { get; set; }

    public int? RestingHeartRate { get; set; }

    public decimal? Spo2Percentage { get; set; }

    public decimal? SkinTempCelsius { get; set; }

    public DateTimeOffset CycleStartAt { get; set; }

    public DateTimeOffset? CycleEndAt { get; set; }

    public string IngestedVia { get; set; } = string.Empty;

    public DateTimeOffset IngestedAt { get; set; }

    public JsonDocument RawPayload { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public User? User { get; set; }

    public ExternalConnection? ExternalConnection { get; set; }
}
