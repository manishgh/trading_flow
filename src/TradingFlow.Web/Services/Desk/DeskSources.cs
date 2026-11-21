using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Earnings;
using TradingFlow.Earnings;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web.Services.Desk;

public sealed record DeskSource(string Key, string Name, string Group, string Description, string? Query = null);
public sealed record DeskMembership(string Symbol, string? Name, IReadOnlyList<DeskWishlist> Wishlists);
public sealed record DeskWishlist(Guid Id, string Name, bool Observed);
public enum DeskStockAddStatus { Added, AlreadyInGroup, Refused, Unavailable }
public sealed record DeskStockAddResult(DeskStockAddStatus Status, string Symbol, string Message);
public sealed record DeskSelection(DeskSource Source, IReadOnlyList<DeskMembership> Members,
    int TotalCount, DateTimeOffset? ScreenedAtUtc, string? Error)
{
    public int MatchedCount { get; init; }
    public int Page { get; init; }
    public int PageCount { get; init; }
}

/// <summary>Resolves observational membership without creating universes, candidates or orders.</summary>
public sealed partial class DeskSources(IDbContextFactory<TradingFlowDbContext> dbFactory,
    IWishlistRepository wishlists, ScreenerPresetService presets, IScreenerSnapshotSource screener,
    IMemoryCache cache, EarningsMonitor? earnings = null, ResearchScreenSources? researchScreens = null,
    TradingFlow.Engine.Execution.IAssetTradingEligibilityProvider? assets = null, ILogger<DeskSources>? logger = null,
    AlpacaCredentialProvider? alpacaCredentials = null)
{
    /// <summary>How long "Add stock" waits for Alpaca's asset record before answering that the check is unavailable.</summary>
    public TimeSpan AssetLookupTimeout { get; init; } = TimeSpan.FromSeconds(10);
    // Earnings groups come from the live earnings monitor. Membership is advisory: reporting is not an entry signal.
    public static readonly IReadOnlyList<DeskSource> EarningsSources = [
        new("earnings:today", "Earnings today", "Earnings", "Reporting today, plus last session's after-close reports that trade today. Membership is not an entry signal."),
        new("earnings:today-positive", "Earnings today · positive results", "Earnings", "Today's earnings group with a positive EPS and revenue result. Membership is not an entry signal."),
        new("earnings:next", "Earnings next reporting day", "Earnings", "Companies scheduled to report on the next reporting day.")
    ];

    private readonly SemaphoreSlim screenReadGate = new(1, 1);
    public const int DisplayLimit = 50;
    public static readonly IReadOnlyList<DeskSource> BuiltIns = [
        new("swing:trend", "Established trends", "Finviz signals", "Above the 50- and 200-day averages; price above $10; average volume above 500K.",
            "ind_stocksonly,sh_avgvol_o500,sh_price_o10,ta_sma50_pa,ta_sma200_pa"),
        new("swing:highs", "New annual highs", "Finviz signals", "New 52-week highs in liquid stocks. Discovery only.",
            "ind_stocksonly,sh_avgvol_o500,sh_price_o10,ta_highlow52w_nh"),
        new("swing:pullback", "Trend pullbacks", "Finviz signals", "Below the 20-day average, above the 50- and 200-day averages. Discovery only.",
            "ind_stocksonly,sh_avgvol_o500,sh_price_o10,ta_sma20_pb,ta_sma50_pa,ta_sma200_pa")
    ];

    public async Task<IReadOnlyList<Wishlist>> FollowedListsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var revisions = (await db.PreparationReviews.Select(x => x.WishlistId).ToArrayAsync(ct)).ToHashSet();
        return (await wishlists.ListAsync(ct)).Where(x => !revisions.Contains(x.Id)).ToArray();
    }

    // Publish operator labels with an explicit optgroup label on every source while retaining persisted source keys and identities.
    public async Task<IReadOnlyList<DeskSource>> CatalogAsync(CancellationToken ct)
    {
        var lists = await FollowedListsAsync(ct);
        var saved = await presets.ListAsync(ScreenerScope.Swing, ct);
        return new[] { new DeskSource("all", "All followed stocks", "Overview", "All active stocks across your stock groups, without duplicates.") }
            .Concat(lists.Select(x => new DeskSource("wishlist:" + x.Id, x.Name, "My groups", "Stock group stocks. Select a strategy to collect and evaluate its conditions.")))
            .Concat(earnings is null ? [] : EarningsSources)
            .Concat(saved.Select(x => new DeskSource("saved:" + x.Id, x.Name, "Saved screens", "Locally saved/imported filters; not automatically synced with Finviz.", x.FilterQuery)))
            .Concat(BuiltIns)
            .Concat(TradingFlow.Finviz.FinvizSignalCatalog.All.Select(x => new DeskSource("signal:" + x.Code, x.Label, "Finviz signals", "Finviz discovery only; normal strategy admission is still required.", x.Query)))
            .ToArray();
    }

    /// <summary>
    /// Offers real operator wishlists and saved Finviz filters to both research clients, excluding diagnostic and built-in
    /// screens, plus published research screens as a third group. Research screens are offered here only, never on the desk.
    /// </summary>
    public async Task<IReadOnlyList<TradingFlow.Contracts.V1.BacktestStockSource>> BacktestChoicesAsync(CancellationToken token) =>
        (await CatalogAsync(token)).Where(source => source.Key.StartsWith("wishlist:", StringComparison.Ordinal) ||
                source.Key.StartsWith("saved:", StringComparison.Ordinal))
            .Select(source => source.Key.StartsWith("wishlist:", StringComparison.Ordinal)
                ? new TradingFlow.Contracts.V1.BacktestStockSource(source.Key, source.Name, "wishlist", Guid.Parse(source.Key[9..]), null)
                : new TradingFlow.Contracts.V1.BacktestStockSource(source.Key, source.Name, "finviz", null, source.Query))
            .Concat((researchScreens?.List() ?? []).Select(name => new TradingFlow.Contracts.V1.BacktestStockSource(
                ResearchScreenSources.KeyPrefix + name, name, TradingFlow.Domain.Discovery.DiscoverySourceKinds.ResearchScreen, null, null)))
            .ToArray();

    public static IReadOnlyList<DeskMembership> Merge(IEnumerable<Wishlist> lists) => lists
        .SelectMany(list => list.Items.Where(item => item.Active && !String.IsNullOrWhiteSpace(item.Ticker))
            .Select(item => (List: list, Item: item, Symbol: item.Ticker.Trim().ToUpperInvariant())))
        .GroupBy(x => x.Symbol, StringComparer.Ordinal)
        .OrderBy(x => x.Key, StringComparer.Ordinal)
        .Select(group => new DeskMembership(group.Key, group.Select(x => x.Item.DisplayName).FirstOrDefault(x => !String.IsNullOrWhiteSpace(x)),
            group.Select(x => new DeskWishlist(x.List.Id, x.List.Name, x.List.IsObserved)).DistinctBy(x => x.Id).OrderBy(x => x.Name).ToArray()))
        .ToArray();

    public async Task<DeskSelection> ResolveAsync(string key, CancellationToken ct, int page = 0, string search = "")
    {
        var source = (await CatalogAsync(ct)).SingleOrDefault(x => x.Key == key)
            ?? throw new ArgumentException("This source no longer exists. Select another source.");
        var lists = await FollowedListsAsync(ct);
        var all = Merge(lists);
        if (key.StartsWith("earnings:", StringComparison.Ordinal))
        {
            var reporting = await EarningsMembersAsync(key, all, ct);
            return Window(source, reporting, reporting.Count, null, null, page, search);
        }
        if (source.Query is null)
        {
            var members = key == "all" ? all : Merge(lists.Where(x => "wishlist:" + x.Id == key));
            return Window(source, members, members.Count, null, null, page, search);
        }
        // Snapshot membership changes at most once a minute; quotes remain independent.
        var screen = await ReadScreenAsync(source.Query, ct);
        var bySymbol = all.ToDictionary(x => x.Symbol, StringComparer.OrdinalIgnoreCase);
        var membersForScreen = (screen!.AllSymbols.Count > 0 ? screen.AllSymbols : screen.Symbols)
            .Select(x => bySymbol.GetValueOrDefault(x) ?? new DeskMembership(x, null, [])).ToArray();
        return Window(source, membersForScreen, screen.TotalCount, screen.SyncedAtUtc,
            screen.Error is null ? null : "Finviz is unavailable. Check the provider connection or saved filters, then retry.", page, search);
    }

    // Display a verified owner's frozen symbols. Only independent followed groups count as membership for orders.
    public async Task<DeskSelection> ResolvePreparedAsync(TradingFlow.Contracts.Preparation.PreparationHandoff review,
        CancellationToken token, int page, string search)
    {
        var source = new DeskSource("prepared", "Reviewed preparation", "Prepared",
            $"Saved revision {review.ParentRevisionId:D} · evidence valid until {review.ExpiresAtUtc:yyyy-MM-dd HH:mm:ss} UTC · advisory stock list.");
        var followed = Merge(await FollowedListsAsync(token)).ToDictionary(member => member.Symbol, StringComparer.Ordinal);
        var members = review.Symbols.Select(symbol => followed.GetValueOrDefault(symbol) ??
            new DeskMembership(symbol, null, [])).ToArray();
        return Window(source, members, members.Length, null, null, page, search);
    }

    /// <summary>
    /// Today's group follows the session that trades the report: today's releases and last session's
    /// after-close releases. Each member is labelled with its release time and result.
    /// </summary>
    /// <summary>Every member of one earnings group, for screens that need the whole group rather than a page.</summary>
    public async Task<IReadOnlyList<DeskMembership>> EarningsGroupAsync(string key, CancellationToken ct) =>
        await EarningsMembersAsync(key, Merge(await FollowedListsAsync(ct)), ct);

    private async Task<IReadOnlyList<DeskMembership>> EarningsMembersAsync(string key, IReadOnlyList<DeskMembership> followed, CancellationToken ct)
    {
        var today = EarningsCalendarDates.ExchangeDate(DateTimeOffset.UtcNow);
        var next = EarningsCalendarDates.NextWeekday(today);
        var snapshot = await earnings!.GetCalendarAsync(EarningsCalendarDates.PreviousWeekday(today), next, ct);
        return EarningsMembers(snapshot.Items, key, today, followed);
    }

    public static IReadOnlyList<DeskMembership> EarningsMembers(IEnumerable<EarningsCalendarSnapshotItem> items, string key,
        DateOnly today, IReadOnlyList<DeskMembership> followed)
    {
        var next = EarningsCalendarDates.NextWeekday(today);
        var bySymbol = followed.ToDictionary(member => member.Symbol, StringComparer.OrdinalIgnoreCase);
        return items
            .Where(item => key == "earnings:next"
                ? item.Event.ReportDateExchange == next
                : EarningsCalendarDates.IsTradingSession(item.Event.ReportDateExchange, item.Event.ReleaseWindow, today))
            .Where(item => key != "earnings:today-positive" || item.Analysis?.ResultAssessment == EarningsResultAssessment.Positive)
            .GroupBy(item => item.Event.Ticker.ToUpperInvariant())
            .Select(group => group.OrderByDescending(item => item.Event.ScheduledAtUtc).First())
            .OrderBy(item => item.Event.ScheduledAtUtc).ThenBy(item => item.Event.Ticker, StringComparer.Ordinal)
            .Select(item => new DeskMembership(item.Event.Ticker.ToUpperInvariant(), EarningsLabel(item),
                bySymbol.GetValueOrDefault(item.Event.Ticker)?.Wishlists ?? []))
            .ToArray();
    }

    private static string EarningsLabel(EarningsCalendarSnapshotItem item)
    {
        var window = item.Event.ReleaseWindow switch
        {
            EarningsReleaseWindow.BeforeMarketOpen => "before open",
            EarningsReleaseWindow.AfterMarketClose => "after close",
            EarningsReleaseWindow.DuringMarket => "during market",
            _ => "time unconfirmed"
        };
        var result = item.Analysis?.ResultAssessment switch
        {
            EarningsResultAssessment.Positive => "positive",
            EarningsResultAssessment.Mixed => "mixed",
            EarningsResultAssessment.Negative => "negative",
            _ => "result pending"
        };
        return $"{item.Event.CompanyName} · {window} {item.Event.ReportDateExchange:d MMM} · {result}";
    }

    /// <summary>One bounded provider observation shared by desk and news, including full returned membership.</summary>
    public async Task<ScreenerSyncResult> ReadScreenAsync(string query, CancellationToken ct)
    {
        var key = "desk-screen:" + query;
        if (cache.TryGetValue<ScreenerSyncResult>(key, out var cached) && cached is not null) return cached;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var entered = false;
        try
        {
            await screenReadGate.WaitAsync(deadline.Token);
            entered = true;
            if (cache.TryGetValue<ScreenerSyncResult>(key, out cached) && cached is not null) return cached;
            var result = await screener.PreviewAsync(query, ScreenerScope.Swing, null, deadline.Token);
            cache.Set(key, result, result.Succeeded ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(5));
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var failed = ScreenerSyncResult.Failed(ScreenerScope.Swing, "Finviz", query, "Finviz observation timed out. Retry the selected source.");
            if (entered) cache.Set(key, failed, TimeSpan.FromSeconds(5));
            return failed;
        }
        finally { if (entered) screenReadGate.Release(); }
    }

    public async Task FollowAsync(string source, string symbol, Guid wishlistId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        var selection = await ResolveAsync(source, ct, search: symbol);
        var target = (await FollowedListsAsync(ct)).SingleOrDefault(x => x.Id == wishlistId);
        if (target is null || !selection.Members.Any(x => x.Symbol == symbol))
            throw new ArgumentException("Stock or wishlist is no longer available in this view. Refresh and retry.");
        var existing = target.Items.SingleOrDefault(x => x.Ticker.Equals(symbol, StringComparison.OrdinalIgnoreCase));
        if (existing?.Active == true) return; // Retrying a follow must not erase operator notes.
        await wishlists.AddOrUpdateItemAsync(wishlistId, symbol, existing?.DisplayName, existing?.Notes, ct);
    }

    /// <summary>
    /// Adds any typed symbol to one of the operator's own stock groups, whether or not the current view lists it.
    /// A symbol already active in the group is left untouched (its notes stay). Otherwise Alpaca's asset record must
    /// show an active, tradable US stock before the stock is added; unknown, inactive, non-tradable and non-stock
    /// symbols are refused with the reason. If Alpaca cannot be asked, nothing is added. Group membership is the only
    /// state changed: it neither starts a strategy nor places an order.
    /// </summary>
    public async Task<DeskStockAddResult> AddStockAsync(string? input, Guid wishlistId, CancellationToken ct)
    {
        var symbol = (input ?? "").Trim().ToUpperInvariant();
        if (!StockSymbolPattern().IsMatch(symbol))
            return new(DeskStockAddStatus.Refused, symbol, "Enter a stock symbol such as AAPL, or BRK.B for a share class: a letter, then up to 7 letters or digits.");
        var target = (await FollowedListsAsync(ct)).SingleOrDefault(list => list.Id == wishlistId);
        if (target is null)
            return new(DeskStockAddStatus.Refused, symbol, "That stock group no longer exists. Reload the Desk and choose another group.");
        var existing = target.Items.SingleOrDefault(item => item.Ticker.Equals(symbol, StringComparison.OrdinalIgnoreCase));
        if (existing?.Active == true)
            return new(DeskStockAddStatus.AlreadyInGroup, symbol, $"{symbol} is already in {target.Name}.");
        var refusal = await CheckStockAsync(symbol, ct);
        if (refusal is not null) return refusal;
        return await AddCheckedStockAsync(target, symbol, existing, ct);
    }

    // Writes the checked stock into the group. If the write fails, the group is read again: a concurrent add of the same
    // symbol (another tab or the phone) has put it there and counts as already added; any other failure (for example a
    // locked database) is reported as not saved. A group deleted or frozen meanwhile is refused.
    private async Task<DeskStockAddResult> AddCheckedStockAsync(Wishlist target, string symbol, WishlistItem? existing, CancellationToken ct)
    {
        try { await wishlists.AddOrUpdateItemAsync(target.Id, symbol, existing?.DisplayName, existing?.Notes, ct); }
        catch (DbUpdateException ex)
        {
            Wishlist? current = null;
            try { current = await wishlists.GetByIdAsync(target.Id, ct); }
            catch (Exception readFailure) when (readFailure is not OperationCanceledException)
            {
                logger?.LogWarning(readFailure, "Desk could not re-read stock group {WishlistId} after a failed save.", target.Id);
            }
            if (current?.Items.Any(item => item.Active && item.Ticker.Equals(symbol, StringComparison.OrdinalIgnoreCase)) == true)
                return new(DeskStockAddStatus.AlreadyInGroup, symbol, $"{symbol} was just added to {target.Name} by another request.");
            logger?.LogWarning(ex, "Desk could not save {Symbol} to stock group {WishlistId}.", symbol, target.Id);
            return new(DeskStockAddStatus.Unavailable, symbol, $"The database did not save {symbol} to {target.Name}; the site log has the details. Retry in a moment.");
        }
        catch (InvalidOperationException)
        {
            return new(DeskStockAddStatus.Refused, symbol, "That stock group no longer exists or can no longer change. Reload the Desk and choose another group.");
        }
        logger?.LogInformation("Desk added {Symbol} to stock group {WishlistId}.", symbol, target.Id);
        return new(DeskStockAddStatus.Added, symbol, $"Added {symbol} to {target.Name}. It appears with the next Desk update, within about 5 seconds.");
    }

    // Asks Alpaca for the symbol's asset record; returns null when it is an active, tradable US stock, else the reason.
    // Missing keys, an error answer and no answer within AssetLookupTimeout are reported separately; details stay in the log.
    private async Task<DeskStockAddResult?> CheckStockAsync(string symbol, CancellationToken ct)
    {
        if (assets is null || alpacaCredentials is { IsConfigured: false })
            return new(DeskStockAddStatus.Unavailable, symbol, "Alpaca keys are not set on this site, so the symbol cannot be checked with Alpaca. Nothing was added.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(AssetLookupTimeout);
        TradingFlow.Engine.Execution.AssetTradingEligibility? asset;
        try { asset = await assets.GetEligibilityAsync(symbol, deadline.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger?.LogWarning("Desk lookup of {Symbol} got no Alpaca answer within {Timeout}.", symbol, AssetLookupTimeout);
            return new(DeskStockAddStatus.Unavailable, symbol, $"Alpaca did not answer the lookup for {symbol} within {AssetLookupTimeout.TotalSeconds:0} seconds. Nothing was added; retry in a moment.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Desk lookup of {Symbol} failed at Alpaca.", symbol);
            return new(DeskStockAddStatus.Unavailable, symbol, DescribeLookupFailure(symbol, ex) + " Nothing was added.");
        }
        string? reason = asset switch
        {
            null => $"Alpaca has no asset with the symbol {symbol}. Check the spelling.",
            { AssetClass: not "us_equity" } => $"{symbol} is not a US stock on Alpaca (asset class {asset.AssetClass ?? "not given"}).",
            { Active: false } => $"Alpaca lists {symbol} as inactive, for example delisted or no longer trading.",
            { Tradable: false } => $"Alpaca does not allow trading {symbol}, for example an over-the-counter stock.",
            _ => null
        };
        return reason is null ? null : new(DeskStockAddStatus.Refused, symbol, reason + " Nothing was added.");
    }

    // Tells the operator which failure happened without provider details: Alpaca's error answers arrive as the
    // broker client's "failed with HTTP nnn" message; an unreachable host is an HttpRequestException.
    private static string DescribeLookupFailure(string symbol, Exception failure) =>
        failure is HttpRequestException ? $"The site could not reach Alpaca to look up {symbol} (network or DNS failure)." :
        HttpStatusPattern().Match(failure.Message) is { Success: true } status
            ? status.Groups["code"].Value is "401" or "403"
                ? $"Alpaca rejected this site's keys when looking up {symbol} (HTTP {status.Groups["code"].Value}). Check the Alpaca keys in the site settings."
                : $"Alpaca answered the lookup for {symbol} with an error (HTTP {status.Groups["code"].Value}). Retry in a moment."
            : $"The Alpaca lookup for {symbol} failed unexpectedly; the site log has the details.";

    [GeneratedRegex(@"failed with HTTP (?<code>\d{3})", RegexOptions.CultureInvariant)]
    private static partial Regex HttpStatusPattern();

    // Exchange-listed US symbols: a letter, then letters/digits, with an optional one- or two-character class suffix.
    [GeneratedRegex(@"\A[A-Z][A-Z0-9]{0,7}(?:\.[A-Z0-9]{1,2})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex StockSymbolPattern();

    public static DeskSelection Window(DeskSource source, IReadOnlyList<DeskMembership> members, int total,
        DateTimeOffset? screenedAt, string? error, int page, string search)
    {
        var term = search.Trim();
        var matches = members.Where(x => x.Symbol.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            (x.Name?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)).ToArray();
        var pages = Math.Max(1, (matches.Length + DisplayLimit - 1) / DisplayLimit);
        page = Math.Clamp(page, 0, pages - 1);
        return new(source, matches.Skip(page * DisplayLimit).Take(DisplayLimit).ToArray(), total, screenedAt, error)
            { MatchedCount = matches.Length, Page = page, PageCount = pages };
    }

    public static string ImportFilter(string input)
    {
        var query = input.Trim();
        if (Uri.TryCreate(query, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != "https" || !(uri.Host == "finviz.com" || uri.Host == "www.finviz.com" || uri.Host == "elite.finviz.com"))
                throw new ArgumentException("Use an HTTPS Finviz screener URL or its filter list.");
            query = ScreenerPresetService.SelectingParameters(uri.Query);
        }
        if (query.StartsWith("f=", StringComparison.OrdinalIgnoreCase) && !query.Contains('&')) query = query[2..];
        if (query.Length is < 1 or > 2000 || !Regex.IsMatch(query, @"\A(?:[a-zA-Z0-9_,.\-]+|s=[a-zA-Z0-9_]+|f=[a-zA-Z0-9_,.\-]+&s=[a-zA-Z0-9_]+)\z"))
            throw new ArgumentException("Paste a Finviz screener URL containing filters, or a comma-separated filter list. Credentials are not saved.");
        return query;
    }
}
