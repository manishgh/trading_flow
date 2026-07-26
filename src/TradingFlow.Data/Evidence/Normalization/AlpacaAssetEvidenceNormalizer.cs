using System.Text.RegularExpressions;
using System.Text.Json;
using TradingFlow.Domain.Research;

namespace TradingFlow.Data.Evidence.Normalization;

public sealed partial class AlpacaAssetEvidenceNormalizer
{
    private static readonly IReadOnlySet<string> AllowedProperties =
        new HashSet<string>(
        [
            "id",
            "class",
            "exchange",
            "symbol",
            "name",
            "status",
            "tradable",
            "marginable",
            "maintenance_margin_requirement",
            "margin_requirement_long",
            "margin_requirement_short",
            "shortable",
            "easy_to_borrow",
            "fractionable",
            "borrow_status",
            "attributes"
        ],
        StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> SupportedExchanges =
        new HashSet<string>(
            ["AMEX", "ARCA", "BATS", "NYSE", "NASDAQ", "NYSEARCA", "OTC"],
            StringComparer.Ordinal);

    public SecurityMasterSnapshotNormalizationResult Normalize(
        EvidenceSourceObservation observation,
        ReadOnlyMemory<byte> rawBytes,
        int schemaVersion)
    {
        try
        {
            if (schemaVersion <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(schemaVersion));
            }

            AlpacaIdentityNormalizationGuard.ValidateObservation(
                observation,
                rawBytes,
                "/v2/assets",
                "alpaca-trading",
                requireRange: false);
            if (observation.RequestedSymbols.Count != 0)
            {
                EvidenceNormalizationGuard.Fail(
                    EvidenceNormalizationFailureCode.ObservationMismatch,
                    observation,
                    "The current asset-master request cannot carry requested symbols.");
            }

            using var document = JsonDocument.Parse(rawBytes);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                EvidenceNormalizationGuard.Fail(
                    EvidenceNormalizationFailureCode.MalformedPayload,
                    observation,
                    "The Alpaca assets response root must be an array.");
            }

            EvidenceNormalizationGuard.EnsureNoDuplicateJsonProperties(
                document.RootElement,
                observation);
            var parsed = new List<ParsedAsset>();
            var securityIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var asset in document.RootElement.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object)
                {
                    EvidenceNormalizationGuard.Fail(
                        EvidenceNormalizationFailureCode.InvalidField,
                        observation,
                        "Every Alpaca asset must be a JSON object.");
                }

                AlpacaIdentityNormalizationGuard.EnsureOnlyProperties(
                    asset,
                    AllowedProperties,
                    observation,
                    "$[]");
                var securityId = EvidenceNormalizationGuard.RequireString(asset, "id", observation);
                if (!Guid.TryParse(securityId, out _))
                {
                    EvidenceNormalizationGuard.Fail(
                        EvidenceNormalizationFailureCode.InvalidField,
                        observation,
                        $"Asset id '{securityId}' is not a UUID.");
                }

                if (!securityIds.Add(securityId))
                {
                    EvidenceNormalizationGuard.Fail(
                        EvidenceNormalizationFailureCode.DuplicateLogicalKey,
                        observation,
                        $"Duplicate asset id '{securityId}'.");
                }

                var assetClass = EvidenceNormalizationGuard.RequireString(
                    asset,
                    "class",
                    observation).ToLowerInvariant();
                if (!assetClass.Equals("us_equity", StringComparison.Ordinal))
                {
                    EvidenceNormalizationGuard.Fail(
                        EvidenceNormalizationFailureCode.InvalidField,
                        observation,
                        $"Unsupported asset class '{assetClass}'.");
                }

                var exchange = EvidenceNormalizationGuard.RequireString(
                    asset,
                    "exchange",
                    observation).ToUpperInvariant();
                if (!SupportedExchanges.Contains(exchange))
                {
                    EvidenceNormalizationGuard.Fail(
                        EvidenceNormalizationFailureCode.InvalidField,
                        observation,
                        $"Unsupported asset exchange '{exchange}'.");
                }

                var symbol = EvidenceNormalizationGuard.RequireString(
                    asset,
                    "symbol",
                    observation).ToUpperInvariant();
                if (!SymbolPattern().IsMatch(symbol))
                {
                    EvidenceNormalizationGuard.Fail(
                        EvidenceNormalizationFailureCode.InvalidField,
                        observation,
                        $"Asset symbol '{symbol}' is invalid.");
                }

                var status = EvidenceNormalizationGuard.RequireString(
                    asset,
                    "status",
                    observation).ToLowerInvariant();
                if (status is not ("active" or "inactive"))
                {
                    EvidenceNormalizationGuard.Fail(
                        EvidenceNormalizationFailureCode.InvalidField,
                        observation,
                        $"Unsupported asset status '{status}'.");
                }

                parsed.Add(new ParsedAsset(
                    securityId,
                    symbol,
                    EvidenceNormalizationGuard.RequireString(asset, "name", observation),
                    exchange,
                    assetClass,
                    status,
                    AlpacaIdentityNormalizationGuard.ReadRequiredBoolean(
                        asset,
                        "tradable",
                        observation),
                    AlpacaIdentityNormalizationGuard.ReadRequiredBoolean(
                        asset,
                        "marginable",
                        observation),
                    AlpacaIdentityNormalizationGuard.ReadRequiredBoolean(
                        asset,
                        "shortable",
                        observation),
                    AlpacaIdentityNormalizationGuard.ReadRequiredBoolean(
                        asset,
                        "easy_to_borrow",
                        observation),
                    AlpacaIdentityNormalizationGuard.ReadRequiredBoolean(
                        asset,
                        "fractionable",
                        observation),
                    AlpacaIdentityNormalizationGuard.OptionalString(
                        asset,
                        "borrow_status",
                        observation),
                    AlpacaIdentityNormalizationGuard.OptionalDecimal(
                        asset,
                        "maintenance_margin_requirement",
                        observation),
                    AlpacaIdentityNormalizationGuard.OptionalDecimal(
                        asset,
                        "margin_requirement_long",
                        observation),
                    AlpacaIdentityNormalizationGuard.OptionalDecimal(
                        asset,
                        "margin_requirement_short",
                        observation),
                    ParseAttributes(asset, observation)));
            }

            if (parsed.Count == 0)
            {
                EvidenceNormalizationGuard.Fail(
                    EvidenceNormalizationFailureCode.MissingRequiredField,
                    observation,
                    "The current Alpaca asset-master snapshot cannot be empty.");
            }

            var ambiguousSymbols = parsed
                .GroupBy(value => value.Symbol, StringComparer.Ordinal)
                .Where(group => group.Select(value => value.SecurityId)
                    .Distinct(StringComparer.Ordinal)
                    .Count() > 1)
                .Select(group => group.Key)
                .ToHashSet(StringComparer.Ordinal);
            var source = EvidenceNormalizationGuard.Source(observation);
            var securities = parsed
                .OrderBy(value => value.Symbol, StringComparer.Ordinal)
                .ThenBy(value => value.SecurityId, StringComparer.Ordinal)
                .Select(value => new SecurityMasterSnapshotEvidenceRow(
                    schemaVersion,
                    observation.RunId,
                    observation.ConfigHash,
                    observation.CodeVersion,
                    observation.DataFeed,
                    observation.Provider,
                    value.SecurityId,
                    issuerId: null,
                    value.Symbol,
                    value.Name,
                    value.Exchange,
                    value.AssetClass,
                    observation.Currency,
                    value.Status,
                    value.Tradable,
                    value.Marginable,
                    value.Shortable,
                    value.EasyToBorrow,
                    value.Fractionable,
                    value.BorrowStatus,
                    value.MaintenanceMarginRequirement,
                    value.MarginRequirementLong,
                    value.MarginRequirementShort,
                    value.Attributes,
                    observation.ReceivedAtUtc,
                    ambiguousSymbols.Contains(value.Symbol),
                    [source]))
                .ToArray();
            var intervals = securities
                .Select(row => new SymbolIntervalEvidenceRow(
                    row.SchemaVersion,
                    row.RunId,
                    row.ConfigHash,
                    row.CodeVersion,
                    row.DataFeed,
                    row.Provider,
                    row.SecurityId,
                    row.IssuerId,
                    row.Symbol,
                    row.ObservedAtUtc,
                    row.ReadinessFailures.Contains(
                        IdentityEvidenceReadinessFailure.SymbolAmbiguousInSnapshot),
                    row.Sources))
                .ToArray();
            return new SecurityMasterSnapshotNormalizationResult(securities, intervals);
        }
        catch (Exception exception)
        {
            throw EvidenceNormalizationGuard.Wrap(observation, exception);
        }
    }

    private static IReadOnlyList<string> ParseAttributes(
        JsonElement asset,
        EvidenceSourceObservation observation)
    {
        var value = EvidenceNormalizationGuard.RequireProperty(
            asset,
            "attributes",
            JsonValueKind.Array,
            observation);
        var attributes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in value.EnumerateArray())
        {
            if (attribute.ValueKind != JsonValueKind.String ||
                String.IsNullOrWhiteSpace(attribute.GetString()))
            {
                EvidenceNormalizationGuard.Fail(
                    EvidenceNormalizationFailureCode.InvalidField,
                    observation,
                    "Asset attributes must be non-empty strings.");
            }

            if (!attributes.Add(attribute.GetString()!.Trim().ToLowerInvariant()))
            {
                EvidenceNormalizationGuard.Fail(
                    EvidenceNormalizationFailureCode.DuplicateLogicalKey,
                    observation,
                    "Asset attributes cannot contain duplicates.");
            }
        }

        return attributes.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9./-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex SymbolPattern();

    private sealed record ParsedAsset(
        string SecurityId,
        string Symbol,
        string Name,
        string Exchange,
        string AssetClass,
        string Status,
        bool Tradable,
        bool Marginable,
        bool Shortable,
        bool EasyToBorrow,
        bool Fractionable,
        string? BorrowStatus,
        decimal? MaintenanceMarginRequirement,
        decimal? MarginRequirementLong,
        decimal? MarginRequirementShort,
        IReadOnlyList<string> Attributes);
}
