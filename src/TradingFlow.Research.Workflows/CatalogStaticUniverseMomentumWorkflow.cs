using System.Globalization;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Research;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Execution;
using TradingFlow.Research.Momentum;

namespace TradingFlow.Research.Workflows;

public sealed partial class CatalogResearchWorkflow
{
    public const string StaticUniversePromotionBlocker =
        "static_universe_survivorship_selection_bias";

    /// <summary>
    /// Runs a deliberately non-promotable momentum diagnostic against a frozen symbol list.
    /// Inputs are resolved only through the committed evidence catalog. This method is
    /// intentionally separate from the point-in-time universe workflow.
    /// </summary>
    public async Task<CatalogStaticUniverseMomentumResult>
        RunStaticUniverseMomentumDiagnosticAsync(
            CatalogStaticUniverseMomentumRequest request,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureUtc(request.CreatedAtUtc, nameof(request.CreatedAtUtc));
        var frozenSymbols = ValidateStaticUniverseRequest(request);

        var adjustedManifest = await RequireDatasetAsync(
            request.ResearchAdjustedBarsDatasetId,
            EvidenceDatasetKind.MarketBarsResearchAdjusted,
            cancellationToken);
        var asTradedManifest = await RequireDatasetAsync(
            request.AsTradedBarsDatasetId,
            EvidenceDatasetKind.MarketBarsAsTraded,
            cancellationToken);
        RequireStaticMomentumDataset(adjustedManifest, "all");
        RequireStaticMomentumDataset(asTradedManifest, "raw");

        var adjustedRows = await partitionReader.ReadMarketBarsAsync(
            adjustedManifest,
            cancellationToken);
        var asTradedRows = await partitionReader.ReadMarketBarsAsync(
            asTradedManifest,
            cancellationToken);
        var evidence = BuildStaticMomentumEvidence(
            adjustedRows,
            asTradedRows,
            frozenSymbols,
            request.Definition.BenchmarkTicker,
            request.Definition.Options.ExchangeTimezone);

        var inputDatasets = new[]
        {
            DatasetReference(adjustedManifest),
            DatasetReference(asTradedManifest)
        };
        var universeIdentity = new
        {
            UniverseType = "frozen_static_symbol_list",
            Symbols = frozenSymbols,
            Warning = request.SurvivorshipSelectionBiasWarning.Trim(),
            PointInTimeMembershipEvidence = false
        };
        var universeLedger = Json(universeIdentity);
        var universeLedgerId =
            $"static-universe-{EvidenceCanonicalJson.ComputeSha256(universeIdentity)}";
        var partitionDefinition = Json(request.StudyPartitions);

        // Reserving the immutable holdout identity is the final action before any
        // momentum outcomes are evaluated.
        var holdout = await packager.PrepareHoldoutAsync(
            request.Definition.StudyName,
            inputDatasets,
            universeLedgerId,
            request.StudyPartitions,
            universeLedger,
            partitionDefinition,
            cancellationToken);
        await catalog.ReserveHoldoutAsync(
            new EvidenceHoldoutConsumption(
                holdout.HoldoutId,
                request.ResearchRunId,
                request.CreatedAtUtc),
            cancellationToken);

        var executionCosts = ToMomentumExecutionCosts(request.Assumptions);
        var evidenceDefinition = request.Definition with
        {
            UniverseDescription =
                $"{request.Definition.UniverseDescription.Trim()} " +
                $"Diagnostic warning: {request.SurvivorshipSelectionBiasWarning.Trim()}",
            PointInTimeUniverseEvidence = false,
            AdjustedPricesConfirmed = true,
            AsTradedLiquidityPricesConfirmed = true,
            SecurityAndIssuerIdentityConfirmed = true,
            CorporateActionsReconciled = HasTrueAttribute(
                adjustedManifest,
                "corporate_actions_reconciled"),
            TerminalOutcomesReconciled = HasTrueAttribute(
                adjustedManifest,
                "terminal_outcomes_reconciled"),
            ListedBarCoverageConfirmed = HasMinimumDecimalAttribute(
                adjustedManifest,
                "listed_bar_coverage_pct",
                95m),
            BenchmarkCoverageConfirmed = evidence.BenchmarkCoverageConfirmed,
            UniverseDecisionCutoffsConfirmed = false
        };
        var analyzed = new CrossSectionalMomentumResearchAnalyzer().Analyze(
            evidence.AdjustedBars,
            evidenceDefinition,
            evidence.EligibleSymbolsByDate,
            evidence.AsTradedBars,
            executionCosts);
        var readinessFailures = analyzed.PromotionBlockers
            .Append(StaticUniversePromotionBlocker)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var report = analyzed with
        {
            PointInTimeUniverseEvidence = false,
            DataEvidenceReady = false,
            PromotionEligible = false,
            PromotionBlockers = readinessFailures
        };
        var audit = new MomentumResearchAuditAnalyzer().Analyze(
            report.RankObservations,
            report.Options.DecisionCadenceBars);
        var canonicalArtifacts = MomentumCanonicalArtifactBuilder.Build(
            report,
            audit);

        var studyConfig = Json(new
        {
            Workflow = "static_universe_momentum_diagnostic",
            request.ResearchAdjustedBarsDatasetId,
            request.AsTradedBarsDatasetId,
            FrozenSymbols = frozenSymbols,
            SurvivorshipSelectionBiasWarning =
                request.SurvivorshipSelectionBiasWarning.Trim(),
            Definition = evidenceDefinition
        });
        var package = await packager.PackageAsync(
            new EvidenceResearchRunPackageRequest(
                request.ResearchRunId,
                request.Definition.StudyName,
                request.CreatedAtUtc,
                request.CodeVersion,
                inputDatasets,
                universeLedgerId,
                request.StudyPartitions,
                studyConfig,
                universeLedger,
                partitionDefinition,
                Assumptions(request.Assumptions),
                canonicalArtifacts
                    .Select(artifact => new EvidenceResearchNamedOutput(
                        artifact.Name,
                        new EvidenceResearchJsonPayload(
                            artifact.GetUtf8Json())))
                    .ToArray(),
                false,
                readinessFailures),
            cancellationToken);
        var commit = await catalog.RegisterResearchRunAsync(
            package.ManifestDraft,
            cancellationToken);
        var registered = await catalog.GetResearchRunAsync(
            package.ManifestDraft.ResearchRunId,
            cancellationToken)
            ?? throw new InvalidDataException(
                "The evidence catalog did not return the registered static-universe diagnostic.");
        EnsureManifestMatchesPackage(registered, package.ManifestDraft);

        return new CatalogStaticUniverseMomentumResult(
            adjustedManifest.DatasetId,
            asTradedManifest.DatasetId,
            frozenSymbols,
            request.SurvivorshipSelectionBiasWarning.Trim(),
            report,
            audit,
            registered,
            commit.AlreadyCommitted);
    }

    private static string[] ValidateStaticUniverseRequest(
        CatalogStaticUniverseMomentumRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ResearchRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CodeVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            request.ResearchAdjustedBarsDatasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AsTradedBarsDatasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            request.SurvivorshipSelectionBiasWarning);
        ArgumentNullException.ThrowIfNull(request.Definition);
        ArgumentNullException.ThrowIfNull(request.StudyPartitions);
        ArgumentNullException.ThrowIfNull(request.Assumptions);
        if (request.Definition.PointInTimeUniverseEvidence)
        {
            throw new ArgumentException(
                "A static-universe diagnostic cannot claim point-in-time universe evidence.",
                nameof(request));
        }

        var symbols = (request.FrozenSymbols ??
                throw new ArgumentNullException(nameof(request.FrozenSymbols)))
            .Select(symbol =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
                return symbol.Trim().ToUpperInvariant();
            })
            .ToArray();
        if (symbols.Length == 0)
        {
            throw new ArgumentException(
                "A static-universe diagnostic requires at least one frozen symbol.",
                nameof(request));
        }

        if (symbols.Distinct(StringComparer.Ordinal).Count() != symbols.Length)
        {
            throw new ArgumentException(
                "Frozen static-universe symbols must be unique.",
                nameof(request));
        }

        if (symbols.Contains(
                request.Definition.BenchmarkTicker.Trim().ToUpperInvariant(),
                StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "The benchmark must not also be a static-universe candidate.",
                nameof(request));
        }

        var expectedValidationStart = DateOnly.FromDateTime(
            request.StudyPartitions.Validation.StartUtc.UtcDateTime);
        var expectedHoldoutStart = DateOnly.FromDateTime(
            request.StudyPartitions.Holdout.StartUtc.UtcDateTime);
        if (request.Definition.Options.ValidationStartDate != expectedValidationStart ||
            request.Definition.Options.HoldoutStartDate != expectedHoldoutStart)
        {
            throw new ArgumentException(
                "Momentum definition partition dates must exactly match the frozen contiguous study windows.",
                nameof(request));
        }

        if (ToMomentumExecutionCosts(request.Assumptions).RoundTripCostBps <= 0m)
        {
            throw new ArgumentException(
                "A static-universe diagnostic requires positive frozen round-trip execution costs.",
                nameof(request));
        }

        return symbols.OrderBy(symbol => symbol, StringComparer.Ordinal).ToArray();
    }

    private static MomentumExecutionCostAssumptions ToMomentumExecutionCosts(
        CatalogResearchAssumptionSet assumptions) =>
        new(
            assumptions.Cost.CommissionBps,
            assumptions.Cost.RegulatoryFeesBps,
            assumptions.Spread.EstimatedFullSpreadBps,
            assumptions.Slippage.MaximumSlippageBps);

    private static void RequireStaticMomentumDataset(
        EvidenceDatasetManifest manifest,
        string expectedAdjustment)
    {
        if (manifest.Partitions.Any(partition => !partition.Quality.Passed))
        {
            throw new InvalidDataException(
                $"Dataset '{manifest.DatasetId}' contains a partition that failed " +
                "committed evidence quality validation.");
        }

        if (!manifest.DataFeed.Equals("sip", StringComparison.OrdinalIgnoreCase) ||
            manifest.Partitions.Any(partition =>
                !partition.Provenance.DataFeed.Equals(
                    "sip",
                    StringComparison.OrdinalIgnoreCase) ||
                !partition.Provenance.Adjustment.Equals(
                    expectedAdjustment,
                    StringComparison.OrdinalIgnoreCase) ||
                !partition.Provenance.Currency.Equals(
                    "USD",
                    StringComparison.OrdinalIgnoreCase) ||
                !partition.Provenance.Timeframe.Equals(
                    "1d",
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"Dataset '{manifest.DatasetId}' must contain committed 1d SIP USD bars " +
                $"with adjustment={expectedAdjustment}.");
        }
    }

    private static StaticMomentumEvidence BuildStaticMomentumEvidence(
        IReadOnlyList<MarketBarEvidenceRow> adjustedRows,
        IReadOnlyList<MarketBarEvidenceRow> asTradedRows,
        IReadOnlyList<string> frozenSymbols,
        string benchmarkTicker,
        string exchangeTimezone)
    {
        var adjustedDaily = RequireStaticDailyRows(
            adjustedRows,
            "all",
            exchangeTimezone);
        var asTradedDaily = RequireStaticDailyRows(
            asTradedRows,
            "raw",
            exchangeTimezone);
        var benchmark = benchmarkTicker.Trim().ToUpperInvariant();
        var requiredSymbols = frozenSymbols
            .Append(benchmark)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        EnsureRequiredSymbols(adjustedDaily, requiredSymbols, "research-adjusted");
        EnsureRequiredSymbols(asTradedDaily, requiredSymbols, "as-traded");
        EnsureMatchingSecurityIdentities(
            adjustedDaily,
            asTradedDaily,
            requiredSymbols);

        var adjustedBars = ToStaticOhlcvBySymbol(
            adjustedDaily.Where(item => requiredSymbols.Contains(item.Row.Symbol)));
        var asTradedBars = ToStaticOhlcvBySymbol(
            asTradedDaily.Where(item => requiredSymbols.Contains(item.Row.Symbol)));
        var eligible = adjustedDaily
            .Where(item => item.Row.Symbol.Equals(
                benchmark,
                StringComparison.OrdinalIgnoreCase))
            .Select(item => item.SessionDate)
            .Distinct()
            .ToDictionary(
                date => date,
                _ => (IReadOnlySet<string>)frozenSymbols.ToHashSet(
                    StringComparer.OrdinalIgnoreCase));
        var adjustedBenchmarkDates = adjustedDaily
            .Where(item => item.Row.Symbol.Equals(
                benchmark,
                StringComparison.OrdinalIgnoreCase))
            .Select(item => item.SessionDate)
            .ToHashSet();
        var asTradedBenchmarkDates = asTradedDaily
            .Where(item => item.Row.Symbol.Equals(
                benchmark,
                StringComparison.OrdinalIgnoreCase))
            .Select(item => item.SessionDate)
            .ToHashSet();

        return new StaticMomentumEvidence(
            adjustedBars,
            asTradedBars,
            eligible,
            adjustedBenchmarkDates.SetEquals(asTradedBenchmarkDates));
    }

    private static IReadOnlyList<StaticDailyRow> RequireStaticDailyRows(
        IReadOnlyList<MarketBarEvidenceRow> rows,
        string expectedAdjustment,
        string exchangeTimezone)
    {
        if (rows.Count == 0)
        {
            throw new InvalidDataException(
                "A static-universe momentum market-bar dataset cannot be empty.");
        }

        var daily = rows.Select(row => new StaticDailyRow(
                row,
                ExecutionRunContextFactory.ResolveSessionDate(
                    row.BarStartUtc,
                    exchangeTimezone)))
            .ToArray();
        if (daily.Any(item =>
                !item.Row.Timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase) ||
                !item.Row.Adjustment.Equals(
                    expectedAdjustment,
                    StringComparison.OrdinalIgnoreCase) ||
                !item.Row.Currency.Equals("USD", StringComparison.OrdinalIgnoreCase) ||
                !item.Row.DataFeed.Equals("sip", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"Static-universe momentum bars must be 1d SIP USD rows with " +
                $"adjustment={expectedAdjustment}.");
        }

        if (daily
            .GroupBy(item => (item.Row.SecurityId, item.SessionDate))
            .Any(group => group.Count() != 1))
        {
            throw new InvalidDataException(
                "Static-universe momentum bars contain duplicate security/session observations.");
        }

        if (daily
            .GroupBy(item => item.Row.Symbol, StringComparer.OrdinalIgnoreCase)
            .Any(group => group
                .Select(item => item.Row.SecurityId)
                .Distinct(StringComparer.Ordinal)
                .Count() != 1))
        {
            throw new InvalidDataException(
                "Static-universe momentum bars contain symbol reuse across security identities.");
        }

        return daily;
    }

    private static void EnsureRequiredSymbols(
        IReadOnlyList<StaticDailyRow> rows,
        IReadOnlySet<string> requiredSymbols,
        string datasetLabel)
    {
        var present = rows
            .Select(item => item.Row.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = requiredSymbols
            .Where(symbol => !present.Contains(symbol))
            .OrderBy(symbol => symbol, StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                $"{datasetLabel} dataset is missing frozen symbols: " +
                String.Join(", ", missing));
        }
    }

    private static void EnsureMatchingSecurityIdentities(
        IReadOnlyList<StaticDailyRow> adjusted,
        IReadOnlyList<StaticDailyRow> asTraded,
        IReadOnlySet<string> requiredSymbols)
    {
        var adjustedIdentities = SecurityIdentities(adjusted, requiredSymbols);
        var asTradedIdentities = SecurityIdentities(asTraded, requiredSymbols);
        foreach (var symbol in requiredSymbols)
        {
            if (!adjustedIdentities[symbol].Equals(
                    asTradedIdentities[symbol],
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Security identity differs between adjusted and as-traded " +
                    $"datasets for '{symbol}'.");
            }
        }
    }

    private static IReadOnlyDictionary<string, string> SecurityIdentities(
        IReadOnlyList<StaticDailyRow> rows,
        IReadOnlySet<string> requiredSymbols) =>
        rows.Where(item => requiredSymbols.Contains(item.Row.Symbol))
            .GroupBy(item => item.Row.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(item => item.Row.SecurityId)
                    .Distinct(StringComparer.Ordinal)
                    .Single(),
                StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>>
        ToStaticOhlcvBySymbol(IEnumerable<StaticDailyRow> rows) =>
        rows.GroupBy(item => item.Row.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<OhlcvBar>)group
                    .OrderBy(item => item.Row.BarStartUtc)
                    .Select(item => new OhlcvBar(
                        item.Row.Symbol,
                        item.Row.BarStartUtc,
                        item.Row.Timeframe,
                        EvidenceFixedDecimal.FromPriceUnits(item.Row.OpenPriceUnits),
                        EvidenceFixedDecimal.FromPriceUnits(item.Row.HighPriceUnits),
                        EvidenceFixedDecimal.FromPriceUnits(item.Row.LowPriceUnits),
                        EvidenceFixedDecimal.FromPriceUnits(item.Row.ClosePriceUnits),
                        item.Row.Volume))
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

    private static bool HasTrueAttribute(
        EvidenceDatasetManifest manifest,
        string name) =>
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
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var parsed) &&
        parsed >= minimum;

    private sealed record StaticDailyRow(
        MarketBarEvidenceRow Row,
        DateOnly SessionDate);

    private sealed record StaticMomentumEvidence(
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> AdjustedBars,
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> AsTradedBars,
        IReadOnlyDictionary<DateOnly, IReadOnlySet<string>> EligibleSymbolsByDate,
        bool BenchmarkCoverageConfirmed);
}
