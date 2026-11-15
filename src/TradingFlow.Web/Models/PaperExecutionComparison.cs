using System.Text.Json;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Web.Models;

public enum PaperPriceReference { AdmissionAsk, AdmissionBid, ModeledExecutable, Decision }

public sealed record PaperUnitFill(string ExecutionId, string BrokerOrderId, DateTimeOffset? AtUtc, decimal Quantity, decimal Price);

public sealed record PaperOrderExecutionDetail(OrderIntentRecord Intent, string State, decimal? BrokerFilledQuantity,
    OrderAdmissionTelemetryRecord? Admission, StrategyArtifactIdentity? Identity,
    IReadOnlyList<OrderDispatchAttemptTelemetryRecord> Attempts, IReadOnlyList<PaperUnitFill> Fills)
{
    public DateTimeOffset? FirstPrePostAtUtc => Attempts.FirstOrDefault(attempt => attempt.AttemptNumber == 1)?.PrePostAtUtc
        ?? (Intent.DispatchAttemptCount == 1 ? Intent.LastDispatchAttemptAtUtc : null);

    public decimal UnitFillQuantity => Fills.Sum(fill => fill.Quantity);
    public decimal? WeightedFillPrice => UnitFillQuantity > 0 ? Fills.Sum(fill => fill.Price * fill.Quantity) / UnitFillQuantity : null;
    public TimeSpan? DecisionToFirstAttempt => PaperExecutionComparison.Elapsed(Admission?.DecisionAtUtc, FirstPrePostAtUtc);

    // Read only bounded persisted evidence; an absent or malformed clock remains explicitly unavailable.
    public OrderEntryWindowEvidence? ReadEntryWindow()
    {
        return OrderEntryWindowEvidence.Read(Admission?.EntryWindowJson);
    }

    private bool HasAdmissionComparison =>
        PaperExecutionComparison.Elapsed(Admission?.QuoteProviderTimestampUtc, Admission?.QuoteObservedAtUtc) is not null &&
        PaperExecutionComparison.Elapsed(Admission?.QuoteObservedAtUtc, FirstPrePostAtUtc) is not null;

    // Select the explicitly named price for the order's side, with each missing reference left unavailable.
    public decimal? Reference(PaperPriceReference kind) => (kind switch
    {
        PaperPriceReference.AdmissionAsk when Intent.Side == "buy" && HasAdmissionComparison => Admission?.Ask,
        PaperPriceReference.AdmissionBid when Intent.Side == "sell" && HasAdmissionComparison => Admission?.Bid,
        PaperPriceReference.ModeledExecutable => Admission?.ModeledExecutablePrice,
        PaperPriceReference.Decision => Admission?.DecisionPrice,
        _ => null
    }) is > 0m and var price ? price : null;

    // Compare against a pre-existing observation with two independent clocks and the threshold frozen at admission.
    public PaperDispatchQuoteTiming QuoteTiming(DateTimeOffset? prePostAtUtc)
    {
        var age = PaperExecutionComparison.Elapsed(Admission?.QuoteProviderTimestampUtc, prePostAtUtc);
        var delay = PaperExecutionComparison.Elapsed(Admission?.QuoteObservedAtUtc, prePostAtUtc);
        return new(age, delay, age is { } measured && Admission?.QuoteFreshnessLimitMilliseconds is { } limit
            ? measured.TotalMilliseconds > limit : null);
    }
}

public sealed record PaperDispatchQuoteTiming(TimeSpan? ProviderQuoteAge, TimeSpan? ObservationDelay, bool? Stale);

public sealed record PaperExecutionMonth(Guid RunId, string Month, string StrategyId, string? Version, string? ContentSha256,
    Guid? UnidentifiedIntentId, PaperPriceReference ReferenceKind, int OrderCount, int CoveredOrderCount,
    int FillCount, int CoveredFillCount, decimal FillQuantity, decimal CoveredFillQuantity,
    decimal WeightedFillPrice, decimal? AdverseBps, decimal? DecisionToAttemptSeconds, decimal? DecisionToFillSeconds,
    int StaleAtFirstAttemptFillCount);

public sealed record PaperOrderCoverage(string State, int OrderCount, int WithoutUnitFills, int MissingUnits, int FillsWithoutClock,
    int WithoutAdmissionQuote, int WithoutDecisionReference);

public sealed record PaperExecutionComparisonResult(IReadOnlyList<PaperOrderExecutionDetail> Orders,
    IReadOnlyList<PaperExecutionMonth> Months, IReadOnlyList<PaperOrderCoverage> Coverage,
    int OrderCount, string? UnavailableReason = null);

internal static class PaperExecutionComparison
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private const int VisibleOrderCount = 100;

    // Repeat identical broker execution IDs only once. Conflicts make comparison unavailable.
    public static IReadOnlyDictionary<string, IReadOnlyList<PaperUnitFill>> ReadUnitFills(
        IEnumerable<PositionEventRecord> events)
    {
        var rows = events.Where(evt => evt.Source == "broker_stream" && evt.FillQuantity > 0m && evt.FillPrice > 0m &&
            !String.IsNullOrWhiteSpace(evt.ExecutionId) && !evt.ExecutionId.StartsWith("rest:", StringComparison.Ordinal))
            .Select(evt => (Event: evt, Fill: ReadUnitFill(evt)));
        var units = new List<(string ClientOrderId, PaperUnitFill Fill)>();
        foreach (var group in rows.GroupBy(row => (row.Event.AccountId, row.Event.ExecutionId)))
        {
            var unique = group.Select(row => (row.Event.ClientOrderId, row.Fill)).Distinct().ToArray();
            if (unique.Length != 1)
                throw new InvalidDataException("A broker execution ID has conflicting order fill evidence.");
            units.Add(unique[0]);
        }
        return units.GroupBy(row => row.ClientOrderId, StringComparer.Ordinal).ToDictionary(group => group.Key,
            group => (IReadOnlyList<PaperUnitFill>)group.Select(row => row.Fill).OrderBy(fill => fill.AtUtc)
                .ThenBy(fill => fill.ExecutionId, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    }

    // Retain proven unit fills even when the separate broker execution clock was not captured.
    private static PaperUnitFill ReadUnitFill(PositionEventRecord evt)
    {
        DateTimeOffset? clock = null;
        try
        {
            using var json = JsonDocument.Parse(evt.PayloadJson);
            if (json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("ExecutionTimestampUtc", out var timestamp) && timestamp.ValueKind == JsonValueKind.String &&
                timestamp.TryGetDateTimeOffset(out var observed) &&
                json.RootElement.TryGetProperty("OrderId", out var order) && order.ValueKind == JsonValueKind.String &&
                order.GetString() == evt.BrokerOrderId)
                clock = observed.ToUniversalTime();
        }
        catch (JsonException)
        {
            // The position ledger still establishes the execution ID, increment and price.
        }
        return new(evt.ExecutionId, evt.BrokerOrderId, clock, evt.FillQuantity, evt.FillPrice);
    }

    // Read immutable identity even from older orders whose reservation has no separate telemetry.
    public static StrategyArtifactIdentity? ReadIdentity(OrderIntentRecord intent, OrderAdmissionTelemetryRecord? admission)
    {
        try
        {
            if (admission is { StrategyArtifactId: { } id, StrategyVersion: { } version, StrategyContentSha256: { } hash })
                return new(id, version, hash);
            using var json = JsonDocument.Parse(intent.RequestJson);
            return json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("strategyIdentity", out var identity) && identity.ValueKind == JsonValueKind.Object
                ? identity.Deserialize<StrategyArtifactIdentity>() : null;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return null;
        }
    }

    // Attribute each fill to its own New York month and emit each reference as a separate series.
    public static PaperExecutionComparisonResult Build(IReadOnlyList<PaperOrderExecutionDetail> orders)
    {
        var months = orders.SelectMany(order => order.Fills.Where(fill => fill.AtUtc is not null).Select(fill => (Order: order, Fill: fill)))
            .GroupBy(row => (row.Order.Intent.RunId, Month: FillMonth(row.Fill.AtUtc!.Value),
                StrategyId: row.Order.Identity?.StrategyId ?? row.Order.Intent.StrategyId,
                Version: row.Order.Identity?.SemanticVersion, Hash: row.Order.Identity?.ContentSha256,
                UnidentifiedIntentId: row.Order.Identity is null ? (Guid?)row.Order.Intent.IntentId : null))
            .OrderBy(group => group.Key.Month, StringComparer.Ordinal)
            .ThenBy(group => group.Key.StrategyId, StringComparer.Ordinal)
            .SelectMany(group =>
            {
                var rows = group.ToArray();
                return Enum.GetValues<PaperPriceReference>().Select(kind => Summarize(
                    group.Key.RunId, group.Key.Month, group.Key.StrategyId, group.Key.Version, group.Key.Hash,
                    group.Key.UnidentifiedIntentId, kind, rows));
            }).ToArray();
        var coverage = orders.GroupBy(order => order.State, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new PaperOrderCoverage(group.Key, group.Count(), group.Count(order => order.Fills.Count == 0),
                group.Count(order => order.BrokerFilledQuantity > order.UnitFillQuantity),
                group.Sum(order => order.Fills.Count(fill => fill.AtUtc is null)),
                group.Count(order => order.Admission?.QuoteObservedAtUtc is null),
                group.Count(order => order.Admission?.DecisionPrice is null))).ToArray();
        return new(orders.OrderByDescending(order => order.Intent.CreatedAtUtc).Take(VisibleOrderCount).ToArray(),
            months, coverage, orders.Count);
    }

    // Preserve the denominator and report fill-quantity-weighted prices and delays on observed fills.
    private static PaperExecutionMonth Summarize(Guid runId, string month, string strategyId, string? version, string? hash,
        Guid? unidentifiedIntentId, PaperPriceReference kind,
        IReadOnlyList<(PaperOrderExecutionDetail Order, PaperUnitFill Fill)> rows)
    {
        var covered = rows.Select(row => (row.Order, row.Fill, Price: row.Order.Reference(kind)))
            .Where(row => row.Price is not null).ToArray();
        var units = rows.Sum(row => row.Fill.Quantity);
        var coveredUnits = covered.Sum(row => row.Fill.Quantity);
        return new(runId, month, strategyId, version, hash, unidentifiedIntentId, kind,
            rows.Select(row => row.Order.Intent.IntentId).Distinct().Count(),
            covered.Select(row => row.Order.Intent.IntentId).Distinct().Count(), rows.Count, covered.Length, units, coveredUnits,
            rows.Sum(row => row.Fill.Quantity * row.Fill.Price) / units,
            coveredUnits > 0 ? covered.Sum(row => row.Fill.Quantity *
                AdverseBps(row.Order.Intent.Side, row.Price!.Value, row.Fill.Price)) / coveredUnits : null,
            WeightedDelay(rows.Select(row => (row.Fill.Quantity, row.Order.DecisionToFirstAttempt))),
            WeightedDelay(rows.Select(row => (row.Fill.Quantity, Elapsed(row.Order.Admission?.DecisionAtUtc, row.Fill.AtUtc)))),
            covered.Count(row => row.Order.QuoteTiming(row.Order.FirstPrePostAtUtc).Stale == true));
    }

    // A higher purchase or lower sale is adverse relative to the explicitly named reference.
    public static decimal AdverseBps(string side, decimal reference, decimal fill) =>
        (side == "sell" ? reference - fill : fill - reference) / reference * 10_000m;

    public static TimeSpan? Elapsed(DateTimeOffset? from, DateTimeOffset? to) =>
        from is { } start && to is { } end && end >= start ? end - start : null;

    private static decimal? WeightedDelay(IEnumerable<(decimal Units, TimeSpan? Delay)> measures)
    {
        var known = measures.Where(measure => measure.Delay is not null).ToArray();
        return known.Length == 0 ? null : known.Sum(measure => measure.Units * (decimal)measure.Delay!.Value.TotalSeconds) /
            known.Sum(measure => measure.Units);
    }

    private static string FillMonth(DateTimeOffset at) =>
        TimeZoneInfo.ConvertTime(at, NewYork).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
}
