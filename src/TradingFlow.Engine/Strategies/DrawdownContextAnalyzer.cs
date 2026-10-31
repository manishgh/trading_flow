using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Strategies;

/// <summary>Computes only source-backed, available context at the decision's completed prefix.</summary>
public static class DrawdownContextAnalyzer
{
    public static DrawdownContextAudit? Analyze(IReadOnlyList<OhlcvBar> bars, int index,
        DateTimeOffset asOfUtc, DrawdownQualificationRules? rules, DrawdownDecisionContext? context)
    {
        if (context is null) return null;
        var metrics = new Dictionary<string, DrawdownMetric>(StringComparer.Ordinal);
        var gate = rules?.Mode == "gate";
        var sectorReason = Sector(bars, index, asOfUtc, gate && !rules!.UsesSectorContext ? null : context.Sector, metrics, out var sectorEvidence);
        var eventReason = Events(bars, index, asOfUtc, rules, gate && !rules!.UsesEventContext ? null : context.Events, out var events, out var eventEvidence);
        if ((!gate || rules!.UsesSectorContext && rules.UsesEventContext) && context.Sector is { } sector && context.Events is { } eventContext &&
            (sector.Identity.SecurityId != eventContext.Identity.SecurityId ||
             sector.Identity.AdjustmentVersion != eventContext.Identity.AdjustmentVersion || sector.SectorId != eventContext.SectorId))
            sectorReason = eventReason = "drawdown_context_identity_or_sector_mismatch";
        if (rules?.MinimumMaturedEpisodes > 0 && context.History?.Identity is { } historical &&
            (rules.UsesSectorContext && context.Sector is { } requiredSector &&
                (historical.SecurityId != requiredSector.Identity.SecurityId || historical.AdjustmentVersion != requiredSector.Identity.AdjustmentVersion) ||
             rules.UsesEventContext && context.Events is { } requiredEvents &&
                (historical.SecurityId != requiredEvents.Identity.SecurityId || historical.AdjustmentVersion != requiredEvents.Identity.AdjustmentVersion)))
            sectorReason = eventReason = "drawdown_context_identity_or_sector_mismatch";
        return new(sectorReason, eventReason, metrics,
            eventReason is null ? events.Count : null,
            eventReason is null ? events.Count(item => item.Adverse || item.Direction == CatalystDirection.Negative) : null,
            eventReason is null ? events.Count(item => item.Scope == DrawdownEventScope.Global) : null,
            eventReason is null ? events.Count(item => item.Scope == DrawdownEventScope.Global &&
                (item.Adverse || item.Direction == CatalystDirection.Negative)) : null,
            EvidenceCanonicalJson.ComputeSha256(new { sectorEvidence, eventEvidence }));
    }

    public static string? ValidateIdentity(DrawdownSecurityIdentity? identity,
        IReadOnlyList<OhlcvBar> bars, int index, DateTimeOffset asOfUtc, DateTimeOffset fromUtc)
    {
        if (identity is null || String.IsNullOrWhiteSpace(identity.SecurityId) ||
            String.IsNullOrWhiteSpace(identity.AdjustmentVersion) || !Hash(identity.EvidenceSha256))
            return "historical_security_identity_unavailable";
        if (!Available(identity.KnownAtUtc, asOfUtc)) return "security_identity_not_available_asof";
        if (identity.Ticker != bars[index].Ticker || identity.DataFeed != bars[index].DataFeed ||
            identity.AdjustmentPolicy != bars[index].AdjustmentPolicy)
            return "historical_security_identity_mismatch";
        if (!Utc(identity.EffectiveFromUtc) || !Utc(identity.EffectiveToUtc) ||
            identity.EffectiveFromUtc > fromUtc || identity.EffectiveToUtc < bars[index].Timestamp.AddDays(1))
            return "historical_security_identity_interval_missing";
        return DrawdownPriceBasisBinding.Validate(identity.PricePrefixBinding, identity.SecurityId,
            identity.AdjustmentVersion, bars.Take(index + 1).ToArray(), asOfUtc);
    }

    private static string? Sector(IReadOnlyList<OhlcvBar> bars, int index, DateTimeOffset asOfUtc,
        DrawdownSectorContext? sector, Dictionary<string, DrawdownMetric> metrics, out object? evidence)
    {
        evidence = null;
        if (sector is null) return "historical_sector_context_unavailable";
        if (index < 5) return "five_completed_session_context_unavailable";
        var measured = bars.Skip(index - 5).Take(6).ToArray();
        var first = measured[0].Timestamp;
        var last = measured[^1].Timestamp;
        var expected = sector.ExpectedSessionTimestamps.IsDefault ? [] : sector.ExpectedSessionTimestamps
            .Where(time => time >= first && time <= last).ToArray();
        var sectorBars = Prefix(sector.SectorBenchmark, first, last);
        var marketBars = Prefix(sector.MarketBenchmark, first, last);
        evidence = new { sector.Identity, sector.SectorId, sector.BenchmarkSymbol,
            sector.MappingEffectiveFromUtc, sector.MappingEffectiveToUtc, sector.MappingKnownAtUtc,
            sector.MappingSha256, sector.CalendarKnownAtUtc, sector.CalendarSha256, expected,
            stock = measured, sectorBenchmark = BenchmarkAudit(sector.SectorBenchmark, sectorBars),
            marketBenchmark = BenchmarkAudit(sector.MarketBenchmark, marketBars) };
        var identityReason = ValidateIdentity(sector.Identity, bars, index, asOfUtc, first);
        if (identityReason is not null) return identityReason;
        if (String.IsNullOrWhiteSpace(sector.SectorId) || String.IsNullOrWhiteSpace(sector.BenchmarkSymbol) ||
            sector.BenchmarkSymbol == "SPY" || sector.SectorBenchmark.Symbol != sector.BenchmarkSymbol ||
            sector.MarketBenchmark.Symbol != "SPY" || !Hash(sector.MappingSha256) || !Hash(sector.CalendarSha256))
            return "historical_sector_mapping_provenance_unavailable";
        if (!Available(sector.MappingKnownAtUtc, asOfUtc) || !Available(sector.CalendarKnownAtUtc, asOfUtc))
            return "sector_mapping_or_calendar_not_available_asof";
        if (!Utc(sector.MappingEffectiveFromUtc) || !Utc(sector.MappingEffectiveToUtc) ||
            sector.MappingEffectiveFromUtc > first || sector.MappingEffectiveToUtc < last.AddDays(1))
            return "historical_sector_mapping_interval_missing";
        if (expected.Length != 6 || !expected.SequenceEqual(measured.Select(bar => bar.Timestamp)))
            return "sector_expected_session_interval_missing";
        var benchmarkReason = ValidateBenchmark(sector.SectorBenchmark, sectorBars, measured, asOfUtc) ??
            ValidateBenchmark(sector.MarketBenchmark, marketBars, measured, asOfUtc);
        if (benchmarkReason is not null) return benchmarkReason;
        var stockReturn = measured[^1].Close / measured[0].Close - 1m;
        var sectorReturn = sectorBars[^1].Close / sectorBars[0].Close - 1m;
        metrics["stock_return_5"] = new(stockReturn);
        metrics["sector_return_5"] = new(sectorReturn);
        metrics["market_return_5"] = new(marketBars[^1].Close / marketBars[0].Close - 1m);
        metrics["stock_minus_sector_return_5"] = new(stockReturn - sectorReturn);
        return null;
    }

    private static OhlcvBar[] Prefix(DrawdownBenchmarkEvidence benchmark, DateTimeOffset first, DateTimeOffset last) =>
        benchmark.Bars.IsDefault ? [] : benchmark.Bars.Where(bar => bar.Timestamp >= first && bar.Timestamp <= last).ToArray();

    private static object BenchmarkAudit(DrawdownBenchmarkEvidence benchmark, OhlcvBar[] bars) => new {
        benchmark.Symbol, benchmark.SecurityId, benchmark.AdjustmentVersion, benchmark.KnownAtUtc,
        benchmark.EvidenceSha256, benchmark.PricePrefixBinding, bars };

    private static string? ValidateBenchmark(DrawdownBenchmarkEvidence benchmark, OhlcvBar[] source,
        OhlcvBar[] measured, DateTimeOffset asOfUtc)
    {
        if (String.IsNullOrWhiteSpace(benchmark.SecurityId) || String.IsNullOrWhiteSpace(benchmark.AdjustmentVersion) ||
            !Hash(benchmark.EvidenceSha256)) return "sector_benchmark_provenance_unavailable";
        if (!Available(benchmark.KnownAtUtc, asOfUtc)) return "sector_benchmark_not_available_asof";
        if (source.Length != 6 || !source.Select(bar => bar.Timestamp).SequenceEqual(measured.Select(bar => bar.Timestamp)))
            return "sector_benchmark_session_mismatch";
        if (source.Any(bar => bar.Ticker != benchmark.Symbol || bar.Timeframe != "1d" ||
            bar.DataFeed != measured[0].DataFeed || bar.AdjustmentPolicy != measured[0].AdjustmentPolicy ||
            bar.Timestamp.AddDays(1) > asOfUtc || bar.KnownAtUtc is { } known && !Available(known, asOfUtc) ||
            bar.Low <= 0 || bar.High < bar.Low || bar.Open < bar.Low || bar.Open > bar.High ||
            bar.Close < bar.Low || bar.Close > bar.High || bar.Volume < 0))
            return "sector_benchmark_identity_or_availability_mismatch";
        // The full source prefix, not just the six measured return rows, must match its frozen basis.
        // Future and unavailable suffix rows cannot alter an earlier decision's prefix hash.
        var prefix = benchmark.Bars.Where(bar => bar.Timestamp <= measured[^1].Timestamp &&
            bar.Timestamp.AddDays(1) <= asOfUtc && (bar.KnownAtUtc is null || bar.KnownAtUtc <= asOfUtc)).ToArray();
        var bindingReason = DrawdownPriceBasisBinding.Validate(benchmark.PricePrefixBinding, benchmark.SecurityId,
            benchmark.AdjustmentVersion, prefix, asOfUtc);
        return bindingReason is null ? null : "sector_benchmark_" + bindingReason;
    }

    private static string? Events(IReadOnlyList<OhlcvBar> bars, int index, DateTimeOffset asOfUtc,
        DrawdownQualificationRules? rules, DrawdownEventContext? context,
        out IReadOnlyList<DrawdownClassifiedEvent> selected, out object? evidence)
    {
        selected = Array.Empty<DrawdownClassifiedEvent>();
        evidence = null;
        if (context is null) return "evaluated_asof_event_context_unavailable";
        var start = asOfUtc.AddDays(-(rules?.EventLookbackDays ?? 5));
        var coverage = context.ScopeCoverages.IsDefault ? [] : context.ScopeCoverages
            .Where(item => item.KnownAtUtc <= asOfUtc).OrderBy(item => item.Scope).ThenBy(item => item.Target, StringComparer.Ordinal).ToArray();
        var available = context.Events.IsDefault ? [] : context.Events.Where(item => item.KnownAtUtc <= asOfUtc)
            .OrderBy(item => item.Provider, StringComparer.Ordinal).ThenBy(item => item.ArticleId, StringComparer.Ordinal)
            .ThenBy(item => item.KnownAtUtc).ThenBy(item => item.RevisionId, StringComparer.Ordinal).ToArray();
        evidence = new { context.Identity, context.SectorId, context.SectorMapping, context.Evaluation, coverage,
            events = available.Where(item => item.KnownAtUtc >= start).ToArray() };
        var identityReason = ValidateIdentity(context.Identity, bars, index, asOfUtc, start);
        if (identityReason is not null) return identityReason;
        if (String.IsNullOrWhiteSpace(context.SectorId)) return "event_sector_identity_unavailable";
        var mapping = context.SectorMapping;
        if (mapping is null || mapping.SectorId != context.SectorId || !Hash(mapping.EvidenceSha256) ||
            !Available(mapping.KnownAtUtc, asOfUtc) || !Utc(mapping.EffectiveFromUtc) || !Utc(mapping.EffectiveToUtc) ||
            mapping.EffectiveFromUtc > start || mapping.EffectiveToUtc < asOfUtc)
            return "event_historical_sector_mapping_unavailable";
        var evaluation = context.Evaluation;
        if (!evaluation.Passed || !Available(evaluation.EvaluatedAtUtc, asOfUtc) ||
            String.IsNullOrWhiteSpace(evaluation.TaxonomyVersion) || String.IsNullOrWhiteSpace(evaluation.ClassifierVersion) ||
            !evaluation.ProtocolVersion.Equals("catalyst-human-labeling-v1", StringComparison.Ordinal) ||
            evaluation.TaxonomyVersion.Contains("display", StringComparison.OrdinalIgnoreCase) ||
            evaluation.ClassifierVersion.Contains("display", StringComparison.OrdinalIgnoreCase) ||
            !Hash(evaluation.LabelDatasetSha256) || !Hash(evaluation.EvaluationSha256))
            return "event_classifier_evaluation_unavailable";
        foreach (var required in new[] { (DrawdownEventScope.Company, bars[index].Ticker),
            (DrawdownEventScope.Sector, context.SectorId), (DrawdownEventScope.Global, "*") })
        {
            var matching = coverage.Where(item => item.Scope == required.Item1 && item.Target == required.Item2).ToArray();
            if (matching.Length != 1) return "event_scope_coverage_unavailable";
            var item = matching[0];
            if (!Utc(item.FromUtc) || !Utc(item.ThroughUtc) || !Available(item.KnownAtUtc, asOfUtc) ||
                !Hash(item.EvidenceSha256) || item.FromUtc > start || item.ThroughUtc < asOfUtc ||
                item.ThroughUtc > item.KnownAtUtc ||
                asOfUtc - item.KnownAtUtc > TimeSpan.FromHours(rules?.EventMaxCoverageAgeHours ?? 24))
                return "event_scope_coverage_interval_or_age_mismatch";
        }
        var latest = new List<DrawdownClassifiedEvent>();
        foreach (var group in available.GroupBy(item => (item.Provider, item.ArticleId)))
        {
            // Receipt/classification can arrive out of order. Select the latest provider
            // revision among available rows before selecting an assessment of that revision.
            var sourceTime = group.Max(item => item.UpdatedAtUtc ?? item.PublishedAtUtc);
            var sourceRevisions = group.Where(item => (item.UpdatedAtUtc ?? item.PublishedAtUtc) == sourceTime).ToArray();
            var known = sourceRevisions.Max(item => item.KnownAtUtc);
            if (known < start) continue;
            if (sourceRevisions.Select(item => (item.RevisionId, item.RevisionContentSha256)).Distinct().Count() != 1)
                return "event_revision_order_ambiguous";
            var revisions = sourceRevisions.Where(item => item.KnownAtUtc == known).Distinct().ToArray();
            if (revisions.Length != 1) return "event_revision_order_ambiguous";
            var item = revisions[0];
            if (!ValidEvent(item, evaluation, asOfUtc)) return "event_revision_or_classifier_provenance_mismatch";
            if (!ValidScope(item, bars[index].Ticker, context.SectorId)) return "event_target_or_scope_mismatch";
            // A revision to an older article remains relevant when that revision became known in the window.
            latest.Add(item);
        }
        selected = latest;
        evidence = new { context.Identity, context.SectorId, context.SectorMapping, context.Evaluation, coverage,
            events = selected };
        return null;
    }

    private static bool ValidEvent(DrawdownClassifiedEvent item, DrawdownClassifierEvaluation evaluation, DateTimeOffset asOfUtc) =>
        !String.IsNullOrWhiteSpace(item.Provider) && !String.IsNullOrWhiteSpace(item.ArticleId) &&
        !String.IsNullOrWhiteSpace(item.RevisionId) && Hash(item.RevisionContentSha256) &&
        item.TaxonomyVersion == evaluation.TaxonomyVersion && item.ClassifierVersion == evaluation.ClassifierVersion &&
        item.EvaluationSha256 == evaluation.EvaluationSha256 &&
        Available(item.PublishedAtUtc, asOfUtc) && Available(item.ReceivedAtUtc, asOfUtc) &&
        Available(item.ClassifiedAtUtc, asOfUtc) && Available(item.KnownAtUtc, asOfUtc) &&
        (item.UpdatedAtUtc is null || Available(item.UpdatedAtUtc.Value, asOfUtc) && item.UpdatedAtUtc >= item.PublishedAtUtc) &&
        item.KnownAtUtc >= item.PublishedAtUtc && item.KnownAtUtc >= item.ReceivedAtUtc &&
        item.KnownAtUtc >= item.ClassifiedAtUtc && (item.UpdatedAtUtc is null || item.KnownAtUtc >= item.UpdatedAtUtc) &&
        Enum.IsDefined(item.Category) && Enum.IsDefined(item.Scope) && Enum.IsDefined(item.Direction);

    private static bool ValidScope(DrawdownClassifiedEvent item, string ticker, string sectorId) => item.Scope switch {
        DrawdownEventScope.Company => item.Target == ticker && item.Category is DrawdownEventCategory.Company or
            DrawdownEventCategory.Financial or DrawdownEventCategory.Policy,
        DrawdownEventScope.Sector => item.Target == sectorId && item.Category is DrawdownEventCategory.Sector or
            DrawdownEventCategory.Financial or DrawdownEventCategory.Policy,
        DrawdownEventScope.Global => item.Target == "*" && item.Category is DrawdownEventCategory.Global or
            DrawdownEventCategory.Financial or DrawdownEventCategory.Policy,
        _ => false };

    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static bool Available(DateTimeOffset value, DateTimeOffset asOfUtc) => Utc(value) && value <= asOfUtc;
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
