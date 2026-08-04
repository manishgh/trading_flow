namespace TradingFlow.Domain.Portfolio;

public class AdvisoryPortfolio
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<AdvisoryPosition> Positions { get; set; } = new();
}
