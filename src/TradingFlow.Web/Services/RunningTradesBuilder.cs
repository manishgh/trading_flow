using TradingFlow.Web.Models;
using TradingFlow.Contracts.Operator;
using TradingFlow.Contracts.V1;
using TradingFlow.Web.Services.Desk;

namespace TradingFlow.Web.Services;

/// <summary>
/// Aggregates live "running trades" across sources (wishlist paper runs, Stock Pulse and
/// manual automation sessions) into a single per-stock list with unrealized P/L. Shared by
/// the mobile API and the web Running Trades page so both stay consistent.
/// </summary>
public static class RunningTradesBuilder
{
    /// <summary>Reads the actual account first; local jobs only supply optional labels and never create or remove broker holdings.</summary>
    public static async Task<AccountPositionsResponse> ReadAccountAsync(IDeskAccountReader accountReader,
        Func<CancellationToken, Task<IReadOnlyList<MobileRunningTrade>>> readLocalMetadata,
        IPositionProtectionReader protectionReader, int? configuredPositionLimit, string? source,
        CancellationToken token)
    {
        var account = await accountReader.ReadAsync(token);
        if (!account.Available || !account.Holdings.Any(item => item.Quantity != 0))
            return ProjectAccount(account, [], new Dictionary<string, PositionProtection>(), configuredPositionLimit, source);
        // Missing local metadata must not hide actual holdings. Existing local
        // readers report their own failures; no local price or quantity is used here.
        IReadOnlyList<MobileRunningTrade> metadata;
        try { metadata = await readLocalMetadata(token); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            metadata = [];
            account = account with { Detail = account.Detail + " Local run details are unavailable; broker holdings are still shown." };
        }
        var protections = await protectionReader.GetAsync(account.Holdings.Where(item => item.Quantity != 0).ToArray(), token);
        return ProjectAccount(account, metadata, protections, configuredPositionLimit, source);
    }

    /// <summary>Projects broker numbers and independent full-account totals before applying a source filter.</summary>
    public static AccountPositionsResponse ProjectAccount(DeskAccount account, IReadOnlyList<MobileRunningTrade> metadata,
        IReadOnlyDictionary<string, PositionProtection> protections, int? configuredPositionLimit, string? source)
    {
        if (!account.Available)
            return new(false, account.Detail, account.ReadAtUtc, [], null, null, null, configuredPositionLimit, null, null);
        var held = account.Holdings.Where(item => item.Quantity != 0).ToArray();
        var all = held.Select(holding => {
            var runs = metadata.Where(item => item.Ticker.Equals(holding.Symbol, StringComparison.OrdinalIgnoreCase))
                .Select(item => new PositionRunReference(item.Source, item.Reference, item.StrategyName, item.Quantity,
                    item.JobId, item.SessionId, item.CloseKind, item.StopLossPrice, item.TakeProfitPrice)).Distinct().ToArray();
            var protection = protections.GetValueOrDefault(holding.Symbol);
            return new AccountPosition(new PortfolioPositionResponse(holding.Symbol, holding.Quantity, holding.AverageEntry,
                holding.CostBasis, holding.MarketValue, holding.UnrealizedPl), runs,
                protection?.IsKnown == true, protection?.HasBrokerStop == true,
                protection?.Detail ?? "Broker protection has not been read.");
        }).ToArray();
        var filtered = String.IsNullOrWhiteSpace(source) || source.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? all : all.Where(item => item.Source.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                item.Runs.Any(run => run.Source.Equals(source, StringComparison.OrdinalIgnoreCase))).ToArray();
        return new(true, account.Detail, account.ReadAtUtc, filtered,
            held.All(item => item.UnrealizedPl is not null) ? held.Sum(item => item.UnrealizedPl!.Value) : null,
            held.All(item => item.MarketValue is not null) ? held.Sum(item => Math.Abs(item.MarketValue!.Value)) : null,
            held.Length, configuredPositionLimit,
            all.Count(item => item.ProtectionKnown && !item.HasBrokerStop), all.Count(item => !item.ProtectionKnown));
    }

    /// <summary>Reads optional local run labels and intended protection; account-wide screens use ReadAccountAsync for position truth.</summary>
    public static async Task<IReadOnlyList<MobileRunningTrade>> BuildAsync(
        PaperJobService paperJobs,
        MobileAutomationService automation)
    {
        var trades = new List<MobileRunningTrade>();

        // Automation sessions already track one live position each (Stock Pulse or manual).
        foreach (var session in automation.List())
        {
            if (session.Status is not ("running" or "starting") || (session.ShareQuantity ?? 0) <= 0)
            {
                continue;
            }

            var source = session.Source.Equals("notification", StringComparison.OrdinalIgnoreCase) ? "stockpulse" : "manual";
            var quantity = session.ShareQuantity ?? 0;
            var entry = session.EntryPrice ?? 0m;
            var current = session.LastObservedPrice ?? entry;
            var pl = session.UnrealizedPl ?? 0m;
            trades.Add(new MobileRunningTrade(
                source,
                session.Ticker,
                quantity,
                entry,
                current,
                pl,
                ComputePlPct(pl, entry, quantity),
                session.Status,
                session.RunName,
                null,
                session.SessionId,
                "automation",
                Path.GetFileNameWithoutExtension(session.StrategyPath),
                session.StopLossPrice,
                session.TakeProfitPrice,
                session.ExitReason,
                session.EntrySubmittedAt ?? session.StartedAt ?? session.CreatedAt,
                BuildProtectionSummary(session.StopLossPrice, session.TakeProfitPrice)));
        }

        // Paper runs hold their positions in the broker; label wishlist vs manual by run-name prefix.
        foreach (var job in paperJobs.List())
        {
            if (job.Status is not ("running" or "starting"))
            {
                continue;
            }

            var source = job.RunName.StartsWith("wishlist_", StringComparison.OrdinalIgnoreCase) ? "wishlist" : "manual";
            var positions = await paperJobs.GetOpenPositionsAsync(job.JobId);
            var openOrders = await paperJobs.GetOpenOrdersAsync(job.JobId);
            foreach (var position in positions)
            {
                var stopOrder = openOrders
                    .Where(order =>
                        order.Ticker.Equals(position.Ticker, StringComparison.OrdinalIgnoreCase) &&
                        order.Side.Equals("sell", StringComparison.OrdinalIgnoreCase) &&
                        order.StopPrice is not null)
                    .OrderByDescending(order => order.CreatedAt)
                    .FirstOrDefault();
                var targetOrder = openOrders
                    .Where(order =>
                        order.Ticker.Equals(position.Ticker, StringComparison.OrdinalIgnoreCase) &&
                        order.Side.Equals("sell", StringComparison.OrdinalIgnoreCase) &&
                        order.LimitPrice is not null)
                    .OrderByDescending(order => order.CreatedAt)
                    .FirstOrDefault();

                trades.Add(new MobileRunningTrade(
                    source,
                    position.Ticker,
                    position.Qty,
                    position.EntryPrice,
                    position.CurrentPrice,
                    position.UnrealizedPl,
                    ComputePlPct(position.UnrealizedPl, position.EntryPrice, position.Qty),
                    "open",
                    job.RunName,
                    job.JobId,
                    null,
                    "paper_job",
                    job.RunName,
                    stopOrder?.StopPrice,
                    targetOrder?.LimitPrice,
                    null,
                    job.StartedAt ?? job.CreatedAt,
                    BuildProtectionSummary(stopOrder?.StopPrice, targetOrder?.LimitPrice)));
            }
        }

        return trades
            .OrderByDescending(trade => trade.UnrealizedPl)
            .ToArray();
    }

    public static IReadOnlyList<MobileRunningTrade> Filter(IReadOnlyList<MobileRunningTrade> trades, string? source)
    {
        if (String.IsNullOrWhiteSpace(source) || source.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return trades;
        }

        return trades
            .Where(trade => trade.Source.Equals(source, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static decimal ComputePlPct(decimal unrealizedPl, decimal entryPrice, decimal quantity)
    {
        var basis = entryPrice * quantity;
        return basis > 0m ? Math.Round(unrealizedPl / basis * 100m, 2) : 0m;
    }

    private static string BuildProtectionSummary(decimal? stopLossPrice, decimal? takeProfitPrice)
    {
        return (stopLossPrice, takeProfitPrice) switch
        {
            ({ } stop, { } target) => $"Stop {stop:0.00} / Target {target:0.00}",
            ({ } stop, null) => $"Stop {stop:0.00}",
            (null, { } target) => $"Target {target:0.00}",
            _ => "No broker protection detected"
        };
    }
}
