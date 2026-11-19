using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Normalization;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Data.Evidence.Screens;
using TradingFlow.Domain.Research;
using TradingFlow.Research.Orchestration;

namespace TradingFlow.Cli;

/// <summary>
/// Thin CLI adapter over <see cref="IntradayBarsCollectionPlanner"/> preserving all existing
/// <c>evidence-plan-opening-bars</c> and <c>evidence-plan-minute-bars</c> options and extending
/// <c>--eligible</c> with <c>--timeframe 5Min [--prior-sessions 20] [--include-held-sessions true]</c>.
/// </summary>
public static class IntradayBarsCollectionPlanCommand
{
    public const string OpeningBarsName = "evidence-plan-opening-bars";
    public const string MinuteBarsName = "evidence-plan-minute-bars";

    /// <summary>The first five minutes, one bar per stock and session.</summary>
    public static readonly IntradayWindow OpeningFiveMinutes = IntradayBarsCollectionPlanner.OpeningFiveMinutes;

    /// <summary>The regular session in one-minute bars; an early close simply returns fewer bars.</summary>
    public static readonly IntradayWindow RegularSessionMinutes = IntradayBarsCollectionPlanner.RegularSessionMinutes;

    /// <summary>The regular session in five-minute bars, for the selected stock-sessions and the sessions their baselines read.</summary>
    public static readonly IntradayWindow RegularSessionFiveMinutes = IntradayBarsCollectionPlanner.RegularSessionFiveMinutes;

    /// <summary>The regular session in clock-aligned hourly bars (Alpaca 1Hour).</summary>
    public static readonly IntradayWindow RegularSessionHours = IntradayBarsCollectionPlanner.RegularSessionHours;

    /// <summary>Plans opening 09:30-09:35 NY five-minute bars for daily-eligible stocks and their baseline sessions.</summary>
    public static async Task<int> RunOpeningBarsAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        var options = Parse(args);
        var store = new FileSystemImmutableArtifactStore(new ImmutableArtifactStoreOptions(Path.GetFullPath(Require(options, "--artifact-root"))));
        var catalog = new SqliteEvidenceCatalog(new EvidenceCatalogOptions(Path.GetFullPath(Require(options, "--catalog")),
            EvidenceCatalogOpenMode.OpenExisting), store);
        var datasetId = Require(options, "--bars");
        var bars = await catalog.GetDatasetAsync(datasetId, cancellationToken) ?? throw new InvalidDataException($"Dataset '{datasetId}' was not found.");
        var eligible = IntradayBarsCollectionPlanner.ReadSessionSymbols(Require(options, "--eligible"), "ticker");
        var baselineSessions = Int32.Parse(Require(options, "--baseline-sessions"), CultureInfo.InvariantCulture);
        var traded = await IntradayBarsCollectionPlanner.TradedSessionsAsync(
            new ParquetEvidencePartitionDataReader(new EvidenceParquetCodec(), store),
            bars,
            eligible.Values.SelectMany(symbols => symbols).ToHashSet(StringComparer.Ordinal),
            cancellationToken);
        var needed = WithBaselineSessions(eligible, traded, baselineSessions);
        return await WriteAsync(
            options,
            output,
            CreateRequest(
                needed,
                OpeningFiveMinutes,
                Batch(options),
                new Dictionary<string, string>
                {
                    ["daily_bars_dataset_id"] = datasetId,
                    ["baseline_sessions"] = baselineSessions.ToString(CultureInfo.InvariantCulture)
                }),
            cancellationToken);
    }

    /// <summary>
    /// Plans minute, 5-minute, or hourly bars from either <c>--ranked ranked-daily-list.csv</c> or
    /// <c>--eligible eligible.csv</c> (<c>--timeframe 1Hour</c> or <c>--timeframe 5Min</c>).
    /// </summary>
    public static async Task<int> RunMinuteBarsAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        var options = Parse(args);
        if (options.ContainsKey("--eligible"))
        {
            return await RunEligibleIntradayBarsAsync(options, output, cancellationToken);
        }

        var ranked = Path.GetFullPath(Require(options, "--ranked"));
        var selected = IntradayBarsCollectionPlanner.ReadSessionSecurities(ranked);
        var attributes = new Dictionary<string, string>
        {
            ["ranked_list_sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(ranked)))
        };
        var timeframe = options.TryGetValue("--timeframe", out var value) ? value : RegularSessionMinutes.Timeframe;
        if (timeframe == RegularSessionMinutes.Timeframe)
        {
            return await WriteAsync(options, output, CreateRequest(selected, RegularSessionMinutes, Batch(options), attributes), cancellationToken);
        }

        if (timeframe != RegularSessionFiveMinutes.Timeframe)
        {
            throw new ArgumentException($"--timeframe must be {RegularSessionMinutes.Timeframe} or {RegularSessionFiveMinutes.Timeframe}.");
        }

        var needed = await WithPriorTradedSessionsAsync(options, selected, attributes, cancellationToken);
        return await WriteAsync(options, output, CreateRequest(needed, RegularSessionFiveMinutes, Batch(options), attributes), cancellationToken);
    }

    /// <summary>Returns non-eligible held sessions between each stock's first eligible session and <paramref name="to"/>.</summary>
    public static IReadOnlyDictionary<DateOnly, IReadOnlyCollection<EvidenceSecurityIdentity>> NonEligibleSecurities(
        IReadOnlyDictionary<DateOnly, IReadOnlyCollection<string>> eligible,
        IReadOnlyDictionary<string, (string SecurityId, DateOnly[] Sessions)> traded,
        DateOnly to) =>
        IntradayBarsCollectionPlanner.NonEligibleSecurities(eligible, traded, to);

    /// <summary>Maps each eligible stock-session to its catalog security identity.</summary>
    public static IReadOnlyDictionary<DateOnly, IReadOnlyCollection<EvidenceSecurityIdentity>> EligibleSecurities(
        IReadOnlyDictionary<DateOnly, IReadOnlyCollection<string>> eligible,
        IReadOnlyDictionary<string, (string SecurityId, DateOnly[] Sessions)> traded) =>
        IntradayBarsCollectionPlanner.EligibleSecurities(eligible, traded);

    /// <summary>Builds eligible-list dataset binding attributes.</summary>
    public static Dictionary<string, string> HourlyAttributes(string eligiblePath, string dailyDatasetId) =>
        IntradayBarsCollectionPlanner.HourlyAttributes(eligiblePath, dailyDatasetId);

    /// <summary>Builds a frozen collection request delegating to <see cref="IntradayBarsCollectionPlanner.CreateRequest"/>.</summary>
    public static EvidenceDatasetCollectionRequest CreateRequest(
        IReadOnlyDictionary<DateOnly, IReadOnlyCollection<EvidenceSecurityIdentity>> securitiesBySession,
        IntradayWindow window,
        int batchSize,
        IReadOnlyDictionary<string, string> sourceAttributes) =>
        ToOrchestrationRequest(IntradayBarsCollectionPlanner.CreateRequest(securitiesBySession, window, batchSize, sourceAttributes));

    /// <summary>Adds each eligible stock-session's preceding baseline traded sessions.</summary>
    public static IReadOnlyDictionary<DateOnly, IReadOnlyCollection<EvidenceSecurityIdentity>> WithBaselineSessions(
        IReadOnlyDictionary<DateOnly, IReadOnlyCollection<string>> eligible,
        IReadOnlyDictionary<string, (string SecurityId, DateOnly[] Sessions)> traded,
        int baselineSessions) =>
        IntradayBarsCollectionPlanner.WithBaselineSessions(eligible, traded, baselineSessions);

    // Plans either 1Hour (existing behavior) or 5Min (swing eligible-list with prior baseline + held sessions) bars.
    private static async Task<int> RunEligibleIntradayBarsAsync(
        IReadOnlyDictionary<string, string> options,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var timeframe = Require(options, "--timeframe");
        if (String.Equals(timeframe, RegularSessionFiveMinutes.Timeframe, StringComparison.Ordinal))
        {
            return await RunEligibleFiveMinuteBarsAsync(options, output, cancellationToken);
        }

        if (!String.Equals(timeframe, RegularSessionHours.Timeframe, StringComparison.Ordinal))
        {
            throw new ArgumentException($"--eligible plans {RegularSessionHours.Timeframe} or {RegularSessionFiveMinutes.Timeframe} bars; got '{timeframe}'.");
        }

        var eligiblePath = Path.GetFullPath(Require(options, "--eligible"));
        var from = options.TryGetValue("--from", out var fromText) ? Date(fromText) : DateOnly.MinValue;
        var to = options.TryGetValue("--to", out var toText) ? Date(toText) : DateOnly.MaxValue;
        var eligible = IntradayBarsCollectionPlanner.ReadSessionSymbols(eligiblePath, "ticker")
            .Where(pair => pair.Key >= from && pair.Key <= to)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var store = new FileSystemImmutableArtifactStore(new ImmutableArtifactStoreOptions(Path.GetFullPath(Require(options, "--artifact-root"))));
        var catalog = new SqliteEvidenceCatalog(new EvidenceCatalogOptions(Path.GetFullPath(Require(options, "--catalog")),
            EvidenceCatalogOpenMode.ReadOnly), store);
        var datasetId = Require(options, "--bars");
        var bars = await catalog.GetDatasetAsync(datasetId, cancellationToken) ?? throw new InvalidDataException($"Dataset '{datasetId}' was not found.");
        var traded = await IntradayBarsCollectionPlanner.TradedSessionsAsync(
            new ParquetEvidencePartitionDataReader(new EvidenceParquetCodec(), store),
            bars,
            eligible.Values.SelectMany(symbols => symbols).ToHashSet(StringComparer.Ordinal),
            cancellationToken);
        var complement = options.TryGetValue("--non-eligible-sessions", out var flag) && Boolean.Parse(flag);
        var securities = complement ? NonEligibleSecurities(eligible, traded, to) : EligibleSecurities(eligible, traded);
        var attributes = HourlyAttributes(eligiblePath, datasetId);
        if (complement) attributes["session_scope"] = "non_eligible_traded_sessions";
        return await WriteAsync(options, output, CreateRequest(securities, RegularSessionHours, Batch(options), attributes), cancellationToken);
    }

    // Plans regular-session 5Min bars for an unranked eligible.csv screen, subtracting already-collected 5m sessions.
    private static async Task<int> RunEligibleFiveMinuteBarsAsync(
        IReadOnlyDictionary<string, string> options,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var eligiblePath = Path.GetFullPath(Require(options, "--eligible"));
        var screenFolder = Path.GetDirectoryName(eligiblePath)
            ?? throw new InvalidDataException($"Could not determine screen directory for '{eligiblePath}'.");
        var screenName = Path.GetFileName(screenFolder);
        var from = options.TryGetValue("--from", out var fromText) ? Date(fromText) : (DateOnly?)null;
        var to = options.TryGetValue("--to", out var toText) ? Date(toText) : (DateOnly?)null;
        var priorSessions = options.TryGetValue("--prior-sessions", out var priorText)
            ? Int32.Parse(priorText, CultureInfo.InvariantCulture)
            : 21;
        var includeHeld = !options.TryGetValue("--include-held-sessions", out var heldText) || Boolean.Parse(heldText);
        var batchSize = options.TryGetValue("--batch", out var batchText)
            ? Int32.Parse(batchText, CultureInfo.InvariantCulture)
            : 50;

        var store = new FileSystemImmutableArtifactStore(new ImmutableArtifactStoreOptions(Path.GetFullPath(Require(options, "--artifact-root"))));
        var catalog = new SqliteEvidenceCatalog(new EvidenceCatalogOptions(Path.GetFullPath(Require(options, "--catalog")),
            EvidenceCatalogOpenMode.ReadOnly), store);
        var planner = new IntradayBarsCollectionPlanner(catalog, store);
        var (summary, request) = await planner.PlanScreenFiveMinuteBarsAsync(
            new MinuteBarsCollectionPlanRequest(screenName, screenFolder, from, to, priorSessions, includeHeld, batchSize),
            cancellationToken);

        if (request is not null && options.TryGetValue("--out", out var outPath) && !String.IsNullOrWhiteSpace(outPath))
        {
            var fullOut = Path.GetFullPath(outPath);
            var bytes = System.Text.Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(ToOrchestrationRequest(request), new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            if (File.Exists(fullOut))
            {
                var existingBytes = await File.ReadAllBytesAsync(fullOut, cancellationToken);
                if (!existingBytes.AsSpan().SequenceEqual(bytes))
                {
                    throw new InvalidOperationException(
                        $"Frozen request file '{fullOut}' already exists with different contents; refusing to overwrite it.");
                }
            }
            else
            {
                await File.WriteAllBytesAsync(fullOut, bytes, cancellationToken);
            }
        }

        output.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    // Adds prior traded sessions for ranked-list 5Min planning via the shared planner.
    private static async Task<IReadOnlyDictionary<DateOnly, IReadOnlyCollection<EvidenceSecurityIdentity>>> WithPriorTradedSessionsAsync(
        IReadOnlyDictionary<string, string> options,
        IReadOnlyDictionary<DateOnly, IReadOnlyCollection<EvidenceSecurityIdentity>> selected,
        Dictionary<string, string> attributes,
        CancellationToken cancellationToken)
    {
        var priorSessions = Int32.Parse(Require(options, "--prior-sessions"), CultureInfo.InvariantCulture);
        var store = new FileSystemImmutableArtifactStore(new ImmutableArtifactStoreOptions(Path.GetFullPath(Require(options, "--artifact-root"))));
        var catalog = new SqliteEvidenceCatalog(new EvidenceCatalogOptions(Path.GetFullPath(Require(options, "--catalog")),
            EvidenceCatalogOpenMode.OpenExisting), store);
        var datasetId = Require(options, "--bars");
        var bars = await catalog.GetDatasetAsync(datasetId, cancellationToken) ?? throw new InvalidDataException($"Dataset '{datasetId}' was not found.");
        var symbols = selected.ToDictionary(pair => pair.Key, pair => (IReadOnlyCollection<string>)pair.Value.Select(item => item.Symbol).ToArray());
        var traded = await IntradayBarsCollectionPlanner.TradedSessionsAsync(
            new ParquetEvidencePartitionDataReader(new EvidenceParquetCodec(), store),
            bars,
            symbols.Values.SelectMany(list => list).ToHashSet(StringComparer.Ordinal),
            cancellationToken);
        attributes["daily_bars_dataset_id"] = datasetId;
        attributes["prior_sessions"] = priorSessions.ToString(CultureInfo.InvariantCulture);
        return WithBaselineSessions(symbols, traded, priorSessions);
    }

    // Converts the shared FrozenEvidenceCollectionRequest to EvidenceDatasetCollectionRequest for CLI compatibility.
    private static EvidenceDatasetCollectionRequest ToOrchestrationRequest(FrozenEvidenceCollectionRequest request) =>
        new(
            request.CreatedAtUtc,
            request.RunId,
            request.ConfigHash,
            request.CodeVersion,
            request.CollectionPolicyVersion,
            request.PartitionPolicyVersion,
            request.DatasetKind,
            request.SchemaVersion,
            request.NormalizerVersion,
            request.OutputNamespace,
            request.Requests,
            request.Securities,
            request.NewsAvailabilityEvidence,
            request.Attributes);

    // Writes the serialized frozen request JSON and prints its compact count summary.
    private static async Task<int> WriteAsync(
        IReadOnlyDictionary<string, string> options,
        TextWriter output,
        EvidenceDatasetCollectionRequest request,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(Require(options, "--out"));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), cancellationToken);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            Request = path,
            Requests = request.Requests.Count,
            Securities = request.Securities.Count,
            StockSessions = request.Attributes["stock_sessions"]
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static int Batch(IReadOnlyDictionary<string, string> options) => Int32.Parse(Require(options, "--batch"), CultureInfo.InvariantCulture);
    private static DateOnly Date(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static Dictionary<string, string> Parse(string[] args)
    {
        if (args.Length % 2 != 0) throw new ArgumentException("Options must be --name value pairs.");
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2) options[args[index]] = args[index + 1];
        return options;
    }

    private static string Require(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !String.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"{name} is required.");
}
