using System.Security.Cryptography;
using TradingFlow.Data.Csv;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Backtesting;

public sealed partial class PreparedMarketDataService
{
    internal sealed record Query(string Id, BacktestRunConfig Run, IMarketDataProvider Provider,
        string? Ticker, string? Timeframe, DateTimeOffset Start, DateTimeOffset End,
        DateOnly? CalendarStart, DateOnly? CalendarEnd, IReadOnlyList<string> Books);

    /// <summary>Checks the full deduplicated source graph and configured allowances without reading cache selections or collecting provider data.</summary>
    public void ValidateRequest(PreparedMarketDataRequest request) => _ = BuildQueries(request);

    /// <summary>Freezes rolling dates at admission's cutoff; callers use the same dates during child execution.</summary>
    public static BacktestRunConfig ResolveRun(BacktestRunConfig run, DateTimeOffset evaluationCutoffUtc)
    {
        if (!run.TimeWindow.Type.Equals("rolling", StringComparison.OrdinalIgnoreCase)) return run;
        var end = (run.TimeWindow.End ?? evaluationCutoffUtc).ToUniversalTime();
        var start = run.TimeWindow.Start ?? end.AddDays(-run.TimeWindow.LookbackDays);
        return run with { TimeWindow = run.TimeWindow with { Type = "fixed", Start = start, End = end } };
    }

    /// <summary>Enumerates and validates every exact source query before allowing the first collection.</summary>
    private IReadOnlyList<Query> BuildQueries(PreparedMarketDataRequest request)
    {
        if (request.SchemaVersion != 1 || String.IsNullOrWhiteSpace(request.ComparisonId) ||
            request.Books is not { Count: > 0 } || request.Books.Select(book => book.BookId).Distinct(StringComparer.Ordinal).Count() != request.Books.Count)
            throw new InvalidDataException("Prepared comparison requires schema 1 and distinct, nonempty books.");
        var queries = new Dictionary<string, Query>(StringComparer.Ordinal);
        foreach (var book in request.Books)
        {
            if (String.IsNullOrWhiteSpace(book.BookId)) throw new InvalidDataException("Prepared book identity is empty.");
            var run = ResolveRun(book.Run, request.EvaluationCutoffUtc);
            if (run.Engine.MaxRetainedReplayBars <= 0 || run.TimeWindow.Start is null || run.TimeWindow.End is null ||
                !run.TimeWindow.Type.Equals("fixed", StringComparison.OrdinalIgnoreCase) || run.TimeWindow.End < run.TimeWindow.Start)
                throw new InvalidDataException("Prepared book requires ordered fixed UTC bounds and a positive replay bar allowance.");
            if (run.Universe is { IsHistoricalScreener: true, UsesFinvizScreen: true })
                throw new InvalidDataException("Resolve the Finviz candidate snapshot before admitting a prepared comparison.");
            if (providerFactory is null && run.Provider.Equals("finviz", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Finviz does not provide a raw API for Historical OHLCV Bars. Please use Alpaca instead.");
            var start = run.TimeWindow.Start.Value.AddDays(-Math.Max(0, run.TimeWindow.WarmupLookbackDays));
            var end = run.TimeWindow.End.Value;
            var provider = providerFactory?.Invoke(run) ?? dataProviders.CreateMarket(run,
                new FileSystemRawArchiveWriter(new RawArchiveOptions(run.RawRoot)), run.NormalizedRoot);
            var descriptor = (provider as IMarketDataCacheSourceDescriptorProvider)?.CacheSourceDescriptor;
            if (descriptor is null && provider is IMarketDataProvenanceProvider provenance)
                descriptor = new(provenance.MarketDataProvenance.ProviderIdentity,
                    provenance.MarketDataProvenance.DataFeed, provenance.MarketDataProvenance.AdjustmentPolicy);
            if (descriptor is null)
                throw new InvalidOperationException("Prepared data requires explicit source/feed/price-basis metadata and captured source evidence, including for frozen fixtures.");
            if (provider is not IHistoricalMarketDataEvidenceProvider)
                throw new InvalidOperationException("Prepared data requires explicit archived source/coverage evidence, including for frozen fixtures.");
            var cache = new CachedMarketDataProvider(provider, run.NormalizedRoot, run.CachePolicy, descriptor);
            var intervals = run.Intervals.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (intervals.Length == 0 || run.Tickers.Count == 0)
                throw new InvalidDataException("Prepared comparison requires stocks and download timeframes.");
            foreach (var interval in intervals)
                if (TimeframeParser.Parse(interval) <= TimeSpan.Zero)
                    throw new InvalidDataException("Prepared download timeframes must have positive durations.");
            var tickers = run.Tickers.Concat(run.Universe is { IsHistoricalScreener: true } universe ? universe.Candidates : []);
            foreach (var ticker in tickers.Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (var interval in intervals) Add(ticker, interval, start, end);
            if (run.Validation.Benchmark.Enabled && !String.IsNullOrWhiteSpace(run.Validation.Benchmark.Ticker))
                foreach (var interval in intervals) Add(run.Validation.Benchmark.Ticker, interval, start, end);
            var reader = new SimpleYamlReader();
            var strategies = book.Strategies?.ToArray() ?? run.Strategies.Select(reader.ReadStrategy).ToArray();
            foreach (var regime in strategies.Select(strategy => strategy.Regime).Where(regime => regime is { IsActive: true }))
            {
                var benchmarkStart = start.AddDays(-checked(regime!.SmaPeriod * 2 + 15));
                foreach (var interval in new[] { "1d" }.Concat(intervals.Where(interval => !TimeframeParser.IsDailyOrHigher(interval))).Distinct())
                    Add(regime.BenchmarkSymbol, interval, benchmarkStart, end);
            }
            // A strategy that exits at the official close reads each session's daily close.
            if (strategies.Any(strategy => strategy.EntryRules.OpeningRangeBreakout is { ExitAtSessionClose: true }))
                foreach (var ticker in run.Tickers.Distinct(StringComparer.OrdinalIgnoreCase)) Add(ticker, "1d", start, end);
            // A research-screen run reads the one-minute bars collected for its ranked list, which order an entry and a stop
            // inside one five-minute bar on the stock's selected sessions.
            if (run.Universe?.RankedMembership is not null)
                foreach (var ticker in run.Tickers.Distinct(StringComparer.OrdinalIgnoreCase)) Add(ticker, "1m", start, end);
            // Per-day screens (historical screener or a saved plan's daily eligibility) read daily bars from before the window.
            if (run.Universe is { IsHistoricalScreener: true } or { DailyEligibility: not null })
            {
                var screen = run.Universe;
                var screeningStart = run.TimeWindow.Start.Value.AddDays(-screen.ScreenFetchCalendarDays);
                var daily = intervals.FirstOrDefault(interval => interval.Equals("1d", StringComparison.OrdinalIgnoreCase) || interval.Equals("1day", StringComparison.OrdinalIgnoreCase)) ?? "1d";
                foreach (var ticker in tickers.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (screen.IsHistoricalScreener) Add(ticker, daily, screeningStart, run.TimeWindow.Start.Value);
                    if (screen.IsPerDayRescreen || screen.DailyEligibility is not null) Add(ticker, daily, screeningStart, end);
                    // A first-five-minute ranking reads the opening five-minute bar of the sessions before each decision day.
                    if (screen.DailyRanking is { } ranking)
                        Add(ticker, "5m", run.TimeWindow.Start.Value.AddDays(-ranking.BaselineFetchCalendarDays), end);
                }
            }
            if (provider is IMarketSessionScheduleProvider)
            {
                var zoneName = strategies.FirstOrDefault()?.Session.ExchangeTimezone ?? "America/New_York";
                var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneName);
                var intradayEnd = BacktestCalendarBounds.IntradayLastRequiredDate(run, end, zoneName);
                AddCalendar(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, zone).DateTime).AddDays(-1),
                    intradayEnd ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(end, zone).DateTime).AddDays(1));
                // Pin the same explicit calendar request used by candidate/regime evaluation for every calendar-capable source.
                AddCalendar(DateOnly.FromDateTime(start.UtcDateTime).AddDays(-1),
                    intradayEnd ?? DateOnly.FromDateTime(end.UtcDateTime).AddDays(10));
            }

            // These local builders retain each book's allowance and deduplicate only identical source requests.
            void Add(string ticker, string timeframe, DateTimeOffset from, DateTimeOffset through)
            {
                var symbol = ticker.Trim().ToUpperInvariant();
                if (symbol.Length == 0 || symbol.Any(character => !Char.IsLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
                    throw new InvalidDataException("Prepared stock symbol contains unsupported characters.");
                var tf = timeframe.Trim().ToLowerInvariant();
                var id = cache.GetPreparedBarsRequestSha256(symbol, tf, from, through);
                AddQuery(new(id, run, provider, symbol, tf, from.ToUniversalTime(), through.ToUniversalTime(), null, null, [book.BookId]));
            }

            // Calendars are exact provider requests rather than inferred weekdays or merged coverage ranges.
            void AddCalendar(DateOnly from, DateOnly through)
            {
                var id = cache.GetPreparedCalendarRequestSha256(from, through);
                AddQuery(new(id, run, provider, null, null, start, end, from, through, [book.BookId]));
            }

            // A shared query consumes source rows from every book that references it.
            void AddQuery(Query query)
            {
                queries[query.Id] = queries.TryGetValue(query.Id, out var previous)
                    ? previous with { Books = previous.Books.Append(book.BookId).Distinct(StringComparer.Ordinal).ToArray(),
                        Run = query.Run.CachePolicy.Equals("refresh", StringComparison.OrdinalIgnoreCase) ? previous.Run with { CachePolicy = "refresh" } : previous.Run }
                    : query;
            }
        }
        return queries.Values.OrderBy(query => query.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Hashes exact UTF-8 document bytes, never filesystem names or secret provider configuration.</summary>
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
