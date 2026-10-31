using TradingFlow.Contracts.V1;

namespace TradingFlow.Contracts.Operator;

/// <summary>Supplemental local ownership and intended protection; broker holdings remain the position authority.</summary>
public sealed record PositionRunReference(string Source, string Name, string StrategyName, decimal TrackedQuantity,
    Guid? JobId, Guid? SessionId, string CloseKind, decimal? PlannedStopLossPrice, decimal? PlannedTakeProfitPrice);

/// <summary>One actual broker holding, including positions without an active or known local run.</summary>
public sealed record AccountPosition(PortfolioPositionResponse Holding, IReadOnlyList<PositionRunReference> Runs,
    bool ProtectionKnown, bool HasBrokerStop, string ProtectionSummary)
{
    public string Ticker => Holding.Symbol;
    public decimal Quantity => Holding.Quantity;
    public decimal? EntryPrice => Holding.AverageEntry;
    public decimal? CurrentPrice => Holding.MarketValue is { } value && Quantity != 0 ? value / Quantity : null;
    public decimal? UnrealizedPl => Holding.UnrealizedPl;
    public decimal? UnrealizedPlPct => Holding.UnrealizedPl is { } pl && Holding.CostBasis is { } basis && basis != 0
        ? pl / Math.Abs(basis) * 100m : null;
    public string Source => Runs.Select(item => item.Source).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() is { Length: 1 } sources
        ? sources[0] : Runs.Count == 0 ? "account" : "multiple";
    public string Reference => Runs.Count == 0 ? "Not linked to a local run" : String.Join(" · ", Runs.Select(item => item.Name).Distinct());
    public string StrategyName => String.Join(" · ", Runs.Select(item => item.StrategyName).Where(item => !String.IsNullOrWhiteSpace(item)).Distinct());
    public string Status => "open";

    // A local exit route is only displayed when one tracked position matches the
    // complete broker quantity. The server must still revalidate any exit command.
    public bool CanReviewExit => Runs.Count == 1 && Runs[0].TrackedQuantity == Quantity && Quantity > 0 &&
        Quantity == Math.Truncate(Quantity) &&
        (Runs[0].CloseKind == "paper_job" && Runs[0].JobId is not null || Runs[0].CloseKind == "automation" && Runs[0].SessionId is not null);
    public string CloseKind => CanReviewExit ? Runs[0].CloseKind : "account";
    public Guid? JobId => CanReviewExit ? Runs[0].JobId : null;
    public Guid? SessionId => CanReviewExit ? Runs[0].SessionId : null;
    public string HeaderText => $"{Ticker}  x{Quantity:0.####}";
    public string PriceText => $"Entry {Money(EntryPrice)} → {Money(CurrentPrice)}";
    public string PlText => UnrealizedPl is { } pl
        ? $"{(pl > 0 ? "+" : String.Empty)}{pl:C2}" + (UnrealizedPlPct is { } percent ? $" ({percent:0.00}%)" : " (percentage unavailable)")
        : "P/L unavailable";
    public string DetailText => SourceLabel(Source) + " · " + Reference;

    /// <summary>Formats an observed value without converting an absent broker field into zero.</summary>
    private static string Money(decimal? value) => value?.ToString("C2") ?? "Unavailable";

    /// <summary>Uses existing source names and explicitly identifies holdings without local run ownership.</summary>
    public static string SourceLabel(string source) => source switch {
        "wishlist" => "Wishlist", "stockpulse" => "Stock Pulse", "manual" => "Manual",
        "account" => "Account holding", "multiple" => "Multiple runs", _ => source
    };
}

/// <summary>Account-wide totals remain unchanged by the filtered position list; unknown reads have null totals.</summary>
public sealed record AccountPositionsResponse(bool Available, string Detail, DateTimeOffset ReadAtUtc,
    IReadOnlyList<AccountPosition> Trades, decimal? TotalUnrealizedPl, decimal? GrossExposure,
    int? SlotsUsed, int? MaxConcurrentPositions, int? UnprotectedCount, int? UnknownProtectionCount)
{
    public int Count => Trades.Count;
    public string TotalText => TotalUnrealizedPl is { } pl ? $"Account P/L {(pl > 0 ? "+" : String.Empty)}{pl:C2}" : "Account P/L unavailable";
}
