namespace TradingFlow.Domain.Persistence;

/// <summary>
/// Required provenance carried by every operational and research-journal row.
/// </summary>
public abstract class OperationalRecord
{
    public Guid RunId { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public string ConfigHash { get; set; } = string.Empty;
    public string CodeVersion { get; set; } = string.Empty;
}

public sealed class ProductionRun : OperationalRecord
{
    public string Profile { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
}
