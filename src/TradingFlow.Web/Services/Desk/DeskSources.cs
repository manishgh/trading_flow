using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web.Services.Desk;

public sealed record DeskSource(string Key, string Name, string Group, string Description, string? Query = null);
public sealed record DeskMembership(string Symbol, string? Name, IReadOnlyList<DeskWishlist> Wishlists);
public sealed record DeskWishlist(Guid Id, string Name, bool Observed);
public sealed record DeskSelection(DeskSource Source, IReadOnlyList<DeskMembership> Members,
    int TotalCount, DateTimeOffset? ScreenedAtUtc, string? Error)
{
    public int MatchedCount { get; init; }
    public int Page { get; init; }
    public int PageCount { get; init; }
}

/// <summary>Resolves observational membership without creating universes, candidates or orders.</summary>
public sealed class DeskSources(IDbContextFactory<TradingFlowDbContext> dbFactory,
    IWishlistRepository wishlists, ScreenerPresetService presets, IScreenerSnapshotSource screener,
    IMemoryCache cache, IDeskAccountReader? account = null)
{
    private readonly SemaphoreSlim screenReadGate = new(1, 1);
    public const int DisplayLimit = 50;
    public static readonly IReadOnlyList<DeskSource> BuiltIns = [
        new("swing:trend", "Established trends", "TradingFlow swing screens", "Above the 50- and 200-day averages; price above $10; average volume above 500K.",
            "ind_stocksonly,sh_avgvol_o500,sh_price_o10,ta_sma50_pa,ta_sma200_pa"),
        new("swing:highs", "New annual highs", "TradingFlow swing screens", "New 52-week highs in liquid stocks. Discovery only.",
            "ind_stocksonly,sh_avgvol_o500,sh_price_o10,ta_highlow52w_nh"),
        new("swing:pullback", "Trend pullbacks", "TradingFlow swing screens", "Below the 20-day average, above the 50- and 200-day averages. Discovery only.",
            "ind_stocksonly,sh_avgvol_o500,sh_price_o10,ta_sma20_pb,ta_sma50_pa,ta_sma200_pa")
    ];

    public async Task<IReadOnlyList<Wishlist>> FollowedListsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var revisions = (await db.PreparationReviews.Select(x => x.WishlistId).ToArrayAsync(ct)).ToHashSet();
        return (await wishlists.ListAsync(ct)).Where(x => !revisions.Contains(x.Id)).ToArray();
    }

    public async Task<IReadOnlyList<DeskSource>> CatalogAsync(CancellationToken ct)
    {
        var lists = await FollowedListsAsync(ct);
        var saved = await presets.ListAsync(ScreenerScope.Swing, ct);
        return new[] { new DeskSource("all", "All followed stocks", "Following", "All active stocks across your wishlists, without duplicates.") }
            .Concat(lists.Select(x => new DeskSource("wishlist:" + x.Id, x.Name, "Wishlists", x.IsObserved ? "Background daily wishlist alerts enabled" : "Market monitoring; background wishlist alerts off")))
            .Concat(BuiltIns)
            .Concat(TradingFlow.Finviz.FinvizSignalCatalog.All.Select(x => new DeskSource("signal:" + x.Code, x.Label, "Finviz · " + x.Group, "Finviz discovery only; normal strategy admission is still required.", x.Query)))
            .Concat(saved.Select(x => new DeskSource("saved:" + x.Id, x.Name, "Saved Elite screens", "Locally saved/imported filters; not automatically synced with Finviz.", x.FilterQuery)))
            .ToArray();
    }

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
        if (source.Query is null)
        {
            var members = key == "all" ? all : Merge(lists.Where(x => "wishlist:" + x.Id == key));
            if (key == "all" && account is not null)
            {
                var holdings = await account.ReadAsync(ct);
                if (holdings.Available)
                    members = members.Concat(holdings.Holdings.Where(x => x.Quantity != 0m)
                        .Select(x => new DeskMembership(x.Symbol, "Held position", [])))
                        .DistinctBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Symbol).ToArray();
            }
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
