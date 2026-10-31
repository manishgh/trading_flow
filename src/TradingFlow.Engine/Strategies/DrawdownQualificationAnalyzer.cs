using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Strategies;

/// <summary>Pure completed-prefix observations; outcome labels never enter price features.</summary>
public static class DrawdownQualificationAnalyzer
{
    private const int Lookback = 20;
    private const int OutcomeHorizon = 5;

    public static DrawdownQualificationAudit Analyze(IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots, int index, DateTimeOffset asOfUtc,
        DrawdownQualificationRules? rules = null, DrawdownHistoryContract? history = null,
        DrawdownDecisionContext? context = null)
    {
        rules?.Validate();
        if (index < 0 || index >= bars.Count || index >= snapshots.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        var inputReason = ValidatePrefix(bars, snapshots, index, asOfUtc);
        var observation = Observe(bars, snapshots, index, inputReason);
        history ??= context?.History;
        var historyReason = inputReason ?? ValidateHistory(bars, index, asOfUtc, history);
        var contextAudit = inputReason is null ? DrawdownContextAnalyzer.Analyze(bars, index, asOfUtc, rules, context) : null;
        var episodes = inputReason is null ? Census(bars, snapshots, index, historyReason, history) : [];
        int? priorCount = historyReason is null ? 0 : null;
        // A matured losing episode enters history after its fixed window even if unrecovered.
        // Exclude only the current active episode, whose future recovery is not yet known.
        if (historyReason is null && episodes.Count > 0)
        {
            var last = episodes[^1];
            var lastIsActive = last.RecoveredAtTimestamp is null || last.Outcomes.Count != 3;
            priorCount = episodes.Take(episodes.Count - (lastIsActive ? 1 : 0))
                .Count(episode => episode.Outcomes.Count == 3);
        }
        var verdict = Verdict(rules, observation, priorCount, historyReason, contextAudit);
        return new(observation, episodes, priorCount, historyReason, verdict.Verdict, verdict.Reason, contextAudit);
    }

    private static DrawdownObservation Observe(IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots, int index, string? inputReason = null)
    {
        var current = bars[index];
        var metrics = DrawdownQualificationRules.Metrics.ToDictionary(key => key,
            _ => new DrawdownMetric(null, inputReason ?? "twenty_prior_sessions_unavailable"), StringComparer.Ordinal);
        if (inputReason is not null || index < Lookback)
            return new(DrawdownQualificationRules.ContractVersion, current.Timestamp, false,
                inputReason ?? "twenty_prior_sessions_unavailable", false, null, null, null, null, null, null, metrics);
        var peak = index - Lookback;
        for (var p = peak + 1; p < index; p++)
            if (bars[p].High >= bars[peak].High) peak = p;
        var declining = current.Close < bars[peak].High;
        var low = bars.Skip(peak + 1).Take(index - peak).Min(bar => bar.Low);
        var preAtr = peak > 0 ? snapshots[peak - 1].Atr : null;
        var laggedAtr = snapshots[index - 1].Atr;
        DrawdownMetric Ratio(decimal numerator, decimal? denominator, string reason) =>
            denominator is > 0 ? new(numerator / denominator.Value) : new(null, reason);
        metrics["close_drawdown_fraction"] = declining
            ? Ratio(bars[peak].High - current.Close, bars[peak].High, "positive_peak_unavailable")
            : new(null, "not_below_prior_peak");
        metrics["low_drawdown_fraction"] = declining
            ? Ratio(bars[peak].High - low, bars[peak].High, "positive_peak_unavailable")
            : new(null, "not_below_prior_peak");
        metrics["predecline_atr_depth"] = declining
            ? Ratio(bars[peak].High - current.Close, preAtr, "prepeak_atr14_unavailable")
            : new(null, "not_below_prior_peak");
        metrics["close_location"] = Ratio(current.Close - current.Low, current.High - current.Low, "zero_range_bar");
        foreach (var k in new[] { 1, 3 })
            metrics[$"move_{k}_atr"] = Ratio(current.Close - bars[index - k].Close,
                snapshots[index - k].Atr, $"interval_start_atr14_{k}_unavailable");
        var tr = Math.Max(current.High - current.Low,
            Math.Max(Math.Abs(current.High - bars[index - 1].Close), Math.Abs(current.Low - bars[index - 1].Close)));
        metrics["true_range_atr"] = Ratio(tr, laggedAtr, "lagged_atr14_unavailable");
        var volume = bars.Skip(index - Lookback).Take(Lookback).Select(bar => bar.Volume).Order().ToArray();
        metrics["volume_median_ratio"] = Ratio(current.Volume, (volume[9] + volume[10]) / 2m, "positive_prior_volume_median_unavailable");
        return new(DrawdownQualificationRules.ContractVersion, current.Timestamp, true, null,
            declining, bars[peak].Timestamp, bars[peak].High, declining ? low : null,
            index - peak, preAtr is > 0 ? preAtr : null, laggedAtr is > 0 ? laggedAtr : null, metrics);
    }

    private static string? ValidatePrefix(IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots, int index, DateTimeOffset asOfUtc)
    {
        var first = bars[0];
        for (var i = 0; i <= index; i++)
        {
            var bar = bars[i];
            var snapshot = snapshots[i];
            if (bar.Timeframe != "1d" || snapshot.Timeframe != "1d") return "native_daily_evidence_required";
            if (i > 0 && bar.Timestamp <= bars[i - 1].Timestamp) return "unordered_or_duplicate_bars";
            if (bar.Timestamp.AddDays(1) > asOfUtc || bar.KnownAtUtc is { } known && known > asOfUtc)
                return "bar_not_completed_or_available";
            if (bar.Timestamp != snapshot.Timestamp || bar.Ticker != first.Ticker || snapshot.Ticker != bar.Ticker ||
                bar.DataFeed != first.DataFeed || bar.AdjustmentPolicy != first.AdjustmentPolicy ||
                snapshot.DataFeed != bar.DataFeed || snapshot.AdjustmentPolicy != bar.AdjustmentPolicy)
                return "unaligned_or_changed_market_identity";
            if (bar.Low <= 0 || bar.High < bar.Low || bar.Open < bar.Low || bar.Open > bar.High ||
                bar.Close < bar.Low || bar.Close > bar.High || bar.Volume < 0)
                return "malformed_ohlcv";
        }
        return null;
    }

    private static string? ValidateHistory(IReadOnlyList<OhlcvBar> bars, int index, DateTimeOffset asOfUtc, DrawdownHistoryContract? history)
    {
        if (history is null || String.IsNullOrWhiteSpace(history.SecurityId) ||
            String.IsNullOrWhiteSpace(history.AdjustmentVersion)) return "historical_identity_and_calendar_unavailable";
        var identityReason = DrawdownContextAnalyzer.ValidateIdentity(history.Identity, bars, index, asOfUtc, bars[0].Timestamp);
        if (identityReason is not null) return identityReason;
        if (history.SecurityId != history.Identity!.SecurityId || history.AdjustmentVersion != history.Identity.AdjustmentVersion)
            return "history_security_or_adjustment_revision_mismatch";
        if (history.CalendarKnownAtUtc is not { } known || known == default || known.Offset != TimeSpan.Zero || known > asOfUtc ||
            history.CalendarSha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit))
            return "history_calendar_provenance_unavailable";
        var expected = history.ExpectedSessionTimestamps.Where(timestamp =>
            timestamp >= bars[0].Timestamp && timestamp <= bars[index].Timestamp).ToArray();
        if (!expected.SequenceEqual(bars.Take(index + 1).Select(bar => bar.Timestamp)))
            return "expected_session_interval_missing";
        return null;
    }

    private static IReadOnlyList<DrawdownEpisode> Census(IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots, int index, string? historyReason, DrawdownHistoryContract? history)
    {
        var episodes = new List<DrawdownEpisode>();
        int? onset = null;
        DrawdownObservation? anchor = null;
        int? recovered = null;
        void Append()
        {
            var start = onset!.Value;
            var matured = start + OutcomeHorizon <= index;
            var outcomes = new List<DrawdownOutcome>();
            if (matured && historyReason is null)
                foreach (var horizon in new[] { 1, 3, 5 })
                {
                    var window = bars.Skip(start + 1).Take(horizon).ToArray();
                    var available = bars.Skip(start).Take(OutcomeHorizon + 1)
                        .Max(bar => bar.KnownAtUtc is { } known && known > bar.Timestamp.AddDays(1) ? known : bar.Timestamp.AddDays(1));
                    outcomes.Add(new(horizon, available, bars[start + horizon].Close / bars[start].Close - 1m,
                        window.Max(bar => bar.High) / bars[start].Close - 1m,
                        window.Min(bar => bar.Low) / bars[start].Close - 1m));
                }
            var id = EvidenceCanonicalJson.ComputeSha256(new { DrawdownQualificationRules.ContractVersion,
                bars[start].Ticker, bars[start].DataFeed, bars[start].AdjustmentPolicy,
                SecurityId = history?.SecurityId, AdjustmentVersion = history?.AdjustmentVersion,
                anchor!.PeakTimestamp, Onset = bars[start].Timestamp });
            episodes.Add(new(id, anchor.PeakTimestamp!.Value, bars[start].Timestamp, anchor.PeakHigh!.Value,
                recovered is { } r ? bars[r].Timestamp : null,
                matured ? historyReason ?? "matured" : "right_censored", outcomes));
        }
        for (var i = Lookback; i <= index; i++)
        {
            if (onset is null)
            {
                var observation = Observe(bars, snapshots, i);
                if (!observation.IsDeclining) continue;
                onset = i;
                anchor = observation;
                recovered = null;
            }
            if (recovered is null && bars[i].Close >= anchor!.PeakHigh) recovered = i;
            if (recovered is not null && i >= onset.Value + OutcomeHorizon)
            {
                Append();
                onset = null;
            }
        }
        if (onset is not null) Append();
        return episodes;
    }

    private static (string Verdict, string Reason) Verdict(DrawdownQualificationRules? rules,
        DrawdownObservation observation, int? priorCount, string? historyReason, DrawdownContextAudit? context)
    {
        if (rules is null || rules.Mode == "observe") return ("observe", "qualification_filter_disabled");
        if (!observation.FeatureEligible) return ("unknown", observation.EligibilityReason!);
        if (rules.UsesSectorContext && (context is null || context.SectorUnknownReason is not null))
            return ("unknown", context?.SectorUnknownReason ?? "historical_sector_context_unavailable");
        if (rules.UsesEventContext && (context is null || context.EventUnknownReason is not null))
            return ("unknown", context?.EventUnknownReason ?? "evaluated_asof_event_context_unavailable");
        if (rules.RejectAdverseEvents && context?.AdverseEventCount > 0)
            return ("rejected", context.AdverseGlobalEventCount > 0 ? "observed_adverse_global_event" : "observed_adverse_event");
        if (rules.MinimumMaturedEpisodes > 0)
        {
            if (priorCount is null) return ("unknown", historyReason ?? "matured_history_unavailable");
            if (priorCount < rules.MinimumMaturedEpisodes) return ("unknown", "minimum_independent_episode_count_not_met");
        }
        if (rules.Metric is { } metric)
        {
            var value = context is not null && context.Metrics.TryGetValue(metric, out var contextual)
                ? contextual : observation.Metrics[metric];
            if (value.Value is null) return ("unknown", value.UnknownReason!);
            if (rules.MinValue is { } min && value.Value < min || rules.MaxValue is { } max && value.Value > max)
                return ("rejected", $"drawdown_{metric}_outside_inclusive_bounds");
        }
        return ("qualifies", "drawdown_qualification_satisfied");
    }
}
