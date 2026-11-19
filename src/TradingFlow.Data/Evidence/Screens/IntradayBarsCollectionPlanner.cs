using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingFlow.Data.Evidence.Normalization;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;
using TradingFlow.Engine.Universe;

namespace TradingFlow.Data.Evidence.Screens;

/// <summary>One collection window: a timeframe and a New York time span requested on every listed session.</summary>
public sealed record IntradayWindow(string Name, string Timeframe, TimeOnly From, TimeOnly To);

/// <summary>
/// Serializable frozen evidence collection request wire contract shared by <c>TradingFlow.Data</c>,
/// <c>TradingFlow.Web</c>, and <c>TradingFlow.Cli</c> (byte-compatible with <c>EvidenceDatasetCollectionRequest</c>).
/// Credentials and provider base URLs are excluded.
/// </summary>
public sealed record FrozenEvidenceCollectionRequest
{
    public FrozenEvidenceCollectionRequest(
        DateTimeOffset createdAtUtc,
        string runId,
        string configHash,
        string codeVersion,
        string collectionPolicyVersion,
        string partitionPolicyVersion,
        EvidenceDatasetKind datasetKind,
        int schemaVersion,
        string normalizerVersion,
        string outputNamespace,
        IReadOnlyList<EvidenceCollectionRequest> requests,
        IReadOnlyCollection<EvidenceSecurityIdentity> securities,
        NewsAvailabilityEvidence? newsAvailabilityEvidence = null,
        IReadOnlyDictionary<string, string>? attributes = null)
    {
        if (createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The collection timestamp must be UTC.", nameof(createdAtUtc));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(configHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionPolicyVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionPolicyVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizerVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputNamespace);
        if (!Enum.IsDefined(datasetKind)) throw new ArgumentOutOfRangeException(nameof(datasetKind));
        if (schemaVersion <= 0) throw new ArgumentOutOfRangeException(nameof(schemaVersion));

        CreatedAtUtc = createdAtUtc;
        RunId = runId.Trim();
        ConfigHash = configHash.Trim().ToLowerInvariant();
        CodeVersion = codeVersion.Trim();
        CollectionPolicyVersion = collectionPolicyVersion.Trim();
        PartitionPolicyVersion = partitionPolicyVersion.Trim();
        DatasetKind = datasetKind;
        SchemaVersion = schemaVersion;
        NormalizerVersion = normalizerVersion.Trim();
        OutputNamespace = outputNamespace.Trim();
        Requests = (requests ?? throw new ArgumentNullException(nameof(requests))).ToArray();
        Securities = (securities ?? throw new ArgumentNullException(nameof(securities))).ToArray();
        NewsAvailabilityEvidence = newsAvailabilityEvidence;
        Attributes = (attributes ?? new Dictionary<string, string>())
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        NormalizationContractHash = EvidenceNormalizationJob.ComputeContractHash(
            DatasetKind,
            SchemaVersion,
            NormalizerVersion,
            Securities,
            new EvidenceObjectNamespace(OutputNamespace),
            NewsAvailabilityEvidence,
            Attributes);
        EffectiveConfigHash = EvidenceCanonicalJson.ComputeSha256(new
        {
            SourceConfigHash = ConfigHash,
            NormalizationContractHash
        });
        ValidateDatasetContract();
    }

    public DateTimeOffset CreatedAtUtc { get; }
    public string RunId { get; }
    public string ConfigHash { get; }
    public string CodeVersion { get; }
    public string CollectionPolicyVersion { get; }
    public string PartitionPolicyVersion { get; }
    public EvidenceDatasetKind DatasetKind { get; }
    public int SchemaVersion { get; }
    public string NormalizerVersion { get; }
    public string OutputNamespace { get; }
    public IReadOnlyList<EvidenceCollectionRequest> Requests { get; }
    public IReadOnlyCollection<EvidenceSecurityIdentity> Securities { get; }
    public NewsAvailabilityEvidence? NewsAvailabilityEvidence { get; }
    public IReadOnlyDictionary<string, string> Attributes { get; }
    public string NormalizationContractHash { get; }
    public string EffectiveConfigHash { get; }

    /// <summary>Materializes the immutable collection plan submitted to <c>EvidenceHttpCollector</c>.</summary>
    public EvidenceCollectionPlan CreatePlan() => new(
        CreatedAtUtc,
        RunId,
        EffectiveConfigHash,
        CodeVersion,
        CollectionPolicyVersion,
        PartitionPolicyVersion,
        Requests);

    /// <summary>Materializes the immutable normalization job submitted to <c>EvidenceNormalizationOrchestrator</c>.</summary>
    public EvidenceNormalizationJob CreateNormalizationJob(EvidenceCollectionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new EvidenceNormalizationJob(
            plan.JobId,
            DatasetKind,
            SchemaVersion,
            NormalizerVersion,
            Securities,
            new EvidenceObjectNamespace(OutputNamespace),
            NewsAvailabilityEvidence,
            Attributes);
    }

    // Mirrors EvidenceDatasetCollectionRequest contract invariants so Web and CLI plans stay strictly interchangeable.
    private void ValidateDatasetContract()
    {
        if (Requests.Count == 0)
            throw new ArgumentException("At least one collection request is required.", nameof(Requests));
        if (Securities.Count == 0 && DatasetKind != EvidenceDatasetKind.ExchangeSessions)
            throw new ArgumentException("At least one security identity is required.", nameof(Securities));
        var requiresNewsAvailability = DatasetKind == EvidenceDatasetKind.NewsRevisions;
        if (requiresNewsAvailability != NewsAvailabilityEvidence.HasValue)
            throw new ArgumentException("News availability evidence is required only for news datasets.", nameof(NewsAvailabilityEvidence));
        if (DatasetKind is not (EvidenceDatasetKind.MarketBarsAsTraded or EvidenceDatasetKind.MarketBarsResearchAdjusted
            or EvidenceDatasetKind.NewsRevisions or EvidenceDatasetKind.SipQuotes or EvidenceDatasetKind.ExchangeSessions))
            throw new NotSupportedException($"Dataset kind '{DatasetKind}' is not supported.");
        if (Requests.Any(r => !r.Provider.Equals("alpaca", StringComparison.Ordinal)))
            throw new ArgumentException("Normalized HTTP evidence workflow accepts Alpaca receipts only.", nameof(Requests));
        if (Requests.Any(r => Uri.TryCreate(r.Endpoint, UriKind.Absolute, out _)))
            throw new ArgumentException("Provider endpoints must be approved relative paths.", nameof(Requests));
        var expectedEndpoint = DatasetKind switch
        {
            EvidenceDatasetKind.MarketBarsAsTraded or EvidenceDatasetKind.MarketBarsResearchAdjusted => "/v2/stocks/bars",
            EvidenceDatasetKind.NewsRevisions => "/v1beta1/news",
            EvidenceDatasetKind.SipQuotes => "/v2/stocks/quotes",
            EvidenceDatasetKind.ExchangeSessions => "/v2/calendar",
            _ => throw new NotSupportedException($"Dataset kind '{DatasetKind}' is not supported.")
        };
        if (Requests.Any(r => !r.Endpoint.Equals(expectedEndpoint, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Dataset kind '{DatasetKind}' requires endpoint '{expectedEndpoint}'.", nameof(Requests));
        if (DatasetKind == EvidenceDatasetKind.MarketBarsAsTraded &&
            Requests.Any(r => !r.Adjustment.Equals("raw", StringComparison.Ordinal)))
            throw new ArgumentException("As-traded market bars require raw, unadjusted provider data.", nameof(Requests));
        var first = Requests[0];
        if (Requests.Any(request =>
                !request.Provider.Equals(first.Provider, StringComparison.Ordinal) ||
                !request.Endpoint.Equals(first.Endpoint, StringComparison.Ordinal) ||
                !request.DataFeed.Equals(first.DataFeed, StringComparison.Ordinal) ||
                !request.Adjustment.Equals(first.Adjustment, StringComparison.Ordinal) ||
                !request.Currency.Equals(first.Currency, StringComparison.Ordinal) ||
                request.RequestedStartUtc is null ||
                request.RequestedEndUtc is null ||
                request.AsOfDate is null))
        {
            throw new ArgumentException("The collection plan must contain homogeneous ranged requests.", nameof(Requests));
        }
    }
}

/// <summary>Inputs for Step 1 dry-run planning of 5-minute regular-session bars for a published eligible-list screen.</summary>
public sealed record MinuteBarsCollectionPlanRequest(
    string ScreenName,
    string ScreenFolder,
    DateOnly? From = null,
    DateOnly? To = null,
    int PriorSessions = 21,
    bool IncludeHeldSessions = true,
    int BatchSize = 50,
    string? RequestsOutputDirectory = null);

/// <summary>Plain-language dry-run estimate and frozen request reference returned by Step 1 before any download begins.</summary>
public sealed record MinuteBarsCollectionPlanSummary(
    string ScreenName,
    string ManifestSha256,
    string RankedListSha256,
    DateOnly From,
    DateOnly To,
    int PriorSessions,
    bool IncludeHeldSessions,
    int TotalRequiredStockSessions,
    int AlreadyCollectedStockSessions,
    int MissingStockSessions,
    int UniqueSymbolCount,
    int SessionCount,
    int HttpRequestBatchCount,
    long EstimatedBars,
    long EstimatedDownloadBytes,
    decimal EstimatedDownloadMegabytes,
    string EstimateBasis,
    bool AlreadyComplete,
    string? PlanFileName,
    string? RequestSha256);

/// <summary>
/// Shared intraday bar collection planner used by both <c>TradingFlow.Cli</c> and <c>TradingFlow.Web</c>.
/// Computes deduplicated required stock-sessions, subtracts stock-sessions already present in cataloged 5-minute
/// datasets matching the screen's <c>ranked_list_sha256</c>, estimates bar/byte volume, and writes idempotent frozen
/// collection request JSON files without issuing any provider HTTP calls.
/// </summary>
public sealed class IntradayBarsCollectionPlanner(
    IEvidenceCatalog catalog,
    IImmutableArtifactStore artifactStore)
{
    public const string CodeVersion = "evidence-plan-intraday-bars-v1";
    public const long RegularSessionFiveMinuteBarsPerDay = 78L;
    public const long EstimatedBytesPerFiveMinuteBar = 110L;

    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly TimeOnly RegularOpen = new(9, 30);
    private static readonly TimeOnly RegularClose = new(16, 0);
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The first five minutes, one bar per stock and session.</summary>
    public static readonly IntradayWindow OpeningFiveMinutes = new("opening-bars", "5Min", RegularOpen, new TimeOnly(9, 35));

    /// <summary>The regular session in one-minute bars; an early close simply returns fewer bars.</summary>
    public static readonly IntradayWindow RegularSessionMinutes = new("minute-bars", "1Min", RegularOpen, RegularClose);

    /// <summary>The regular session in five-minute bars (09:30-16:00 NY).</summary>
    public static readonly IntradayWindow RegularSessionFiveMinutes = new("selected-session-bars", "5Min", RegularOpen, RegularClose);

    /// <summary>The regular session in clock-aligned hourly bars (09:30-16:00 NY).</summary>
    public static readonly IntradayWindow RegularSessionHours = new("hourly-bars", "1Hour", RegularOpen, RegularClose);

    /// <summary>
    /// Runs Step 1 dry-run planning for regular-session 5-minute bars of a published unranked <c>eligible.csv</c>
    /// screen over <c>[From, To]</c>: combines eligible stock-sessions, optional prior baseline sessions, and optional
    /// held non-eligible sessions, subtracts stock-sessions already covered by cataloged 5-minute datasets, estimates
    /// download size, and writes the frozen request JSON idempotently when missing sessions remain.
    /// </summary>
    public async Task<(MinuteBarsCollectionPlanSummary Summary, FrozenEvidenceCollectionRequest? Request)> PlanScreenFiveMinuteBarsAsync(
        MinuteBarsCollectionPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PriorSessions < 0)
            throw new ArgumentOutOfRangeException(nameof(request.PriorSessions), "PriorSessions cannot be negative.");
        if (request.BatchSize is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(request.BatchSize), "BatchSize must be between 1 and 1000.");

        var screen = PublishedResearchScreen.Read(request.ScreenFolder);
        if (screen.Manifest.RankingRules is not null)
        {
            throw new InvalidOperationException(
                $"Screen '{screen.Name}' is a ranked screen. Use an unranked eligible.csv research screen for swing 5-minute bar planning.");
        }

        var from = request.From ?? screen.Manifest.From;
        var to = request.To ?? screen.Manifest.To;
        if (from > to)
            throw new ArgumentException($"Requested start date {from:yyyy-MM-dd} cannot follow end date {to:yyyy-MM-dd}.");
        if (from < screen.Manifest.From || to > screen.Manifest.To)
        {
            throw new ArgumentException(
                $"Requested window {from:yyyy-MM-dd} to {to:yyyy-MM-dd} lies outside screen '{screen.Name}' bounds " +
                $"({screen.Manifest.From:yyyy-MM-dd} to {screen.Manifest.To:yyyy-MM-dd}).");
        }

        var eligiblePath = Path.Combine(Path.GetFullPath(request.ScreenFolder), "eligible.csv");
        var eligible = ReadSessionSymbols(eligiblePath, "ticker")
            .Where(pair => pair.Key >= from && pair.Key <= to)
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        var barsDatasetId = screen.Manifest.Inputs.DailyBarsDatasetId;
        var dailyManifest = await catalog.GetDatasetAsync(barsDatasetId, cancellationToken)
            ?? throw new InvalidDataException($"Daily bars dataset '{barsDatasetId}' was not found in the evidence catalog.");
        var reader = new ParquetEvidencePartitionDataReader(new EvidenceParquetCodec(), artifactStore);
        var wantedSymbols = eligible.Values.SelectMany(symbols => symbols).ToHashSet(StringComparer.Ordinal);
        var traded = wantedSymbols.Count == 0
            ? new Dictionary<string, (string SecurityId, DateOnly[] Sessions)>(StringComparer.Ordinal)
            : await TradedSessionsAsync(reader, dailyManifest, wantedSymbols, cancellationToken);

        var required = BuildRequiredEligibleFiveMinuteSessions(eligible, traded, to, request.PriorSessions, request.IncludeHeldSessions);
        var totalRequired = required.Values.Sum(list => list.Count);
        var alreadyCovered = await ReadAlreadyCoveredFiveMinuteSessionsAsync(reader, screen.RankedListSha256, cancellationToken);
        var missing = SubtractCoveredSessions(required, alreadyCovered);
        var missingStockSessions = missing.Values.Sum(list => list.Count);
        var alreadyCollected = totalRequired - missingStockSessions;
        var estimatedBars = missingStockSessions * RegularSessionFiveMinuteBarsPerDay;
        var estimatedBytes = estimatedBars * EstimatedBytesPerFiveMinuteBar;
        var estimatedMb = Math.Round(estimatedBytes / (1024m * 1024m), 2, MidpointRounding.AwayFromZero);
        const string basis = "Estimated from 78 regular-session 5-minute bars (09:30-16:00 NY) per missing stock-session at ~110 bytes/bar; early-close sessions return fewer bars.";

        if (missingStockSessions == 0)
        {
            var uniqueSymbols = required.Values.SelectMany(list => list).Select(s => s.Symbol).Distinct(StringComparer.Ordinal).Count();
            return (new MinuteBarsCollectionPlanSummary(
                request.ScreenName,
                screen.ManifestSha256,
                screen.RankedListSha256,
                from,
                to,
                request.PriorSessions,
                request.IncludeHeldSessions,
                totalRequired,
                alreadyCollected,
                MissingStockSessions: 0,
                UniqueSymbolCount: uniqueSymbols,
                SessionCount: 0,
                HttpRequestBatchCount: 0,
                EstimatedBars: 0,
                EstimatedDownloadBytes: 0,
                EstimatedDownloadMegabytes: 0m,
                EstimateBasis: basis,
                AlreadyComplete: true,
                PlanFileName: null,
                RequestSha256: null), null);
        }

        var attributes = HourlyAttributes(eligiblePath, barsDatasetId);
        attributes["prior_sessions"] = request.PriorSessions.ToString(CultureInfo.InvariantCulture);
        attributes["include_held_sessions"] = request.IncludeHeldSessions ? "true" : "false";
        attributes["requested_from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        attributes["requested_to"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var collectionRequest = CreateRequest(missing, RegularSessionFiveMinutes, request.BatchSize, attributes);
        string? planFileName = null;
        string? requestSha256 = null;
        if (!string.IsNullOrWhiteSpace(request.RequestsOutputDirectory))
        {
            var serialized = JsonSerializer.Serialize(collectionRequest, RequestJsonOptions);
            var bytes = Encoding.UTF8.GetBytes(serialized);
            requestSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var safeScreen = SanitizeFileSegment(request.ScreenName);
            planFileName = $"five-minute-bars-{safeScreen}-{from:yyyyMMdd}-{to:yyyyMMdd}-{collectionRequest.ConfigHash[..12]}.json";
            var directory = Path.GetFullPath(request.RequestsOutputDirectory);
            Directory.CreateDirectory(directory);
            var targetPath = Path.Combine(directory, planFileName);
            if (File.Exists(targetPath))
            {
                var existingBytes = await File.ReadAllBytesAsync(targetPath, cancellationToken);
                if (!existingBytes.AsSpan().SequenceEqual(bytes))
                {
                    throw new InvalidOperationException(
                        $"Frozen request file '{planFileName}' already exists with different contents; refusing to overwrite it.");
                }
            }
            else
            {
                await File.WriteAllBytesAsync(targetPath, bytes, cancellationToken);
            }
        }

        var missingSymbols = missing.Values.SelectMany(list => list).Select(s => s.Symbol).Distinct(StringComparer.Ordinal).Count();
        return (new MinuteBarsCollectionPlanSummary(
            request.ScreenName,
            screen.ManifestSha256,
            screen.RankedListSha256,
            from,
            to,
            request.PriorSessions,
            request.IncludeHeldSessions,
            totalRequired,
            alreadyCollected,
            missingStockSessions,
            missingSymbols,
            missing.Count,
            collectionRequest.Requests.Count,
            estimatedBars,
            estimatedBytes,
            estimatedMb,
            basis,
            AlreadyComplete: false,
            PlanFileName: planFileName,
            RequestSha256: requestSha256), collectionRequest);
    }

    /// <summary>
    /// Combines eligible stock-sessions, optional prior baseline sessions, and optional post-eligibility held sessions
    /// into one deduplicated map keyed by trading session date and symbol.
    /// </summary>
    public static IReadOnlyDictionary<DateOnly, IReadOnlyCollection<EvidenceSecurityIdentity>> BuildRequiredEligibleFiveMinuteSessions(
        IReadOnlyDictionary<DateOnly, IReadOnlyCollection<string>> eligible,
        IReadOnlyDictionary<string, (string SecurityId, DateOnly[] Sessions)> traded,
        DateOnly to,
        int priorSessions,
        bool includeHeldSessions)
    {
        if (priorSessions < 0) throw new ArgumentOutOfRangeException(nameof(priorSessions));
        var combined = new SortedDictionary<DateOnly, Dictionary<string, EvidenceSecurityIdentity>>();

        void Merge(IReadOnlyDictionary<DateOnly, IReadOnlyCollection<EvidenceSecurityIdentity>> source)
        {
            foreach (var (session, securities) in source)
            {
                if (!combined.TryGetValue(session, out var bySymbol))
                {
                    combined[session] = bySymbol = new Dictionary<string, EvidenceSecurityIdentity>(StringComparer.Ordinal);
                }

                foreach (var security in securities)
                {
                    bySymbol[security.Symbol] = security;
                }
            }
        }

        if (priorSessions > 0)
        {
            Merge(WithBaselineSessions(eligible, traded, priorSessions));
        }
        else
        {
            Merge(EligibleSecurities(eligible, traded));
        }

        if (includeHeldSessions)
        {
            Merge(NonEligibleSecurities(eligible, traded, to));
        }

        return combined.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyCollection<EvidenceSecurityIdentity>)pair.Value.Values
                .OrderBy(item => item.Symbol, StringComparer.Ordinal)
                .ToArray());
    }

    /// <summary>
    /// Builds a frozen intraday bar collection request batching listed securities per New York trading session.
    /// </summary>
    public static FrozenEvidenceCollectionRequest CreateRequest(
        IReadOnlyDictionary<DateOnly, IReadOnlyCollection<EvidenceSecurityIdentity>> securitiesBySession,
        IntradayWindow window,
        int batchSize,
        IReadOnlyDictionary<string, string> sourceAttributes)
    {
        if (batchSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (securitiesBySession.Count == 0 || securitiesBySession.Values.All(list => list.Count == 0))
            throw new InvalidDataException("No stock sessions to collect.");

        var requests = new List<EvidenceCollectionRequest>();
        foreach (var (session, securities) in securitiesBySession.OrderBy(pair => pair.Key))
        {
            var symbols = securities.Select(security => security.Symbol).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            foreach (var (chunk, index) in symbols.Chunk(batchSize).Select((chunk, index) => (chunk, index)))
            {
                requests.Add(new EvidenceCollectionRequest(
                    $"{window.Name}-{session:yyyyMMdd}-{index:000}",
                    "alpaca",
                    "/v2/stocks/bars",
                    chunk,
                    At(session, window.From),
                    At(session, window.To),
                    "sip",
                    "raw",
                    "USD",
                    session,
                    new Dictionary<string, string> { ["timeframe"] = window.Timeframe }));
            }
        }

        var first = securitiesBySession.Keys.Min();
        var last = securitiesBySession.Keys.Max();
        var identities = securitiesBySession.Values.SelectMany(list => list)
            .DistinctBy(security => security.Symbol, StringComparer.Ordinal)
            .OrderBy(security => security.Symbol, StringComparer.Ordinal)
            .ToArray();
        var sessions = securitiesBySession.OrderBy(pair => pair.Key)
            .Select(pair => new { session = pair.Key, symbols = pair.Value.Select(item => item.Symbol).Order(StringComparer.Ordinal).ToArray() });
        var configHash = EvidenceCanonicalJson.ComputeSha256(new { CodeVersion, window, batchSize, sessions, sourceAttributes });
        var attributes = new Dictionary<string, string>(sourceAttributes)
        {
            ["stock_sessions"] = securitiesBySession.Values.Sum(list => list.Count).ToString(CultureInfo.InvariantCulture),
            ["new_york_window"] = $"{window.From:HH\\:mm}-{window.To:HH\\:mm}"
        };
        return new FrozenEvidenceCollectionRequest(
            At(last, RegularClose),
            $"{window.Name}-as-traded-{first:yyyyMMdd}-{last:yyyyMMdd}",
            configHash,
            CodeVersion,
            "alpaca-intraday-bars-collection-v1",
            "alpaca-intraday-bars-partition-v1",
            EvidenceDatasetKind.MarketBarsAsTraded,
            1,
            "alpaca-bars-normalizer-v1",
            $"normalized/{window.Name}-as-traded",
            requests,
            identities,
            attributes: attributes);
    }

    /// <summary>
    /// Adds each eligible stock-session's own preceding <paramref name="baselineSessions"/> traded sessions so
    /// opening-volume and indicator baselines have complete intraday bar history.
    /// </summary>
    public static IReadOnlyDictionary<DateOnly, IReadOnlyCollection<EvidenceSecurityIdentity>> WithBaselineSessions(
        IReadOnlyDictionary<DateOnly, IReadOnlyCollection<string>> eligible,
        IReadOnlyDictionary<string, (string SecurityId, DateOnly[] Sessions)> traded,
        int baselineSessions)
    {
        if (baselineSessions < 1) throw new ArgumentOutOfRangeException(nameof(baselineSessions));
        var needed = new SortedDictionary<DateOnly, Dictionary<string, EvidenceSecurityIdentity>>();
        foreach (var (session, symbols) in eligible)
        {
            foreach (var symbol in symbols)
            {
                if (!traded.TryGetValue(symbol, out var history))
                    throw new InvalidDataException($"{symbol} has no daily bars in the dataset.");
                var index = Array.BinarySearch(history.Sessions, session);
                if (index < 0)
                    throw new InvalidDataException($"{symbol} was eligible on {session:yyyy-MM-dd} without a daily bar that day.");
                foreach (var day in history.Sessions.Skip(Math.Max(0, index - baselineSessions)).Take(Math.Min(index, baselineSessions) + 1))
                {
                    if (!needed.TryGetValue(day, out var list)) needed[day] = list = new(StringComparer.Ordinal);
                    list[symbol] = new EvidenceSecurityIdentity(history.SecurityId, symbol);
                }
            }
        }

        return needed.ToDictionary(pair => pair.Key, pair => (IReadOnlyCollection<EvidenceSecurityIdentity>)pair.Value.Values.ToArray());
    }

    /// <summary>
    /// Returns each listed stock's traded sessions after its first eligible session through <paramref name="to"/> on
    /// which it is not eligible, so open holdings still receive intraday bars for stop/target replay.
    /// </summary>
    public static IReadOnlyDictionary<DateOnly, IReadOnlyCollection<EvidenceSecurityIdentity>> NonEligibleSecurities(
        IReadOnlyDictionary<DateOnly, IReadOnlyCollection<string>> eligible,
        IReadOnlyDictionary<string, (string SecurityId, DateOnly[] Sessions)> traded,
        DateOnly to)
    {
        var eligibleDays = eligible.SelectMany(pair => pair.Value.Select(symbol => (symbol, pair.Key))).ToHashSet();
        var firstEligible = eligibleDays.GroupBy(item => item.symbol, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Min(item => item.Key), StringComparer.Ordinal);
        var needed = new SortedDictionary<DateOnly, List<EvidenceSecurityIdentity>>();
        foreach (var (symbol, first) in firstEligible)
        {
            var history = traded.TryGetValue(symbol, out var found)
                ? found
                : throw new InvalidDataException($"{symbol} has no daily bars in the dataset.");
            foreach (var day in history.Sessions.Where(day => day > first && day <= to && !eligibleDays.Contains((symbol, day))))
            {
                if (!needed.TryGetValue(day, out var list)) needed[day] = list = [];
                list.Add(new EvidenceSecurityIdentity(history.SecurityId, symbol));
            }
        }

        return needed.ToDictionary(pair => pair.Key, pair => (IReadOnlyCollection<EvidenceSecurityIdentity>)pair.Value);
    }

    /// <summary>Maps each eligible stock-session to its catalog security identity, failing if missing from daily bars.</summary>
    public static IReadOnlyDictionary<DateOnly, IReadOnlyCollection<EvidenceSecurityIdentity>> EligibleSecurities(
        IReadOnlyDictionary<DateOnly, IReadOnlyCollection<string>> eligible,
        IReadOnlyDictionary<string, (string SecurityId, DateOnly[] Sessions)> traded) =>
        eligible.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyCollection<EvidenceSecurityIdentity>)pair.Value.Select(symbol =>
                traded.TryGetValue(symbol, out var history)
                    ? new EvidenceSecurityIdentity(history.SecurityId, symbol)
                    : throw new InvalidDataException($"{symbol} has no daily bars in the dataset.")).ToArray());

    /// <summary>Builds dataset binding attributes (eligible/ranked list SHA-256, daily bars ID, optional screen manifest SHA-256).</summary>
    public static Dictionary<string, string> HourlyAttributes(string eligiblePath, string dailyDatasetId)
    {
        var listSha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(eligiblePath)));
        var attributes = new Dictionary<string, string>
        {
            ["ranked_list_sha256"] = listSha,
            ["eligible_list_sha256"] = listSha,
            ["daily_bars_dataset_id"] = dailyDatasetId
        };
        var manifest = Path.Combine(Path.GetDirectoryName(eligiblePath)!, "screen-manifest.json");
        if (File.Exists(manifest))
        {
            attributes["screen_manifest_sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(manifest)));
        }

        return attributes;
    }

    /// <summary>Reads traded daily session dates per wanted symbol from a published daily bar dataset.</summary>
    public static async Task<Dictionary<string, (string SecurityId, DateOnly[] Sessions)>> TradedSessionsAsync(
        ParquetEvidencePartitionDataReader reader,
        EvidenceDatasetManifest bars,
        IReadOnlySet<string> wanted,
        CancellationToken cancellationToken)
    {
        var traded = new Dictionary<string, (string, DateOnly[])>(StringComparer.Ordinal);
        await foreach (var partition in reader.ReadMarketBarPartitionsAsync(bars, cancellationToken))
        {
            foreach (var group in partition.Where(row => wanted.Contains(row.Symbol)).GroupBy(row => row.Symbol, StringComparer.Ordinal))
            {
                traded[group.Key] = (
                    group.First().SecurityId,
                    group.Select(row => DateOnly.FromDateTime(row.BarStartUtc.UtcDateTime)).Distinct().Order().ToArray());
            }
        }

        return traded;
    }

    /// <summary>Reads session dates and upper-case symbol lists from a CSV screen output file.</summary>
    public static Dictionary<DateOnly, IReadOnlyCollection<string>> ReadSessionSymbols(string path, string symbolColumn)
    {
        var rows = ReadCsv(path, "session_date", symbolColumn);
        return rows.GroupBy(row => DateOnly.ParseExact(row[0], "yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyCollection<string>)group.Select(row => row[1].Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Reads session dates, symbols, and issuer/security identifiers from a ranked-daily-list.csv file.</summary>
    public static Dictionary<DateOnly, IReadOnlyCollection<EvidenceSecurityIdentity>> ReadSessionSecurities(string path) =>
        ReadCsv(path, "session_date", "symbol", "issuer_id")
            .GroupBy(row => DateOnly.ParseExact(row[0], "yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyCollection<EvidenceSecurityIdentity>)group.Select(row => new EvidenceSecurityIdentity(row[2], row[1])).ToArray());

    /// <summary>Full New York regular trading session window required for swing 5-minute bar coverage.</summary>
    public const string FullRegularSessionWindow = "09:30-16:00";

    /// <summary>
    /// True when <paramref name="dataset"/> is a SIP 5-minute bar dataset whose optional <c>new_york_window</c>
    /// attribute matches the full regular session (<c>09:30-16:00</c>), excluding opening-only 09:30-09:35 datasets.
    /// </summary>
    public static bool IsFullRegularSessionFiveMinuteDataset(EvidenceDatasetManifest dataset) =>
        String.Equals(dataset.DataFeed, "sip", StringComparison.OrdinalIgnoreCase) &&
        dataset.Partitions.Count > 0 &&
        dataset.Partitions.All(partition => String.Equals(partition.Provenance.Timeframe, "5m", StringComparison.OrdinalIgnoreCase)) &&
        (!dataset.Attributes.TryGetValue("new_york_window", out var window) ||
         String.Equals(window, FullRegularSessionWindow, StringComparison.Ordinal));

    // Reads already-collected (upper-case symbol, New York date) pairs from existing full-RTH 5m catalog datasets
    // bound to the screen. Uses O(partitions) provenance metadata for single-session partitions and reads multi-day
    // partitions individually via ReadMarketBarsAsync so session-sharded 5m datasets never fail single-partition checks.
    private async Task<HashSet<(string Symbol, DateOnly Session)>> ReadAlreadyCoveredFiveMinuteSessionsAsync(
        ParquetEvidencePartitionDataReader reader,
        string rankedListSha256,
        CancellationToken cancellationToken)
    {
        var covered = new HashSet<(string Symbol, DateOnly Session)>();
        var datasets = await catalog.FindDatasetsAsync(new EvidenceDatasetQuery(EvidenceDatasetKind.MarketBarsAsTraded), cancellationToken);
        foreach (var dataset in datasets)
        {
            if (!String.Equals(dataset.Attributes.GetValueOrDefault("ranked_list_sha256"), rankedListSha256, StringComparison.OrdinalIgnoreCase) ||
                !IsFullRegularSessionFiveMinuteDataset(dataset))
            {
                continue;
            }

            foreach (var partition in dataset.Partitions)
            {
                var provenance = partition.Provenance;
                if (!String.Equals(provenance.Timeframe, "5m", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var span = provenance.RequestedEndUtc - provenance.RequestedStartUtc;
                if (provenance.Symbols.Count > 0 &&
                    provenance.AsOfDate is { } day &&
                    provenance.RequestedStartUtc <= At(day, RegularOpen) &&
                    provenance.RequestedEndUtc >= At(day, RegularClose) &&
                    span <= TimeSpan.FromHours(24))
                {
                    foreach (var symbol in provenance.Symbols)
                    {
                        covered.Add((symbol.Trim().ToUpperInvariant(), day));
                    }
                    continue;
                }

                // Multi-day partition fallback: read this partition alone via ReadMarketBarsAsync (never ReadMarketBarPartitionsAsync).
                var singlePartitionManifest = new EvidenceDatasetManifest(
                    dataset.Kind,
                    dataset.SchemaVersion,
                    dataset.CreatedAtUtc,
                    dataset.CollectionJobId,
                    dataset.CollectionPlanHash,
                    dataset.ConfigHash,
                    dataset.CodeVersion,
                    dataset.NormalizerVersion,
                    dataset.DataFeed,
                    [partition],
                    dataset.Quality,
                    dataset.Attributes);
                foreach (var row in await reader.ReadMarketBarsAsync(singlePartitionManifest, cancellationToken))
                {
                    if (!String.Equals(row.Timeframe, "5m", StringComparison.OrdinalIgnoreCase)) continue;
                    var session = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(row.BarStartUtc, NewYork).DateTime);
                    if (provenance.RequestedStartUtc <= At(session, RegularOpen) &&
                        provenance.RequestedEndUtc >= At(session, RegularClose))
                    {
                        covered.Add((row.Symbol.Trim().ToUpperInvariant(), session));
                    }
                }
            }

            // Also include stock-sessions whose full-session 5Min collection request completed cleanly (even if zero trades).
            if (!string.IsNullOrWhiteSpace(dataset.CollectionJobId))
            {
                var plan = await catalog.GetCollectionPlanAsync(dataset.CollectionJobId, cancellationToken);
                var checkpoint = plan is null ? null : await catalog.GetCollectionCheckpointAsync(dataset.CollectionJobId, cancellationToken);
                if (plan is not null && checkpoint is not null)
                {
                    var completedIds = checkpoint.CompletedRequestIds.ToHashSet(StringComparer.Ordinal);
                    foreach (var req in plan.Requests)
                    {
                        if (!completedIds.Contains(req.RequestId) || req.AsOfDate is not { } day ||
                            !req.Parameters.TryGetValue("timeframe", out var tf) || tf != RegularSessionFiveMinutes.Timeframe ||
                            req.RequestedStartUtc > At(day, RegularOpen) || req.RequestedEndUtc < At(day, RegularClose))
                        {
                            continue;
                        }

                        foreach (var symbol in req.Symbols)
                        {
                            covered.Add((symbol.Trim().ToUpperInvariant(), day));
                        }
                    }
                }
            }
        }

        return covered;
    }

    // Removes already-collected (symbol, session) pairs from the required map and drops empty sessions.
    private static IReadOnlyDictionary<DateOnly, IReadOnlyCollection<EvidenceSecurityIdentity>> SubtractCoveredSessions(
        IReadOnlyDictionary<DateOnly, IReadOnlyCollection<EvidenceSecurityIdentity>> required,
        IReadOnlySet<(string Symbol, DateOnly Session)> covered)
    {
        var missing = new SortedDictionary<DateOnly, IReadOnlyCollection<EvidenceSecurityIdentity>>();
        foreach (var (session, securities) in required)
        {
            var remaining = securities
                .Where(security => !covered.Contains((security.Symbol.Trim().ToUpperInvariant(), session)))
                .OrderBy(security => security.Symbol, StringComparer.Ordinal)
                .ToArray();
            if (remaining.Length > 0)
            {
                missing[session] = remaining;
            }
        }

        return missing;
    }

    // Reads named columns from a simple comma-separated file with a header row.
    private static IEnumerable<string[]> ReadCsv(string path, params string[] columns)
    {
        using var lines = File.ReadLines(Path.GetFullPath(path)).GetEnumerator();
        if (!lines.MoveNext()) throw new InvalidDataException($"{path} is empty.");
        var header = lines.Current.Split(',');
        var indexes = columns.Select(column => Array.IndexOf(header, column) is var index and >= 0
            ? index
            : throw new InvalidDataException($"{path} has no {column} column.")).ToArray();
        var rows = new List<string[]>();
        while (lines.MoveNext())
        {
            if (lines.Current.Length == 0) continue;
            var cells = lines.Current.Split(',');
            rows.Add(indexes.Select(index => cells[index]).ToArray());
        }

        return rows;
    }

    // Converts a New York wall-clock time on a trading session to UTC, honoring DST.
    private static DateTimeOffset At(DateOnly session, TimeOnly time)
    {
        var local = session.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, NewYork.GetUtcOffset(local)).ToUniversalTime();
    }

    // Sanitizes a screen name into a safe single-segment filename token.
    private static string SanitizeFileSegment(string value)
    {
        var cleaned = new string(value.Trim().Select(ch => Char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());
        return String.IsNullOrWhiteSpace(cleaned) ? "screen" : cleaned;
    }
}
