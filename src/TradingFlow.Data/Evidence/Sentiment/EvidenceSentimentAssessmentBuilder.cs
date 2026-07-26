using System.Collections.Concurrent;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;

namespace TradingFlow.Data.Evidence.Sentiment;

/// <summary>
/// Exact, immutable input presented to an evidence sentiment assessor.
/// </summary>
public sealed record EvidenceSentimentAssessmentInput(
    string NewsProvider,
    string ProviderArticleId,
    string NewsRevisionId,
    string Headline,
    string? Summary,
    string CanonicalInputContent,
    string CanonicalInputSha256,
    DateTimeOffset NewsAvailableAtUtc,
    string ModelArtifactId,
    string ModelArtifactSha256,
    DateTimeOffset ModelTrainingDataCutoffUtc);

/// <summary>
/// Auditable output returned by a sentiment assessor. Timestamps describe when the
/// assessment completed and when this exact output became observable to TradingFlow.
/// </summary>
public sealed record EvidenceSentimentAssessmentOutput(
    decimal Score,
    string AssessmentProvider,
    string AssessmentModel,
    string AssessmentModelVersion,
    string ModelArtifactId,
    string ModelArtifactSha256,
    DateTimeOffset ModelTrainingDataCutoffUtc,
    string PromptOrStageVersion,
    DateTimeOffset AssessedAtUtc,
    DateTimeOffset ObservedAtUtc);

/// <summary>
/// Evidence-only inference boundary. Implementations may call a local model or a remote
/// provider, but the dataset builder remains deterministic and provider agnostic.
/// </summary>
public interface IEvidenceSentimentAssessor
{
    Task<EvidenceSentimentAssessmentOutput> AssessAsync(
        EvidenceSentimentAssessmentInput input,
        CancellationToken cancellationToken = default);
}

public sealed record EvidenceSentimentAssessmentBuildRequest(
    string SourceNewsDatasetId,
    string RunId,
    string ConfigHash,
    string CodeVersion,
    string BuilderVersion,
    string ModelArtifactId,
    string ModelArtifactSha256,
    DateTimeOffset ModelTrainingDataCutoffUtc,
    DateTimeOffset CreatedAtUtc,
    int MaximumConcurrency = 4);

public sealed record EvidenceSentimentAssessmentBuildResult(
    string DatasetId,
    bool AlreadyCommitted,
    long RowCount);

/// <summary>
/// Converts one committed news-revision dataset into a deterministic, immutable sentiment
/// assessment dataset. Publication is content addressed; catalog visibility occurs only after
/// every partition has been published and verified.
/// </summary>
public sealed class EvidenceSentimentAssessmentBuilder(
    IEvidenceCatalog catalog,
    IEvidencePartitionDataReader partitionReader,
    EvidenceParquetPartitionPublisher publisher,
    IEvidenceSentimentAssessor assessor,
    TimeProvider timeProvider)
{
    private static readonly ConcurrentDictionary<string, BuildGate> BuildGates =
        new(StringComparer.Ordinal);

    private static readonly EvidenceObjectNamespace ObjectNamespace =
        new("normalized/sentiment-assessments");

    public async Task<EvidenceSentimentAssessmentBuildResult> BuildAsync(
        EvidenceSentimentAssessmentBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var source = await catalog.GetDatasetAsync(
            request.SourceNewsDatasetId,
            cancellationToken) ?? throw new InvalidOperationException(
                $"Committed news dataset '{request.SourceNewsDatasetId}' was not found.");
        ValidateSource(source, request);

        var gateKey = EvidenceCanonicalJson.ComputeSha256(new
        {
            source.DatasetId,
            request.RunId,
            request.ConfigHash,
            request.CodeVersion,
            request.BuilderVersion,
            request.ModelArtifactId,
            request.ModelArtifactSha256,
            request.ModelTrainingDataCutoffUtc,
            request.CreatedAtUtc
        });
        using var gate = await AcquireGateAsync(gateKey, cancellationToken);
        return await BuildUnderGateAsync(request, source, cancellationToken);
    }

    private async Task<EvidenceSentimentAssessmentBuildResult> BuildUnderGateAsync(
        EvidenceSentimentAssessmentBuildRequest request,
        EvidenceDatasetManifest source,
        CancellationToken cancellationToken)
    {
        var news = (await partitionReader.ReadNewsRevisionsAsync(source, cancellationToken))
            .OrderBy(row => row.Provider, StringComparer.Ordinal)
            .ThenBy(row => row.ProviderArticleId, StringComparer.Ordinal)
            .ThenBy(row => row.RevisionId, StringComparer.Ordinal)
            .ToArray();
        if (news.Length == 0)
        {
            throw new InvalidDataException(
                "A sentiment dataset cannot be built from an empty news-revision dataset.");
        }

        var duplicate = news
            .GroupBy(row => (row.Provider, row.ProviderArticleId, row.RevisionId))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"Duplicate news revision '{duplicate.Key.Provider}/" +
                $"{duplicate.Key.ProviderArticleId}/{duplicate.Key.RevisionId}' was found.");
        }

        var rows = new SentimentAssessmentEvidenceRow[news.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, news.Length),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = request.MaximumConcurrency
            },
            async (index, token) =>
            {
                var revision = news[index];
                var canonicalInput =
                    SentimentAssessmentEvidenceRow.CreateNewsRevisionInputContent(revision);
                var inputHash =
                    SentimentAssessmentEvidenceRow.ComputeInputContentSha256(canonicalInput);
                var input = new EvidenceSentimentAssessmentInput(
                    revision.Provider,
                    revision.ProviderArticleId,
                    revision.RevisionId,
                    revision.Headline,
                    revision.Summary,
                    canonicalInput,
                    inputHash,
                    revision.AvailabilityTimestampUtc,
                    request.ModelArtifactId,
                    request.ModelArtifactSha256,
                    request.ModelTrainingDataCutoffUtc);
                var output = await assessor.AssessAsync(input, token);
                ValidateOutput(output, revision, request, timeProvider.GetUtcNow());

                var assessmentId = "sentiment-" + EvidenceCanonicalJson.ComputeSha256(new
                {
                    revision.Provider,
                    revision.ProviderArticleId,
                    revision.RevisionId,
                    InputContentSha256 = inputHash,
                    output.AssessmentProvider,
                    output.AssessmentModel,
                    output.AssessmentModelVersion,
                    output.ModelArtifactId,
                    output.ModelArtifactSha256,
                    output.ModelTrainingDataCutoffUtc,
                    output.PromptOrStageVersion,
                    output.AssessedAtUtc,
                    output.ObservedAtUtc
                });
                rows[index] = new SentimentAssessmentEvidenceRow(
                    SentimentAssessmentEvidenceRow.ContractSchemaVersion,
                    request.RunId,
                    request.ConfigHash,
                    request.CodeVersion,
                    output.AssessmentProvider,
                    assessmentId,
                    revision.Provider,
                    revision.ProviderArticleId,
                    revision.RevisionId,
                    canonicalInput,
                    inputHash,
                    output.AssessmentProvider,
                    output.AssessmentModel,
                    output.AssessmentModelVersion,
                    output.ModelArtifactId,
                    output.ModelArtifactSha256,
                    output.ModelTrainingDataCutoffUtc,
                    output.PromptOrStageVersion,
                    output.Score,
                    output.AssessedAtUtc,
                    output.ObservedAtUtc,
                    revision.Sources);
            });

        var orderedRows = rows
            .OrderBy(row => row.NewsProvider, StringComparer.Ordinal)
            .ThenBy(row => row.ProviderArticleId, StringComparer.Ordinal)
            .ThenBy(row => row.NewsRevisionId, StringComparer.Ordinal)
            .ToArray();
        RequireHomogeneousAssessmentIdentity(orderedRows);

        var assessmentProvider = orderedRows[0].AssessmentProvider;
        var requestedStart = orderedRows.Min(row => row.ObservedAtUtc);
        var maximumObserved = orderedRows.Max(row => row.ObservedAtUtc);
        if (maximumObserved == DateTimeOffset.MaxValue)
        {
            throw new InvalidDataException(
                "Sentiment observation time cannot be represented as an exclusive range.");
        }

        var requestedEnd = maximumObserved.AddTicks(1);
        var sourceReferences = SourceReferences(source, orderedRows);
        var symbols = news
            .SelectMany(row => row.Symbols)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(symbol => symbol, StringComparer.Ordinal)
            .ToArray();
        var partitionId = "sentiment-" + EvidenceCanonicalJson.ComputeSha256(new
        {
            source.DatasetId,
            request.ConfigHash,
            request.BuilderVersion,
            assessmentProvider,
            orderedRows[0].AssessmentModel,
            orderedRows[0].AssessmentModelVersion,
            orderedRows[0].ModelArtifactId,
            orderedRows[0].ModelArtifactSha256,
            orderedRows[0].ModelTrainingDataCutoffUtc,
            orderedRows[0].PromptOrStageVersion
        });
        var quality = new EvidenceQualityReport();
        var partition = await publisher.PublishSentimentAssessmentsAsync(
            new EvidenceParquetPartitionRequest(
                partitionId,
                EvidenceDatasetKind.SentimentAssessments,
                SentimentAssessmentEvidenceRow.ContractSchemaVersion,
                new EvidencePartitionProvenance(
                    assessmentProvider,
                    "derived/sentiment-assessment",
                    assessmentProvider,
                    "none",
                    "N/A",
                    null,
                    [],
                    [],
                    symbols,
                    "event",
                    requestedStart,
                    requestedEnd),
                sourceReferences,
                request.RunId,
                request.ConfigHash,
                request.BuilderVersion,
                request.CodeVersion,
                quality,
                ObjectNamespace,
                Dimensions: new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["source_news_dataset_id"] = source.DatasetId,
                    ["assessment_model"] = orderedRows[0].AssessmentModel,
                    ["assessment_model_version"] = orderedRows[0].AssessmentModelVersion,
                    ["model_artifact_id"] = orderedRows[0].ModelArtifactId,
                    ["model_artifact_sha256"] = orderedRows[0].ModelArtifactSha256,
                    ["model_training_data_cutoff_utc"] =
                        orderedRows[0].ModelTrainingDataCutoffUtc.ToString("O"),
                    ["prompt_or_stage_version"] = orderedRows[0].PromptOrStageVersion
                }),
            orderedRows,
            cancellationToken);

        var manifest = EvidenceParquetPartitionPublisher.BuildDatasetManifest(
            EvidenceDatasetKind.SentimentAssessments,
            SentimentAssessmentEvidenceRow.ContractSchemaVersion,
            request.CreatedAtUtc,
            source.CollectionJobId,
            source.CollectionPlanHash,
            request.ConfigHash,
            request.CodeVersion,
            request.BuilderVersion,
            assessmentProvider,
            [partition],
            quality,
            new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["source_news_dataset_id"] = source.DatasetId,
                ["source_news_logical_dataset_key"] = source.LogicalDatasetKey,
                ["input_format_version"] =
                    SentimentAssessmentEvidenceRow.NewsRevisionInputFormatVersion,
                ["assessment_provider"] = assessmentProvider,
                ["assessment_model"] = orderedRows[0].AssessmentModel,
                ["assessment_model_version"] = orderedRows[0].AssessmentModelVersion,
                ["model_artifact_id"] = orderedRows[0].ModelArtifactId,
                ["model_artifact_sha256"] = orderedRows[0].ModelArtifactSha256,
                ["model_training_data_cutoff_utc"] =
                    orderedRows[0].ModelTrainingDataCutoffUtc.ToString("O"),
                ["prompt_or_stage_version"] = orderedRows[0].PromptOrStageVersion
            });

        var commit = await catalog.CommitDatasetAsync(manifest, cancellationToken);
        if (commit.Id.Equals(manifest.DatasetId, StringComparison.Ordinal))
        {
            return new(
                commit.Id,
                commit.AlreadyCommitted,
                orderedRows.LongLength);
        }

        var authoritative = await catalog.GetDatasetAsync(commit.Id, cancellationToken)
            ?? throw new InvalidDataException(
                $"Catalog returned missing sentiment dataset '{commit.Id}'.");
        if (!authoritative.LogicalDatasetKey.Equals(
                manifest.LogicalDatasetKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Catalog resolved a conflicting sentiment dataset logical identity.");
        }

        return new(commit.Id, true, authoritative.Partitions.Sum(value => value.RowCount));
    }

    private static void ValidateRequest(EvidenceSentimentAssessmentBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceNewsDatasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CodeVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BuilderVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelArtifactId);
        if (request.ConfigHash is null ||
            request.ConfigHash.Length != 64 ||
            request.ConfigHash.Any(character => !Uri.IsHexDigit(character)) ||
            !request.ConfigHash.Equals(request.ConfigHash.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Config hash must be a canonical lowercase SHA-256 value.",
                nameof(request));
        }
        RequireCanonicalSha256(
            request.ModelArtifactSha256,
            nameof(request.ModelArtifactSha256));
        RequireUtc(
            request.ModelTrainingDataCutoffUtc,
            nameof(request.ModelTrainingDataCutoffUtc));

        if (request.CreatedAtUtc == default || request.CreatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Dataset creation time must be a non-default UTC value.",
                nameof(request));
        }
        if (request.ModelTrainingDataCutoffUtc > request.CreatedAtUtc)
        {
            throw new ArgumentException(
                "Model training-data cutoff cannot follow dataset creation time.",
                nameof(request));
        }

        if (request.MaximumConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Maximum concurrency must be positive.");
        }
    }

    private static void ValidateSource(
        EvidenceDatasetManifest source,
        EvidenceSentimentAssessmentBuildRequest request)
    {
        if (!source.DatasetId.Equals(request.SourceNewsDatasetId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Catalog returned the wrong source-news dataset.");
        }

        if (source.Kind != EvidenceDatasetKind.NewsRevisions)
        {
            throw new InvalidDataException(
                $"Dataset '{source.DatasetId}' is '{source.Kind}', not NewsRevisions.");
        }

        if (!source.Quality.Passed ||
            source.Partitions.Count == 0 ||
            source.Partitions.Any(partition =>
                partition.DatasetKind != EvidenceDatasetKind.NewsRevisions ||
                !partition.Quality.Passed))
        {
            throw new InvalidDataException(
                "The source news dataset has not passed evidence quality validation.");
        }
    }

    private static void ValidateOutput(
        EvidenceSentimentAssessmentOutput output,
        NewsRevisionEvidenceRow revision,
        EvidenceSentimentAssessmentBuildRequest request,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(output.AssessmentProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(output.AssessmentModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(output.AssessmentModelVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(output.ModelArtifactId);
        RequireCanonicalSha256(
            output.ModelArtifactSha256,
            nameof(output.ModelArtifactSha256));
        RequireUtc(
            output.ModelTrainingDataCutoffUtc,
            nameof(output.ModelTrainingDataCutoffUtc));
        ArgumentException.ThrowIfNullOrWhiteSpace(output.PromptOrStageVersion);
        if (nowUtc == default || nowUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("The assessment clock must return UTC.");
        }

        if (output.AssessedAtUtc == default ||
            output.AssessedAtUtc.Offset != TimeSpan.Zero ||
            output.ObservedAtUtc == default ||
            output.ObservedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("Assessment timestamps must be non-default UTC values.");
        }

        if (output.AssessedAtUtc < revision.AvailabilityTimestampUtc)
        {
            throw new InvalidDataException(
                "Sentiment assessment cannot precede the exact news revision becoming available.");
        }

        if (output.ObservedAtUtc < output.AssessedAtUtc)
        {
            throw new InvalidDataException(
                "Sentiment observation cannot precede assessment completion.");
        }

        if (output.ObservedAtUtc > request.CreatedAtUtc ||
            request.CreatedAtUtc > nowUtc)
        {
            throw new InvalidDataException(
                "Sentiment assessment or dataset creation time is in the future.");
        }
        if (!output.ModelArtifactId.Equals(
                request.ModelArtifactId,
                StringComparison.Ordinal) ||
            !output.ModelArtifactSha256.Equals(
                request.ModelArtifactSha256,
                StringComparison.Ordinal) ||
            output.ModelTrainingDataCutoffUtc != request.ModelTrainingDataCutoffUtc)
        {
            throw new InvalidDataException(
                "Sentiment assessor model vintage does not match the immutable build request.");
        }
        if (output.ModelTrainingDataCutoffUtc > output.AssessedAtUtc)
        {
            throw new InvalidDataException(
                "Model training-data cutoff cannot follow assessment completion.");
        }
    }

    private static void RequireHomogeneousAssessmentIdentity(
        IReadOnlyList<SentimentAssessmentEvidenceRow> rows)
    {
        var first = rows[0];
        if (rows.Any(row =>
                !row.AssessmentProvider.Equals(first.AssessmentProvider, StringComparison.Ordinal) ||
                !row.AssessmentModel.Equals(first.AssessmentModel, StringComparison.Ordinal) ||
                !row.AssessmentModelVersion.Equals(
                    first.AssessmentModelVersion,
                    StringComparison.Ordinal) ||
                !row.ModelArtifactId.Equals(first.ModelArtifactId, StringComparison.Ordinal) ||
                !row.ModelArtifactSha256.Equals(
                    first.ModelArtifactSha256,
                    StringComparison.Ordinal) ||
                row.ModelTrainingDataCutoffUtc != first.ModelTrainingDataCutoffUtc ||
                !row.PromptOrStageVersion.Equals(
                    first.PromptOrStageVersion,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "One sentiment dataset must use one exact assessor/model/stage identity.");
        }
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Timestamp must be a non-default UTC value.",
                parameterName);
        }
    }

    private static void RequireCanonicalSha256(string value, string parameterName)
    {
        if (String.IsNullOrWhiteSpace(value) ||
            value.Length != 64 ||
            value.Any(character => !Uri.IsHexDigit(character)) ||
            !value.Equals(value.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "SHA-256 must be a canonical 64-character lowercase hexadecimal value.",
                parameterName);
        }
    }

    private static IReadOnlyList<EvidenceSourceReference> SourceReferences(
        EvidenceDatasetManifest source,
        IReadOnlyList<SentimentAssessmentEvidenceRow> rows)
    {
        var references = source.Partitions
            .SelectMany(partition => partition.SourceObservations)
            .GroupBy(reference => reference.ObservationId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var distinct = group.Distinct().ToArray();
                    if (distinct.Length != 1)
                    {
                        throw new InvalidDataException(
                            $"Source observation '{group.Key}' has conflicting references.");
                    }

                    return distinct[0];
                },
                StringComparer.Ordinal);
        var used = rows
            .SelectMany(row => row.Sources)
            .Select(sourceAddress =>
            {
                if (!references.TryGetValue(sourceAddress.ObservationId, out var reference) ||
                    !reference.Artifact.Content.Sha256.Equals(
                        sourceAddress.ObservationSha256,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"News lineage '{sourceAddress.ObservationId}' is absent or conflicting.");
                }

                return reference;
            })
            .Distinct()
            .OrderBy(reference => reference.ObservationId, StringComparer.Ordinal)
            .ToArray();
        if (used.Length == 0)
        {
            throw new InvalidDataException("Sentiment assessments require exact news lineage.");
        }

        return used;
    }

    private static async Task<BuildGateLease> AcquireGateAsync(
        string key,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var gate = BuildGates.GetOrAdd(key, _ => new BuildGate());
            lock (gate)
            {
                if (gate.Retired)
                {
                    continue;
                }

                gate.ReferenceCount++;
            }

            try
            {
                await gate.Semaphore.WaitAsync(cancellationToken);
                return new BuildGateLease(key, gate);
            }
            catch
            {
                ReleaseGate(key, gate, releaseSemaphore: false);
                throw;
            }
        }
    }

    private static void ReleaseGate(string key, BuildGate gate, bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
            gate.Semaphore.Release();
        }

        var remove = false;
        lock (gate)
        {
            gate.ReferenceCount--;
            if (gate.ReferenceCount == 0)
            {
                gate.Retired = true;
                remove = true;
            }
        }

        if (remove)
        {
            BuildGates.TryRemove(new KeyValuePair<string, BuildGate>(key, gate));
        }
    }

    private sealed class BuildGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
        public bool Retired { get; set; }
    }

    private sealed class BuildGateLease(string key, BuildGate gate) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                ReleaseGate(key, gate, releaseSemaphore: true);
            }
        }
    }
}
