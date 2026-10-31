using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Backtesting;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Risk;

namespace TradingFlow.Web.Services;

/// <summary>Projects one verified SIP fill ledger into existing close valuation and monthly accounting; no fill or close is inferred.</summary>
internal static class EventMonthlyRunResults
{
    public const string ValuationBasis = "marked_to_market_session_close_equity";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private sealed record FrozenRequest(int SchemaVersion, string SourceManifestSha256,
        EventPortfolioReplayRequest Request, ExecutionEventSimulationPolicy Policy);
    private sealed record Evidence(FrozenRequest Frozen, EventPortfolioReplayOutcome Outcome, FrozenExchangeCalendar Calendar);

    /// <summary>Verifies immutable inputs and accounting once, then keeps complete earlier months if later evidence becomes unavailable.</summary>
    public static MonthlyRunResultReport Create(BacktestRunConfig config, BacktestResult result,
        AuthorizedRuntimeStrategy strategy, string cohortName, DateTimeOffset start, DateTimeOffset end)
    {
        var evidence = ReadEvidence(config, result, strategy);
        var fills = ReconcileLedger(evidence.Frozen.Request, evidence.Outcome, result, strategy);
        return ProjectMonths(config, result, strategy, cohortName, start, end, evidence, fills);
    }

    /// <summary>Checks captured request, source, calendar and outcome against their exact result-owned hashes and complete source audit.</summary>
    private static Evidence ReadEvidence(BacktestRunConfig config, BacktestResult result, AuthorizedRuntimeStrategy strategy)
    {
        var manifest = Read<EventPortfolioSourceManifest>(result, "event_source_manifest", ".event-source-manifest.json");
        var frozen = Read<FrozenRequest>(result, "event_portfolio_request", ".event-portfolio-request.json");
        var outcome = Read<EventPortfolioReplayOutcome>(result, "event_portfolio_outcome", ".event-portfolio-outcome.json");
        var audit = Read<SipPortfolioSourceAudit>(result, "event_portfolio_source_audit", ".event-portfolio-source-audit.json");
        using var receipt = OpenArtifact(result, "event_exchange_calendar_receipt", ".event-calendar.receipt.json");
        using var payload = OpenArtifact(result, "event_exchange_calendar", ".event-calendar.json");
        var calendar = FrozenExchangeCalendar.Read(receipt.Name, manifest.CalendarReceiptSha256);
        var manifestReference = result.ArtifactReferences!.Single(item => item.Kind == "event_source_manifest");
        var request = frozen.Request ?? throw new InvalidDataException("Frozen SIP request is missing.");
        if (manifest.SchemaVersion != 1 || frozen.SchemaVersion != 1 ||
            !String.Equals(config.Engine.ExecutionSourceManifestSha256, manifestReference.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !String.Equals(frozen.SourceManifestSha256, manifestReference.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SIP monthly source identity differs from the frozen profile/request hash.");
        if (request.RunName != result.RunName || request.Portfolio != config.Portfolio ||
            request.Strategies is not { Count: 1 } || request.Candidates is null || request.MarketBars is null ||
            result.StartingCapital != config.Portfolio.StartingCapital || result.StrategyResults.Count != 1 ||
            result.StrategyResults[0].StartingCapital != config.Portfolio.StartingCapital || result.StrategyResults[0].StrategyId != strategy.Definition.StrategyId)
            throw new InvalidDataException("SIP monthly request must match this run's single strategy and independent capital.");
        var canonical = new CanonicalStrategyDocument(strategy.Identity.StrategyId, strategy.Identity.SemanticVersion,
            request.Strategies[0], strategy.AdmissionProfile);
        if (EvidenceCanonicalJson.ComputeSha256(canonical) != strategy.Identity.ContentSha256)
            throw new InvalidDataException("SIP request strategy settings differ from its immutable identity.");
        var selected = config.Tickers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (request.Candidates.Any(candidate => !selected.Contains(candidate.Ticker) || candidate.StrategyId != strategy.Definition.StrategyId) ||
            request.Candidates.Select(candidate => candidate.CandidateId).Distinct(StringComparer.Ordinal).Count() != request.Candidates.Count ||
            result.TickerResults.Any(ticker => !ticker.Succeeded) || !selected.SetEquals(result.TickerResults.Select(ticker => ticker.Ticker)))
            throw new InvalidDataException("Complete candidate generation for every selected stock is required before SIP monthly performance can be published.");
        ValidatePolicy(request, frozen.Policy, manifest.Assumptions);
        ValidateSourceAudit(config, manifest, audit);
        return new(frozen, outcome, calendar);
    }

    /// <summary>Requires the same configured costs and explicitly supported retrospective zero-delay raw-unit assumptions as execution.</summary>
    private static void ValidatePolicy(EventPortfolioReplayRequest request, ExecutionEventSimulationPolicy policy,
        EventPortfolioExecutionAssumptions assumptions)
    {
        var portfolio = request.Portfolio;
        var costs = new ExecutionSimulationPolicy(portfolio.MaxBarParticipationPct, request.Strategies[0].Execution.SlippageBps,
            0, portfolio.ImpactBpsAtMaxParticipation, portfolio.FixedBuyFee, portfolio.FixedSellFee, portfolio.SecFeeRate,
            portfolio.FinraTafPerShare, portfolio.FinraTafCap, MaximumTrackedOrderContracts: portfolio.MaxExecutionOrderHistory);
        if (policy is null || assumptions is null || policy.Costs != costs ||
            policy.ProtectionActivation != (request.Strategies[0].EntryRules.IntradayZone is null
                ? ExecutionProtectionActivation.NativeParentFullyFilled : ExecutionProtectionActivation.CustomPerFill) ||
            policy.FillReportLatencyNanoseconds != 0 || !policy.RawPriceAndQuantityBasisVerified ||
            policy.PriceFactorToRaw != 1 || policy.QuantityFactorToRaw != 1 || policy.MaximumQuoteAgeNanoseconds <= 0 ||
            policy.ProtectionActivation != assumptions.ProtectionActivation || policy.MaximumQuoteAgeNanoseconds != assumptions.MaximumQuoteAgeNanoseconds ||
            policy.MaximumEntrySpreadBps != assumptions.MaxEntrySpreadBps ||
            policy.FillReportLatencyNanoseconds != assumptions.FillReportLatencyNanoseconds ||
            policy.RawPriceAndQuantityBasisVerified != assumptions.RawPriceAndQuantityBasisVerified ||
            policy.PriceFactorToRaw != assumptions.PriceFactorToRaw || policy.QuantityFactorToRaw != assumptions.QuantityFactorToRaw ||
            policy.EvidenceVersion != assumptions.EvidenceVersion)
            throw new InvalidDataException("SIP monthly execution costs or raw-unit/zero-delay assumptions differ from the recorded run.");
    }

    /// <summary>Uses the completed physical audit without reparsing raw market archives or claiming historical receipt evidence.</summary>
    private static void ValidateSourceAudit(BacktestRunConfig config, EventPortfolioSourceManifest manifest, SipPortfolioSourceAudit audit)
    {
        if (audit.Version != SipPortfolioExecutionSource.Version || !audit.FullyConsumedAndVerified || audit.Sessions.IsDefaultOrEmpty || manifest.SourcePolicy is null ||
            manifest.SourcePolicy.SourceAvailabilityDelayNanoseconds != 0 ||
            manifest.SourcePolicy.KnownAtBasis != SipPortfolioKnownAtBasis.RetrospectiveSourceTimePlusDeclaredDelay ||
            audit.PolicySha256 != SipPortfolioExecutionSource.ComputePolicySha256(manifest.SourcePolicy) ||
            audit.ConditionContractSha256 != SipPortfolioExecutionSource.ComputeConditionContractSha256(manifest.SourcePolicy.Conditions) ||
            audit.QuoteRows != audit.Sessions.Sum(session => session.PrefixQuotes.Rows + session.Quotes.Rows) ||
            audit.TradeRows != audit.Sessions.Sum(session => session.Trades.Rows))
            throw new InvalidDataException("SIP source audit is incomplete or differs from its frozen source policy and physical row counts.");
        var selected = config.Tickers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sessions = manifest.Sessions.Where(session => selected.Contains(session.RegularQuotesRequest.Symbol)).ToDictionary(session => session.SessionKey);
        if (sessions.Count != audit.Sessions.Length) throw new InvalidDataException("SIP source audit does not cover this cohort's declared archives.");
        foreach (var observed in audit.Sessions)
        {
            if (!sessions.TryGetValue(observed.SessionKey, out var expected) || observed.RegularQuotesRequest != expected.RegularQuotesRequest ||
                observed.PrefixQuotesRequest != expected.PrefixQuotesRequest ||
                observed.Quotes.ManifestSha256 != expected.QuotesManifestSha256 || observed.Trades.ManifestSha256 != expected.TradesManifestSha256 ||
                observed.PrefixQuotes.ManifestSha256 != expected.PrefixQuotesManifestSha256 ||
                observed.Quotes.Directory != expected.QuotesDirectory || observed.Trades.Directory != expected.TradesDirectory ||
                observed.PrefixQuotes.Directory != expected.PrefixQuotesDirectory)
                throw new InvalidDataException("SIP source audit contains substituted session/archive bindings.");
        }
    }

    /// <summary>Reconciles all entry/exit quantities, prices and fees to closed trades or explicitly reported open failures.</summary>
    private static IReadOnlyList<PortfolioValuationFill> ReconcileLedger(EventPortfolioReplayRequest request,
        EventPortfolioReplayOutcome outcome, BacktestResult result, AuthorizedRuntimeStrategy strategy)
    {
        if (outcome.Events is null || outcome.Trades is null || outcome.Failures is null || outcome.UnavailableReasons is null ||
            ((outcome.Failures.Count > 0 || outcome.UnavailableReasons.Count > 0) && outcome.UnavailableFromNanoseconds is null) ||
            (!result.EconomicResultsComplete && outcome.UnavailableFromNanoseconds is null))
            throw new InvalidDataException("Incomplete SIP execution requires its first unavailable timestamp; monthly returns cannot assume an earlier complete period.");
        var owners = new Dictionary<string, BacktestCandidateTrade>(StringComparer.Ordinal);
        foreach (var candidate in request.Candidates)
        foreach (var suffix in new[] { ":entry", ":stop", ":target", ":liquidation" }) owners.Add(candidate.CandidateId + suffix, candidate);
        var byCandidate = new Dictionary<string, List<TimedSimulatedExecutionEvent>>(StringComparer.Ordinal);
        var fills = new List<PortfolioValuationFill>();
        foreach (var observation in outcome.Events.OrderBy(item => item.OccurredAtNanoseconds))
        {
            var fill = observation.Event;
            if (fill.LastFillQuantity == 0)
            {
                if (fill.Fees != 0) throw new InvalidDataException("SIP ledger contains an unallocated non-fill fee.");
                continue;
            }
            if (fill.LastFillQuantity < 0 || fill.FillPrice is not > 0 || fill.Fees < 0 || !Enum.IsDefined(fill.Side) ||
                observation.KnownAtNanoseconds != observation.OccurredAtNanoseconds ||
                fill.OccurredAtUtc != ExecutionEventTime.ToUtc(observation.OccurredAtNanoseconds) ||
                !TryResolveFillOwner(owners, fill.ClientOrderId, out var candidate) || fill.Symbol != candidate.Ticker ||
                observation.OccurredAtNanoseconds <= Nanoseconds(candidate.EntryTimestamp) ||
                (fill.ClientOrderId.EndsWith(":entry", StringComparison.Ordinal) != (fill.Side == (candidate.Direction == "long" ? SimulatedOrderSide.Buy : SimulatedOrderSide.Sell))) ||
                fill.IsRiskReducing == fill.ClientOrderId.EndsWith(":entry", StringComparison.Ordinal))
                throw new InvalidDataException("SIP fill lacks its exact candidate, positive raw quantity/price, fee or supported receipt time.");
            if (!byCandidate.TryGetValue(candidate.CandidateId, out var records)) byCandidate.Add(candidate.CandidateId, records = []);
            records.Add(observation);
            fills.Add(new(fill.Symbol, fill.OccurredAtUtc, fill.Side == SimulatedOrderSide.Buy, fill.LastFillQuantity, fill.FillPrice.Value, fill.Fees));
        }
        if (outcome.Trades.Select(trade => trade.CandidateId).Distinct(StringComparer.Ordinal).Count() != outcome.Trades.Count)
            throw new InvalidDataException("SIP outcome contains duplicate closed-candidate records.");
        foreach (var trade in outcome.Trades)
        {
            if (!byCandidate.TryGetValue(trade.CandidateId, out var records) || trade.StrategyId != strategy.Definition.StrategyId)
                throw new InvalidDataException("SIP closed trade has no matching fill ledger or exact strategy.");
            ReconcileClosedTrade(trade, records);
        }
        var completedIds = outcome.Trades.Select(trade => trade.CandidateId).ToHashSet(StringComparer.Ordinal);
        foreach (var pair in byCandidate.Where(pair => !completedIds.Contains(pair.Key)))
        {
            var failure = outcome.Failures.SingleOrDefault(item => item.CandidateId == pair.Key)
                ?? throw new InvalidDataException("SIP fills are absent from both closed trades and reported open exposure.");
            var owner = request.Candidates.Single(candidate => candidate.CandidateId == pair.Key);
            var buys = pair.Value.Where(item => !item.Event.IsRiskReducing).ToArray();
            var sells = pair.Value.Where(item => item.Event.IsRiskReducing).ToArray();
            if (failure.EntryFilledQuantity != buys.Sum(item => item.Event.LastFillQuantity) ||
                failure.ExitFilledQuantity != sells.Sum(item => item.Event.LastFillQuantity) ||
                failure.OpenSignedQuantity != (owner.Direction == "long" ? 1 : -1) * (failure.EntryFilledQuantity - failure.ExitFilledQuantity) ||
                failure.EntryFilledNotional != Notional(buys) || failure.ExitFilledNotional != Notional(sells) ||
                failure.Fees != pair.Value.Sum(item => item.Event.Fees))
                throw new InvalidDataException("SIP open exposure or fees do not reconcile with its physical fill ledger.");
        }
        if (result.EconomicResultsComplete && (outcome.UnavailableFromNanoseconds is not null ||
            result.AcceptedTradeCount != outcome.Trades.Count || result.CompletedTrades.Count != outcome.Trades.Count ||
            outcome.Trades.Any(trade => !result.CompletedTrades.Contains(trade)) ||
            Decimal.Round(result.StartingCapital + outcome.Trades.Sum(trade => trade.NetProfit), 4) != result.EndingCapital))
            throw new InvalidDataException("Published SIP result totals or closed trades differ from the event outcome.");
        return fills;
    }

    /// <summary>Recognizes exact parent orders and positive numbered zone-protection revisions without accepting arbitrary candidate prefixes.</summary>
    private static bool TryResolveFillOwner(IReadOnlyDictionary<string, BacktestCandidateTrade> owners,
        string orderId, out BacktestCandidateTrade candidate)
    {
        if (owners.TryGetValue(orderId, out candidate!)) return true;
        var separator = orderId.LastIndexOf(':');
        if (separator > 0 && int.TryParse(orderId.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var revision) &&
            revision > 0)
        {
            var baseId = orderId[..separator];
            if ((baseId.EndsWith(":stop", StringComparison.Ordinal) || baseId.EndsWith(":target", StringComparison.Ordinal)) &&
                owners.TryGetValue(baseId, out candidate!) && candidate.ZoneExecution is not null) return true;
        }
        candidate = null!;
        return false;
    }

    /// <summary>Requires the reported closed-parent record to equal its actual buy/sell ledger, including fees and clocks.</summary>
    private static void ReconcileClosedTrade(BacktestTrade trade, IReadOnlyList<TimedSimulatedExecutionEvent> records)
    {
        var buys = records.Where(item => !item.Event.IsRiskReducing).ToArray();
        var sells = records.Where(item => item.Event.IsRiskReducing).ToArray();
        var quantity = buys.Sum(item => item.Event.LastFillQuantity);
        var gross = (Notional(sells) - Notional(buys)) * (trade.Direction == "long" ? 1 : -1);
        var fees = records.Sum(item => item.Event.Fees);
        if (quantity <= 0 || quantity != sells.Sum(item => item.Event.LastFillQuantity) || trade.ShareQuantity != quantity ||
            trade.EntryPrice != Notional(buys) / quantity || trade.ExitPrice != Notional(sells) / quantity ||
            trade.GrossProfit != gross || trade.Fees != fees || trade.NetProfit != gross - fees ||
            trade.EntryTimestamp != ExecutionEventTime.ToUtc(buys.Min(item => item.OccurredAtNanoseconds)) ||
            trade.ExitTimestamp != ExecutionEventTime.ToUtc(sells.Max(item => item.OccurredAtNanoseconds)))
            throw new InvalidDataException("SIP closed-trade quantity, prices, fees, profit or timestamps do not reconcile with its fill ledger.");
    }

    /// <summary>Values captured session closes with the shared accountant, withholding the first affected month and all later months.</summary>
    private static MonthlyRunResultReport ProjectMonths(BacktestRunConfig config, BacktestResult result,
        AuthorizedRuntimeStrategy strategy, string cohort, DateTimeOffset start, DateTimeOffset end,
        Evidence evidence, IReadOnlyList<PortfolioValuationFill> fills)
    {
        var identity = new StrategyReference(strategy.Identity.StrategyId, strategy.Identity.SemanticVersion, strategy.Identity.ContentSha256);
        var first = LocalDate(start); var last = LocalDate(end.AddTicks(-1));
        if (first < evidence.Calendar.From || last > evidence.Calendar.Through || fills.Any(fill => fill.Timestamp < start || fill.Timestamp >= end))
            throw new InvalidDataException("Captured calendar/fills do not match the complete requested monthly period.");
        var sessions = evidence.Calendar.Sessions.Where(session => session.CloseUtc >= start && session.CloseUtc < end).ToArray();
        var closeTimes = sessions.Select(session => session.CloseUtc).ToHashSet();
        var marks = evidence.Frozen.Request.MarketBars.Where(bar => bar.Timeframe == "5m" && bar.DataFeed == "sip" &&
            bar.AdjustmentPolicy == "raw" && bar.Close > 0 && bar.Timestamp.Offset == TimeSpan.Zero)
            .Where(bar => closeTimes.Contains(bar.Timestamp.AddMinutes(5)))
            .Select(bar => new PortfolioCloseMark(bar.Ticker, bar.Timestamp.AddMinutes(5), bar.Close)).ToArray();
        var rows = new List<MonthlyRunResult>();
        var prefix = new List<FrozenExchangeSession>();
        var opening = config.Portfolio.StartingCapital;
        string? unavailable = null;
        var boundaryMonth = evidence.Outcome.UnavailableFromNanoseconds is { } boundary
            ? LocalDate(ExecutionEventTime.ToUtc(boundary)) : (DateOnly?)null;
        for (var month = new DateOnly(first.Year, first.Month, 1); month <= last; month = month.AddMonths(1))
        {
            var periodEnd = month.AddMonths(1).AddDays(-1) < last ? month.AddMonths(1).AddDays(-1) : last;
            if (boundaryMonth is { } date && date < month.AddMonths(1))
                unavailable ??= "SIP execution becomes unavailable at " + ExecutionEventTime.ToUtc(evidence.Outcome.UnavailableFromNanoseconds!.Value).ToString("O", CultureInfo.InvariantCulture) +
                    "; this month and later months are withheld. " + String.Join(" ", evidence.Outcome.UnavailableReasons);
            var monthSessions = sessions.Where(session => session.Date >= month && session.Date <= periodEnd).ToArray();
            if (monthSessions.Length == 0) unavailable ??= "No captured regular-session close is available in this requested month.";
            if (monthSessions.Length > 0 && fills.Any(fill => fill.Timestamp > monthSessions[^1].CloseUtc && LocalDate(fill.Timestamp) <= periodEnd))
                unavailable ??= "A fill occurs after the last captured regular-session valuation close at " +
                    monthSessions[^1].CloseUtc.ToString("O", CultureInfo.InvariantCulture) +
                    "; this month's equity and trade count are withheld because its final fills and fees are not covered by a valuation close.";
            if (unavailable is null)
            {
                prefix.AddRange(monthSessions);
                try
                {
                    var grid = prefix.Select(session => session.CloseUtc).ToArray();
                    var gridSet = grid.ToHashSet();
                    var valued = PortfolioCloseValuation.Analyze(config.Portfolio.StartingCapital,
                        fills.Where(fill => fill.Timestamp <= grid[^1]).ToArray(), marks.Where(mark => gridSet.Contains(mark.Timestamp)).ToArray(), grid, true);
                    var byTime = valued.Observations.ToDictionary(observation => observation.Timestamp);
                    var points = prefix.Select(session => new StrategySessionEquity(session.Date, byTime[session.CloseUtc].Equity)).ToArray();
                    var performance = MonthlyStrategyPerformance.Calculate(identity.StrategyId, identity.SemanticVersion, identity.ContentSha256,
                        result.RunName, "independent_strategy_cohort", ValuationBasis, config.Portfolio.StartingCapital,
                        first, periodEnd, points, prefix.Select(session => session.Date).ToArray()).Last();
                    var closing = valued.Observations[^1].Equity;
                    rows.Add(new(identity, cohort, performance.Month, opening, closing, performance.ReturnPct,
                        evidence.Outcome.Trades.Count(trade => LocalDate(trade.ExitTimestamp).ToString("yyyy-MM", CultureInfo.InvariantCulture) == performance.Month),
                        ValuationBasis, performance.PartialPeriod, null, performance.CloseDrawdownPct, strategy.Definition.StrategyId, strategy.Definition.StrategyName));
                    opening = closing;
                    continue;
                }
                catch (Exception error) when (error is InvalidOperationException or ArgumentException or OverflowException)
                { unavailable = error.Message; }
            }
            rows.Add(new(identity, cohort, month.ToString("yyyy-MM", CultureInfo.InvariantCulture), null, null, null, null,
                ValuationBasis, first > month || periodEnd < month.AddMonths(1).AddDays(-1), unavailable,
                StrategyAlias: strategy.Definition.StrategyId, StrategyName: strategy.Definition.StrategyName));
        }
        return new(rows, unavailable);
    }

    /// <summary>Reads the artifact under one verified lease so parsing cannot race an ordinary file replacement.</summary>
    private static T Read<T>(BacktestResult result, string kind, string extension)
    {
        using var stream = OpenArtifact(result, kind, extension);
        return JsonSerializer.Deserialize<T>(stream, Json) ?? throw new InvalidDataException($"SIP monthly artifact '{kind}' is empty.");
    }

    /// <summary>Requires one complete exact sibling artifact and verifies its declared length and hash before reading.</summary>
    private static FileStream OpenArtifact(BacktestResult result, string kind, string extension)
    {
        var reference = result.ArtifactReferences?.SingleOrDefault(item => item.Kind == kind)
            ?? throw new InvalidDataException($"SIP monthly results require the hashed '{kind}' artifact; it was not published for this run.");
        var owner = Path.GetDirectoryName(Path.GetFullPath(result.ResultPath))!;
        var path = Path.GetFullPath(Path.Combine(owner, reference.Path));
        var expected = Path.GetFullPath(Path.ChangeExtension(result.ResultPath, extension));
        if (reference.SchemaVersion != 1 || !reference.Complete ||
            (kind != "event_exchange_calendar" && reference.RecordCount != 1) || !String.Equals(path, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SIP monthly artifact '{kind}' has an unsupported or substituted result path.");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length != reference.ByteLength ||
                !Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(reference.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SIP monthly artifact '{kind}' differs from its published hash or length.");
            stream.Position = 0;
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    /// <summary>Sums actual filled notional without approximating prices or inventing absent executions.</summary>
    private static decimal Notional(IEnumerable<TimedSimulatedExecutionEvent> rows) =>
        rows.Sum(row => row.Event.LastFillQuantity * row.Event.FillPrice!.Value);

    /// <summary>Uses the exchange date only for monthly attribution; nanoseconds remain the source execution ordering.</summary>
    private static DateOnly LocalDate(DateTimeOffset time) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time, NewYork).DateTime);

    /// <summary>Preserves exact UTC timestamp units when comparing legacy candidate clocks with event nanoseconds.</summary>
    private static long Nanoseconds(DateTimeOffset time) => checked((time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100);
}
