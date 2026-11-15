using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Pipeline;

namespace TradingFlow.Web.Services.Charts;

internal sealed record ChartHistoryBatch(
    ChartBarProjector Projector,
    IReadOnlyList<DateOnly> Sessions,
    IReadOnlyDictionary<DateTimeOffset, ChartSourceBar> Bars,
    IReadOnlyDictionary<DateTimeOffset, DateTimeOffset> Quiet,
    IReadOnlySet<DateTimeOffset> BasisBreaks,
    IReadOnlySet<DateOnly> AcquiredSessions,
    IReadOnlyDictionary<DateOnly, DateTimeOffset> RetryUnverifiedAfterUtc,
    IReadOnlyDictionary<DateOnly, ChartMinuteAcquisition> MinuteAcquisitions,
    DateTimeOffset? DailyReconciledAtUtc,
    DateTimeOffset? CoveredThroughUtc,
    string? Limitation,
    string? CalendarRevision = null)
{
    private readonly Lazy<string> rawRevision = new(() => ChartInputRevision.History(Sessions, Bars, Quiet, BasisBreaks));
    public string Revision => $"{CalendarRevision}:{rawRevision.Value}";
}

internal sealed record ChartMinuteAcquisition(
    DateTimeOffset? VerifiedThroughUtc, DateTimeOffset? FullReadAtUtc, bool NeedsFullRepair);

/// <summary>Acquires bounded, coalesced chart history through the existing SIP archive and shared candle store.</summary>
internal sealed class ChartHistorySource(
    Lazy<IMarketDataProvider> provider, ICandleStore candles, TimeProvider clock, TimeSpan liveSettlementDelay, ILogger logger)
{
    private const int MaximumEntries = 32;
    private const int MaximumMinutes = 16_000;
    private const int MaximumDaily = 500;
    private static readonly TimeSpan UnverifiedHistoryRetry = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FullReconciliationInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MinuteQueryOverlap = TimeSpan.FromMinutes(15);
    private readonly object gate = new();
    private readonly Dictionary<ChartHistoryKey, Entry> cache = [];
    private readonly SemaphoreSlim permits = new(2);
    private readonly SemaphoreSlim calendarGate = new(1);
    private (DateOnly Start, DateOnly End, DateTimeOffset Expires, IReadOnlyDictionary<DateOnly, MarketSessionSchedule> Calendar)? cachedCalendar;
    private sealed record Entry(Task<ChartHistoryBatch> Load, DateTimeOffset Expires, DateTimeOffset Used);

    // Share underlying daily or minute work across display intervals and contexts.
    // Pending loads own a bounded deadline independent of any one subscriber.
    public Task<ChartHistoryBatch> ReadAsync(string symbol, bool daily, DateOnly anchor, CancellationToken cancellationToken)
    {
        var key = new ChartHistoryKey(symbol, daily, anchor);
        var now = clock.GetUtcNow();
        Task<ChartHistoryBatch> load;
        lock (gate)
        {
            if (cache.TryGetValue(key, out var hit) && (!hit.Load.IsCompleted || now < hit.Expires))
            {
                cache[key] = hit with { Used = now };
                load = hit.Load;
            }
            else
            {
                var previous = hit?.Load.IsCompletedSuccessfully == true ? hit.Load.Result : MostRecentSeed(symbol, daily, anchor);
                cache.Remove(key);
                if (cache.Count >= MaximumEntries)
                {
                    var oldest = cache.Where(item => item.Value.Load.IsCompleted)
                        .OrderBy(item => item.Value.Used).FirstOrDefault();
                    if (oldest.Value is null)
                    {
                        throw new InvalidOperationException("The bounded chart history queue is full.");
                    }
                    cache.Remove(oldest.Key);
                }

                load = LoadAsync(symbol, daily, anchor, previous);
                var recent = anchor >= ChartBarProjector.NewYorkDate(now).AddDays(-1);
                var expires = now.Add(recent ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(15));
                cache.Add(key, new Entry(load, expires, now));
            }
        }

        return load.WaitAsync(cancellationToken);
    }

    // Only active source entries can own retained indicator profiles.
    public bool IsActive(ChartHistoryKey key)
    {
        lock (gate) return cache.ContainsKey(key);
    }

    // Reuse already loaded regular-session proof for RVOL without fetching or extending a history entry.
    public ChartHistoryBatch[] WarmMinuteHistory(string symbol, DateOnly first, DateOnly last)
    {
        lock (gate)
            return cache.Where(pair => pair.Key.Symbol == symbol && !pair.Key.Daily &&
                    pair.Key.Anchor >= first && pair.Value.Load.IsCompletedSuccessfully)
                .OrderByDescending(pair => pair.Key.Anchor)
                .Select(pair => pair.Value.Load.Result)
                .Where(batch => batch.Sessions.Count > 0 && batch.Sessions[0] <= last)
                .Distinct(ReferenceEqualityComparer.Instance)
                .Cast<ChartHistoryBatch>().ToArray();
    }

    // A forward calendar rollover can reuse a recent completed seed. Never seed an older page from a newer anchor.
    private ChartHistoryBatch? MostRecentSeed(string symbol, bool daily, DateOnly anchor) =>
        cache.Where(pair => pair.Key.Symbol == symbol && pair.Key.Daily == daily &&
                pair.Key.Anchor < anchor && pair.Key.Anchor >= anchor.AddDays(-7) && pair.Value.Load.IsCompletedSuccessfully)
            .OrderByDescending(pair => pair.Key.Anchor)
            .Select(pair => pair.Value.Load.Result)
            .FirstOrDefault();

    // Load one bounded history batch and retain its retrieval clock when there is no new work.
    private async Task<ChartHistoryBatch> LoadAsync(string symbol, bool daily, DateOnly anchor, ChartHistoryBatch? previous)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await permits.WaitAsync(budget.Token);
        try
        {
            var asOf = clock.GetUtcNow().ToUniversalTime();
            var calendar = await LoadCalendarAsync(anchor, asOf, budget.Token);
            var projector = new ChartBarProjector(calendar);
            var calendarRevision = ChartInputRevision.Calendar(calendar);
            var eligible = calendar.Where(pair => pair.Key <= anchor && pair.Value.IsTradingDay)
                .Select(pair => pair.Key).Order().TakeLast(daily ? MaximumDaily : 15).ToArray();
            if (eligible.Length == 0)
            {
                return new ChartHistoryBatch(projector, eligible, new Dictionary<DateTimeOffset, ChartSourceBar>(),
                    new Dictionary<DateTimeOffset, DateTimeOffset>(), new HashSet<DateTimeOffset>(), new HashSet<DateOnly>(),
                    new Dictionary<DateOnly, DateTimeOffset>(), new Dictionary<DateOnly, ChartMinuteAcquisition>(),
                    null, null, "No scheduled exchange sessions were returned for this window.", calendarRevision);
            }

            var state = new HistoryRefresh(previous, eligible[0]);
            DateOnly[] dailyDates = daily ? SelectDailyDates(projector, eligible, state, asOf) : [];
            MinuteRead[] minuteReads = daily ? [] : PlanMinuteReads(eligible, state, asOf);
            var firstRequested = daily ? dailyDates.FirstOrDefault() : minuteReads.LastOrDefault()?.Date ?? default;
            if (firstRequested == default)
            {
                if (previous is not null && previous.Sessions.SequenceEqual(eligible))
                {
                    return previous with { Projector = projector, CalendarRevision = calendarRevision };
                }

                return state.ToBatch(projector, eligible, daily, asOf, previous?.Limitation, calendarRevision);
            }

            var lastRequested = daily ? dailyDates[^1] : minuteReads[0].Date;
            await LoadStoredAsync(symbol, daily, firstRequested, lastRequested, state, asOf, budget.Token);
            var limitation = daily
                ? await FetchDailyAsync(symbol, projector, dailyDates, state, asOf, budget.Token)
                : await FetchMinutesAsync(symbol, projector, minuteReads, state, asOf, budget.Token);
            if (!daily && limitation is null && eligible.Any(date => MinuteSessionEnd(date) + liveSettlementDelay <= asOf &&
                    !state.Acquired.Contains(date) && !state.Retry.ContainsKey(date)))
            {
                limitation = "Some scheduled SIP sessions have not completed history acquisition.";
            }
            return state.ToBatch(projector, eligible, daily, asOf, limitation, calendarRevision);
        }
        finally
        {
            permits.Release();
        }
    }

    // Load stored prices only when refreshing an eligible window. Receipts here do not establish REST pagination coverage.
    private async Task LoadStoredAsync(string symbol, bool daily, DateOnly first, DateOnly last,
        HistoryRefresh state, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        var interval = daily ? "1d" : "1m";
        var start = ChartBarProjector.At(first, TimeOnly.MinValue);
        var end = ChartBarProjector.At(last.AddDays(1), TimeOnly.MinValue);
        var stored = await candles.ReadBarsAsync(
            new CandleStoreReadRequest("market-state", "shared", "alpaca-sip", symbol, interval, start, end,
                Source: daily ? null : "stream", AsOfUtc: asOf), cancellationToken);
        var materialized = clock.GetUtcNow().ToUniversalTime();
        foreach (var bar in stored.Where(bar => Compatible(bar, symbol, interval)).TakeLast(daily ? MaximumDaily : MaximumMinutes))
        {
            var utc = bar.Timestamp.ToUniversalTime();
            if (!state.Bars.TryGetValue(utc, out var prior) || bar.KnownAtUtc is { } received && received > prior.AvailableAtUtc)
            {
                state.Bars[utc] = Source(bar, "shared_store", materialized, TimeSpan.Zero);
                state.Quiet.Remove(utc);
            }
        }
    }

    // Plan completed native daily dates, with bounded active-hours overlap reconciliation.
    private DateOnly[] SelectDailyDates(ChartBarProjector projector, DateOnly[] eligible, HistoryRefresh state, DateTimeOffset asOf)
    {
        var due = eligible.Where(date => projector.Regular(date) is { } range && range.EndUtc + liveSettlementDelay <= asOf).ToArray();
        var reconcile = projector.Segment(asOf) == "regular" &&
            (state.DailyReconciledAtUtc is not { } previous || asOf - previous >= FullReconciliationInterval);
        var newClose = due.Length > 0 && !state.Acquired.Contains(due[^1]) && CanRetry(state, due[^1], asOf);
        var recent = due.TakeLast(2).ToHashSet();
        return due.Where(date => CanRetry(state, date, asOf) &&
            (!state.Acquired.Contains(date) || (reconcile || newClose) && recent.Contains(date))).ToArray();
    }

    // Plan advancing settled-minute ranges, a final full read at close and periodic full reconciliation while active.
    private MinuteRead[] PlanMinuteReads(DateOnly[] eligible, HistoryRefresh state, DateTimeOffset asOf)
    {
        var delayed = asOf - liveSettlementDelay;
        var settled = delayed.AddTicks(-(delayed.UtcTicks % TimeSpan.TicksPerMinute));
        var pending = new List<MinuteRead>();
        foreach (var date in eligible.Reverse())
        {
            if (state.Acquired.Contains(date)) continue;
            var start = ChartBarProjector.At(date, new TimeOnly(4, 0));
            var sessionEnd = MinuteSessionEnd(date);
            var end = settled < sessionEnd ? settled : sessionEnd;
            if (start >= end) continue;

            state.MinuteAcquisitions.TryGetValue(date, out var prior);
            var final = end == sessionEnd;
            var firstFinalAttempt = final && (prior?.FullReadAtUtc is not { } lastFull || lastFull < sessionEnd + liveSettlementDelay);
            if (!firstFinalAttempt && !CanRetry(state, date, asOf)) continue;

            var full = final || prior is null || prior.NeedsFullRepair || prior.VerifiedThroughUtc is null ||
                prior.FullReadAtUtc is not { } recent || asOf - recent >= FullReconciliationInterval;
            if (!full && prior?.VerifiedThroughUtc == end) continue;

            var overlap = prior?.VerifiedThroughUtc - MinuteQueryOverlap;
            var queryStart = !full && overlap is { } from && from > start ? from : start;
            pending.Add(new MinuteRead(date, start, sessionEnd, queryStart, end));
        }
        return pending.ToArray();
    }

    // A backed-off source date may be probed again at its recorded retry boundary.
    private static bool CanRetry(HistoryRefresh state, DateOnly date, DateTimeOffset asOf) =>
        !state.Retry.TryGetValue(date, out var retryAt) || retryAt <= asOf;

    // This is the chart's approved extended-hours cutoff for scheduled trading dates.
    private static DateTimeOffset MinuteSessionEnd(DateOnly date) => ChartBarProjector.At(date, new TimeOnly(20, 0));

    private sealed record MinuteRead(DateOnly Date, DateTimeOffset SessionStart, DateTimeOffset SessionEnd,
        DateTimeOffset Start, DateTimeOffset End)
    {
        public bool IsFull => Start == SessionStart;
    }

    // Reuse an authoritative calendar across symbols, and cover prior-week holiday boundaries.
    private async Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadCalendarAsync(
        DateOnly anchor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await calendarGate.WaitAsync(cancellationToken);
        try
        {
            var start = anchor.AddDays(-780);
            var end = anchor.AddDays(12);
            if (cachedCalendar is { } existing && existing.Start <= start && existing.End >= end && existing.Expires > now)
            {
                return existing.Calendar;
            }
            if (provider.Value is not IMarketSessionScheduleProvider source)
            {
                throw new InvalidDataException("An authoritative New York exchange calendar is required for chart data.");
            }

            var bufferedStart = start.AddDays(-7);
            var bufferedEnd = end.AddDays(7);
            var loaded = await source.LoadMarketSessionSchedulesAsync(bufferedStart, bufferedEnd, cancellationToken);
            if (Enumerable.Range(bufferedStart.DayNumber, bufferedEnd.DayNumber - bufferedStart.DayNumber + 1)
                .Select(DateOnly.FromDayNumber).Any(date => !loaded.TryGetValue(date, out var day) || day.TradeDate != date))
            {
                throw new InvalidDataException("The exchange calendar does not cover every date requested for this chart.");
            }

            cachedCalendar = (bufferedStart, bufferedEnd, now.AddHours(6), loaded);
            return loaded;
        }
        finally
        {
            calendarGate.Release();
        }
    }

    // Native daily rows use one bounded query for due dates, including an overlap when the latest close arrives.
    private async Task<string?> FetchDailyAsync(string symbol, ChartBarProjector projector,
        DateOnly[] dates, HistoryRefresh state, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        var start = ChartBarProjector.At(dates[0], TimeOnly.MinValue);
        var end = ChartBarProjector.At(dates[^1].AddDays(1), TimeOnly.MinValue);
        try
        {
            var loaded = await FetchAsync(symbol, "1d", start, end < asOf ? end : asOf, MaximumDaily, cancellationToken);
            var retrieved = clock.GetUtcNow().ToUniversalTime();
            // Keep completed rows between queued repair dates in the same coherent response.
            // Dropping them would make retained candles look like unverified islands after a revision.
            var confirmed = loaded.Bars.Where(bar =>
                    projector.Regular(ChartBarProjector.NewYorkDate(bar.Timestamp)) is { } range &&
                    range.EndUtc + liveSettlementDelay <= asOf)
                .ToDictionary(bar => bar.Timestamp);
            MergeHistory(state.Bars, confirmed, retrieved, state.Breaks);
            var observed = confirmed.Keys.Select(ChartBarProjector.NewYorkDate).ToHashSet();
            foreach (var date in dates)
            {
                // A revision probe omitting a previously verified daily row cannot retract it without removal evidence.
                if (!state.Acquired.Contains(date))
                {
                    TrackAcquisition(date, observed.Contains(date), retrieved, state.Acquired, state.Retry);
                }
            }
            state.DailyReconciledAtUtc = retrieved;
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Daily chart history unavailable for {Symbol}.", symbol);
            return "The daily SIP history read did not complete. Showing retained compatible candles.";
        }
    }

    // Acquire each planned minute range atomically. Partial pagination never changes visible proof.
    private async Task<string?> FetchMinutesAsync(string symbol, ChartBarProjector projector,
        IReadOnlyList<MinuteRead> reads, HistoryRefresh state, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        foreach (var read in reads)
        {
            state.MinuteAcquisitions.TryGetValue(read.Date, out var prior);
            var fullAt = read.IsFull ? asOf : prior?.FullReadAtUtc;
            try
            {
                var expected = (int)(read.End - read.Start).TotalMinutes;
                var loaded = await FetchAsync(symbol, "1m", read.Start, read.End, expected, cancellationToken);
                var retrieved = clock.GetUtcNow().ToUniversalTime();
                var bars = loaded.Bars.Where(bar => projector.Segment(bar.Timestamp) is not null).ToDictionary(bar => bar.Timestamp);
                var responseThrough = ApplyMinuteEvidence(state, read, loaded, bars, expected, retrieved);
                MergeHistory(state.Bars, bars, retrieved, state.Breaks);
                var through = AdvanceMinuteProof(state, read, prior?.VerifiedThroughUtc, responseThrough);
                var verified = through == read.End;
                state.MinuteAcquisitions[read.Date] = new ChartMinuteAcquisition(through, fullAt, !verified);
                if (read.IsFull)
                {
                    if (read.End == read.SessionEnd)
                    {
                        TrackAcquisition(read.Date, verified, retrieved, state.Acquired, state.Retry);
                    }
                    else if (!verified)
                    {
                        state.Retry[read.Date] = retrieved + UnverifiedHistoryRetry;
                    }
                    else
                    {
                        state.Retry.Remove(read.Date);
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || cancellationToken.IsCancellationRequested)
            {
                state.MinuteAcquisitions[read.Date] = new ChartMinuteAcquisition(prior?.VerifiedThroughUtc, fullAt, true);
                if (read.End == read.SessionEnd) state.Retry.Remove(read.Date);
                logger.LogWarning(exception, "Intraday chart history unavailable for {Symbol} on {SessionDate}.", symbol, read.Date);
                return "Part of the intraday SIP history is unavailable. Gaps remain visible.";
            }
        }
        return null;
    }

    // Bound omission evidence to an archived nonempty interval and preserve clocks for unchanged candles and quiet minutes.
    private static DateTimeOffset? ApplyMinuteEvidence(HistoryRefresh state, MinuteRead read, RetrievedBars loaded,
        IReadOnlyDictionary<DateTimeOffset, OhlcvBar> bars, int expected, DateTimeOffset retrieved)
    {
        foreach (var stamp in bars.Keys) state.Quiet.Remove(stamp);
        var provesOmissions = loaded.ProvesOmissions && bars.Count > 0;
        if (!provesOmissions) return loaded.Archived && bars.Count == expected ? read.End : null;

        var through = read.End;
        for (var minute = read.Start; minute < read.End; minute = minute.AddMinutes(1))
        {
            if (bars.ContainsKey(minute)) continue;
            if (state.Bars.TryGetValue(minute, out var retained))
            {
                if (retained.SourceKind != "retrieved_sip")
                {
                    // A later omission cannot undo independently accepted stored stream evidence.
                    if (minute < through) through = minute;
                    continue;
                }
                state.Bars.Remove(minute);
            }
            state.Quiet.TryAdd(minute, retrieved);
        }
        return through;
    }

    // Full archived intervals establish their own coverage. A suffix still needs a verified overlap.
    private static DateTimeOffset? AdvanceMinuteProof(HistoryRefresh state, MinuteRead read,
        DateTimeOffset? prior, DateTimeOffset? responseThrough)
    {
        if (responseThrough is { } fullThrough && read.IsFull)
        {
            return fullThrough > read.Start ? fullThrough : null;
        }

        var firstBreak = state.Breaks.Where(time => time >= read.Start && time < read.End)
            .DefaultIfEmpty(read.End).Min();
        var nextThrough = responseThrough ?? prior;
        if (prior is not { } established || established < read.Start || nextThrough is null) return prior;
        return nextThrough <= firstBreak ? nextThrough : firstBreak;
    }

    // Back off a completed but unproved old window without treating it as closed
    // evidence. Actual transport failures can retry at the normal cache refresh.
    private static void TrackAcquisition(DateOnly date, bool covered, DateTimeOffset retrieved,
        HashSet<DateOnly> acquired, Dictionary<DateOnly, DateTimeOffset> retry)
    {
        if (covered)
        {
            acquired.Add(date);
            retry.Remove(date);
        }
        else
        {
            acquired.Remove(date);
            retry[date] = retrieved + UnverifiedHistoryRetry;
        }
    }

    // Use the adapter's archival capture mode for explicit pagination proof and
    // an enforced row ceiling. Other sources can supply prices, not omission proof.
    private async Task<RetrievedBars> FetchAsync(
        string symbol, string interval, DateTimeOffset start, DateTimeOffset end, int cap, CancellationToken cancellationToken)
    {
        var owner = provider.Value;
        var output = new Dictionary<DateTimeOffset, OhlcvBar>();
        if (owner is IHistoricalMarketDataEvidenceProvider archive)
        {
            var capture = await archive.CaptureHistoricalBarsAsync(symbol, interval, start, end, cap + 1L, cancellationToken);
            if (!capture.Evidence.PaginationCompleted || capture.Evidence.Responses.Count == 0)
            {
                throw new InvalidDataException("The chart provider could not prove complete archived pagination.");
            }

            foreach (var bar in capture.Bars)
            {
                Accept(bar);
            }

            var provesOmissions = output.Count > 0 && owner is IMarketDataCompletenessProvider
                { OmittedSubDailyIntervalsMeanNoQualifyingTrades: true } &&
                output.Values.All(bar => bar.CoverageVerifiedThroughUtc >= end);
            return new RetrievedBars(output.Values.ToArray(), true, provesOmissions);
        }

        await foreach (var bar in owner.GetBarsAsync([symbol], [interval], start, end, cancellationToken))
        {
            Accept(bar);
        }
        return new RetrievedBars(output.Values.ToArray(), false, false);

        // An inclusive endpoint may return one bar exactly at the chart's exclusive end.
        // Every candle retained in the page must be unique, compatible and within scope.
        void Accept(OhlcvBar input)
        {
            var bar = CanonicalMarketBarValidator.Normalize(input);
            if (bar.Timestamp == end)
            {
                return;
            }
            if (!Compatible(bar, symbol, interval) || bar.Timestamp < start || bar.Timestamp > end || output.Count == cap ||
                !output.TryAdd(bar.Timestamp, bar with { KnownAtUtc = null }))
            {
                throw new InvalidDataException("The chart provider returned incompatible or out-of-window candles.");
            }
        }
    }

    private sealed record RetrievedBars(IReadOnlyList<OhlcvBar> Bars, bool Archived, bool ProvesOmissions);

    // Own a single bounded refresh's mutable copy and publish it only after acquisition completes.
    private sealed class HistoryRefresh(ChartHistoryBatch? previous, DateOnly firstDate)
    {
        public Dictionary<DateTimeOffset, ChartSourceBar> Bars { get; } = previous?.Bars
            .Where(pair => ChartBarProjector.NewYorkDate(pair.Key) >= firstDate).ToDictionary() ?? [];
        public Dictionary<DateTimeOffset, DateTimeOffset> Quiet { get; } = previous?.Quiet
            .Where(pair => ChartBarProjector.NewYorkDate(pair.Key) >= firstDate).ToDictionary() ?? [];
        public HashSet<DateTimeOffset> Breaks { get; } = previous?.BasisBreaks
            .Where(stamp => ChartBarProjector.NewYorkDate(stamp) >= firstDate).ToHashSet() ?? [];
        public HashSet<DateOnly> Acquired { get; } = previous?.AcquiredSessions.Where(date => date >= firstDate).ToHashSet() ?? [];
        public Dictionary<DateOnly, DateTimeOffset> Retry { get; } = previous?.RetryUnverifiedAfterUtc
            .Where(pair => pair.Key >= firstDate).ToDictionary() ?? [];
        public Dictionary<DateOnly, ChartMinuteAcquisition> MinuteAcquisitions { get; } = previous?.MinuteAcquisitions
            .Where(pair => pair.Key >= firstDate).ToDictionary() ?? [];
        public DateTimeOffset? DailyReconciledAtUtc { get; set; } = previous?.DailyReconciledAtUtc;

        // Report the end of the first contiguous run of scheduled dates. Later verified islands do not bridge an older gap.
        public ChartHistoryBatch ToBatch(ChartBarProjector projector, DateOnly[] sessions, bool daily,
            DateTimeOffset asOf, string? limitation, string calendarRevision)
        {
            DateTimeOffset? through = null;
            foreach (var date in sessions)
            {
                if (daily)
                {
                    if (!Acquired.Contains(date) || projector.Regular(date) is not { } range || range.EndUtc > asOf ||
                        Breaks.Any(time => time > (through ?? DateTimeOffset.MinValue) && time < range.EndUtc)) break;
                    through = range.EndUtc;
                }
                else
                {
                    if (!MinuteAcquisitions.TryGetValue(date, out var prior) || prior.VerifiedThroughUtc is null) break;
                    through = prior.VerifiedThroughUtc;
                    if (!Acquired.Contains(date)) break;
                }
            }
            return new ChartHistoryBatch(projector, sessions, Bars, Quiet, Breaks, Acquired, Retry,
                MinuteAcquisitions, DailyReconciledAtUtc, through, limitation, calendarRevision);
        }
    }

    // Prefer one coherent retrieved batch. A mismatch isolates the retained and
    // replacement series instead of authorizing an overlay across their seam.
    private static void MergeHistory(Dictionary<DateTimeOffset, ChartSourceBar> history,
        IReadOnlyDictionary<DateTimeOffset, OhlcvBar> retrieved, DateTimeOffset retrievedAt, HashSet<DateTimeOffset> breaks)
    {
        if (retrieved.Count == 0)
        {
            return;
        }
        var overlapping = retrieved.Where(item => history.ContainsKey(item.Key)).ToArray();
        var provenJoin = overlapping.Length > 0 && overlapping.All(item => SamePrices(history[item.Key].Bar, item.Value));
        if (!provenJoin)
        {
            var incompatible = overlapping.Length > 0;
            var first = retrieved.Keys.Min();
            var last = retrieved.Keys.Max();
            if (history.Any(item => item.Key < first && (incompatible || item.Value.SourceKind == "shared_store")))
            {
                breaks.Add(first);
            }
            var after = history.Where(item => item.Key > last && (incompatible || item.Value.SourceKind == "shared_store"))
                .Select(item => item.Key).Order().FirstOrDefault();
            if (after != default)
            {
                breaks.Add(after);
            }

            // Mark retained islands as isolated when a compatible join cannot be proved.
            var retained = history.Keys.Where(stamp => stamp > first && stamp < last && !retrieved.ContainsKey(stamp)).ToArray();
            foreach (var stamp in retained)
            {
                breaks.Add(stamp);
            }
        }
        foreach (var (utc, bar) in retrieved)
        {
            if (!history.TryGetValue(utc, out var stored) || !SamePrices(stored.Bar, bar))
            {
                history[utc] = Source(bar, "retrieved_sip", retrievedAt, TimeSpan.Zero);
            }
        }
    }

    // Matching labels alone cannot prove a source join.
    internal static bool SamePrices(OhlcvBar left, OhlcvBar right) =>
        left.Open == right.Open && left.High == right.High && left.Low == right.Low &&
        left.Close == right.Close && left.Volume == right.Volume &&
        left.DataFeed.Equals(right.DataFeed, StringComparison.OrdinalIgnoreCase) &&
        left.AdjustmentPolicy.Equals(right.AdjustmentPolicy, StringComparison.OrdinalIgnoreCase);

    // Carry actual source receipt where recorded, and a stable reconstruction stamp otherwise.
    internal static ChartSourceBar Source(OhlcvBar bar, string kind, DateTimeOffset retrievedAt, TimeSpan closeDelay)
    {
        var retrieved = bar.KnownAtUtc is null ? retrievedAt : (DateTimeOffset?)null;
        var clock = bar.KnownAtUtc?.ToUniversalTime() ?? retrievedAt;
        var end = bar.Timestamp.ToUniversalTime() + closeDelay;
        return new ChartSourceBar(bar, kind, retrieved, clock > end ? clock : end);
    }

    // Fail closed on mixed feed/basis data even when ticker and timestamps match.
    internal static bool Compatible(OhlcvBar bar, string symbol, string interval) =>
        bar.Ticker.Equals(symbol, StringComparison.OrdinalIgnoreCase) &&
        bar.Timeframe.Equals(interval, StringComparison.OrdinalIgnoreCase) &&
        bar.DataFeed.Equals("sip", StringComparison.OrdinalIgnoreCase) &&
        bar.AdjustmentPolicy.Equals("all", StringComparison.OrdinalIgnoreCase) &&
        CanonicalMarketBarValidator.IsValid(bar, interval == "1m", out _);
}
