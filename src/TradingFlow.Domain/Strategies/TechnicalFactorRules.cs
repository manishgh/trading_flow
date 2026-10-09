using TradingFlow.Domain.Market;
namespace TradingFlow.Domain.Strategies;

/// <summary>Opt-in completed-daily factors. Min/max values are strict; the context maximum is inclusive.</summary>
public sealed record TechnicalFactorRules(string Metric, int LookbackBars = 20,
    decimal? MinValue = null, decimal? MaxValue = null, decimal? MaxContextValue = null,
    string BenchmarkSymbol = "SPY")
{
    public const string AtrDistance = "atr_sma20_distance";
    public const string Roc = "roc";
    public const string Ibs = "internal_bar_strength";
    public const string Connors = "connors_rsi_3_2_100";
    public const string Squeeze = "bollinger_squeeze_breakout";
    public const string RelativeRoc = "benchmark_relative_roc";
    [System.Text.Json.Serialization.JsonIgnore]
    public int RequiredCompletedBars => Metric switch
    {
        Connors => 260,
        AtrDistance => 20,
        Ibs => 2,
        Squeeze => LookbackBars + 21,
        _ => LookbackBars + 1
    };
    public void Validate()
    {
        if (Metric is not (AtrDistance or Roc or Ibs or Connors or Squeeze or RelativeRoc) ||
            LookbackBars < 1 || LookbackBars > 500 || MinValue is null && MaxValue is null ||
            MinValue is { } min && MaxValue is { } max && min >= max)
            throw new InvalidOperationException("Unsupported or unbounded technical factor.");
        if (Metric == AtrDistance && LookbackBars != 20 || Metric == Ibs && LookbackBars != 1 ||
            Metric == Connors && LookbackBars != 100)
            throw new InvalidOperationException("The named factor period and declared lookback differ.");
        if (Metric == Squeeze && (MaxContextValue is null or < 0 or > 100 || MinValue is null or < 0) ||
            Metric != Squeeze && MaxContextValue is not null)
            throw new InvalidOperationException("Squeeze requires a prior-width percentile and nonnegative breakout threshold.");
        if (Metric == RelativeRoc && (String.IsNullOrWhiteSpace(BenchmarkSymbol) || BenchmarkSymbol != BenchmarkSymbol.Trim().ToUpperInvariant()))
            throw new InvalidOperationException("Relative ROC requires a canonical benchmark symbol.");
    }
}

public sealed record TechnicalFactorEvidence(string Metric, int LookbackBars,
    DateTimeOffset SourceTimestamp, decimal? Value, decimal? ContextValue = null,
    string? UnavailableReason = null, string? BenchmarkSymbol = null,
    IReadOnlyList<OhlcvBar>? ReferenceInputs = null,
    decimal? PriceRsi = null, decimal? StreakRsi = null, decimal? PercentRank = null);
