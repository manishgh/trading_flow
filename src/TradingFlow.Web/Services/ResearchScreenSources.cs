using System.Text.Json;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Universe;

namespace TradingFlow.Web.Services;

/// <summary>
/// Published research screens under {data root}/research/screens and the evidence-catalog datasets their backtests read.
/// Only reads files and the catalog; it never writes wishlists, Finviz lists or other operator data.
/// </summary>
public sealed class ResearchScreenSources(ProjectPaths paths, IEvidenceCatalog? catalog = null)
{
    /// <summary>Key prefix of a research screen in the Backtests stock-source picker.</summary>
    public const string KeyPrefix = "research_screen:";

    /// <summary>Names of the published screens that carry a ranked list, sorted; folders without a readable manifest are skipped.</summary>
    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(paths.ResearchScreensRoot)) return [];
        return Directory.EnumerateDirectories(paths.ResearchScreensRoot).Where(HasRankedList)
            .Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Reads and verifies one published screen by its picker key or folder name. Refuses names that are not a single folder
    /// under the screens root, and (with <paramref name="expectedManifestSha256"/>) a manifest whose bytes changed.
    /// </summary>
    public PublishedResearchScreen Read(string key, string? expectedManifestSha256 = null) =>
        PublishedResearchScreen.Read(FolderForKey(key), expectedManifestSha256);

    /// <summary>Protects unopened membership when showing an unregistered stock source preview.</summary>
    public PublishedResearchScreen ReadForPreview(string key)
    {
        var folder = FolderForKey(key);
        var (manifest, sha, _) = PublishedResearchScreen.ReadDefinition(folder);
        if (ResearchObservationBoundary.IncludesDocumentedHoldout(manifest.From, manifest.To))
            throw new InvalidOperationException("This eligible or ranked list includes protected dates. Select a separately published development-only screen.");
        return PublishedResearchScreen.Read(folder, sha);
    }

    /// <summary>Resolves the published folder without reading membership.</summary>
    private string FolderForKey(string key)
    {
        var name = key.StartsWith(KeyPrefix, StringComparison.Ordinal) ? key[KeyPrefix.Length..] : key;
        if (String.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException($"'{key}' is not a published research screen name.");
        var folder = Path.Combine(paths.ResearchScreensRoot, name);
        if (!PublishedResearchScreen.IsPublished(folder))
            throw new InvalidOperationException($"Research screen '{name}' has no {ResearchScreenManifest.FileName} under {paths.ResearchScreensRoot}.");
        return folder;
    }

    /// <summary>Resolves the same frozen datasets without opening the ranked or eligible member observations.</summary>
    public async Task<ResearchScreenReference> ResolveReferenceFromMetadataAsync(string name, string manifestSha, string membershipSha,
        CancellationToken token)
    {
        var source = catalog ?? throw new InvalidOperationException("The evidence catalog is not available in this host.");
        var (manifest, sha, membership) = PublishedResearchScreen.ReadDefinition(FolderForKey(name), manifestSha);
        if (!membership.Equals(membershipSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The ranked list changed since the plan was saved (research screen '{name}').");
        return ResolveReference(name, manifest, sha, membership, await source.FindDatasetsAsync(new(EvidenceDatasetKind.MarketBarsAsTraded), token));
    }

    /// <summary>Looks up the catalog's as-traded bar datasets and returns the screen's frozen inputs (see <see cref="ResolveReference"/>).</summary>
    public async Task<ResearchScreenReference> ResolveReferenceAsync(PublishedResearchScreen screen, CancellationToken token)
    {
        var source = catalog ?? throw new InvalidOperationException("The evidence catalog is not available in this host.");
        return ResolveReference(screen, await source.FindDatasetsAsync(new EvidenceDatasetQuery(EvidenceDatasetKind.MarketBarsAsTraded), token));
    }

    /// <summary>
    /// The datasets a research-screen run reads: daily as-traded bars and XNYS sessions named by the manifest, and every
    /// five-minute and one-minute dataset whose collection attribute ranked_list_sha256 is this screen's ranked list.
    /// Refuses a manifest without an exchange-session dataset and a ranked list without five-minute or one-minute bars. A
    /// screen without a ranking serves daily strategies, which read daily bars only, so it needs no intraday datasets.
    /// </summary>
    public static ResearchScreenReference ResolveReference(PublishedResearchScreen screen, IEnumerable<EvidenceDatasetManifest> asTradedBars) =>
        ResolveReference(screen.Name, screen.Manifest, screen.ManifestSha256, screen.RankedListSha256, asTradedBars);

    /// <summary>Applies exact dataset selection to the manifest, independent of whether membership was read.</summary>
    private static ResearchScreenReference ResolveReference(string name, ResearchScreenManifest manifest, string sha, string membership,
        IEnumerable<EvidenceDatasetManifest> asTradedBars)
    {
        var inputs = manifest.Inputs;
        var sessions = inputs.ExchangeSessionsDatasetId ?? throw new InvalidOperationException(
            $"Research screen '{name}' names no exchange-session dataset. Publish it again with research-daily-screen --sessions <XNYS dataset ID>.");
        var collected = asTradedBars.Where(dataset => dataset.Attributes.GetValueOrDefault("ranked_list_sha256") == membership).ToArray();
        var fredVixSha = manifest.Outputs.GetValueOrDefault(ResearchScreenManifest.FredVixFileName);
        if (manifest.RankingRules is null)
            return new(name, sha, membership, inputs.DailyBarsDatasetId, sessions, DatasetsOf(collected, "5m"), [],
                inputs.CorporateActionsDatasetId, DatasetsOf(collected, "1h"), inputs.SecurityMasterDatasetId, fredVixSha);
        string[] Of(string timeframe) => DatasetsOf(collected, timeframe);
        var five = Of("5m");
        if (five.Length == 0)
            throw new InvalidOperationException($"No five-minute bar dataset collected for ranked list {membership} of research screen " +
                $"'{name}' is in the evidence catalog. Plan it with evidence-plan-minute-bars --timeframe 5Min --prior-sessions 15 and collect it first.");
        var minute = Of("1m");
        if (minute.Length == 0)
            throw new InvalidOperationException($"No one-minute bar dataset collected for ranked list {membership} of research screen " +
                $"'{name}' is in the evidence catalog. Plan it with evidence-plan-minute-bars and collect it first.");
        return new(name, sha, membership, inputs.DailyBarsDatasetId, sessions, five, minute,
            inputs.CorporateActionsDatasetId, SecurityMasterDatasetId: inputs.SecurityMasterDatasetId, FredVixSha256: fredVixSha);
    }

    /// <summary>The run settings (providers.evidence_catalog) that let a run read exactly the referenced datasets.</summary>
    /// <summary>IDs of the collected datasets that hold only bars of one timeframe, in ordinal order.</summary>
    private static string[] DatasetsOf(IEnumerable<EvidenceDatasetManifest> collected, string timeframe) =>
        collected.Where(dataset => dataset.Partitions.All(partition => partition.Provenance.Timeframe == timeframe))
            .Select(dataset => dataset.DatasetId).Order(StringComparer.Ordinal).ToArray();

    public EvidenceCatalogProviderConfig ProviderConfig(ResearchScreenReference reference) => new(paths.EvidenceCatalogPath,
        paths.EvidenceObjectRoot, reference.RankedListSha256, reference.DailyBarsDatasetId, reference.ExchangeSessionsDatasetId,
        reference.FiveMinuteDatasetIds, reference.OneMinuteDatasetIds, reference.CorporateActionsDatasetId,
        reference.HourlyDatasetIds, reference.SecurityMasterDatasetId);

    /// <summary>The provider reference a source preview records: screen name, manifest SHA-256 and ranked-list SHA-256.</summary>
    public static string ProviderReference(PublishedResearchScreen screen) =>
        $"{KeyPrefix}{screen.Name};manifest_sha256={screen.ManifestSha256};ranked_list_sha256={screen.RankedListSha256}";

    /// <summary>Reads back <see cref="ProviderReference"/>; anything else fails.</summary>
    public static (string Name, string ManifestSha256, string RankedListSha256) ParseProviderReference(string? reference)
    {
        var parts = (reference ?? String.Empty).Split(';');
        if (parts.Length != 3 || !parts[0].StartsWith(KeyPrefix, StringComparison.Ordinal) ||
            !parts[1].StartsWith("manifest_sha256=", StringComparison.Ordinal) || !parts[2].StartsWith("ranked_list_sha256=", StringComparison.Ordinal))
            throw new InvalidOperationException("The research-screen source preview has no manifest and ranked-list fingerprints. Preview the source again.");
        return (parts[0][KeyPrefix.Length..], parts[1]["manifest_sha256=".Length..], parts[2]["ranked_list_sha256=".Length..]);
    }

    /// <summary>
    /// True when a folder's manifest parses and names its membership list (the ranked list, or eligible.csv for a screen
    /// without a ranking); fingerprints are checked only when the screen is read.
    /// </summary>
    private static bool HasRankedList(string folder)
    {
        try
        {
            if (!PublishedResearchScreen.IsPublished(folder)) return false;
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, ResearchScreenManifest.FileName)));
            var ranked = document.RootElement.TryGetProperty("rankingRules", out var rules) && rules.ValueKind == JsonValueKind.Object;
            return document.RootElement.TryGetProperty("outputs", out var outputs) &&
                outputs.TryGetProperty(ranked ? ResearchScreenManifest.RankedListFileName : "eligible.csv", out _);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
