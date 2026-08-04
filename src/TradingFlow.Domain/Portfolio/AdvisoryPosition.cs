namespace TradingFlow.Domain.Portfolio;

public class AdvisoryPosition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PortfolioId { get; set; }
    public AdvisoryPortfolio Portfolio { get; set; } = null!;
    
    public string Ticker { get; set; } = string.Empty;
    public decimal Shares { get; set; }
    public decimal PurchasePrice { get; set; }
    
    public DateTimeOffset AcquiredAtUtc { get; set; } = DateTimeOffset.UtcNow;
    
    public decimal? LastMlScore { get; set; }
    public DateTimeOffset? LastScoredAtUtc { get; set; }
    public string? LastRecommendation { get; set; } // e.g. "Hold", "Sell"
}
