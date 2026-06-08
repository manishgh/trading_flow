using System;

namespace TradingFlow.Domain.Jobs;

public class PersistedJob
{
    public Guid Id { get; set; }
    public string RunName { get; set; } = string.Empty;
    public string ConfigPath { get; set; } = string.Empty;
    public string Status { get; set; } = "queued"; // queued, running, completed, failed, cancelled
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
