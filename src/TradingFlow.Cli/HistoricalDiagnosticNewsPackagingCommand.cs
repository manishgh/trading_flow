using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Historical;

namespace TradingFlow.Cli;

/// <summary>Ordinary offline evidence packaging: selected stocks come from an existing saved plan, never a handwritten economic configuration.</summary>
public static class HistoricalDiagnosticNewsPackagingCommand
{
    /// <summary>Accepts option-only arguments, joins retained archives and catalog datasets, and publishes an immutable server-selectable package.</summary>
    public static async Task ExecuteAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var options = Parse(args);
        var planPath = Path.GetFullPath(Require(options, "--saved-plan"));
        var planBytes = await File.ReadAllBytesAsync(planPath, cancellationToken);
        var plan = JsonSerializer.Deserialize<BacktestPlanResponse>(planBytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Saved backtest plan is empty.");
        if (plan.PlanId <= 0 || plan.Cohorts.Count == 0) throw new InvalidDataException("Package selection requires an existing saved plan with explicit stock groups.");
        var symbols = plan.Cohorts.SelectMany(item => item.Symbols).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (symbols.Length == 0) throw new InvalidDataException("Saved plan has no selected stocks.");
        var researchRoot = Path.GetFullPath(Require(options, "--research-root"));
        var evidenceRoot = Path.GetFullPath(Require(options, "--evidence-root"));
        var archives = new List<HistoricalEvidenceManifest>();
        foreach (var legacy in Values(options, "--legacy-root"))
            archives.Add(HistoricalEvidenceManifestBuilder.BuildFromCollectionRoot(Path.GetFullPath(legacy)));
        var datasets = Values(options, "--dataset");
        if (datasets.Count > 0)
        {
            var derivedRoot = Path.GetFullPath(Require(options, "--derived-root"));
            // Match the server's established evidence layout: immutable objects and diagnostic publications are siblings.
            var store = new FileSystemImmutableArtifactStore(new ImmutableArtifactStoreOptions(Path.Combine(evidenceRoot, "objects")));
            var catalog = new SqliteEvidenceCatalog(new EvidenceCatalogOptions(Path.GetFullPath(Require(options, "--catalog")),
                EvidenceCatalogOpenMode.OpenExisting), store);
            var marketDataRest = TradingFlow.Alpaca.AlpacaEndpointResolver.Resolve(TradingFlow.Engine.Configuration.ProductionProfile.Paper).MarketDataRest;
            await HistoricalCatalogArchiveImporter.ImportAsync(catalog, store, datasets, derivedRoot, marketDataRest, cancellationToken: cancellationToken);
            if (options.ContainsKey("--calendar-manifest"))
                HistoricalCatalogArchiveImporter.ImportCapturedCalendar(derivedRoot,
                    Path.GetFullPath(Require(options, "--calendar-manifest")), Path.GetFullPath(Require(options, "--calendar-payload")));
            archives.Add(HistoricalEvidenceManifestBuilder.BuildFromCollectionRoot(derivedRoot));
        }
        if (archives.Count == 0) throw new InvalidDataException("Packaging requires retained legacy archives or existing catalog datasets.");
        HistoricalDiagnosticQuerySemantics? semantics = null;
        if (options.ContainsKey("--query-semantics-evidence"))
        {
            var documentation = Path.GetFullPath(Require(options, "--query-semantics-evidence"));
            semantics = new(Require(options, "--query-timestamp-field"), Path.GetRelativePath(researchRoot, documentation),
                Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(documentation, cancellationToken))),
                "observed_provider_query_behavior");
        }
        var manifest = HistoricalDiagnosticNewsManifestBuilder.Build(researchRoot, archives, semantics, selectedSymbols: symbols);
        var publication = HistoricalDiagnosticNewsManifestStore.PublishExclusive(Path.Combine(evidenceRoot, "historical-news-diagnostics"), manifest);
        var provider = HistoricalDiagnosticNewsProvider.LoadVerified(researchRoot, publication.Path, publication.Sha256);
        Console.WriteLine(JsonSerializer.Serialize(new {
            publication.Id, publication.Path, publication.Sha256, provider.DependencyGraphSha256,
            SavedPlanId = plan.PlanId, SavedPlanSha256 = Convert.ToHexStringLower(SHA256.HashData(planBytes)),
            SelectedSymbols = symbols, RevisionCount = manifest.Revisions.Length, DependencyCount = provider.Dependencies.Count,
            CompleteQueryWindows = manifest.QueryCoverage.Count(item => item.Complete),
            UnknownQueryWindows = manifest.QueryCoverage.Count(item => !item.Complete),
            QueryTimestampBasis = manifest.QuerySemantics?.Basis,
            OriginalAvailabilityProven = false, ClassifierAccuracyEvaluated = false
        }));
    }

    /// <summary>Accepts only named packaging inputs; repeated roots and dataset IDs are ordinary reusable collection selections.</summary>
    private static Dictionary<string, List<string>> Parse(string[] args)
    {
        var allowed = new HashSet<string>(["--saved-plan", "--research-root", "--evidence-root", "--legacy-root", "--dataset",
            "--derived-root", "--catalog", "--calendar-manifest", "--calendar-payload", "--query-semantics-evidence", "--query-timestamp-field"], StringComparer.Ordinal);
        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !allowed.Contains(args[index]) || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Historical news packaging requires named option/value pairs.");
            if (!options.TryGetValue(args[index], out var values)) options[args[index]] = values = [];
            if (values.Count > 0 && args[index] is not ("--legacy-root" or "--dataset"))
                throw new ArgumentException("Packaging option may be supplied only once: " + args[index]);
            values.Add(args[index + 1]);
        }
        return options;
    }

    /// <summary>Fails before source mutation when a required packaging location or selection is absent.</summary>
    private static string Require(Dictionary<string, List<string>> options, string name) =>
        options.TryGetValue(name, out var values) && values.Count == 1 && !String.IsNullOrWhiteSpace(values[0])
            ? values[0] : throw new ArgumentException("Missing required packaging option: " + name);

    /// <summary>Copies a repeated selection without inventing a default archive or dataset.</summary>
    private static IReadOnlyList<string> Values(Dictionary<string, List<string>> options, string name) =>
        options.TryGetValue(name, out var values) ? values : [];
}
