using TradingFlow.Data.Evidence.Normalization;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Research.Orchestration;

/// <summary>
/// Frozen, serializable input for one raw-collection and normalization workflow.
/// Credentials and provider base URLs are intentionally excluded.
/// </summary>
public sealed record EvidenceDatasetCollectionRequest
{
    public EvidenceDatasetCollectionRequest(
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
        if (!Enum.IsDefined(datasetKind))
        {
            throw new ArgumentOutOfRangeException(nameof(datasetKind));
        }

        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

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
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
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

    public EvidenceCollectionPlan CreatePlan() =>
        new(
            CreatedAtUtc,
            RunId,
            EffectiveConfigHash,
            CodeVersion,
            CollectionPolicyVersion,
            PartitionPolicyVersion,
            Requests);

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

    private void ValidateDatasetContract()
    {
        if (Requests.Count == 0)
        {
            throw new ArgumentException("At least one collection request is required.", nameof(Requests));
        }

        if (Securities.Count == 0 &&
            DatasetKind != EvidenceDatasetKind.ExchangeSessions)
        {
            throw new ArgumentException("At least one security identity is required.", nameof(Securities));
        }

        var requiresNewsAvailability = DatasetKind == EvidenceDatasetKind.NewsRevisions;
        if (requiresNewsAvailability != NewsAvailabilityEvidence.HasValue)
        {
            throw new ArgumentException(
                requiresNewsAvailability
                    ? "News evidence requires an explicit point-in-time availability policy."
                    : "News availability evidence is valid only for news datasets.",
                nameof(NewsAvailabilityEvidence));
        }

        if (DatasetKind is not (
                EvidenceDatasetKind.MarketBarsAsTraded or
                EvidenceDatasetKind.MarketBarsResearchAdjusted or
                EvidenceDatasetKind.NewsRevisions or
                EvidenceDatasetKind.SipQuotes or
                EvidenceDatasetKind.ExchangeSessions))
        {
            throw new NotSupportedException(
                $"Dataset kind '{DatasetKind}' is not supported by the HTTP normalization workflow.");
        }

        if (Requests.Any(request =>
                !request.Provider.Equals("alpaca", StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The normalized HTTP evidence workflow currently accepts Alpaca receipts only.",
                nameof(Requests));
        }

        if (Requests.Any(request =>
                Uri.TryCreate(request.Endpoint, UriKind.Absolute, out _)))
        {
            throw new ArgumentException(
                "Provider endpoints must be approved relative paths; absolute URLs are forbidden.",
                nameof(Requests));
        }

        var expectedEndpoint = DatasetKind switch
        {
            EvidenceDatasetKind.MarketBarsAsTraded or
            EvidenceDatasetKind.MarketBarsResearchAdjusted => "/v2/stocks/bars",
            EvidenceDatasetKind.NewsRevisions => "/v1beta1/news",
            EvidenceDatasetKind.SipQuotes => "/v2/stocks/quotes",
            EvidenceDatasetKind.ExchangeSessions => "/v2/calendar",
            _ => throw new NotSupportedException(
                $"Dataset kind '{DatasetKind}' is not supported.")
        };
        if (Requests.Any(request =>
                !request.Endpoint.Equals(expectedEndpoint, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                $"Dataset kind '{DatasetKind}' requires endpoint '{expectedEndpoint}'.",
                nameof(Requests));
        }

        if (DatasetKind == EvidenceDatasetKind.MarketBarsAsTraded &&
            Requests.Any(request => !request.Adjustment.Equals("raw", StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "As-traded market bars require raw, unadjusted provider data.",
                nameof(Requests));
        }

        if (DatasetKind == EvidenceDatasetKind.MarketBarsResearchAdjusted &&
            Requests.Any(request => !request.Adjustment.Equals("all", StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Adjusted market bars require adjustment 'all'.",
                nameof(Requests));
        }

        if (DatasetKind == EvidenceDatasetKind.NewsRevisions &&
            Requests.Any(request => request.Symbols.Count == 0))
        {
            throw new ArgumentException(
                "News evidence requests must explicitly name their research-universe symbols.",
                nameof(Requests));
        }

        if (DatasetKind == EvidenceDatasetKind.ExchangeSessions &&
            (Securities.Count != 0 ||
             Requests.Any(request =>
                request.Symbols.Count != 0 ||
                request.RequestedStartUtc is null ||
                request.RequestedEndUtc is null ||
                request.AsOfDate is null ||
                !request.DataFeed.Equals("alpaca-trading", StringComparison.Ordinal) ||
                !request.Adjustment.Equals("raw", StringComparison.Ordinal) ||
                !request.Currency.Equals("USD", StringComparison.Ordinal) ||
                request.Parameters.Count != 0)))
        {
            throw new ArgumentException(
                "Exchange-calendar evidence requires an explicit UTC date range, an as-of date, " +
                "feed 'alpaca-trading', raw adjustment, USD, no symbols, and no endpoint parameters.",
                nameof(Requests));
        }
    }
}
