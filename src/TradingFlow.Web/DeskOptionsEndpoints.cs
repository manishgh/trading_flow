using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Market;
using TradingFlow.Web.Services;

namespace TradingFlow.Web;

/// <summary>
/// Read-only Desk API payload combining broad-market (SPY, QQQ) and optional selected-stock OPRA options sentiment.
/// </summary>
public sealed record DeskOptionsSentimentResponse(
    string ContractVersion,
    DateTimeOffset ObservedAtUtc,
    OptionsSentimentSnapshot Spy,
    OptionsSentimentSnapshot Qqq,
    OptionsSentimentSnapshot? Selected);

/// <summary>
/// Maps authenticated read-only Desk endpoints exposing live OPRA options volatility, skew, and term-structure context.
/// </summary>
public static class DeskOptionsEndpoints
{
    /// <summary>Registers <c>GET /api/v1/desk/options-sentiment</c> under the authenticated v1 API group.</summary>
    public static void MapDeskOptionsEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/desk/options-sentiment", async (
            string? symbol,
            IOptionsSentimentSource optionsSource,
            AlpacaQuoteService quotes,
            CatalystOptionsAdrService adrService,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            string? selectedSymbol = null;
            if (!string.IsNullOrWhiteSpace(symbol))
            {
                var trimmed = symbol.Trim().ToUpperInvariant();
                if (!IsValidEquitySymbol(trimmed))
                {
                    return Results.BadRequest(new ApiErrorResponse(
                        ContractVersions.VersionOne,
                        "invalid_symbol",
                        "Invalid stock symbol. Expected 1-12 ASCII letters, digits, '.' or '-'."));
                }

                selectedSymbol = trimmed;
            }

            var requestedSymbols = selectedSymbol is null
                ? new[] { "SPY", "QQQ" }
                : new[] { "SPY", "QQQ", selectedSymbol }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            // The selected stock's 20-day ADR% and the 65-day forward exchange calendar (both cached per New York day)
            // load in parallel with OPRA so holiday-aware trading days and implied/ADR ratios stay consistent without blocking OPRA.
            using var adrDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            adrDeadline.CancelAfter(TimeSpan.FromSeconds(6));
            var adrTask = selectedSymbol is null
                ? Task.FromResult(new Dictionary<string, Adr20CalculationResult>(StringComparer.OrdinalIgnoreCase))
                : adrService.LoadAdrAsync([selectedSymbol], adrDeadline.Token);
            var schedulesTask = adrService.TryLoadOptionsSchedulesAsync(adrDeadline.Token);
            var spotHints = await TryLoadSpotHintsAsync(quotes, requestedSymbols, cancellationToken, cancellationToken);

            var spyTask = optionsSource.GetSnapshotAsync("SPY", spotHints.GetValueOrDefault("SPY"), cancellationToken: cancellationToken);
            var qqqTask = optionsSource.GetSnapshotAsync("QQQ", spotHints.GetValueOrDefault("QQQ"), cancellationToken: cancellationToken);
            Task<OptionsSentimentSnapshot>? selectedTask = selectedSymbol switch
            {
                null => null,
                "SPY" => spyTask,
                "QQQ" => qqqTask,
                _ => optionsSource.GetSnapshotAsync(selectedSymbol, spotHints.GetValueOrDefault(selectedSymbol), cancellationToken: cancellationToken)
            };

            var pending = new List<Task> { spyTask, qqqTask, schedulesTask };
            if (selectedTask is not null && selectedTask != spyTask && selectedTask != qqqTask)
            {
                pending.Add(selectedTask);
            }

            await Task.WhenAll(pending);
            var schedules = await schedulesTask;
            var spySnapshot = OptionsSentimentCalculator.WithMarketContext(await spyTask, adr20Pct: null, schedules);
            var qqqSnapshot = OptionsSentimentCalculator.WithMarketContext(await qqqTask, adr20Pct: null, schedules);

            var selectedSnapshot = selectedTask is null ? null : await selectedTask;
            if (selectedSnapshot is not null && selectedSymbol is not null)
            {
                var adr = await adrTask;
                selectedSnapshot = OptionsSentimentCalculator.WithMarketContext(
                    selectedSnapshot,
                    adr.GetValueOrDefault(selectedSymbol)?.Adr20Pct,
                    schedules);
            }

            var response = new DeskOptionsSentimentResponse(
                ContractVersions.VersionOne,
                clock.GetUtcNow().ToUniversalTime(),
                spySnapshot,
                qqqSnapshot,
                selectedSnapshot);
            return Results.Ok(response);
        });

        // Read-only Catalysts endpoint comparing OPRA front-expiry implied move against completed 20-session ADR%.
        group.MapGet("/catalysts/options-adr", async (
            string? symbols,
            CatalystOptionsAdrService adrService,
            CancellationToken cancellationToken) =>
        {
            if (!CatalystOptionsAdrService.TryParseSymbols(symbols, out var parsedSymbols, out var error))
            {
                return Results.BadRequest(new ApiErrorResponse(
                    ContractVersions.VersionOne,
                    "invalid_symbols",
                    error ?? "Invalid symbols query."));
            }

            var response = await adrService.LoadAsync(parsedSymbols, cancellationToken);
            return Results.Ok(response);
        });
    }

    // Validates a US equity symbol string before passing it to provider quote and OPRA snapshot queries.
    internal static bool IsValidEquitySymbol(string symbol) =>
        symbol.Length is >= 1 and <= 12 &&
        symbol.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-');

    // Best-effort lookup of Desk mid-prices so OPRA queries can narrow strikes without failing when quotes are absent or timed out.
    internal static async Task<Dictionary<string, decimal?>> TryLoadSpotHintsAsync(
        AlpacaQuoteService quotes,
        string[] symbols,
        CancellationToken deadlineToken,
        CancellationToken callerToken = default)
    {
        var hints = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var latest = await quotes.GetNewestQuotesAsync(symbols, deadlineToken);
            foreach (var pair in latest)
            {
                if (pair.Value?.MidPrice is > 0m)
                {
                    hints[pair.Key] = pair.Value.MidPrice;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !callerToken.IsCancellationRequested)
        {
            // AlpacaOptionsSnapshotClient falls back to stock snapshot / option parity when hints are absent.
        }

        return hints;
    }
}
