using System.Text;

namespace TradingFlow.Domain.Research;

/// <summary>
/// Immutable sentiment output for one exact news revision. The canonical input content and
/// its hash bind the assessment to the text and metadata that the model actually received.
/// </summary>
public sealed record SentimentAssessmentEvidenceRow : INormalizedEvidenceRow
{
    public const int ContractSchemaVersion = 2;
    public const int ScoreScale = 6;
    public const long ScoreScaleFactor = 1_000_000L;
    public const string NewsRevisionInputFormatVersion = "news-revision-sentiment-input/v1";

    public SentimentAssessmentEvidenceRow(
        int schemaVersion,
        string runId,
        string configHash,
        string codeVersion,
        string dataFeed,
        string assessmentId,
        string newsProvider,
        string providerArticleId,
        string newsRevisionId,
        string inputContent,
        string inputContentSha256,
        string assessmentProvider,
        string assessmentModel,
        string assessmentModelVersion,
        string modelArtifactId,
        string modelArtifactSha256,
        DateTimeOffset modelTrainingDataCutoffUtc,
        string promptOrStageVersion,
        decimal score,
        DateTimeOffset assessedAtUtc,
        DateTimeOffset observedAtUtc,
        IReadOnlyList<EvidenceRowSourceAddress> sources)
    {
        SchemaVersion = EvidenceRowContract.NormalizeSchemaVersion(schemaVersion);
        if (SchemaVersion != ContractSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                $"Sentiment assessment schema must be {ContractSchemaVersion}.");
        }
        RunId = EvidenceRowContract.NormalizeRequired(runId, nameof(runId));
        ConfigHash = EvidenceRowContract.NormalizeSha256(configHash);
        CodeVersion = EvidenceRowContract.NormalizeRequired(codeVersion, nameof(codeVersion));
        DataFeed = EvidenceRowContract.NormalizeRequired(dataFeed, nameof(dataFeed)).ToLowerInvariant();
        AssessmentId = EvidenceRowContract.NormalizeRequired(assessmentId, nameof(assessmentId));
        NewsProvider = EvidenceRowContract.NormalizeRequired(newsProvider, nameof(newsProvider))
            .ToLowerInvariant();
        ProviderArticleId = EvidenceRowContract.NormalizeRequired(
            providerArticleId,
            nameof(providerArticleId));
        NewsRevisionId = EvidenceRowContract.NormalizeRequired(
            newsRevisionId,
            nameof(newsRevisionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(inputContent);
        InputContent = inputContent;
        InputContentSha256 = EvidenceRowContract.NormalizeSha256(inputContentSha256);
        var computedInputHash = EvidenceValue.ComputeSha256(InputContent);
        if (!computedInputHash.Equals(InputContentSha256, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Sentiment input content does not match its SHA-256 identity.",
                nameof(inputContentSha256));
        }

        AssessmentProvider = EvidenceRowContract.NormalizeRequired(
            assessmentProvider,
            nameof(assessmentProvider)).ToLowerInvariant();
        AssessmentModel = EvidenceRowContract.NormalizeRequired(
            assessmentModel,
            nameof(assessmentModel));
        AssessmentModelVersion = EvidenceRowContract.NormalizeRequired(
            assessmentModelVersion,
            nameof(assessmentModelVersion));
        ModelArtifactId = EvidenceRowContract.NormalizeRequired(
            modelArtifactId,
            nameof(modelArtifactId));
        ModelArtifactSha256 = EvidenceRowContract.NormalizeSha256(modelArtifactSha256);
        ModelTrainingDataCutoffUtc = EvidenceRowContract.RequireUtc(
            modelTrainingDataCutoffUtc,
            nameof(modelTrainingDataCutoffUtc));
        PromptOrStageVersion = EvidenceRowContract.NormalizeRequired(
            promptOrStageVersion,
            nameof(promptOrStageVersion));
        ScoreUnits = ToScoreUnits(score);
        AssessedAtUtc = EvidenceRowContract.RequireUtc(assessedAtUtc, nameof(assessedAtUtc));
        ObservedAtUtc = EvidenceRowContract.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        if (ObservedAtUtc < AssessedAtUtc)
        {
            throw new ArgumentException(
                "Sentiment observation cannot precede assessment completion.",
                nameof(observedAtUtc));
        }
        if (ModelTrainingDataCutoffUtc > AssessedAtUtc)
        {
            throw new ArgumentException(
                "Model training-data cutoff cannot follow assessment completion.",
                nameof(modelTrainingDataCutoffUtc));
        }

        Sources = EvidenceRowContract.CopySources(sources);
    }

    public int SchemaVersion { get; }
    public string RunId { get; }
    public string ConfigHash { get; }
    public string CodeVersion { get; }
    public string DataFeed { get; }
    public string AssessmentId { get; }
    public string NewsProvider { get; }
    public string ProviderArticleId { get; }
    public string NewsRevisionId { get; }
    public string InputContent { get; }
    public string InputContentSha256 { get; }
    public string AssessmentProvider { get; }
    public string AssessmentModel { get; }
    public string AssessmentModelVersion { get; }
    public string ModelArtifactId { get; }
    public string ModelArtifactSha256 { get; }
    public DateTimeOffset ModelTrainingDataCutoffUtc { get; }
    public string PromptOrStageVersion { get; }
    public long ScoreUnits { get; }
    public decimal Score => ScoreUnits / (decimal)ScoreScaleFactor;
    public DateTimeOffset AssessedAtUtc { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public IReadOnlyList<EvidenceRowSourceAddress> Sources { get; }
    public DateTimeOffset SourceTimestampUtc => ObservedAtUtc;

    /// <summary>
    /// Produces the exact versioned input envelope used to bind an assessment to a news
    /// revision. Length-prefixing prevents delimiter ambiguity in provider text.
    /// </summary>
    public static string CreateNewsRevisionInputContent(NewsRevisionEvidenceRow news)
    {
        ArgumentNullException.ThrowIfNull(news);
        var builder = new StringBuilder(512);
        builder.Append(NewsRevisionInputFormatVersion).Append('\n');
        AppendField(builder, "provider", news.Provider);
        AppendField(builder, "provider_article_id", news.ProviderArticleId);
        AppendField(builder, "revision_id", news.RevisionId);
        AppendField(builder, "headline", news.Headline);
        AppendField(builder, "summary", news.Summary ?? String.Empty);
        AppendField(builder, "article_url", news.ArticleUrl);
        AppendField(builder, "published_at_utc", news.PublishedAtUtc.ToString("O"));
        AppendField(builder, "provider_created_at_utc", news.ProviderCreatedAtUtc.ToString("O"));
        AppendField(builder, "provider_updated_at_utc", news.ProviderUpdatedAtUtc.ToString("O"));
        AppendValues(builder, "symbols", news.Symbols);
        AppendValues(builder, "categories", news.Categories);
        return builder.ToString();
    }

    public static string ComputeInputContentSha256(string inputContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputContent);
        return EvidenceValue.ComputeSha256(inputContent);
    }

    private static long ToScoreUnits(decimal score)
    {
        if (score is < -1m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(score), "Sentiment score must be in [-1, 1].");
        }

        var units = checked((long)Decimal.Round(
            score * ScoreScaleFactor,
            0,
            MidpointRounding.ToEven));
        if (units / (decimal)ScoreScaleFactor != score)
        {
            throw new ArgumentException(
                $"Sentiment score supports at most {ScoreScale} decimal places.",
                nameof(score));
        }

        return units;
    }

    private static void AppendValues(
        StringBuilder builder,
        string field,
        IReadOnlyList<string> values)
    {
        AppendField(builder, $"{field}_count", values.Count.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        for (var index = 0; index < values.Count; index++)
        {
            AppendField(builder, $"{field}_{index}", values[index]);
        }
    }

    private static void AppendField(StringBuilder builder, string name, string value)
    {
        builder
            .Append(name)
            .Append(':')
            .Append(Encoding.UTF8.GetByteCount(value).ToString(
                System.Globalization.CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('\n');
    }
}
