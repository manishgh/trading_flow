using System.Globalization;
using System.Text.Json;
using TradingFlow.Domain.Research;

namespace TradingFlow.Data.Evidence.Normalization;

/// <summary>
/// Normalizes the official Alpaca market calendar into explicit open-session rows.
/// Closed dates are intentionally absent; consumers may interpret an omission only
/// when the committed dataset proves complete coverage for that date.
/// </summary>
public sealed class AlpacaExchangeCalendarEvidenceNormalizer
{
    private static readonly TimeOnly PremarketOpen = new(4, 0);
    private static readonly TimeOnly RegularOpen = new(9, 30);
    private static readonly TimeOnly RegularClose = new(16, 0);
    private static readonly TimeOnly PostmarketClose = new(20, 0);

    public IReadOnlyList<ExchangeSessionEvidenceRow> Normalize(
        EvidenceSourceObservation observation,
        ReadOnlyMemory<byte> rawBytes,
        AlpacaEvidenceNormalizationContext context)
    {
        try
        {
            EvidenceNormalizationGuard.ValidateObservation(
                observation,
                rawBytes,
                context,
                "/v2/calendar",
                requireRange: true);

            if (observation.RequestedSymbols.Count != 0)
            {
                EvidenceNormalizationGuard.Fail(
                    EvidenceNormalizationFailureCode.UnexpectedSymbol,
                    observation,
                    "Exchange-calendar evidence cannot be scoped to symbols.");
            }

            var startDate = DateOnly.FromDateTime(
                observation.RequestedStartUtc!.Value.UtcDateTime);
            var endDateExclusive = DateOnly.FromDateTime(
                observation.RequestedEndUtc!.Value.UtcDateTime);
            if (observation.RequestedStartUtc.Value.TimeOfDay != TimeSpan.Zero ||
                observation.RequestedEndUtc.Value.TimeOfDay != TimeSpan.Zero ||
                endDateExclusive <= startDate)
            {
                EvidenceNormalizationGuard.Fail(
                    EvidenceNormalizationFailureCode.ObservationMismatch,
                    observation,
                    "Exchange-calendar evidence requires UTC midnight, half-open date coverage.");
            }

            using var document = JsonDocument.Parse(rawBytes);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                EvidenceNormalizationGuard.Fail(
                    EvidenceNormalizationFailureCode.MalformedPayload,
                    observation,
                    "The Alpaca calendar response root must be a JSON array.");
            }

            EvidenceNormalizationGuard.EnsureNoDuplicateJsonProperties(
                document.RootElement,
                observation);
            var exchangeTimeZone = ResolveNewYorkTimeZone();
            var rows = new List<ExchangeSessionEvidenceRow>();
            var dates = new HashSet<DateOnly>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    EvidenceNormalizationGuard.Fail(
                        EvidenceNormalizationFailureCode.InvalidField,
                        observation,
                        "Every Alpaca calendar entry must be an object.");
                }

                var tradeDate = ParseDate(item, observation);
                if (tradeDate < startDate || tradeDate >= endDateExclusive)
                {
                    EvidenceNormalizationGuard.Fail(
                        EvidenceNormalizationFailureCode.TimestampOutsideRequestedRange,
                        observation,
                        $"Calendar date {tradeDate:yyyy-MM-dd} is outside requested coverage.");
                }

                if (tradeDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                {
                    EvidenceNormalizationGuard.Fail(
                        EvidenceNormalizationFailureCode.InvalidField,
                        observation,
                        $"Alpaca returned a weekend exchange session for {tradeDate:yyyy-MM-dd}.");
                }

                if (!dates.Add(tradeDate))
                {
                    EvidenceNormalizationGuard.Fail(
                        EvidenceNormalizationFailureCode.DuplicateLogicalKey,
                        observation,
                        $"Alpaca returned duplicate calendar date {tradeDate:yyyy-MM-dd}.");
                }

                var open = ParseTime(item, "open", observation);
                var close = ParseTime(item, "close", observation);
                if (open != RegularOpen || close > RegularClose || close <= open)
                {
                    EvidenceNormalizationGuard.Fail(
                        EvidenceNormalizationFailureCode.InvalidField,
                        observation,
                        $"Calendar date {tradeDate:yyyy-MM-dd} has unsupported regular-session bounds.");
                }

                rows.Add(new ExchangeSessionEvidenceRow(
                    context.SchemaVersion,
                    observation.RunId,
                    observation.ConfigHash,
                    observation.CodeVersion,
                    context.ExpectedDataFeed,
                    observation.Provider,
                    "XNYS",
                    tradeDate,
                    ToUtc(tradeDate, PremarketOpen, exchangeTimeZone),
                    ToUtc(tradeDate, open, exchangeTimeZone),
                    ToUtc(tradeDate, close, exchangeTimeZone),
                    ToUtc(tradeDate, PostmarketClose, exchangeTimeZone),
                    close < RegularClose,
                    "alpaca_official_market_calendar",
                    observation.ReceivedAtUtc,
                    [EvidenceNormalizationGuard.Source(observation)]));
            }

            return rows.OrderBy(row => row.TradeDate).ToArray();
        }
        catch (Exception exception)
        {
            throw EvidenceNormalizationGuard.Wrap(observation, exception);
        }
    }

    private static DateOnly ParseDate(
        JsonElement item,
        EvidenceSourceObservation observation)
    {
        var text = EvidenceNormalizationGuard.RequireString(item, "date", observation);
        if (!DateOnly.TryParseExact(
                text,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.InvalidField,
                observation,
                "Calendar property 'date' must use yyyy-MM-dd.");
        }

        return date;
    }

    private static TimeOnly ParseTime(
        JsonElement item,
        string name,
        EvidenceSourceObservation observation)
    {
        var text = EvidenceNormalizationGuard.RequireString(item, name, observation);
        if (!TimeOnly.TryParseExact(
                text,
                ["H:mm", "HH:mm"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var time))
        {
            EvidenceNormalizationGuard.Fail(
                EvidenceNormalizationFailureCode.InvalidField,
                observation,
                $"Calendar property '{name}' must use 24-hour H:mm or HH:mm.");
        }

        return time;
    }

    private static DateTimeOffset ToUtc(
        DateOnly date,
        TimeOnly time,
        TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsAmbiguousTime(local) || zone.IsInvalidTime(local))
        {
            throw new InvalidDataException(
                $"Exchange-local calendar boundary {local:O} is ambiguous or invalid.");
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static TimeZoneInfo ResolveNewYorkTimeZone()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
        }

        throw new InvalidOperationException(
            "A New York exchange timezone definition is required for calendar evidence.");
    }
}
