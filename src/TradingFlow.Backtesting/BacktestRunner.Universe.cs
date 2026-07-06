using System.Text.Json;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Backtesting;

// Point-in-time universe helpers for BacktestRunner. Split into a partial file so the runner's
// orchestration stays readable; behavior is identical to when these lived inline. The as-of and
// per-day screening logic itself lives in the Engine (HistoricalScreenerUniverseProvider).
public sealed partial class BacktestRunner
{
    private static string ResolveDailyTimeframe(BacktestRunConfig run)
    {
        return run.Intervals.FirstOrDefault(interval =>
            interval.Equals("1d", StringComparison.OrdinalIgnoreCase) ||
            interval.Equals("1day", StringComparison.OrdinalIgnoreCase) ||
            interval.Equals("1D", StringComparison.Ordinal)) ?? "1d";
    }

    // Broad candidate pool from a Finviz Elite screener export. Intended for structural/liquidity
    // screens (price, volume, market cap, sector) so the pool is not hand-picked. Note: Finviz
    // returns a current screen with no as-of, so for historical backtests this carries delisting
    // survivorship at the candidate level; the no-lookahead screen still governs selection.
    private static async Task<IReadOnlyList<string>> ResolveFinvizCandidatePoolAsync(
        UniverseConfig universe,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(universe.CandidateScreenerQuery))
        {
            throw new InvalidOperationException(
                "universe.candidate_source=finviz requires universe.candidate_screener_query (a Finviz screener URL or filter string).");
        }

        using var httpClient = new HttpClient();
        using var client = new TradingFlow.Finviz.FinvizClient(
            httpClient,
            TradingFlow.Finviz.FinvizOptions.CreateDefault() with
            {
                AuthToken = Environment.GetEnvironmentVariable("FINVIZ_API_KEY") ?? String.Empty
            });

        var tickers = await client.GetScreenerTickersAsync(universe.CandidateScreenerQuery, cancellationToken);
        if (tickers.Count == 0)
        {
            throw new InvalidOperationException(
                "Finviz screener returned zero candidates. Check universe.candidate_screener_query and FINVIZ_API_KEY.");
        }

        return tickers;
    }

    private static async Task PersistUniverseSnapshotAsync(
        BacktestRunConfig run,
        UniverseResolution resolution,
        CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.Combine(run.ResultsRoot, "universe");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{resolution.AsOfDate:yyyyMMdd}-{run.RunName}.json");
            var payload = new
            {
                runName = run.RunName,
                asOfDate = resolution.AsOfDate.ToString("yyyy-MM-dd"),
                source = resolution.Source,
                rule = run.Universe,
                selected = resolution.Selected,
                rejections = resolution.Rejections
            };
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
        }
        catch
        {
            // Snapshot persistence is best-effort audit; never fail a run because of it.
        }
    }
}
