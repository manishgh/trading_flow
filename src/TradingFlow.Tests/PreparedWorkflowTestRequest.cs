using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Jobs;
using TradingFlow.Application.Jobs;

namespace TradingFlow.Tests;

/// <summary>Provides immutable, fixture-owned comparison requests for admission tests whose configuration edge writes JSON choices.</summary>
internal static class PreparedWorkflowTestRequest
{
    /// <summary>Freezes the mock's exact stock groups, UTC dates and capital without any provider call or operator file access.</summary>
    public static async Task<PreparedMarketDataRequestReference?> WriteAsync(string root, string comparisonId,
        IReadOnlyList<PersistedJob> books, DateTimeOffset cutoffUtc, UniversePreviewResponse source, CancellationToken token)
    {
        var captured = new List<PreparedMarketDataBook>();
        foreach (var book in books)
        {
            var path = Path.GetFullPath(book.ConfigPath);
            if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Synthetic request cannot read outside its test-owned root.");
            var request = JsonSerializer.Deserialize<StartBacktestRequest>(await File.ReadAllTextAsync(path, token))!;
            var cohort = DurableFileJobRequest.Deserialize(book.RequestJson).Workflow?.Cohort;
            var run = new BacktestRunConfig(book.RunName, "backtest", new("tpl", 1, 100, 5, 30, true),
                new("fixed", 0, request.TestWindowStartUtc, request.TestWindowEndUtc),
                cohort?.Symbols ?? source.Members.Select(member => member.Symbol).ToArray(), "synthetic", ["1d"],
                Path.Combine(root, "raw"), Path.Combine(root, "normalized"), Path.Combine(root, "results"), request.CachePolicy,
                new("1d"), new(new(false, 0), new(false, 0, 0), new(false, "SYNTHETIC"), new(false, 0, 0, 0), new("synthetic", null, "all")),
                new(new("sip")), new(request.StartingCapital, request.AccountRiskBudgetPct, request.MaxPositionNotionalPct,
                    request.MaxConcurrentPositions, 0, 0, 1, true), new("internal_candles", false, "", 300, 0),
                new("simulated", "none", true, false, "market", "day", "market"), new(false, "none", 0, 0), new(false, "none", []), new("full"), []);
            captured.Add(new(book.Id.ToString("N"), run, []));
        }
        var reference = await PreparedMarketDataService.WriteRequestAsync(new(1, comparisonId, cutoffUtc, captured),
            Path.Combine(root, "prepared-comparisons", comparisonId, Path.GetFileNameWithoutExtension(books[0].ConfigPath), "request.json"), token);
        return reference;
    }
}
