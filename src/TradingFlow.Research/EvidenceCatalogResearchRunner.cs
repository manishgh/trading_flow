using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Research.Momentum;

namespace TradingFlow.Research;

public sealed record CatalogMomentumStudyRequest(
    string ResearchAdjustedBarsDatasetId,
    string AsTradedBarsDatasetId,
    string UniverseMembershipDatasetId,
    CrossSectionalMomentumStudyDefinition Definition);

public sealed record CatalogMomentumStudyResult(
    string ResearchAdjustedBarsDatasetId,
    string AsTradedBarsDatasetId,
    string UniverseMembershipDatasetId,
    CrossSectionalMomentumReport Report,
    MomentumResearchAuditReport Audit);

/// <summary>
/// Runs the swing momentum study only against immutable datasets registered in
/// the evidence catalog. Provider clients are deliberately excluded so the
/// result can be reproduced from committed inputs.
/// </summary>
public sealed class EvidenceCatalogResearchRunner(
    IEvidenceCatalog catalog,
    IEvidencePartitionDataReader dataReader)
{
    public async Task<CatalogMomentumStudyResult> RunMomentumAsync(
        CatalogMomentumStudyRequest request,
        MomentumExecutionCostAssumptions? executionCosts = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Definition);

        var adjustedBarsManifest = await RequireDatasetAsync(
            request.ResearchAdjustedBarsDatasetId,
            EvidenceDatasetKind.MarketBarsResearchAdjusted,
            cancellationToken);
        var asTradedBarsManifest = await RequireDatasetAsync(
            request.AsTradedBarsDatasetId,
            EvidenceDatasetKind.MarketBarsAsTraded,
            cancellationToken);
        var universeManifest = await RequireDatasetAsync(
            request.UniverseMembershipDatasetId,
            EvidenceDatasetKind.UniverseMembership,
            cancellationToken);
        RequireMarketBarSemantics(adjustedBarsManifest, "all");
        RequireMarketBarSemantics(asTradedBarsManifest, "raw");

        var adjustedRows = await dataReader.ReadMarketBarsAsync(adjustedBarsManifest, cancellationToken);
        var asTradedRows = await dataReader.ReadMarketBarsAsync(asTradedBarsManifest, cancellationToken);
        var universeRows = await dataReader.ReadUniverseMembershipAsync(universeManifest, cancellationToken);
        var evidence = BuildMomentumEvidence(
            adjustedRows,
            asTradedRows,
            universeRows,
            request.Definition.Options.ExchangeTimezone);
        if (evidence.EligibleSymbolsByDate.Count == 0)
        {
            throw new InvalidDataException(
                "The committed universe-membership dataset contains no active members.");
        }

        var evidenceDefinition = request.Definition with
        {
            PointInTimeUniverseEvidence = true,
            AdjustedPricesConfirmed = true,
            AsTradedLiquidityPricesConfirmed = true,
            SecurityAndIssuerIdentityConfirmed = evidence.SecurityIdentityConfirmed,
            CorporateActionsReconciled = HasTrueAttribute(adjustedBarsManifest, "corporate_actions_reconciled"),
            TerminalOutcomesReconciled = HasTrueAttribute(adjustedBarsManifest, "terminal_outcomes_reconciled"),
            ListedBarCoverageConfirmed = HasMinimumDecimalAttribute(
                adjustedBarsManifest,
                "listed_bar_coverage_pct",
                95m),
            BenchmarkCoverageConfirmed = HasTrueAttribute(adjustedBarsManifest, "benchmark_coverage_confirmed"),
            UniverseDecisionCutoffsConfirmed = evidence.DecisionCutoffsConfirmed
        };
        var report = new CrossSectionalMomentumResearchAnalyzer().Analyze(
            evidence.AdjustedBars,
            evidenceDefinition,
            evidence.EligibleSymbolsByDate,
            evidence.AsTradedBars,
            executionCosts);
        var audit = new MomentumResearchAuditAnalyzer().Analyze(
            report.RankObservations,
            report.Options.DecisionCadenceBars,
            executionCosts);
        return new CatalogMomentumStudyResult(
            adjustedBarsManifest.DatasetId,
            asTradedBarsManifest.DatasetId,
            universeManifest.DatasetId,
            report,
            audit);
    }

    private async Task<EvidenceDatasetManifest> RequireDatasetAsync(
        string datasetId,
        EvidenceDatasetKind expectedKind,
        CancellationToken cancellationToken)
    {
        var manifest = await catalog.GetDatasetAsync(
            RequireId(datasetId, nameof(datasetId)),
            cancellationToken) ?? throw new KeyNotFoundException(
            $"Committed evidence dataset '{datasetId}' was not found.");
        if (manifest.Kind != expectedKind)
        {
            throw new InvalidDataException(
                $"Dataset '{datasetId}' has kind {manifest.Kind}; expected {expectedKind}.");
        }

        if (!manifest.Quality.Passed || manifest.Partitions.Any(partition => !partition.Quality.Passed))
        {
            throw new InvalidDataException(
                $"Dataset '{datasetId}' failed evidence quality validation.");
        }

        return manifest;
    }

    private static MomentumEvidence BuildMomentumEvidence(
        IReadOnlyList<MarketBarEvidenceRow> adjustedRows,
        IReadOnlyList<MarketBarEvidenceRow> asTradedRows,
        IReadOnlyList<UniverseMembershipEvidenceRow> universeRows,
        string exchangeTimezone)
    {
        var adjustedDaily = RequireDailyRows(adjustedRows, "all", exchangeTimezone);
        var asTradedDaily = RequireDailyRows(asTradedRows, "raw", exchangeTimezone);
        var adjustedByIdentity = adjustedDaily.ToDictionary(
            item => (item.Row.SecurityId, item.SessionDate),
            item => item.Row);
        var asTradedByIdentity = asTradedDaily.ToDictionary(
            item => (item.Row.SecurityId, item.SessionDate),
            item => item.Row);

        var eligible = new Dictionary<DateOnly, HashSet<string>>();
        var identityConfirmed = true;
        var cutoffsConfirmed = true;
        foreach (var row in universeRows.Where(row => row.Included))
        {
            var key = (row.SecurityId, row.EffectiveSessionDate);
            if (!adjustedByIdentity.TryGetValue(key, out var adjusted) ||
                !asTradedByIdentity.TryGetValue(key, out var asTraded) ||
                !adjusted.Symbol.Equals(row.Symbol, StringComparison.OrdinalIgnoreCase) ||
                !asTraded.Symbol.Equals(row.Symbol, StringComparison.OrdinalIgnoreCase) ||
                String.IsNullOrWhiteSpace(row.IssuerId))
            {
                identityConfirmed = false;
                continue;
            }

            if (row.ReceivedAtUtc > adjusted.BarEndUtc || row.AsOfUtc > adjusted.BarEndUtc)
            {
                cutoffsConfirmed = false;
                continue;
            }

            if (!eligible.TryGetValue(row.EffectiveSessionDate, out var symbols))
            {
                symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                eligible.Add(row.EffectiveSessionDate, symbols);
            }

            symbols.Add(row.Symbol);
        }

        return new MomentumEvidence(
            ToOhlcvBySymbol(adjustedDaily.Select(item => item.Row)),
            ToOhlcvBySymbol(asTradedDaily.Select(item => item.Row)),
            eligible.ToDictionary(item => item.Key, item => (IReadOnlySet<string>)item.Value),
            identityConfirmed,
            cutoffsConfirmed);
    }

    private static IReadOnlyList<(MarketBarEvidenceRow Row, DateOnly SessionDate)> RequireDailyRows(
        IReadOnlyList<MarketBarEvidenceRow> rows,
        string expectedAdjustment,
        string exchangeTimezone)
    {
        if (rows.Count == 0)
        {
            throw new InvalidDataException("A momentum market-bar dataset cannot be empty.");
        }

        var daily = rows
            .Where(row => row.Timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase))
            .Select(row => (
                Row: row,
                SessionDate: TradingFlow.Engine.Execution.ExecutionRunContextFactory.ResolveSessionDate(
                    row.BarStartUtc,
                    exchangeTimezone)))
            .ToArray();
        if (daily.Length != rows.Count ||
            daily.Any(item =>
                !item.Row.Adjustment.Equals(expectedAdjustment, StringComparison.OrdinalIgnoreCase) ||
                !item.Row.Currency.Equals("USD", StringComparison.OrdinalIgnoreCase) ||
                !item.Row.DataFeed.Equals("sip", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"Momentum bars must be 1d SIP USD rows with adjustment={expectedAdjustment}.");
        }

        if (daily.GroupBy(item => (item.Row.SecurityId, item.SessionDate)).Any(group => group.Count() != 1))
        {
            throw new InvalidDataException("Momentum bars contain duplicate security/session observations.");
        }

        if (daily
            .GroupBy(item => item.Row.Symbol, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Select(item => item.Row.SecurityId).Distinct(StringComparer.Ordinal).Count() != 1))
        {
            throw new InvalidDataException(
                "Momentum bars contain symbol reuse across distinct security identities.");
        }

        if (daily
            .GroupBy(item => item.Row.SecurityId, StringComparer.Ordinal)
            .Any(group => group.Select(item => item.Row.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1))
        {
            throw new InvalidDataException(
                "Momentum bars contain ticker changes that require an explicit identity-aware series mapping.");
        }

        return daily;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> ToOhlcvBySymbol(
        IEnumerable<MarketBarEvidenceRow> rows) =>
        rows
            .GroupBy(row => row.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<OhlcvBar>)group
                    .OrderBy(row => row.BarStartUtc)
                    .Select(row => new OhlcvBar(
                        row.Symbol,
                        row.BarStartUtc,
                        row.Timeframe,
                        EvidenceFixedDecimal.FromPriceUnits(row.OpenPriceUnits),
                        EvidenceFixedDecimal.FromPriceUnits(row.HighPriceUnits),
                        EvidenceFixedDecimal.FromPriceUnits(row.LowPriceUnits),
                        EvidenceFixedDecimal.FromPriceUnits(row.ClosePriceUnits),
                        row.Volume))
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

    private static void RequireMarketBarSemantics(
        EvidenceDatasetManifest manifest,
        string expectedAdjustment)
    {
        if (!manifest.DataFeed.Equals("sip", StringComparison.OrdinalIgnoreCase) ||
            manifest.Partitions.Any(partition =>
                !partition.Provenance.Adjustment.Equals(expectedAdjustment, StringComparison.OrdinalIgnoreCase) ||
                !partition.Provenance.Currency.Equals("USD", StringComparison.OrdinalIgnoreCase) ||
                !partition.Provenance.Timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"Dataset '{manifest.DatasetId}' must use 1d SIP USD bars with adjustment={expectedAdjustment}.");
        }
    }

    private static bool HasTrueAttribute(EvidenceDatasetManifest manifest, string name) =>
        manifest.Attributes.TryGetValue(name, out var value) &&
        Boolean.TryParse(value, out var parsed) &&
        parsed;

    private static bool HasMinimumDecimalAttribute(
        EvidenceDatasetManifest manifest,
        string name,
        decimal minimum) =>
        manifest.Attributes.TryGetValue(name, out var value) &&
        Decimal.TryParse(
            value,
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed) &&
        parsed >= minimum;

    private static string RequireId(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private sealed record MomentumEvidence(
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> AdjustedBars,
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> AsTradedBars,
        IReadOnlyDictionary<DateOnly, IReadOnlySet<string>> EligibleSymbolsByDate,
        bool SecurityIdentityConfirmed,
        bool DecisionCutoffsConfirmed);
}
