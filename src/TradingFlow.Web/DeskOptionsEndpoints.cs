using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
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

            var spotHints = await TryLoadSpotHintsAsync(quotes, requestedSymbols, cancellationToken);

            var spyTask = optionsSource.GetSnapshotAsync("SPY", spotHints.GetValueOrDefault("SPY"), cancellationToken: cancellationToken);
            var qqqTask = optionsSource.GetSnapshotAsync("QQQ", spotHints.GetValueOrDefault("QQQ"), cancellationToken: cancellationToken);
            Task<OptionsSentimentSnapshot>? selectedTask = selectedSymbol switch
            {
                null => null,
                "SPY" => spyTask,
                "QQQ" => qqqTask,
                _ => optionsSource.GetSnapshotAsync(selectedSymbol, spotHints.GetValueOrDefault(selectedSymbol), cancellationToken: cancellationToken)
            };

            if (selectedTask is not null && selectedTask != spyTask && selectedTask != qqqTask)
            {
                await Task.WhenAll(spyTask, qqqTask, selectedTask);
            }
            else
            {
                await Task.WhenAll(spyTask, qqqTask);
            }

            var response = new DeskOptionsSentimentResponse(
                ContractVersions.VersionOne,
                clock.GetUtcNow().ToUniversalTime(),
                await spyTask,
                await qqqTask,
                selectedTask is null ? null : await selectedTask);
            return Results.Ok(response);
        });
    }

    // Validates a US equity symbol string before passing it to provider quote and OPRA snapshot queries.
    internal static bool IsValidEquitySymbol(string symbol) =>
        symbol.Length is >= 1 and <= 12 &&
        symbol.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-');

    // Best-effort lookup of Desk mid-prices so OPRA queries can narrow strikes without failing when quotes are absent.
    private static async Task<Dictionary<string, decimal?>> TryLoadSpotHintsAsync(
        AlpacaQuoteService quotes,
        string[] symbols,
        CancellationToken cancellationToken)
    {
        var hints = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var latest = await quotes.GetNewestQuotesAsync(symbols, cancellationToken);
            foreach (var pair in latest)
            {
                if (pair.Value?.MidPrice is > 0m)
                {
                    hints[pair.Key] = pair.Value.MidPrice;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // AlpacaOptionsSnapshotClient falls back to stock snapshot / option parity when hints are absent.
        }

        return hints;
    }
}
