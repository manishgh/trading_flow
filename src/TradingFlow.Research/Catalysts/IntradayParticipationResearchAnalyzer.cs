using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Research.Intraday;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Market;

namespace TradingFlow.Research.Catalysts;

/// <summary>
/// Produces Track B catalyst, participation, and morphology evidence. It deliberately
/// has no order or entry-rule output: B1-B4 measure the event before B5 defines a trade.
/// </summary>
internal sealed class IntradayParticipationResearchAnalyzer
{
    private static readonly TimeSpan QuoteTolerance = TimeSpan.FromSeconds(5);
    private readonly IExchangeSessionResolver _sessions;

    public IntradayParticipationResearchAnalyzer(IExchangeSessionResolver sessions)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public IntradayEvidenceStudyReport Analyze(IntradayEvidenceStudyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var blockers = AdmissionBlockers(request);
        if (blockers.Count != 0)
        {
            return new IntradayEvidenceStudyReport(
                request.EndUtc,
                IntradayEvidenceEligibility.Rejected,
                blockers,
                [],
                []);
        }

        var classifications = request.Classifications.ToDictionary(
            ClassificationKey,
            StringComparer.Ordinal);
        var globallyClustered = BuildGlobalStoryExpansion(request, classifications);
        var observations = new List<IntradayEventEvidenceObservation>();
        foreach (var item in globallyClustered)
        {
            observations.Add(BuildObservation(request, item, globallyClustered));
        }

        var ordered = observations
            .OrderBy(value => value.AvailableAtUtc)
            .ThenBy(value => value.GlobalStoryCluster, StringComparer.Ordinal)
            .ThenBy(value => value.Ticker, StringComparer.Ordinal)
            .ToArray();
        var reportEligibility = ordered.Length == 0
            ? IntradayEvidenceEligibility.Rejected
            : ordered.All(value => value.Eligibility == IntradayEvidenceEligibility.PromotionEligible)
                ? IntradayEvidenceEligibility.PromotionEligible
                : IntradayEvidenceEligibility.DiagnosticOnly;
        var reportBlockers = ordered
            .Where(value => value.Eligibility != IntradayEvidenceEligibility.PromotionEligible)
            .Select(value => value.CensorReason)
            .Where(value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        return new IntradayEvidenceStudyReport(
            request.EndUtc,
            reportEligibility,
            reportBlockers,
            ordered,
            BuildHolmReadyPValues(ordered));
    }

    private IntradayEventEvidenceObservation BuildObservation(
        IntradayEvidenceStudyRequest request,
        StoryExpansion item,
        IReadOnlyList<StoryExpansion> allStories)
    {
        var catalyst = item.Catalyst;
        var classification = item.Classification;
        var ticker = catalyst.Ticker.ToUpperInvariant();
        var receipt = ObservedReceipt(catalyst);
        var diagnosticClock = receipt ??
            (catalyst.UpdatedAt is not null && catalyst.UpdatedAt > catalyst.Timestamp
                ? catalyst.UpdatedAt
                : catalyst.Timestamp);
        var availableAt = Later(diagnosticClock.Value, classification.InferenceCompletedAtUtc);
        var eligibility = receipt is null
            ? IntradayEvidenceEligibility.DiagnosticOnly
            : IntradayEvidenceEligibility.PromotionEligible;
        var diagnosticReason = receipt is null
            ? "provider_timestamp_only_diagnostic"
            : null;
        var partition = request.Options.Partitions.Single(value =>
            availableAt >= value.StartUtc && availableAt < value.EndUtc);
        var eventSession = _sessions.Resolve(availableAt);
        if (classification.Direction is CatalystDirection.Neutral or CatalystDirection.Ambiguous)
        {
            return RejectedObservation(
                item,
                partition.Label,
                eventSession.Label,
                availableAt,
                "classified_direction_not_tradeable",
                IntradayEvidenceEligibility.Rejected);
        }

        var bars = request.BarsByTicker[ticker]
            .OrderBy(value => value.Timestamp)
            .ToArray();
        var duration = TimeframeParser.Parse(request.CandleTimeframe);
        var anchorIndex = FindResponseAnchor(
            bars,
            duration,
            availableAt,
            eventSession);
        if (anchorIndex < 0)
        {
            return RejectedObservation(
                item,
                partition.Label,
                eventSession.Label,
                availableAt,
                "response_completed_bar_missing",
                eligibility);
        }

        var anchor = bars[anchorIndex];
        var anchorCompletedAt = anchor.Timestamp + duration;
        if (anchorCompletedAt > partition.EndUtc)
        {
            return RejectedObservation(
                item,
                partition.Label,
                eventSession.Label,
                availableAt,
                "response_crosses_partition_end",
                eligibility);
        }

        var responseSession = _sessions.Resolve(anchorCompletedAt.AddTicks(-1));
        if (responseSession.Session != EquityTradingSession.Regular)
        {
            return RejectedObservation(
                item,
                partition.Label,
                eventSession.Label,
                availableAt,
                "response_not_regular_session",
                eligibility);
        }

        var sector = request.SectorMembership.SingleOrDefault(value =>
            value.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
            value.EffectiveFromUtc <= availableAt &&
            value.EffectiveToUtc > availableAt &&
            value.ObservedAtUtc <= availableAt);
        if (sector is null)
        {
            return RejectedObservation(
                item,
                partition.Label,
                eventSession.Label,
                availableAt,
                "point_in_time_sector_evidence_missing",
                IntradayEvidenceEligibility.Rejected);
        }

        if (!request.BenchmarkBarsByTicker.TryGetValue("SPY", out var spyBars) ||
            !request.BenchmarkBarsByTicker.TryGetValue(
                sector.BenchmarkTicker,
                out var sectorBars))
        {
            return RejectedObservation(
                item,
                partition.Label,
                eventSession.Label,
                availableAt,
                "benchmark_bars_missing",
                IntradayEvidenceEligibility.Rejected);
        }

        var participation = BuildParticipation(
            bars,
            duration,
            responseSession,
            anchorIndex,
            request.Options);
        if (participation.ComparableSessionCount < request.Options.PriorSessionMinimum)
        {
            return RejectedObservation(
                item,
                partition.Label,
                eventSession.Label,
                availableAt,
                $"comparable_regular_sessions_below_minimum:{participation.ComparableSessionCount}/{request.Options.PriorSessionMinimum}",
                IntradayEvidenceEligibility.Rejected,
                anchorCompletedAt,
                responseSession.Label,
                participation);
        }

        if (!request.QuotesByTicker.TryGetValue(ticker, out var tickerQuotes))
        {
            return RejectedObservation(
                item,
                partition.Label,
                eventSession.Label,
                availableAt,
                "sip_quote_series_missing",
                IntradayEvidenceEligibility.Rejected,
                anchorCompletedAt,
                responseSession.Label,
                participation);
        }

        var entryQuote = SelectQuote(tickerQuotes, anchorCompletedAt);
        if (entryQuote is null)
        {
            return RejectedObservation(
                item,
                partition.Label,
                eventSession.Label,
                availableAt,
                "sip_entry_quote_missing_or_stale",
                IntradayEvidenceEligibility.Rejected,
                anchorCompletedAt,
                responseSession.Label,
                participation);
        }

        var nextIndependentEventAt = allStories
            .Where(value =>
                value.Catalyst.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
                !value.Classification.GlobalStoryCluster.Equals(
                    classification.GlobalStoryCluster,
                    StringComparison.Ordinal) &&
                value.Classification.Materiality != CatalystMateriality.Low)
            .Select(value => EffectiveAvailability(value))
            .Where(value => value > availableAt)
            .OrderBy(value => value)
            .Cast<DateTimeOffset?>()
            .FirstOrDefault();
        var multiplier = classification.Direction == CatalystDirection.Positive ? 1m : -1m;
        var horizons = request.Options.Horizons
            .Select(horizon => BuildHorizon(
                request,
                ticker,
                bars,
                spyBars,
                sectorBars,
                duration,
                anchorIndex,
                anchorCompletedAt,
                responseSession,
                partition.EndUtc,
                nextIndependentEventAt,
                entryQuote,
                multiplier,
                horizon))
            .ToArray();
        var morphology = BuildMorphologyTimeline(
            bars,
            duration,
            anchorIndex,
            responseSession,
            multiplier,
            request.Options.OpeningRange);

        return new IntradayEventEvidenceObservation(
            ticker,
            classification.GlobalStoryCluster,
            classification.NewsRevisionId,
            partition.Label,
            eventSession.Label,
            responseSession.Label,
            catalyst.Timestamp.ToUniversalTime(),
            receipt?.ToUniversalTime(),
            classification.InferenceCompletedAtUtc,
            availableAt,
            anchorCompletedAt,
            classification.Category,
            classification.Direction,
            classification.Materiality,
            eligibility,
            diagnosticReason,
            participation.CumulativeRvol,
            participation.ComparableSessionCount,
            participation.RobustLogVolumeZScore,
            participation.PremarketDollarVolume,
            SpreadBasisPoints(entryQuote),
            GapPercent(bars, duration, anchorIndex, responseSession),
            morphology.Current,
            morphology.Timeline,
            horizons);
    }

    private IntradayHorizonEvidence BuildHorizon(
        IntradayEvidenceStudyRequest request,
        string ticker,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<OhlcvBar> spyBars,
        IReadOnlyList<OhlcvBar> sectorBars,
        TimeSpan duration,
        int anchorIndex,
        DateTimeOffset anchorCompletedAt,
        ExchangeSessionResolution responseSession,
        DateTimeOffset partitionEndUtc,
        DateTimeOffset? nextIndependentEventAt,
        IntradayQuoteEvidence entryQuote,
        decimal direction,
        TimeSpan horizon)
    {
        var targetAt = anchorCompletedAt + horizon;
        if (targetAt > partitionEndUtc)
        {
            return CensoredHorizon(horizon, "partition_end_embargo");
        }

        if (nextIndependentEventAt is not null && targetAt >= nextIndependentEventAt)
        {
            return CensoredHorizon(horizon, "second_independent_material_event");
        }

        var targetIndex = FindCompletedTarget(
            bars,
            duration,
            anchorIndex,
            targetAt,
            responseSession);
        if (targetIndex < 0)
        {
            return CensoredHorizon(horizon, "target_completed_bar_missing");
        }

        var targetCompletedAt = bars[targetIndex].Timestamp + duration;
        var exitQuote = SelectQuote(request.QuotesByTicker[ticker], targetCompletedAt);
        if (exitQuote is null)
        {
            return CensoredHorizon(horizon, "sip_exit_quote_missing_or_stale");
        }

        var spyReturn = BenchmarkReturn(spyBars, duration, anchorCompletedAt, targetCompletedAt);
        var sectorReturn = BenchmarkReturn(
            sectorBars,
            duration,
            anchorCompletedAt,
            targetCompletedAt);
        if (spyReturn is null || sectorReturn is null)
        {
            return CensoredHorizon(horizon, "aligned_benchmark_evidence_missing");
        }

        var anchorClose = bars[anchorIndex].Close;
        var raw = direction * PercentChange(anchorClose, bars[targetIndex].Close);
        var window = bars.Skip(anchorIndex + 1).Take(targetIndex - anchorIndex).ToArray();
        var favorable = direction > 0m
            ? window.Max(value => value.High)
            : window.Min(value => value.Low);
        var adverse = direction > 0m
            ? window.Min(value => value.Low)
            : window.Max(value => value.High);
        var mfe = direction * PercentChange(anchorClose, favorable);
        var mae = direction * PercentChange(anchorClose, adverse);
        var executable = direction > 0m
            ? PercentChange(entryQuote.Ask, exitQuote.Bid)
            : PercentChange(exitQuote.Ask, entryQuote.Bid);

        return new IntradayHorizonEvidence(
            FormatHorizon(horizon),
            targetCompletedAt,
            raw,
            Decimal.Round(raw - direction * spyReturn.Value, 6),
            Decimal.Round(raw - direction * sectorReturn.Value, 6),
            executable,
            Decimal.Round(Math.Max(0m, mfe), 6),
            Decimal.Round(Math.Min(0m, mae), 6),
            false,
            null);
    }

    private ParticipationEvidence BuildParticipation(
        IReadOnlyList<OhlcvBar> bars,
        TimeSpan duration,
        ExchangeSessionResolution currentSession,
        int anchorIndex,
        IntradayEvidenceStudyOptions options)
    {
        var anchorCompletedAt = bars[anchorIndex].Timestamp + duration;
        var elapsed = anchorCompletedAt - currentSession.SessionStartUtc;
        var currentVolume = SessionVolumeToElapsed(
            bars,
            duration,
            currentSession,
            elapsed,
            requireComplete: true);
        var priorDates = bars
            .Select(value => _sessions.Resolve(value.Timestamp + duration - TimeSpan.FromTicks(1)))
            .Where(value =>
                value.Session == EquityTradingSession.Regular &&
                value.TradeDate < currentSession.TradeDate)
            .Select(value => value.TradeDate)
            .Distinct()
            .OrderByDescending(value => value)
            .Take(options.PriorSessionTarget)
            .ToArray();
        var priorVolumes = priorDates
            .Select(date => SessionForDate(bars, duration, date))
            .Where(value => value is not null)
            .Select(value => SessionVolumeToElapsed(
                bars,
                duration,
                value!,
                elapsed,
                requireComplete: true))
            .Where(value => value is not null && value > 0m)
            .Select(value => value!.Value)
            .ToArray();
        var median = Median(priorVolumes);
        decimal? rvol = currentVolume is null || median is null || median == 0m
            ? null
            : Decimal.Round(currentVolume.Value / median.Value, 6);
        var robustZ = RobustLogZ(currentVolume, priorVolumes);
        var premarketDollarVolume = bars
            .Where(value =>
            {
                var completedAt = value.Timestamp + duration;
                var session = _sessions.Resolve(completedAt - TimeSpan.FromTicks(1));
                return session.TradeDate == currentSession.TradeDate &&
                       session.Session == EquityTradingSession.Premarket &&
                       completedAt <= currentSession.SessionStartUtc;
            })
            .Sum(value => value.Close * value.Volume);

        return new ParticipationEvidence(
            rvol,
            priorVolumes.Length,
            robustZ,
            Decimal.Round(premarketDollarVolume, 2));
    }

    private decimal? SessionVolumeToElapsed(
        IReadOnlyList<OhlcvBar> bars,
        TimeSpan duration,
        ExchangeSessionResolution session,
        TimeSpan elapsed,
        bool requireComplete)
    {
        var expected = (int)Math.Ceiling(elapsed.TotalMinutes / duration.TotalMinutes);
        var sessionBars = bars
            .Where(value =>
            {
                var completedAt = value.Timestamp + duration;
                var resolved = _sessions.Resolve(completedAt - TimeSpan.FromTicks(1));
                return resolved.Session == EquityTradingSession.Regular &&
                       resolved.TradeDate == session.TradeDate &&
                       completedAt > session.SessionStartUtc &&
                       completedAt <= session.SessionStartUtc + elapsed;
            })
            .OrderBy(value => value.Timestamp)
            .ToArray();
        if (sessionBars.Length == 0 ||
            (requireComplete && sessionBars.Length != expected) ||
            sessionBars[0].Timestamp != session.SessionStartUtc)
        {
            return null;
        }

        return sessionBars.Sum(value => value.Volume);
    }

    private ExchangeSessionResolution? SessionForDate(
        IReadOnlyList<OhlcvBar> bars,
        TimeSpan duration,
        DateOnly date)
    {
        return bars
            .Select(value => _sessions.Resolve(value.Timestamp + duration - TimeSpan.FromTicks(1)))
            .FirstOrDefault(value =>
                value.TradeDate == date &&
                value.Session == EquityTradingSession.Regular);
    }

    private MorphologyEvidence BuildMorphologyTimeline(
        IReadOnlyList<OhlcvBar> bars,
        TimeSpan duration,
        int anchorIndex,
        ExchangeSessionResolution session,
        decimal direction,
        TimeSpan openingRangeDuration)
    {
        var sessionBars = bars
            .Where(value =>
            {
                var completedAt = value.Timestamp + duration;
                return completedAt > session.SessionStartUtc &&
                       completedAt <= session.SessionEndUtc;
            })
            .OrderBy(value => value.Timestamp)
            .ToArray();
        var openingBars = sessionBars
            .Where(value => value.Timestamp + duration <= session.SessionStartUtc + openingRangeDuration)
            .ToArray();
        if (openingBars.Length == 0)
        {
            return new MorphologyEvidence(IntradayMorphology.None, []);
        }

        var openingHigh = openingBars.Max(value => value.High);
        var openingLow = openingBars.Min(value => value.Low);
        var afterOpening = sessionBars
            .Where(value => value.Timestamp + duration > session.SessionStartUtc + openingRangeDuration)
            .ToArray();
        var timeline = new List<IntradayMorphologyEvidence>();
        var state = IntradayMorphology.None;
        var broke = false;
        var failed = false;
        foreach (var bar in afterOpening)
        {
            var directionalBreak = direction > 0m
                ? bar.Close > openingHigh
                : bar.Close < openingLow;
            var backInside = direction > 0m
                ? bar.Close <= openingHigh
                : bar.Close >= openingLow;
            IntradayMorphology? next = null;
            if (!broke && directionalBreak)
            {
                broke = true;
                next = IntradayMorphology.OpeningRangeContinuation;
            }
            else if (broke && !failed && backInside)
            {
                failed = true;
                next = IntradayMorphology.FailedBreak;
            }
            else if (failed && directionalBreak)
            {
                failed = false;
                next = IntradayMorphology.PullbackReclaim;
            }

            if (next is null || next == state)
            {
                continue;
            }

            state = next.Value;
            timeline.Add(new IntradayMorphologyEvidence(
                bar.Timestamp + duration,
                state,
                bar.Close,
                SessionVwap(sessionBars, bar.Timestamp + duration, duration)));
        }

        return new MorphologyEvidence(state, timeline);
    }

    private static decimal? SessionVwap(
        IReadOnlyList<OhlcvBar> sessionBars,
        DateTimeOffset completedAtUtc,
        TimeSpan duration)
    {
        var included = sessionBars
            .Where(value => value.Timestamp + duration <= completedAtUtc)
            .ToArray();
        var volume = included.Sum(value => value.Volume);
        if (volume <= 0m)
        {
            return null;
        }

        return Decimal.Round(
            included.Sum(value =>
                ((value.High + value.Low + value.Close) / 3m) * value.Volume) / volume,
            6);
    }

    private static IReadOnlyList<IntradayHolmPValue> BuildHolmReadyPValues(
        IReadOnlyList<IntradayEventEvidenceObservation> observations)
    {
        var hypotheses = observations
            .Where(value => value.Eligibility == IntradayEvidenceEligibility.PromotionEligible)
            .SelectMany(observation => observation.Horizons
                .Where(horizon =>
                    !horizon.IsCensored &&
                    horizon.SectorAbnormalReturnPct is not null)
                .Select(horizon => new
                {
                    Family = $"{observation.Partition}|{observation.Category}|{observation.Direction}",
                    Hypothesis = $"{observation.EventSession}|{observation.Morphology}|{horizon.Horizon}",
                    Cluster = $"{observation.GlobalStoryCluster}|{observation.ResponseAnchorCompletedAtUtc:yyyy-MM-dd}",
                    Value = horizon.SectorAbnormalReturnPct!.Value
                }))
            .GroupBy(value => new { value.Family, value.Hypothesis })
            .Select(group => new
            {
                group.Key.Family,
                group.Key.Hypothesis,
                Values = group
                    .GroupBy(value => value.Cluster, StringComparer.Ordinal)
                    .Select(cluster => cluster.Average(value => value.Value))
                    .ToArray()
            })
            .GroupBy(value => value.Family, StringComparer.Ordinal)
            .SelectMany(family =>
            {
                var ranked = family
                    .Select(value => new
                    {
                        value.Hypothesis,
                        value.Values,
                        P = SignTestPValue(value.Values)
                    })
                    .OrderBy(value => value.P)
                    .ThenBy(value => value.Hypothesis, StringComparer.Ordinal)
                    .ToArray();
                return ranked.Select((value, index) => new IntradayHolmPValue(
                    family.Key,
                    value.Hypothesis,
                    value.Values.Count(item => item != 0m),
                    value.P,
                    index + 1,
                    ranked.Length));
            })
            .OrderBy(value => value.Family, StringComparer.Ordinal)
            .ThenBy(value => value.HolmRank)
            .ToArray();
        return hypotheses;
    }

    private static decimal SignTestPValue(IReadOnlyList<decimal> values)
    {
        var nonZero = values.Where(value => value != 0m).ToArray();
        if (nonZero.Length == 0)
        {
            return 1m;
        }

        var successes = nonZero.Count(value => value > 0m);
        var tail = Math.Min(successes, nonZero.Length - successes);
        var probability = 0d;
        for (var value = 0; value <= tail; value++)
        {
            probability += BinomialCoefficient(nonZero.Length, value) *
                Math.Pow(0.5d, nonZero.Length);
        }

        return Decimal.Round((decimal)Math.Min(1d, probability * 2d), 8);
    }

    private static double BinomialCoefficient(int n, int k)
    {
        var result = 1d;
        for (var index = 1; index <= k; index++)
        {
            result *= (n - (k - index)) / (double)index;
        }

        return result;
    }

    private IReadOnlyList<StoryExpansion> BuildGlobalStoryExpansion(
        IntradayEvidenceStudyRequest request,
        IReadOnlyDictionary<string, ClassifiedCatalystEvidenceRow> classifications)
    {
        var expanded = request.CatalystsByTicker
            .SelectMany(pair => pair.Value.Select(catalyst =>
            {
                var availability = CatalystAvailability.Resolve(catalyst);
                classifications.TryGetValue(
                    ClassificationKey(catalyst, availability.RevisionId),
                    out var classification);
                return new
                {
                    Catalyst = catalyst,
                    Availability = availability,
                    Classification = classification
                };
            }))
            .Where(value => value.Classification is not null)
            .Select(value => new StoryExpansion(
                value.Catalyst,
                value.Availability,
                value.Classification!))
            .Where(value =>
            {
                var available = EffectiveAvailability(value);
                return available >= request.StartUtc && available < request.EndUtc;
            })
            .GroupBy(
                value => value.Classification.GlobalStoryCluster,
                StringComparer.Ordinal)
            .SelectMany(cluster => cluster
                .GroupBy(value => value.Catalyst.Ticker, StringComparer.OrdinalIgnoreCase)
                .Select(ticker => ticker
                    .OrderBy(EffectiveAvailability)
                    .ThenBy(value => value.Availability.RevisionId, StringComparer.Ordinal)
                    .First()))
            .OrderBy(EffectiveAvailability)
            .ThenBy(value => value.Classification.GlobalStoryCluster, StringComparer.Ordinal)
            .ThenBy(value => value.Catalyst.Ticker, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return expanded;
    }

    private static List<string> AdmissionBlockers(IntradayEvidenceStudyRequest request)
    {
        var blockers = new List<string>();
        if (!request.ClassifierValidation.MeetsTrackBMinimum)
        {
            blockers.Add("classifier_validation_not_ready");
        }

        if (request.Classifications.Count == 0)
        {
            blockers.Add("classified_catalyst_evidence_missing");
        }
        else
        {
            var classificationKeys = request.Classifications
                .Select(ClassificationKey)
                .ToHashSet(StringComparer.Ordinal);
            var missingClassification = request.CatalystsByTicker
                .SelectMany(value => value.Value)
                .Any(catalyst =>
                {
                    var revisionId = CatalystAvailability.Resolve(catalyst).RevisionId;
                    return !classificationKeys.Contains(
                        ClassificationKey(catalyst, revisionId));
                });
            if (missingClassification)
            {
                blockers.Add("classified_revision_missing");
            }
        }

        if (!request.BenchmarkBarsByTicker.ContainsKey("SPY"))
        {
            blockers.Add("spy_benchmark_missing");
        }

        if (request.SectorMembership.Count == 0)
        {
            blockers.Add("point_in_time_sector_evidence_missing");
        }

        if (request.QuotesByTicker.Count == 0)
        {
            blockers.Add("sip_quotes_missing");
        }

        var eventTickers = request.CatalystsByTicker
            .SelectMany(value => value.Value)
            .Select(value => value.Ticker)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (eventTickers.Any(ticker => !request.BarsByTicker.ContainsKey(ticker)))
        {
            blockers.Add("event_ticker_bars_missing");
        }

        if (eventTickers.Any(ticker => !request.QuotesByTicker.ContainsKey(ticker)))
        {
            blockers.Add("event_ticker_quotes_missing");
        }

        var lacksReceipt = request.CatalystsByTicker
            .SelectMany(value => value.Value)
            .Any(value => ObservedReceipt(value) is null);
        if (lacksReceipt && !request.Options.AllowProviderTimestampDiagnosticMode)
        {
            blockers.Add("observed_news_receipt_missing");
        }

        return blockers;
    }

    private static void ValidateRequest(IntradayEvidenceStudyRequest request)
    {
        if (!request.CandleTimeframe.Equals("1m", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Track B participation evidence requires one-minute bars.");
        }

        EnsureUtc(request.StartUtc, nameof(request.StartUtc));
        EnsureUtc(request.EndUtc, nameof(request.EndUtc));
        if (request.EndUtc <= request.StartUtc)
        {
            throw new ArgumentException("Study end must follow start.");
        }

        if (request.Options.PriorSessionMinimum < 40 ||
            request.Options.PriorSessionTarget < 63 ||
            request.Options.PriorSessionTarget < request.Options.PriorSessionMinimum)
        {
            throw new ArgumentException(
                "Participation requires a target of at least 63 sessions and minimum of 40.");
        }

        var partitions = request.Options.Partitions
            .OrderBy(value => value.StartUtc)
            .ToArray();
        if (partitions.Length == 0 ||
            partitions[0].StartUtc != request.StartUtc ||
            partitions[^1].EndUtc != request.EndUtc)
        {
            throw new ArgumentException(
                "Frozen partitions must cover the declared study bounds exactly.");
        }

        for (var index = 1; index < partitions.Length; index++)
        {
            if (partitions[index - 1].EndUtc != partitions[index].StartUtc)
            {
                throw new ArgumentException(
                    "Frozen partitions must be chronological, contiguous, and non-overlapping.");
            }
        }
    }

    private static IntradayQuoteEvidence? SelectQuote(
        IReadOnlyList<IntradayQuoteEvidence> quotes,
        DateTimeOffset eligibleAtUtc)
    {
        return quotes
            .Where(value =>
                value.Feed.Equals("sip", StringComparison.OrdinalIgnoreCase) &&
                value.Bid > 0m &&
                value.Ask >= value.Bid &&
                value.TimestampUtc >= eligibleAtUtc &&
                value.TimestampUtc - eligibleAtUtc <= QuoteTolerance)
            .OrderBy(value => value.TimestampUtc)
            .FirstOrDefault();
    }

    private int FindResponseAnchor(
        IReadOnlyList<OhlcvBar> bars,
        TimeSpan duration,
        DateTimeOffset availableAt,
        ExchangeSessionResolution eventSession)
    {
        for (var index = 0; index < bars.Count; index++)
        {
            var completedAt = bars[index].Timestamp + duration;
            if (completedAt <= availableAt)
            {
                continue;
            }

            var session = _sessions.Resolve(completedAt - TimeSpan.FromTicks(1));
            if (session.Session != EquityTradingSession.Regular)
            {
                continue;
            }

            if (eventSession.Session == EquityTradingSession.Premarket &&
                session.TradeDate != eventSession.TradeDate)
            {
                continue;
            }

            if (eventSession.Session == EquityTradingSession.Regular &&
                session.TradeDate != eventSession.TradeDate)
            {
                continue;
            }

            return index;
        }

        return -1;
    }

    private int FindCompletedTarget(
        IReadOnlyList<OhlcvBar> bars,
        TimeSpan duration,
        int anchorIndex,
        DateTimeOffset targetAt,
        ExchangeSessionResolution responseSession)
    {
        for (var index = anchorIndex + 1; index < bars.Count; index++)
        {
            var completedAt = bars[index].Timestamp + duration;
            var session = _sessions.Resolve(completedAt - TimeSpan.FromTicks(1));
            if (session.TradeDate != responseSession.TradeDate ||
                session.Session != EquityTradingSession.Regular)
            {
                if (completedAt > responseSession.SessionEndUtc)
                {
                    return -1;
                }

                continue;
            }

            if (completedAt >= targetAt)
            {
                return index;
            }
        }

        return -1;
    }

    private static decimal? BenchmarkReturn(
        IReadOnlyList<OhlcvBar> bars,
        TimeSpan duration,
        DateTimeOffset anchorCompletedAt,
        DateTimeOffset targetCompletedAt)
    {
        var anchor = bars.LastOrDefault(value => value.Timestamp + duration <= anchorCompletedAt);
        var target = bars.FirstOrDefault(value => value.Timestamp + duration >= targetCompletedAt);
        return anchor is null || target is null
            ? null
            : PercentChange(anchor.Close, target.Close);
    }

    private decimal? GapPercent(
        IReadOnlyList<OhlcvBar> bars,
        TimeSpan duration,
        int anchorIndex,
        ExchangeSessionResolution currentSession)
    {
        var priorRegular = bars
            .Take(anchorIndex)
            .Where(value =>
            {
                var completed = value.Timestamp + duration;
                var session = _sessions.Resolve(completed - TimeSpan.FromTicks(1));
                return session.Session == EquityTradingSession.Regular &&
                       session.TradeDate < currentSession.TradeDate;
            })
            .LastOrDefault();
        return priorRegular is null
            ? null
            : PercentChange(priorRegular.Close, bars[anchorIndex].Open);
    }

    private static decimal SpreadBasisPoints(IntradayQuoteEvidence quote)
    {
        var midpoint = (quote.Bid + quote.Ask) / 2m;
        return Decimal.Round((quote.Ask - quote.Bid) / midpoint * 10_000m, 6);
    }

    private static decimal? RobustLogZ(decimal? currentVolume, IReadOnlyList<decimal> prior)
    {
        if (currentVolume is null || currentVolume <= 0m || prior.Count == 0)
        {
            return null;
        }

        var logs = prior.Select(value => Math.Log((double)value)).OrderBy(value => value).ToArray();
        var median = Median(logs);
        var deviations = logs.Select(value => Math.Abs(value - median)).OrderBy(value => value).ToArray();
        var mad = Median(deviations);
        if (mad == 0d)
        {
            return Math.Abs(Math.Log((double)currentVolume.Value) - median) < 1e-12
                ? 0m
                : null;
        }

        var z = 0.6744897501960817d *
            (Math.Log((double)currentVolume.Value) - median) / mad;
        return Decimal.Round((decimal)z, 6);
    }

    private static decimal? Median(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var ordered = values.OrderBy(value => value).ToArray();
        return ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2m;
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        return ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2d;
    }

    private static DateTimeOffset? ObservedReceipt(CatalystEvent catalyst)
    {
        return String.Equals(
                   catalyst.AvailabilityEvidence,
                   CatalystAvailabilityEvidence.ObservedReceiptTime,
                   StringComparison.Ordinal) &&
               catalyst.ReceivedAt is not null
            ? catalyst.ReceivedAt.Value.ToUniversalTime()
            : null;
    }

    private static DateTimeOffset EffectiveAvailability(StoryExpansion item)
    {
        var receipt = ObservedReceipt(item.Catalyst) ??
            (item.Catalyst.UpdatedAt is not null &&
             item.Catalyst.UpdatedAt > item.Catalyst.Timestamp
                ? item.Catalyst.UpdatedAt.Value
                : item.Catalyst.Timestamp);
        return Later(receipt.ToUniversalTime(), item.Classification.InferenceCompletedAtUtc);
    }

    private static DateTimeOffset Later(DateTimeOffset first, DateTimeOffset second) =>
        first >= second ? first.ToUniversalTime() : second.ToUniversalTime();

    private static string ClassificationKey(ClassifiedCatalystEvidenceRow value) =>
        $"{value.NewsProvider}|{value.ProviderArticleId}|{value.NewsRevisionId}";

    private static string ClassificationKey(CatalystEvent catalyst, string revisionId) =>
        $"{catalyst.Provider?.Trim().ToLowerInvariant()}|{catalyst.ExternalId}|{revisionId}";

    private static IntradayHorizonEvidence CensoredHorizon(
        TimeSpan horizon,
        string reason) =>
        new(
            FormatHorizon(horizon),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            true,
            reason);

    private static IntradayEventEvidenceObservation RejectedObservation(
        StoryExpansion item,
        string partition,
        string eventSession,
        DateTimeOffset availableAt,
        string reason,
        IntradayEvidenceEligibility eligibility,
        DateTimeOffset? anchorCompletedAt = null,
        string responseSession = "unknown",
        ParticipationEvidence? participation = null) =>
        new(
            item.Catalyst.Ticker.ToUpperInvariant(),
            item.Classification.GlobalStoryCluster,
            item.Classification.NewsRevisionId,
            partition,
            eventSession,
            responseSession,
            item.Catalyst.Timestamp.ToUniversalTime(),
            ObservedReceipt(item.Catalyst),
            item.Classification.InferenceCompletedAtUtc,
            availableAt,
            anchorCompletedAt ?? availableAt,
            item.Classification.Category,
            item.Classification.Direction,
            item.Classification.Materiality,
            eligibility == IntradayEvidenceEligibility.DiagnosticOnly
                ? eligibility
                : IntradayEvidenceEligibility.Rejected,
            reason,
            participation?.CumulativeRvol,
            participation?.ComparableSessionCount ?? 0,
            participation?.RobustLogVolumeZScore,
            participation?.PremarketDollarVolume,
            null,
            null,
            IntradayMorphology.None,
            [],
            []);

    private static decimal PercentChange(decimal start, decimal end) =>
        start == 0m
            ? 0m
            : Decimal.Round((end / start - 1m) * 100m, 6);

    private static string FormatHorizon(TimeSpan horizon) =>
        horizon.TotalMinutes < 60
            ? $"{(int)horizon.TotalMinutes}m"
            : $"{(int)horizon.TotalHours}h";

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Timestamp must be a non-default UTC value.",
                parameterName);
        }
    }

    private sealed record StoryExpansion(
        CatalystEvent Catalyst,
        CatalystAvailability Availability,
        ClassifiedCatalystEvidenceRow Classification);

    private sealed record ParticipationEvidence(
        decimal? CumulativeRvol,
        int ComparableSessionCount,
        decimal? RobustLogVolumeZScore,
        decimal? PremarketDollarVolume);

    private sealed record MorphologyEvidence(
        IntradayMorphology Current,
        IReadOnlyList<IntradayMorphologyEvidence> Timeline);
}
