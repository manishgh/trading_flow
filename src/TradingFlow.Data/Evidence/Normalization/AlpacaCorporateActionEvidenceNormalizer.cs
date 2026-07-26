using System.Globalization;
using System.Text.Json;
using TradingFlow.Domain.Research;

namespace TradingFlow.Data.Evidence.Normalization;

public sealed class AlpacaCorporateActionEvidenceNormalizer
{
    private static readonly IReadOnlyDictionary<string, ActionSchema> Schemas = BuildSchemas();
    private static readonly IReadOnlySet<string> RootProperties =
        new HashSet<string>(["corporate_actions", "next_page_token"], StringComparer.Ordinal);

    public IReadOnlyList<CorporateActionEvidenceRow> Normalize(
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
                "/v1/corporate-actions",
                "alpaca-market-data",
                requireRange: true);
            if (observation.RequestedStartUtc!.Value.TimeOfDay != TimeSpan.Zero ||
                observation.RequestedEndUtc!.Value.TimeOfDay != TimeSpan.Zero)
            {
                EvidenceNormalizationGuard.Fail(
                    EvidenceNormalizationFailureCode.ObservationMismatch,
                    observation,
                    "Corporate-action evidence ranges must use UTC midnight boundaries.");
            }

            using var document = JsonDocument.Parse(rawBytes);
            var root = EvidenceNormalizationGuard.RequireRootObject(document, observation);
            EvidenceNormalizationGuard.EnsureNoDuplicateJsonProperties(root, observation);
            AlpacaIdentityNormalizationGuard.EnsureOnlyProperties(
                root,
                RootProperties,
                observation,
                "$");
            EvidenceNormalizationGuard.ValidatePagination(root, observation);
            var buckets = EvidenceNormalizationGuard.RequireProperty(
                root,
                "corporate_actions",
                JsonValueKind.Object,
                observation);

            var rows = new List<CorporateActionEvidenceRow>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var bucket in buckets.EnumerateObject())
            {
                if (!Schemas.TryGetValue(bucket.Name, out var schema))
                {
                    EvidenceNormalizationGuard.Fail(
                        EvidenceNormalizationFailureCode.InvalidField,
                        observation,
                        $"Unsupported corporate-action bucket '{bucket.Name}'.");
                }

                if (bucket.Value.ValueKind != JsonValueKind.Array)
                {
                    EvidenceNormalizationGuard.Fail(
                        EvidenceNormalizationFailureCode.InvalidField,
                        observation,
                        $"Corporate-action bucket '{bucket.Name}' must be an array.");
                }

                foreach (var action in bucket.Value.EnumerateArray())
                {
                    if (action.ValueKind != JsonValueKind.Object)
                    {
                        EvidenceNormalizationGuard.Fail(
                            EvidenceNormalizationFailureCode.InvalidField,
                            observation,
                            $"Every '{bucket.Name}' action must be an object.");
                    }

                    AlpacaIdentityNormalizationGuard.EnsureOnlyProperties(
                        action,
                        schema.AllowedProperties,
                        observation,
                        $"$.corporate_actions.{bucket.Name}[]");
                    var actionId = EvidenceNormalizationGuard.RequireString(
                        action,
                        "id",
                        observation);
                    if (!Guid.TryParse(actionId, out _))
                    {
                        EvidenceNormalizationGuard.Fail(
                            EvidenceNormalizationFailureCode.InvalidField,
                            observation,
                            $"Corporate-action id '{actionId}' is not a UUID.");
                    }

                    var logicalKey = $"{schema.Type}|{actionId}";
                    if (!keys.Add(logicalKey))
                    {
                        EvidenceNormalizationGuard.Fail(
                            EvidenceNormalizationFailureCode.DuplicateLogicalKey,
                            observation,
                            $"Duplicate corporate action '{logicalKey}'.");
                    }

                    var processDate = RequireDate(action, "process_date", observation);
                    EnsureProcessDateInRange(processDate, observation);
                    var identity = ResolvePrimaryIdentity(action, schema, observation);
                    EnsureRequestedSymbol(identity.Symbols, observation);
                    ValidateTypedFields(action, schema, observation);
                    rows.Add(new CorporateActionEvidenceRow(
                        schemaVersion,
                        observation.RunId,
                        observation.ConfigHash,
                        observation.CodeVersion,
                        observation.DataFeed,
                        observation.Provider,
                        schema.Type,
                        actionId,
                        securityId: null,
                        issuerId: null,
                        identity.PrimarySymbol,
                        identity.PrimaryCusip,
                        processDate,
                        OptionalDate(action, "effective_date", observation),
                        OptionalDate(action, "ex_date", observation),
                        OptionalDate(action, "record_date", observation),
                        OptionalDate(action, "payable_date", observation),
                        CopyProviderFields(action),
                        observation.ReceivedAtUtc,
                        [EvidenceNormalizationGuard.Source(observation)]));
                }
            }

            return rows
                .OrderBy(row => row.ProcessDate)
                .ThenBy(row => row.ActionType)
                .ThenBy(row => row.ProviderActionId, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception)
        {
            throw EvidenceNormalizationGuard.Wrap(observation, exception);
        }
    }

    private static void ValidateTypedFields(
        JsonElement action,
        ActionSchema schema,
        EvidenceSourceObservation observation)
    {
        foreach (var field in schema.DateProperties)
        {
            _ = OptionalDate(action, field, observation);
        }

        foreach (var field in schema.DecimalProperties)
        {
            _ = AlpacaIdentityNormalizationGuard.OptionalDecimal(action, field, observation);
        }

        foreach (var field in schema.BooleanProperties)
        {
            if (action.TryGetProperty(field, out var value) &&
                value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            {
                EvidenceNormalizationGuard.Fail(
                    EvidenceNormalizationFailureCode.InvalidField,
                    observation,
                    $"Corporate-action property '{field}' must be boolean or null.");
            }
        }

        foreach (var field in schema.StringProperties)
        {
            _ = AlpacaIdentityNormalizationGuard.OptionalString(action, field, observation);
        }

        if (action.TryGetProperty("stock_movements", out var stockMovements) &&
            stockMovements.ValueKind != JsonValueKind.Array)
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.InvalidField,
                observation,
                "Corporate-action property 'stock_movements' must be an array.");
        }
    }

    private static (string? PrimarySymbol, string? PrimaryCusip, IReadOnlyList<string> Symbols)
        ResolvePrimaryIdentity(
            JsonElement action,
            ActionSchema schema,
            EvidenceSourceObservation observation)
    {
        var symbols = schema.SymbolProperties
            .Select(name => AlpacaIdentityNormalizationGuard.OptionalString(
                action,
                name,
                observation))
            .Where(value => value is not null)
            .Select(value => value!.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var cusips = schema.CusipProperties
            .Select(name => AlpacaIdentityNormalizationGuard.OptionalString(
                action,
                name,
                observation))
            .Where(value => value is not null)
            .Select(value => value!.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (symbols.Length == 0 && cusips.Length == 0)
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.MissingRequiredField,
                observation,
                $"Corporate action '{schema.Type}' has no symbol or CUSIP identity reference.");
        }

        foreach (var cusip in cusips)
        {
            if (cusip.Length != 9 || cusip.Any(character => !Char.IsAsciiLetterOrDigit(character)))
            {
                EvidenceNormalizationGuard.Fail(
                    EvidenceNormalizationFailureCode.InvalidField,
                    observation,
                    $"Corporate-action CUSIP '{cusip}' is invalid.");
            }
        }

        return (symbols.FirstOrDefault(), cusips.FirstOrDefault(), symbols);
    }

    private static void EnsureRequestedSymbol(
        IReadOnlyList<string> symbols,
        EvidenceSourceObservation observation)
    {
        if (observation.RequestedSymbols.Count > 0 &&
            !symbols.Any(symbol => observation.RequestedSymbols.Contains(
                symbol,
                StringComparer.Ordinal)))
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.UnexpectedSymbol,
                observation,
                $"Corporate action does not reference any requested symbol.");
        }
    }

    private static void EnsureProcessDateInRange(
        DateOnly processDate,
        EvidenceSourceObservation observation)
    {
        var start = DateOnly.FromDateTime(observation.RequestedStartUtc!.Value.UtcDateTime);
        var end = DateOnly.FromDateTime(observation.RequestedEndUtc!.Value.UtcDateTime);
        if (processDate < start || processDate >= end)
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.TimestampOutsideRequestedRange,
                observation,
                $"Corporate-action process date '{processDate:yyyy-MM-dd}' is outside " +
                $"[{start:yyyy-MM-dd}, {end:yyyy-MM-dd}).");
        }
    }

    private static DateOnly RequireDate(
        JsonElement parent,
        string name,
        EvidenceSourceObservation observation) =>
        OptionalDate(parent, name, observation) ??
        throw new EvidenceNormalizationException(
            EvidenceNormalizationFailureCode.MissingRequiredField,
            observation.ObservationId,
            $"Required corporate-action date '{name}' is missing.");

    private static DateOnly? OptionalDate(
        JsonElement parent,
        string name,
        EvidenceSourceObservation observation)
    {
        var raw = AlpacaIdentityNormalizationGuard.OptionalString(parent, name, observation);
        if (raw is null)
        {
            return null;
        }

        if (!DateOnly.TryParseExact(
                raw,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.InvalidField,
                observation,
                $"Corporate-action date '{name}' must use yyyy-MM-dd.");
        }

        return parsed;
    }

    private static IReadOnlyDictionary<string, string> CopyProviderFields(JsonElement action) =>
        action.EnumerateObject()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToDictionary(
                property => property.Name,
                property => property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()!
                    : property.Value.GetRawText(),
                StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, ActionSchema> BuildSchemas()
    {
        var commonDates = new[] { "process_date", "effective_date", "ex_date", "record_date", "payable_date" };
        return new Dictionary<string, ActionSchema>(StringComparer.Ordinal)
        {
            ["reverse_splits"] = Schema(CorporateActionEvidenceType.ReverseSplit,
                ["symbol", "new_symbol"], ["old_cusip", "new_cusip"],
                commonDates, ["old_rate", "new_rate"], [],
                ["id", "symbol", "new_symbol", "old_cusip", "new_cusip", "process_date", "effective_date", "ex_date", "record_date", "payable_date", "old_rate", "new_rate", "isin", "currency"]),
            ["forward_splits"] = Schema(CorporateActionEvidenceType.ForwardSplit,
                ["symbol"], ["cusip"], [.. commonDates, "due_bill_redemption_date"],
                ["old_rate", "new_rate"], [],
                ["id", "symbol", "cusip", "process_date", "effective_date", "ex_date", "record_date", "payable_date", "due_bill_redemption_date", "old_rate", "new_rate", "isin", "currency"]),
            ["unit_splits"] = Schema(CorporateActionEvidenceType.UnitSplit,
                ["old_symbol", "new_symbol", "alternate_symbol"],
                ["old_cusip", "new_cusip", "alternate_cusip"],
                commonDates, ["old_rate", "new_rate", "alternate_rate"], [],
                ["id", "old_symbol", "new_symbol", "alternate_symbol", "old_cusip", "new_cusip", "alternate_cusip", "process_date", "effective_date", "ex_date", "record_date", "payable_date", "old_rate", "new_rate", "alternate_rate", "isin", "currency"]),
            ["cash_dividends"] = Schema(CorporateActionEvidenceType.CashDividend,
                ["symbol"], ["cusip"],
                [.. commonDates, "due_bill_on_date", "due_bill_off_date"],
                ["rate"], ["foreign", "special"],
                ["id", "symbol", "cusip", "process_date", "effective_date", "ex_date", "record_date", "payable_date", "due_bill_on_date", "due_bill_off_date", "rate", "foreign", "special", "sub_type", "isin", "currency"]),
            ["stock_dividends"] = Schema(CorporateActionEvidenceType.StockDividend,
                ["symbol"], ["cusip"], commonDates, ["rate"], [],
                ["id", "symbol", "cusip", "process_date", "effective_date", "ex_date", "record_date", "payable_date", "rate", "isin", "currency"]),
            ["spin_offs"] = Schema(CorporateActionEvidenceType.SpinOff,
                ["source_symbol", "new_symbol"], ["source_cusip", "new_cusip"],
                commonDates, ["source_rate", "new_rate"], [],
                ["id", "source_symbol", "new_symbol", "source_cusip", "new_cusip", "process_date", "effective_date", "ex_date", "record_date", "payable_date", "source_rate", "new_rate", "isin", "currency"]),
            ["cash_mergers"] = Schema(CorporateActionEvidenceType.CashMerger,
                ["acquiree_symbol"], ["acquiree_cusip"], commonDates, ["rate"], [],
                ["id", "acquiree_symbol", "acquiree_cusip", "process_date", "effective_date", "ex_date", "record_date", "payable_date", "rate", "isin", "currency"]),
            ["stock_mergers"] = Schema(CorporateActionEvidenceType.StockMerger,
                ["acquiree_symbol", "acquirer_symbol"], ["acquiree_cusip", "acquirer_cusip"],
                commonDates, ["acquiree_rate", "acquirer_rate"], [],
                ["id", "acquiree_symbol", "acquirer_symbol", "acquiree_cusip", "acquirer_cusip", "process_date", "effective_date", "ex_date", "record_date", "payable_date", "acquiree_rate", "acquirer_rate", "isin", "currency"]),
            ["stock_and_cash_mergers"] = Schema(CorporateActionEvidenceType.StockAndCashMerger,
                ["acquiree_symbol", "acquirer_symbol"], ["acquiree_cusip", "acquirer_cusip"],
                commonDates, ["acquiree_rate", "acquirer_rate", "cash_rate"], [],
                ["id", "acquiree_symbol", "acquirer_symbol", "acquiree_cusip", "acquirer_cusip", "process_date", "effective_date", "ex_date", "record_date", "payable_date", "acquiree_rate", "acquirer_rate", "cash_rate", "isin", "currency"]),
            ["redemptions"] = Schema(CorporateActionEvidenceType.Redemption,
                ["symbol"], ["cusip"], commonDates, ["rate"], [],
                ["id", "symbol", "cusip", "process_date", "effective_date", "ex_date", "record_date", "payable_date", "rate", "isin", "currency"]),
            ["name_changes"] = Schema(CorporateActionEvidenceType.NameChange,
                ["old_symbol", "new_symbol"], ["old_cusip", "new_cusip"],
                commonDates, [], [],
                ["id", "old_symbol", "new_symbol", "old_cusip", "new_cusip", "process_date", "effective_date", "ex_date", "record_date", "payable_date", "isin", "currency"]),
            ["worthless_removals"] = Schema(CorporateActionEvidenceType.WorthlessRemoval,
                ["symbol"], ["cusip"], commonDates, [], [],
                ["id", "symbol", "cusip", "process_date", "effective_date", "ex_date", "record_date", "payable_date", "isin", "currency"]),
            ["rights_distributions"] = Schema(CorporateActionEvidenceType.RightsDistribution,
                ["source_symbol", "new_symbol"], ["source_cusip", "new_cusip"],
                [.. commonDates, "expiration_date"], ["rate"], [],
                ["id", "source_symbol", "new_symbol", "source_cusip", "new_cusip", "process_date", "effective_date", "ex_date", "record_date", "payable_date", "expiration_date", "rate", "isin", "currency"]),
            ["partial_calls"] = Schema(CorporateActionEvidenceType.PartialCall,
                ["symbol"], ["cusip"], [.. commonDates, "lottery_date"], ["price"], [],
                ["id", "symbol", "cusip", "process_date", "effective_date", "ex_date", "record_date", "payable_date", "lottery_date", "price", "lottery_type", "isin", "currency"]),
            ["reorganizations"] = Schema(CorporateActionEvidenceType.Reorganization,
                ["symbol"], ["cusip"], commonDates, ["cash_rate"], [],
                ["id", "symbol", "cusip", "process_date", "effective_date", "ex_date", "record_date", "payable_date", "cash_rate", "stock_movements", "isin", "currency"])
        };
    }

    private static ActionSchema Schema(
        CorporateActionEvidenceType type,
        IReadOnlyList<string> symbols,
        IReadOnlyList<string> cusips,
        IReadOnlyList<string> dates,
        IReadOnlyList<string> decimals,
        IReadOnlyList<string> booleans,
        IReadOnlyList<string> allowed)
    {
        var stringFields = allowed
            .Except(dates, StringComparer.Ordinal)
            .Except(decimals, StringComparer.Ordinal)
            .Except(booleans, StringComparer.Ordinal)
            .Except(["stock_movements"], StringComparer.Ordinal)
            .ToArray();
        return new ActionSchema(
            type,
            symbols,
            cusips,
            dates,
            decimals,
            booleans,
            stringFields,
            allowed.ToHashSet(StringComparer.Ordinal));
    }

    private sealed record ActionSchema(
        CorporateActionEvidenceType Type,
        IReadOnlyList<string> SymbolProperties,
        IReadOnlyList<string> CusipProperties,
        IReadOnlyList<string> DateProperties,
        IReadOnlyList<string> DecimalProperties,
        IReadOnlyList<string> BooleanProperties,
        IReadOnlyList<string> StringProperties,
        IReadOnlySet<string> AllowedProperties);
}
