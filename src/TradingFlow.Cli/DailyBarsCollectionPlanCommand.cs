using System.Globalization;
using System.Text.Json;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Normalization;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Research;
using TradingFlow.Research.Orchestration;

namespace TradingFlow.Cli;

/// <summary>
/// Turns a published security master into a frozen as-traded daily-bar collection request that the
/// existing evidence-collect-normalize command runs. Contains no download or price logic.
/// </summary>
public static class DailyBarsCollectionPlanCommand
{
    public const string Name = "evidence-plan-daily-bars";
    private const string CodeVersion = "evidence-plan-daily-bars-v1";
    private static readonly string[] ListedExchanges = ["AMEX", "ARCA", "BATS", "NASDAQ", "NYSE", "TXSE"];
    private static readonly System.Text.RegularExpressions.Regex TickerPattern = new(@"^[A-Z][A-Z0-9]{0,5}(\.[A-Z0-9]{1,4})?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Usage: evidence-plan-daily-bars --catalog path --artifact-root path --security-master datasetId --start yyyy-MM-dd --end yyyy-MM-dd --batch n --out file.</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        var options = Parse(args);
        var store = new FileSystemImmutableArtifactStore(new ImmutableArtifactStoreOptions(Path.GetFullPath(Require(options, "--artifact-root"))));
        var catalog = new SqliteEvidenceCatalog(new EvidenceCatalogOptions(Path.GetFullPath(Require(options, "--catalog")),
            EvidenceCatalogOpenMode.OpenExisting), store);
        var datasetId = Require(options, "--security-master");
        var manifest = await catalog.GetDatasetAsync(datasetId, cancellationToken)
            ?? throw new InvalidDataException($"Security master dataset '{datasetId}' was not found.");
        var snapshots = await new ParquetEvidencePartitionDataReader(new EvidenceParquetCodec(), store)
            .ReadSecurityMasterSnapshotsAsync(manifest, cancellationToken);
        var securities = snapshots.Select(row => new ListedSecurity(row.SecurityId, row.Symbol, row.Exchange, row.AssetClass)).ToArray();
        var start = Date(Require(options, "--start"));
        var end = Date(Require(options, "--end"));
        var batch = Int32.Parse(Require(options, "--batch"), CultureInfo.InvariantCulture);
        var plan = CreateRequest(securities, start, end, batch, datasetId, manifest.CollectionPlanHash);
        var path = Path.GetFullPath(Require(options, "--out"));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(plan.Request, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), cancellationToken);
        output.WriteLine(JsonSerializer.Serialize(new { Request = path, plan.Request.Requests.Count, Securities = plan.Request.Securities.Count,
            ExcludedReusedSymbols = plan.ExcludedReusedSymbols.Count, ExcludedNonTickerSymbols = plan.ExcludedNonTickerSymbols.Count, plan.ExcludedOtherVenues }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>One security on the day the security master was observed.</summary>
    public sealed record ListedSecurity(string SecurityId, string Symbol, string Exchange, string AssetClass);

    /// <summary>The frozen request plus what was left out and why.</summary>
    public sealed record DailyBarsPlan(EvidenceDatasetCollectionRequest Request, IReadOnlyList<string> ExcludedReusedSymbols,
        IReadOnlyList<string> ExcludedNonTickerSymbols, int ExcludedOtherVenues);

    /// <summary>
    /// Keeps listed US equities, drops symbols held by more than one security because a symbol-keyed
    /// bar request cannot tell them apart, and batches the rest in symbol order so reruns are identical.
    /// </summary>
    public static DailyBarsPlan CreateRequest(IReadOnlyList<ListedSecurity> securities, DateOnly start, DateOnly end, int batchSize,
        string securityMasterDatasetId, string securityMasterPlanHash)
    {
        if (end <= start) throw new ArgumentException("The end date must follow the start date.");
        if (batchSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var venue = securities.Where(security => security.AssetClass == "us_equity" && ListedExchanges.Contains(security.Exchange)).ToArray();
        // The bars endpoint rejects identifier-style names such as 003CVR016 and renamed listings such as BRAC_DELISTED.
        var nonTicker = venue.Where(security => !TickerPattern.IsMatch(security.Symbol)).Select(security => security.Symbol).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var listed = venue.Where(security => TickerPattern.IsMatch(security.Symbol)).ToArray();
        var reused = listed.GroupBy(security => security.Symbol, StringComparer.Ordinal)
            .Where(group => group.Select(security => security.SecurityId).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => group.Key).Order(StringComparer.Ordinal).ToArray();
        var kept = listed.Where(security => !reused.Contains(security.Symbol, StringComparer.Ordinal))
            .DistinctBy(security => security.Symbol, StringComparer.Ordinal)
            .OrderBy(security => security.Symbol, StringComparer.Ordinal).ToArray();
        if (kept.Length == 0) throw new InvalidDataException("No listed US equities remain to collect.");
        var startUtc = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var endUtc = new DateTimeOffset(end.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var requests = kept.Chunk(batchSize).Select((chunk, index) => new EvidenceCollectionRequest(
            $"daily-bars-{index:00000}", "alpaca", "/v2/stocks/bars", chunk.Select(security => security.Symbol).ToArray(),
            startUtc, endUtc, "sip", "raw", "USD", end, new Dictionary<string, string> { ["timeframe"] = "1Day" })).ToArray();
        var configHash = EvidenceCanonicalJson.ComputeSha256(new { CodeVersion, start, end, batchSize, securityMasterDatasetId, securityMasterPlanHash });
        var request = new EvidenceDatasetCollectionRequest(endUtc,
            $"daily-bars-as-traded-{start:yyyyMMdd}-{end:yyyyMMdd}", configHash, CodeVersion, "alpaca-daily-bars-collection-v1",
            "alpaca-daily-bars-partition-v1", EvidenceDatasetKind.MarketBarsAsTraded, 1, "alpaca-bars-normalizer-v1",
            "normalized/daily-bars-as-traded", requests, kept.Select(security => new EvidenceSecurityIdentity(security.SecurityId, security.Symbol)).ToArray(),
            attributes: new Dictionary<string, string>
            {
                ["security_master_dataset_id"] = securityMasterDatasetId,
                ["excluded_reused_symbol_count"] = reused.Length.ToString(CultureInfo.InvariantCulture),
                ["excluded_non_ticker_symbol_count"] = nonTicker.Length.ToString(CultureInfo.InvariantCulture)
            });
        return new DailyBarsPlan(request, reused, nonTicker, securities.Count - venue.Length);
    }

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
            ? value : throw new ArgumentException($"{name} is required.");
}
