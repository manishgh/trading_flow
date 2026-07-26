namespace TradingFlow.Domain.Research.Intraday;

/// <summary>
/// Immutable point-in-time classifier output for one exact news revision.
/// Completion and observation clocks prevent later classifications from being
/// projected backward into earlier market decisions.
/// </summary>
public sealed record ClassifiedCatalystEvidenceRow : INormalizedEvidenceRow
{
    public ClassifiedCatalystEvidenceRow(
        int schemaVersion,
        string runId,
        string configHash,
        string codeVersion,
        string dataFeed,
        string classificationId,
        string newsProvider,
        string providerArticleId,
        string newsRevisionId,
        string newsRevisionContentSha256,
        string globalStoryCluster,
        CatalystNewsCategory category,
        CatalystDirection direction,
        CatalystMateriality materiality,
        string classifierProvider,
        string classifierArtifact,
        string classifierVersion,
        string classifierArtifactSha256,
        DateTimeOffset classifierTrainingCutoffUtc,
        DateTimeOffset inferenceCompletedAtUtc,
        DateTimeOffset observedAtUtc,
        DateTimeOffset newsAvailableAtUtc,
        IReadOnlyList<EvidenceRowSourceAddress> sources)
    {
        SchemaVersion = EvidenceRowContract.NormalizeSchemaVersion(schemaVersion);
        RunId = EvidenceRowContract.NormalizeRequired(runId, nameof(runId));
        ConfigHash = EvidenceValue.NormalizeSha256(configHash);
        CodeVersion = EvidenceRowContract.NormalizeRequired(codeVersion, nameof(codeVersion));
        DataFeed = EvidenceRowContract.NormalizeRequired(dataFeed, nameof(dataFeed)).ToLowerInvariant();
        ClassificationId = EvidenceRowContract.NormalizeRequired(
            classificationId,
            nameof(classificationId));
        NewsProvider = EvidenceRowContract.NormalizeRequired(newsProvider, nameof(newsProvider))
            .ToLowerInvariant();
        ProviderArticleId = EvidenceRowContract.NormalizeRequired(
            providerArticleId,
            nameof(providerArticleId));
        NewsRevisionId = EvidenceRowContract.NormalizeRequired(
            newsRevisionId,
            nameof(newsRevisionId));
        NewsRevisionContentSha256 = EvidenceValue.NormalizeSha256(newsRevisionContentSha256);
        GlobalStoryCluster = EvidenceRowContract.NormalizeRequired(
            globalStoryCluster,
            nameof(globalStoryCluster));
        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        if (!Enum.IsDefined(materiality))
        {
            throw new ArgumentOutOfRangeException(nameof(materiality));
        }

        Category = category;
        Direction = direction;
        Materiality = materiality;
        ClassifierProvider = EvidenceRowContract.NormalizeRequired(
            classifierProvider,
            nameof(classifierProvider)).ToLowerInvariant();
        ClassifierArtifact = EvidenceRowContract.NormalizeRequired(
            classifierArtifact,
            nameof(classifierArtifact));
        ClassifierVersion = EvidenceRowContract.NormalizeRequired(
            classifierVersion,
            nameof(classifierVersion));
        ClassifierArtifactSha256 = EvidenceValue.NormalizeSha256(classifierArtifactSha256);
        ClassifierTrainingCutoffUtc = EvidenceRowContract.RequireUtc(
            classifierTrainingCutoffUtc,
            nameof(classifierTrainingCutoffUtc));
        InferenceCompletedAtUtc = EvidenceRowContract.RequireUtc(
            inferenceCompletedAtUtc,
            nameof(inferenceCompletedAtUtc));
        ObservedAtUtc = EvidenceRowContract.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        NewsAvailableAtUtc = EvidenceRowContract.RequireUtc(
            newsAvailableAtUtc,
            nameof(newsAvailableAtUtc));
        if (ClassifierTrainingCutoffUtc > InferenceCompletedAtUtc)
        {
            throw new ArgumentException(
                "Classifier training cutoff cannot follow inference completion.");
        }

        if (ObservedAtUtc < InferenceCompletedAtUtc)
        {
            throw new ArgumentException(
                "Classification observation cannot precede inference completion.");
        }

        AvailableAtUtc = new[]
        {
            NewsAvailableAtUtc,
            InferenceCompletedAtUtc,
            ObservedAtUtc
        }.Max();
        Sources = EvidenceRowContract.CopySources(sources);
    }

    public int SchemaVersion { get; }
    public string RunId { get; }
    public string ConfigHash { get; }
    public string CodeVersion { get; }
    public string DataFeed { get; }
    public string ClassificationId { get; }
    public string NewsProvider { get; }
    public string ProviderArticleId { get; }
    public string NewsRevisionId { get; }
    public string NewsRevisionContentSha256 { get; }
    public string GlobalStoryCluster { get; }
    public CatalystNewsCategory Category { get; }
    public CatalystDirection Direction { get; }
    public CatalystMateriality Materiality { get; }
    public string ClassifierProvider { get; }
    public string ClassifierArtifact { get; }
    public string ClassifierVersion { get; }
    public string ClassifierArtifactSha256 { get; }
    public DateTimeOffset ClassifierTrainingCutoffUtc { get; }
    public DateTimeOffset InferenceCompletedAtUtc { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public DateTimeOffset NewsAvailableAtUtc { get; }
    public DateTimeOffset AvailableAtUtc { get; }
    public IReadOnlyList<EvidenceRowSourceAddress> Sources { get; }
    public DateTimeOffset SourceTimestampUtc => ObservedAtUtc;
}
