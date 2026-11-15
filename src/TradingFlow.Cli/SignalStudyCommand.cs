using System.Globalization;
using System.Text;
using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Governance;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Storage;
using TradingFlow.Engine.Universe;
using TradingFlow.Research.Signals;

namespace TradingFlow.Cli;

/// <summary>
/// Stage 1 of the research funnel (docs/research/fable-review-2026-11-12.md, sections 2.2 and 4.1): every signal of each
/// declared strategy variant on a published research screen's eligible stock-days, followed from entry to its own exit on
/// as-traded daily bars from the evidence catalog and judged by <see cref="SignalStudyAnalyzer"/> with its frozen default
/// pass rules against the equal-weight return of the eligible stocks. No portfolio, slots or journal.
/// </summary>
public static class SignalStudyCommand
{
    public const string Name = "research-signal-study";

    /// <summary>The development years a study may read (2.2 point 4); later years stay unopened for the holdout and paper.</summary>
    public static readonly DateOnly DevelopmentFrom = new(2017, 1, 1);
    /// <summary>The last date of the development years.</summary>
    public static readonly DateOnly DevelopmentTo = new(2023, 12, 31);

    /// <summary>The manifest file written beside the reports.</summary>
    public const string ManifestFileName = "signal-study-manifest.json";

    private const string CodeVersion = "research-signal-study-v1";

    /// <summary>A study request: catalog locations, screen, datasets, declared variants, entry, cost and window.</summary>
    public sealed record Request(string CatalogPath, string ArtifactRoot, string ScreenFolder, string BarsDatasetId, string ActionsDatasetId,
        IReadOnlyList<string> StrategyFiles, SignalStudyEntry Entry, decimal CostBps, DateOnly From, DateOnly To, string OutFolder,
        int WarmupBars = 250);

    /// <summary>
    /// Usage: research-signal-study --catalog path --artifact-root path --screen folder --bars datasetId --actions datasetId
    /// --strategy file.yaml [--strategy other.yaml ...] --entry next_open|same_close --cost-bps 10 --from yyyy-MM-dd
    /// --to yyyy-MM-dd --out folder [--warmup-bars 250]. Opens the SQLite evidence catalog read-only and runs the study.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        var request = ParseRequest(args);
        var (manifest, _, membershipSha) = PublishedResearchScreen.ReadDefinition(request.ScreenFolder);
        var config = ProviderConfig(request, manifest, membershipSha);
        var provider = EvidenceCatalogMarketDataProvider.Open(config,
            new FileSystemRawArchiveWriter(new RawArchiveOptions(Path.Combine(Path.GetFullPath(request.OutFolder), "catalog-reads"))));
        await RunAsync(request, provider, ResearchCliRegistry.Open(), output, cancellationToken);
        return 0;
    }

    /// <summary>
    /// Runs the study on an already opened catalog provider: checks the window and inputs, loads each pool stock's bars once
    /// (from the warm-up start to the window end, nothing later), computes its indicators with the shared engine, follows every
    /// variant's signals and writes the reports, the outcome and monthly tables and the manifest. Returns the manifest path.
    /// </summary>
    public static async Task<string> RunAsync(Request request, EvidenceCatalogMarketDataProvider provider,
        SqliteResearchTrialRegistry registry, TextWriter output, CancellationToken cancellationToken)
    {
        RequireStudyWindow(request.From, request.To);
        var (screen, screenSha, membershipSha) = PublishedResearchScreen.ReadDefinition(request.ScreenFolder);
        RequireScreenInputs(request, screen);
        if (request.WarmupBars <= 0 || request.CostBps is < 0m or >= 10_000m)
            throw new ArgumentException("Signal studies require positive warm-up bars and costs from zero up to 10,000 bps per side.");
        var variants = request.StrategyFiles.Select(ReadVariant).ToArray();
        if (variants.Length == 0 || variants.Select(variant => FileStem(variant.Label)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != variants.Length)
            throw new InvalidDataException("Select at least one exact signal-study variant, with distinct output names.");
        var folder = Path.GetFullPath(request.OutFolder);
        var protectedEnd = ResearchObservationBoundary.DocumentedHoldoutEndExclusive;
        var hasDeclaredHoldout = await provider.CanDeclareSignalStudyHoldoutAsync(ProviderConfig(request, screen, membershipSha),
            NewYorkMidnight(protectedEnd.AddDays(-1)), cancellationToken);
        var frozen = FreezeStudy(request, screenSha, membershipSha, screen.Inputs.ExchangeSessionsDatasetId!, variants, folder,
            hasDeclaredHoldout ? protectedEnd : null);
        var path = Path.Combine(folder, ManifestFileName);
        using var outputOwner = AcquireOutputOwner(folder);
        return await registry.RunDirectAsync(frozen, "study", (start, token) =>
                ComputeStudyAsync(request, screenSha, provider, variants, start, folder, output, token),
            (prior, _) => Task.FromResult(RequirePublishedStudy(path, prior)), cancellationToken);
    }

    /// <summary>Uses the output folder as an immutable study destination, even when two different requests are submitted concurrently.</summary>
    private static FileStream AcquireOutputOwner(string folder)
    {
        Directory.CreateDirectory(folder);
        return new(Path.Combine(folder, ".signal-study.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>Protects existing report bytes and verifies the completed registration before returning a retry result.</summary>
    private static string RequirePublishedStudy(string path, ResearchExecutionResult prior)
    {
        if (prior.ArtifactReference != path || prior.ArtifactSha256 != ResearchStartIdentity.HashFile(path))
            throw new InvalidDataException("The published signal-study manifest differs from its registered result.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(path));
        VerifyPublishedFiles(path, manifest.RootElement);
        return path;
    }

    /// <summary>Verifies an existing manifest's exact named report and table bytes without reading market inputs.</summary>
    private static void VerifyPublishedFiles(string path, JsonElement manifest)
    {
        foreach (var variant in manifest.GetProperty("variants").EnumerateArray())
        foreach (var file in variant.GetProperty("artifacts").EnumerateObject())
            if (Path.GetFileName(file.Name) != file.Name || ResearchStartIdentity.HashFile(Path.Combine(Path.GetDirectoryName(path)!, file.Name)) != file.Value.GetString())
                throw new InvalidDataException("A published signal-study output differs from its frozen manifest.");
    }

    /// <summary>Computes only after the exact trial identities and output-folder owner have been persisted.</summary>
    private static async Task<DirectResearchCompletion<string>> ComputeStudyAsync(Request request, string screenSha,
        EvidenceCatalogMarketDataProvider provider, IReadOnlyList<Variant> variants, ResearchStart start, string folder,
        TextWriter output, CancellationToken cancellationToken)
    {
        var registrationPath = Path.Combine(folder, "signal-study-registration.json");
        var manifestPath = Path.Combine(folder, ManifestFileName);
        if (File.Exists(manifestPath))
            return RecoverPublishedStudy(request, start, registrationPath, manifestPath);
        await WriteExactAsync(registrationPath, Json(start.Binding), cancellationToken);
        var screen = PublishedResearchScreen.Read(request.ScreenFolder, screenSha);
        provider = provider.BeforeProtectedObservations(NewYorkMidnight(DevelopmentTo.AddDays(1)));
        var membership = screen.ToMembership(request.From, request.To);
        var pool = screen.PoolSymbols(request.From, request.To);
        var proposedDataStart = request.From.AddDays(-(int)Math.Ceiling(request.WarmupBars * 1.5) - 10);
        var evidenceFloor = new DateOnly(2016, 1, 1);
        var dataStart = proposedDataStart < evidenceFloor ? evidenceFloor : proposedDataStart;
        var schedules = await provider.LoadMarketSessionSchedulesAsync(dataStart.AddDays(-1), request.To, cancellationToken);
        var splits = await provider.LoadShareSplitsAsync(cancellationToken);
        var stocks = await LoadStocksAsync(provider, pool, schedules, splits, dataStart, request.To, cancellationToken);
        bool Eligible(string ticker, DateOnly day) => membership.IsMember(ticker, day);
        var universe = SignalStudyOutcomes.Universe(stocks, Eligible, request.From, request.To);
        var settings = new SignalStudySettings(request.Entry, (double)(request.CostBps / 10_000m), request.From, request.To, request.WarmupBars);
        var results = new List<VariantOutcome>();
        foreach (var variant in variants)
        {
            results.Add(await StudyVariantAsync(variant, stocks, Eligible, universe, settings, folder, cancellationToken));
            output.WriteLine($"{variant.Label}: written to {folder}");
        }
        await WriteExactAsync(manifestPath, Json(Manifest(request, screen, stocks, results, start)), cancellationToken);
        return CompletePublishedStudy(request, start, manifestPath, results);
    }

    /// <summary>Finishes an interrupted registry publication or refuses an old artifact before any screen or candle reads.</summary>
    private static DirectResearchCompletion<string> RecoverPublishedStudy(Request request, ResearchStart start, string registrationPath, string manifestPath)
    {
        if (!File.Exists(registrationPath) || ResearchStartIdentity.HashFile(registrationPath) != ResearchStartIdentity.HashText(Json(start.Binding)))
            throw new InvalidDataException("The destination already holds a signal study without this exact durable registration. Use a new output folder.");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifest = document.RootElement;
        if (!manifest.TryGetProperty("registration", out var registration) ||
            registration.GetProperty("StartId").GetString() != start.Binding.Request.StartId ||
            registration.GetProperty("RequestSha256").GetString() != start.Binding.Request.RequestSha256 ||
            manifest.GetProperty("codeSha256").GetString() != start.Binding.Request.CodeSha256)
            throw new InvalidDataException("The existing signal-study manifest belongs to a different frozen start.");
        VerifyPublishedFiles(manifestPath, manifest);
        var results = manifest.GetProperty("variants").Deserialize<VariantOutcome[]>()
            ?? throw new InvalidDataException("The published signal-study variants are missing.");
        return CompletePublishedStudy(request, start, manifestPath, results);
    }

    /// <summary>Creates a canonical result per exact declared variant, linking all its output hashes and the shared manifest.</summary>
    private static DirectResearchCompletion<string> CompletePublishedStudy(Request request, ResearchStart start, string manifestPath,
        IReadOnlyList<VariantOutcome> results)
    {
        if (!results.Select(result => result.identity.ContentSha256).SequenceEqual(start.Binding.Trials.Select(trial => trial.VariantKey)))
            throw new InvalidDataException("Published signal-study variants differ from the frozen registration.");
        var manifestHash = ResearchStartIdentity.HashFile(manifestPath);
        var canonical = results.Select(result =>
        {
            var trial = start.Binding.Trials.Single(item => item.VariantKey == result.identity.ContentSha256);
            return new CanonicalResearchResultManifest(trial.ExperimentId + "-development", trial.ExperimentId,
                trial.DefinitionSha256, start.Binding.Request.StartId, start.Binding.Request.CodeSha256, "development", "passed",
                new Dictionary<string, decimal>
                {
                    ["passed"] = request.Entry == SignalStudyEntry.NextOpen && result.decision == SignalStudyAnalyzer.AdvanceToHoldout ? 1m : 0m,
                    ["trades"] = result.trades
                },
                result.artifacts.Values.Append(manifestHash).ToArray(), start.RegisteredAtUtc);
        }).ToArray();
        return new(manifestPath, manifestPath, manifestHash, canonical);
    }

    /// <summary>One declared variant: its definition, its catalog identity (or the definition hash) and its source file.</summary>
    private sealed record Variant(StrategyDefinition Strategy, string Label, StrategyArtifactIdentity Identity, string FilePath, string FileSha256);

    private sealed record VariantOutcome(string variant, StrategyArtifactIdentity identity, string strategyFile,
        string strategyFileSha256, string decision, int trades, IReadOnlyDictionary<string, int> counts,
        IReadOnlyDictionary<string, string> artifacts);

    /// <summary>Freezes actual dataset identities and analyzer parameters without reading any price outcomes.</summary>
    private static FrozenResearchStart FreezeStudy(Request request, string screenSha, string membershipSha, string calendarId,
        IReadOnlyList<Variant> variants, string folder, DateOnly? protectedEnd)
    {
        var code = ResearchFileStart.EngineCodeSha256(typeof(SignalStudyAnalyzer).Assembly, typeof(SignalStudyCommand).Assembly);
        var passRules = new SignalStudyPassRules();
        var thresholds = JsonSerializer.SerializeToElement(passRules).EnumerateObject()
            .ToDictionary(item => item.Name, item => item.Value.GetDecimal(), StringComparer.Ordinal);
        var partitions = new List<ResearchPartitionDefinition>
        {
            new("development", NewYorkMidnight(request.From), NewYorkMidnight(request.To.AddDays(1)), TimeSpan.Zero)
        };
        var holdoutStart = DevelopmentTo.AddDays(1);
        if (protectedEnd is { } holdoutEnd)
            partitions.Add(new("holdout", NewYorkMidnight(holdoutStart), NewYorkMidnight(holdoutEnd), TimeSpan.Zero));
        var frozenVariants = variants.Select(variant => new FrozenResearchVariant(variant.Identity.ContentSha256, variant.Identity.StrategyId,
            $"Signals from {variant.Label} exceed the equal-weight eligible-stock return after frozen costs and require the declared sample and robustness criteria.",
            variant.Identity.ContentSha256,
            [request.BarsDatasetId, request.ActionsDatasetId, calendarId,
                "screen-manifest:" + screenSha, "screen-membership:" + membershipSha],
            new Dictionary<string, string> { ["market_clock"] = "America/New_York", ["stored_clock"] = "UTC" },
            new Dictionary<string, string>
            {
                ["strategy_document_sha256"] = variant.FileSha256,
                ["price_basis"] = "as-traded daily bars with explicit split handling",
                ["signal_horizon"] = "daily",
                ["entry"] = request.Entry.ToString(),
                ["warmup_bars"] = request.WarmupBars.ToString(CultureInfo.InvariantCulture),
                ["benchmark"] = "equal-weight eligible stocks over the same holding dates"
            }, thresholds,
            new Dictionary<string, string> { ["bps_per_side"] = request.CostBps.ToString(CultureInfo.InvariantCulture) },
            partitions, "passed",
            ["Fail any frozen SignalStudyPassRules gate", "Only next-open execution can substantiate a portfolio study review."])).ToArray();
        var hash = EvidenceCanonicalJson.ComputeSha256(new { Variants = frozenVariants, Screen = screenSha });
        return new(ResearchStartIdentity.StartId("signal-study", folder), ResearchStartKind.SignalStudy, hash, code, frozenVariants,
            new Dictionary<string, string[]> { ["study"] = frozenVariants.Select(variant => variant.VariantKey).ToArray() });
    }

    /// <summary>Publishes exact immutable bytes, or reconciles an identical file left by an interrupted attempt.</summary>
    private static async Task WriteExactAsync(string path, string content, CancellationToken token)
    {
        var expected = ResearchStartIdentity.HashText(content);
        if (!File.Exists(path))
        {
            await AtomicFileArtifactWriter.Instance.WriteTextExclusiveAsync(path, content, token);
        }
        else if (ResearchStartIdentity.HashFile(path) != expected)
        {
            throw new InvalidDataException($"The research output '{Path.GetFileName(path)}' already belongs to different evidence. Use a new output folder.");
        }
    }

    // Refuses a window outside the development years, so the holdout and the latest data stay unopened.
    private static void RequireStudyWindow(DateOnly from, DateOnly to)
    {
        if (from > to || from < DevelopmentFrom || to > DevelopmentTo)
            throw new InvalidOperationException($"The study window {from:yyyy-MM-dd} to {to:yyyy-MM-dd} must lie inside the development years " +
                $"{DevelopmentFrom:yyyy-MM-dd} to {DevelopmentTo:yyyy-MM-dd}; later dates stay unopened for the holdout.");
    }

    // The named datasets must be the ones the published screen was built from, with an exchange-session dataset.
    private static void RequireScreenInputs(Request request, ResearchScreenManifest screen)
    {
        if (screen.To > DevelopmentTo)
            throw new InvalidOperationException("The published eligible list contains protected dates. Publish an eligible list confined to the development years before running the study.");
        if (request.From < screen.From || request.To > screen.To)
            throw new InvalidOperationException("The published eligible list does not cover the requested development window.");
        var inputs = screen.Inputs;
        if (screen.RankingRules is not null || inputs.DailyBarsDatasetId != request.BarsDatasetId || inputs.CorporateActionsDatasetId != request.ActionsDatasetId ||
            inputs.ExchangeSessionsDatasetId is null)
            throw new InvalidOperationException($"Research screen must be an eligible list built from daily bars {request.BarsDatasetId} " +
                $"and corporate actions {request.ActionsDatasetId}, with an exchange-session dataset.");
    }

    // Reads one strategy file, refuses rules the study cannot follow or a price basis other than as-traded bars, and resolves
    // its catalog identity (strategy ID, version and content hash) when the exact definition is registered.
    private static Variant ReadVariant(string file)
    {
        var path = Path.GetFullPath(file);
        using var held = File.OpenRead(path);
        var reader = new SimpleYamlReader();
        var repository = RepositoryRoot(path);
        var catalog = new StrategyArtifactCatalog(repository, Path.Combine(repository, "configs", "strategy-catalog.json"), reader);
        var strategy = new StrategyRunArtifactValidator(reader, catalog).ReadResearchDefinition(path);
        SignalStudyOutcomes.RequireSupported(strategy);
        var runtime = StrategyDecisionRequestAssembler.CreateBacktestRuntimeStrategy(strategy, catalog);
        var timing = runtime.AdmissionProfile.ExecutionTimingPolicy;
        if (timing == ExecutionRules.DecisionHourAsTradedTimingPolicy ||
            MarketEvidenceOperationalRules.ForExecutionTimingPolicy(timing).RequiredAdjustmentPolicy != "raw")
            throw new NotSupportedException($"{strategy.StrategyName}: the signal study reads as-traded daily bars decided at the close; " +
                $"timing '{timing}' does not.");
        return new(strategy, $"{runtime.Identity.StrategyId}@{runtime.Identity.SemanticVersion}", runtime.Identity, path,
            ResearchScreenManifest.Sha256(File.ReadAllBytes(path)));
    }

    // The repository folder holding configs/strategy-catalog.json above a strategy file.
    private static string RepositoryRoot(string strategyPath)
    {
        for (var folder = Path.GetDirectoryName(strategyPath); folder is not null; folder = Path.GetDirectoryName(folder))
            if (File.Exists(Path.Combine(folder, "configs", "strategy-catalog.json"))) return folder;
        throw new InvalidOperationException($"No configs/strategy-catalog.json above {strategyPath}.");
    }

    // The catalog provider settings: the screen's daily bars, exchange sessions and corporate actions; no intraday datasets.
    private static EvidenceCatalogProviderConfig ProviderConfig(Request request, ResearchScreenManifest screen, string membershipSha) => new(
        Path.GetFullPath(request.CatalogPath), Path.GetFullPath(request.ArtifactRoot), membershipSha, request.BarsDatasetId,
        screen.Inputs.ExchangeSessionsDatasetId!, [], [], request.ActionsDatasetId);

    /// <summary>
    /// Loads each pool stock's daily bars from the data start through the window end (the request ends at the midnight after
    /// it, so no later bar is read) and computes its indicators once with the shared engine and the backtest evidence profile.
    /// </summary>
    private static async Task<IReadOnlyList<SignalStudyStock>> LoadStocksAsync(EvidenceCatalogMarketDataProvider provider, IReadOnlyList<string> pool,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules, IReadOnlyDictionary<string, IReadOnlyList<ShareSplit>> splits,
        DateOnly dataStart, DateOnly to, CancellationToken cancellationToken)
    {
        var engine = new IndicatorEngine(new MarketEvidenceProfile(MarketEvidenceProfile.ProductionVersion, MarketEvidenceProfile.DefaultLookbackSessions,
            MarketEvidenceProfile.DefaultMinimumValidSamples, "America/New_York", schedules,
            MarketEvidenceOperationalRules.ForExecutionTimingPolicy(ExecutionRules.KeptGapAsTradedTimingPolicy), allowStandardWeekdayFallback: false));
        var stocks = new List<SignalStudyStock>();
        foreach (var ticker in pool)
        {
            var bars = new List<OhlcvBar>();
            await foreach (var bar in provider.GetBarsAsync([ticker], ["1d"], NewYorkMidnight(dataStart), NewYorkMidnight(to.AddDays(1)), cancellationToken))
                bars.Add(bar);
            var ordered = bars.Where(bar => SignalStudyOutcomes.SessionDate(bar.Timestamp) <= to).OrderBy(bar => bar.Timestamp).ToArray();
            if (ordered.Length == 0) continue;
            var exDates = splits.GetValueOrDefault(ticker, []).Select(split => split.ExDate).ToHashSet();
            stocks.Add(new(ticker, ordered, engine.Compute(ordered), exDates));
        }
        return stocks;
    }

    // Follows one variant over every stock and writes its report, outcome table and monthly table; returns its manifest entry.
    private static async Task<VariantOutcome> StudyVariantAsync(Variant variant, IReadOnlyList<SignalStudyStock> stocks, Func<string, DateOnly, bool> eligible,
        IReadOnlyList<EligibleUniverseDay> universe, SignalStudySettings settings, string folder, CancellationToken cancellationToken)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var signals = stocks.SelectMany(stock => SignalStudyOutcomes.ForStock(variant.Strategy, stock, eligible, settings, counts)).ToArray();
        signals = SignalStudyOutcomes.RequireUniverseDays(signals, universe, counts)
            .OrderBy(signal => signal.Outcome.SignalDate).ThenBy(signal => signal.Outcome.Ticker, StringComparer.Ordinal).ToArray();
        var report = SignalStudyAnalyzer.Analyze(variant.Label, signals.Select(signal => signal.Outcome).ToArray(), universe, new SignalStudyPassRules());
        var stem = FileStem(variant.Label);
        var artifacts = new Dictionary<string, string>
        {
            [$"{stem}-report.json"] = Json(report),
            [$"{stem}-outcomes.csv"] = OutcomesCsv(signals, universe),
            [$"{stem}-months.csv"] = MonthsCsv(report)
        };
        foreach (var (name, content) in artifacts)
            await WriteExactAsync(Path.Combine(folder, name), content, cancellationToken);
        return new(variant.Label, variant.Identity, variant.FilePath, variant.FileSha256, report.Decision, report.Trades, counts,
            artifacts.ToDictionary(item => item.Key, item => ResearchStartIdentity.HashText(item.Value), StringComparer.Ordinal));
    }

    // One row per signal outcome: dates, exit reason, holding days, compounded gross, net (cost on both sides) and universe
    // returns over the same days, and the net excess the analyzer averages.
    private static string OutcomesCsv(IReadOnlyList<StudiedSignal> signals, IReadOnlyList<EligibleUniverseDay> universe)
    {
        var baseline = universe.ToDictionary(day => day.Date, day => day.EqualWeightReturn);
        var text = new StringBuilder("ticker,direction,signal_date,entry_date,exit_date,exit_reason,holding_days,outcome_known,gross_return,net_return,universe_return,net_excess\n");
        foreach (var signal in signals)
        {
            var outcome = signal.Outcome;
            var gross = outcome.HoldingDays.Aggregate(1d, (value, day) => value * (1 + day.GrossReturn)) - 1;
            var net = (1 + gross) * Math.Pow(1 - outcome.CostPerSide, 2) - 1;
            var bench = outcome.OutcomeKnown ? outcome.HoldingDays.Aggregate(1d, (value, day) => value * (1 + baseline[day.Date])) - 1 : 0;
            text.Append(CultureInfo.InvariantCulture, $"{outcome.Ticker},{signal.Direction},{outcome.SignalDate:yyyy-MM-dd},{signal.EntryDate:yyyy-MM-dd},");
            text.Append(CultureInfo.InvariantCulture, $"{signal.ExitDate:yyyy-MM-dd},{signal.ExitReason},{outcome.HoldingDays.Count},{outcome.OutcomeKnown},");
            text.Append(outcome.OutcomeKnown ? FormattableString.Invariant($"{gross:F8},{net:F8},{bench:F8},{net - bench:F8}\n") : ",,,\n");
        }
        return text.ToString();
    }

    // The analyzer's monthly table: mean net excess per trade, not account returns.
    private static string MonthsCsv(SignalStudyReport report) => "year,month,trades,unviewed_trades,mean_net_excess_per_trade\n" +
        String.Concat(report.Months.Select(month => FormattableString.Invariant(
            $"{month.Year},{month.Month},{month.Trades},{month.UnviewedTrades},{month.MeanNetExcessPerTrade:F8}\n")));

    // The manifest: input fingerprints, variants with identities and counts, cost, entry, window and the dates actually read.
    private static object Manifest(Request request, PublishedResearchScreen screen, IReadOnlyList<SignalStudyStock> stocks, IReadOnlyList<VariantOutcome> variants,
        ResearchStart start)
    {
        var read = stocks.SelectMany(stock => stock.Bars).Select(bar => SignalStudyOutcomes.SessionDate(bar.Timestamp)).ToArray();
        return new
        {
            codeVersion = CodeVersion,
            codeSha256 = start.Binding.Request.CodeSha256,
            registration = new { start.Binding.Request.StartId, start.Binding.Request.RequestSha256, start.Binding.Trials },
            inputs = new
            {
                catalog = Path.GetFullPath(request.CatalogPath), barsDatasetId = request.BarsDatasetId, actionsDatasetId = request.ActionsDatasetId,
                sessionsDatasetId = screen.Manifest.Inputs.ExchangeSessionsDatasetId, screen = screen.Name,
                screenManifestSha256 = screen.ManifestSha256, eligibleListSha256 = screen.RankedListSha256
            },
            entry = request.Entry == SignalStudyEntry.NextOpen ? "next_open" : "same_close",
            costBpsPerSide = request.CostBps, passRules = new SignalStudyPassRules(), warmupBars = request.WarmupBars,
            window = new { from = request.From, to = request.To },
            viewedWindow = new
            {
                firstBarRead = read.Length == 0 ? (DateOnly?)null : read.Min(), lastBarRead = read.Length == 0 ? (DateOnly?)null : read.Max(),
                studyFrom = request.From, studyTo = request.To, stocksRead = stocks.Count,
                note = "Bars before the window serve only the indicator warm-up; no bar after the window end was requested."
            },
            variants
        };
    }

    // The UTC instant of New York midnight on a date.
    private static DateTimeOffset NewYorkMidnight(DateOnly day) => new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue),
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York")), TimeSpan.Zero);

    // A file-name stem from a variant label (letters, digits, dot and dash kept).
    private static string FileStem(string label) => new(label.Select(character => Char.IsLetterOrDigit(character) || character is '.' or '-' ? character : '_').ToArray());

    // Indented JSON with dates as yyyy-MM-dd.
    private static string Json(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });

    // Parses the command's --name value pairs (--strategy repeatable) into a request.
    private static Request ParseRequest(string[] args)
    {
        if (args.Length % 2 != 0) throw new ArgumentException("Options must be --name value pairs.");
        var pairs = Enumerable.Range(0, args.Length / 2).Select(index => (Name: args[2 * index], Value: args[2 * index + 1])).ToArray();
        string Require(string name) => pairs.LastOrDefault(pair => pair.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } value
            ? value : throw new ArgumentException($"{name} is required.");
        var strategies = pairs.Where(pair => pair.Name.Equals("--strategy", StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Value).ToArray();
        if (strategies.Length == 0) throw new ArgumentException("--strategy is required (repeat it for each declared variant).");
        var entry = Require("--entry") switch
        {
            "next_open" => SignalStudyEntry.NextOpen,
            "same_close" => SignalStudyEntry.SameClose,
            var other => throw new ArgumentException($"--entry must be next_open or same_close, not {other}.")
        };
        var warmup = pairs.Any(pair => pair.Name == "--warmup-bars") ? Int32.Parse(Require("--warmup-bars"), CultureInfo.InvariantCulture) : 250;
        return new(Require("--catalog"), Require("--artifact-root"), Require("--screen"), Require("--bars"), Require("--actions"), strategies, entry,
            Decimal.Parse(Require("--cost-bps"), CultureInfo.InvariantCulture), Date(Require("--from")), Date(Require("--to")), Require("--out"), warmup);
    }

    // A yyyy-MM-dd date.
    private static DateOnly Date(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
