namespace TradingFlow.Domain.Earnings;

/// <summary>
/// Advisory analysis derived only from earnings information, news, and completed bars
/// available at <see cref="AnalyzedAtUtc"/>. It is not an order instruction.
/// </summary>
public sealed class EarningsAnalysisSnapshot
{
    public Guid Id { get; set; }
    public string EarningsEventId { get; set; } = String.Empty;
    public string Ticker { get; set; } = String.Empty;
    public DateTimeOffset AnalyzedAtUtc { get; set; }
    public EarningsResultAssessment ResultAssessment { get; set; }
    public EarningsBreakoutAssessment BreakoutAssessment { get; set; }
    public string Reason { get; set; } = String.Empty;
    public DateTimeOffset? ResultNewsPublishedAtUtc { get; set; }
    public string? NewsHeadline { get; set; }
    public string? NewsUrl { get; set; }
    public string? NewsProvider { get; set; }
    public decimal? NewsSentiment { get; set; }
    public DateTimeOffset? LatestCompletedBarAtUtc { get; set; }
    public decimal? PreReleaseReferenceHigh { get; set; }
    public decimal? PreReleaseReferenceClose { get; set; }
    public decimal? LatestClose { get; set; }
    public decimal? EventReturnPercent { get; set; }
    public decimal? Ema10 { get; set; }
    public decimal? Ema20 { get; set; }
    public decimal? MacdHistogram { get; set; }
    public decimal? SlotRelativeVolume { get; set; }
}
