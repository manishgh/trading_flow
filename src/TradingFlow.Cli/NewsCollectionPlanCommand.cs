using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Normalization;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Research;
using TradingFlow.Research.Orchestration;

namespace TradingFlow.Cli;

/// <summary>
/// Turns a saved backtest plan's stocks and a date range into a frozen Alpaca news collection request that the existing
/// evidence-collect-normalize command runs. Contains no download logic. The news is collected after the fact, so its
/// availability is the provider update time only (never the download time), as the news normalizer requires.
/// </summary>
public static class NewsCollectionPlanCommand
{
    public const string Name = "evidence-plan-news";
    private const string CodeVersion = "evidence-plan-news-v1";

    /// <summary>
    /// Usage: evidence-plan-news --catalog path --artifact-root path --security-master datasetId --saved-plan file
    /// --start yyyy-MM-dd --end yyyy-MM-dd --batch n --out file. The saved plan is the JSON of GET /api/v1/backtests/plans/{id}.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        var options = Parse(args);
        var store = new FileSystemImmutableArtifactStore(new ImmutableArtifactStoreOptions(Path.GetFullPath(Require(options, "--artifact-root"))));
        var catalog = new SqliteEvidenceCatalog(new EvidenceCatalogOptions(Path.GetFullPath(Require(options, "--catalog")),
            EvidenceCatalogOpenMode.OpenExisting), store);
        var datasetId = Require(options, "--security-master");
        var manifest = await catalog.GetDatasetAsync(datasetId, cancellationToken)
            ?? throw new InvalidDataException($"Security master dataset '{datasetId}' was not found.");
        var securities = (await new ParquetEvidencePartitionDataReader(new EvidenceParquetCodec(), store)
            .ReadSecurityMasterSnapshotsAsync(manifest, cancellationToken))
            .Select(row => new EvidenceSecurityIdentity(row.SecurityId, row.Symbol)).ToArray();
        var planPath = Path.GetFullPath(Require(options, "--saved-plan"));
        var planBytes = await File.ReadAllBytesAsync(planPath, cancellationToken);
        var plan = JsonSerializer.Deserialize<BacktestPlanResponse>(planBytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Saved backtest plan is empty.");
        var request = CreateRequest(plan, Convert.ToHexStringLower(SHA256.HashData(planBytes)), securities,
            Date(Require(options, "--start")), Date(Require(options, "--end")),
            Int32.Parse(Require(options, "--batch"), CultureInfo.InvariantCulture), datasetId, manifest.CollectionPlanHash);
        var path = Path.GetFullPath(Require(options, "--out"));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(request,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), cancellationToken);
        output.WriteLine(JsonSerializer.Serialize(new { Request = path, request.Requests.Count, Stocks = request.Securities.Count, request.RunId },
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>
    /// One request per calendar-month window and per batch of stocks (alphabetical, so reruns are identical), for every
    /// stock in the saved plan's groups. Each stock must map to exactly one security in the security master, otherwise
    /// the plan fails instead of collecting news for the wrong company.
    /// </summary>
    public static EvidenceDatasetCollectionRequest CreateRequest(BacktestPlanResponse plan, string planSha256,
        IReadOnlyList<EvidenceSecurityIdentity> securityMaster, DateOnly start, DateOnly end, int batchSize,
        string securityMasterDatasetId, string securityMasterPlanHash)
    {
        if (end <= start) throw new ArgumentException("The end date must follow the start date.");
        if (batchSize is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(batchSize), "Use 1 to 50 stocks per request.");
        var symbols = plan.Cohorts.SelectMany(cohort => cohort.Symbols).Select(symbol => symbol.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (plan.PlanId <= 0 || symbols.Length == 0) throw new InvalidDataException("News planning requires a saved plan with stocks.");
        var bySymbol = securityMaster.GroupBy(security => security.Symbol, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.DistinctBy(security => security.SecurityId).ToArray(), StringComparer.Ordinal);
        var unknown = symbols.Where(symbol => !bySymbol.ContainsKey(symbol)).ToArray();
        var ambiguous = symbols.Where(symbol => bySymbol.TryGetValue(symbol, out var matches) && matches.Length > 1).ToArray();
        if (unknown.Length > 0 || ambiguous.Length > 0)
            throw new InvalidDataException($"Stocks must map to one security. Not in the security master: {String.Join(", ", unknown)}. " +
                $"Held by several securities: {String.Join(", ", ambiguous)}. Exclude them from the saved plan explicitly.");
        var requests = new List<EvidenceCollectionRequest>();
        foreach (var (from, to) in MonthWindows(start, end))
        foreach (var batch in symbols.Chunk(batchSize))
            requests.Add(new EvidenceCollectionRequest($"news-{requests.Count:00000}", "alpaca", "/v1beta1/news", batch,
                Utc(from), Utc(to), "alpaca_news", "raw", "USD", end, new Dictionary<string, string>
                {
                    ["limit"] = "50", ["sort"] = "asc", ["include_content"] = "true", ["exclude_contentless"] = "false"
                }));
        var configHash = EvidenceCanonicalJson.ComputeSha256(new { CodeVersion, plan.PlanId, planSha256, start, end, batchSize,
            securityMasterDatasetId, securityMasterPlanHash });
        return new EvidenceDatasetCollectionRequest(Utc(end), $"news-plan{plan.PlanId}-{start:yyyyMMdd}-{end:yyyyMMdd}", configHash,
            CodeVersion, "alpaca-news-collection-v1", "alpaca-news-partition-v1", EvidenceDatasetKind.NewsRevisions, 1,
            "alpaca-news-normalizer-v1", "normalized/news-revisions", requests,
            symbols.Select(symbol => bySymbol[symbol][0]).ToArray(), NewsAvailabilityEvidence.ProviderUpdatedTimestampOnly,
            new Dictionary<string, string>
            {
                ["purpose"] = "saved-plan-company-news-for-historical-news-diagnostic",
                ["news_source"] = "benzinga-via-alpaca",
                ["saved_plan_id"] = plan.PlanId.ToString(CultureInfo.InvariantCulture),
                ["saved_plan_sha256"] = planSha256,
                ["security_master_dataset_id"] = securityMasterDatasetId,
                ["collected_after_the_fact"] = "true",
                ["provider_updated_timestamp_only"] = "true",
                ["historical_availability_proven"] = "false",
                ["revision_history_completeness_proven"] = "false",
                ["collection_scope"] = "current saved-plan stocks, not historical universe membership",
                ["promotion_eligible"] = "false"
            });
    }

    // Splits [start, end) at calendar-month boundaries so each request stays a bounded page set.
    private static IEnumerable<(DateOnly From, DateOnly To)> MonthWindows(DateOnly start, DateOnly end)
    {
        for (var from = start; from < end;)
        {
            var next = new DateOnly(from.Year, from.Month, 1).AddMonths(1);
            var to = next < end ? next : end;
            yield return (from, to);
            from = to;
        }
    }

    private static DateTimeOffset Utc(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    private static DateOnly Date(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Reads --name value pairs; an odd count is a malformed command line.
    private static Dictionary<string, string> Parse(string[] args)
    {
        if (args.Length % 2 != 0) throw new ArgumentException("Options must be --name value pairs.");
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2) options[args[index]] = args[index + 1];
        return options;
    }

    // Returns a required option or fails with its name.
    private static string Require(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !String.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"{name} is required.");
}
