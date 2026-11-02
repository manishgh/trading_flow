using System.Globalization;
using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Collection;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Configuration;
using TradingFlow.Research.Orchestration;

namespace TradingFlow.Cli;

/// <summary>
/// Collects Alpaca's active and inactive US equity list plus corporate actions, then publishes the
/// security master, symbol intervals and corporate actions through the existing identity coordinator.
/// </summary>
public static class IdentityEvidenceCollectionCommand
{
    public const string Name = "evidence-collect-identity";
    private const string CodeVersion = "evidence-collect-identity-v1";
    private const string CollectionPolicyVersion = "alpaca-identity-collection-v1";
    private const string PartitionPolicyVersion = "alpaca-identity-partition-v1";

    /// <summary>Usage: evidence-collect-identity --as-of yyyy-MM-dd --actions-start yyyy-MM-dd --catalog path --artifact-root path.</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        var options = Parse(args);
        var asOf = DateOnly.ParseExact(Require(options, "--as-of"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var actionsStart = DateOnly.ParseExact(Require(options, "--actions-start"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var plan = CreatePlan(asOf, actionsStart);
        var keyId = Environment.GetEnvironmentVariable("ALPACA_KEY_ID");
        var secretKey = Environment.GetEnvironmentVariable("ALPACA_SECRET_KEY");
        if (String.IsNullOrWhiteSpace(keyId) || String.IsNullOrWhiteSpace(secretKey))
            throw new InvalidOperationException("Alpaca credentials are not configured. Set ALPACA_KEY_ID and ALPACA_SECRET_KEY.");

        var catalogPath = Path.GetFullPath(Require(options, "--catalog"));
        var store = new FileSystemImmutableArtifactStore(new ImmutableArtifactStoreOptions(Path.GetFullPath(Require(options, "--artifact-root"))));
        var catalog = new SqliteEvidenceCatalog(new EvidenceCatalogOptions(catalogPath,
            File.Exists(catalogPath) ? EvidenceCatalogOpenMode.OpenExisting : EvidenceCatalogOpenMode.BootstrapNew), store);
        var endpoints = AlpacaEndpointResolver.Resolve(ProductionProfile.Paper);
        using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        httpClient.DefaultRequestHeaders.Add("APCA-API-KEY-ID", keyId);
        httpClient.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", secretKey);
        var collector = new EvidenceHttpCollector(httpClient, catalog, new EvidencePublicationCoordinator(catalog, store),
            [new AlpacaAssetsEvidenceRequestAdapter(endpoints.TradingRest), new AlpacaCorporateActionsEvidenceRequestAdapter(endpoints.MarketDataRest)],
            new EvidenceHttpCollectionOptions(workerCount: 2, boundedCapacity: 8, maximumAttempts: 4, requestTimeout: TimeSpan.FromSeconds(60)));
        var collection = await collector.CollectAsync(plan, cancellationToken);
        var identity = new IdentityEvidencePublicationCoordinator(catalog, catalog, store,
            new EvidenceParquetPartitionPublisher(new EvidenceParquetCodec(), store));
        var published = await identity.PublishAsync(new IdentityEvidencePublicationRequest(plan.JobId, 1, "alpaca-identity-v1",
            new EvidenceObjectNamespace("normalized/identity")), cancellationToken);
        output.WriteLine(JsonSerializer.Serialize(new { collection.JobId, collection.State, Published = published, Catalog = catalogPath },
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>One asset snapshot as of the given date and quarterly corporate-action windows, so the same inputs always give the same job.</summary>
    public static EvidenceCollectionPlan CreatePlan(DateOnly asOf, DateOnly actionsStart)
    {
        if (actionsStart >= asOf) throw new ArgumentException("Corporate actions must start before the as-of date.");
        var requests = new List<EvidenceCollectionRequest>
        {
            new("assets", "alpaca", "/v2/assets", [], null, null, "alpaca-trading", "raw", "USD", asOf,
                new Dictionary<string, string> { ["status"] = "all", ["asset_class"] = "us_equity" })
        };
        for (var start = actionsStart; start < asOf; start = start.AddMonths(3))
        {
            var end = start.AddMonths(3) < asOf ? start.AddMonths(3) : asOf.AddDays(1);
            requests.Add(new($"actions-{start:yyyyMMdd}", "alpaca", "/v1/corporate-actions", [], Utc(start), Utc(end),
                "alpaca-market-data", "raw", "USD", asOf));
        }
        var configHash = EvidenceCanonicalJson.ComputeSha256(new { asOf, actionsStart, CodeVersion, CollectionPolicyVersion });
        return new EvidenceCollectionPlan(Utc(asOf),
            $"alpaca-identity-{asOf:yyyyMMdd}-{actionsStart:yyyyMMdd}", configHash, CodeVersion, CollectionPolicyVersion,
            PartitionPolicyVersion, requests);
    }

    private static DateTimeOffset Utc(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

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
