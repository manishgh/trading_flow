using System;

namespace TradingFlow.Domain.Jobs;

public class PersistedJob
{
    public Guid Id { get; set; }
    public string JobType { get; set; } = string.Empty;
    public string RunName { get; set; } = string.Empty;
    public string ConfigPath { get; set; } = string.Empty;
    public string RequestJson { get; set; } = "{}";
    public string? SnapshotJson { get; set; }
    public string? ResultReference { get; set; }
    public string Status { get; set; } = "queued"; // queued, running, completed, failed, cancelled
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long CreatedAtUtcTicks { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? CancellationRequestedAtUtc { get; set; }
    public string? LeaseOwner { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public long? LeaseExpiresAtUtcTicks { get; set; }
    public DateTimeOffset? HeartbeatAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public string? ErrorMessage { get; set; }
}
