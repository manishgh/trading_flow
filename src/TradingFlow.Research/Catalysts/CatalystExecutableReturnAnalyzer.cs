using TradingFlow.Domain.Research;

namespace TradingFlow.Research.Catalysts;

public enum ExecutablePositionDirection
{
    Long = 1,
    Short = 2
}

public enum UsEquityTradingSession
{
    Premarket = 1,
    Regular = 2,
    Postmarket = 3
}

public enum CatalystExecutableReturnStatus
{
    NetExecutableEstimate = 1,
    Rejected = 2,
    Censored = 3,
    NoFill = 4
}

public enum CatalystExecutableReturnFailure
{
    None = 0,
    QuoteUnavailable = 1,
    QuoteStale = 2,
    QuoteWrongFeed = 3,
    QuoteWrongSession = 4,
    QuoteOneSided = 5,
    QuoteCrossed = 6,
    QuoteConditionIneligible = 7,
    QuoteExchangeIneligible = 8,
    QuoteDisplayedSizeUnavailable = 9,
    NoDisplayedLiquidity = 10,
    SpreadExceedsMaximum = 11,
    AuctionEvidenceUnavailable = 12,
    PartialExitNotFullyClosed = 13
}

public sealed record SipQuoteEligibilityPolicy
{
    public SipQuoteEligibilityPolicy(
        IEnumerable<string> eligibleConditions,
        IEnumerable<string> eligibleExchanges,
        bool allowUnconditionedQuotes)
    {
        EligibleConditions = NormalizeCodes(eligibleConditions, nameof(eligibleConditions));
        EligibleExchanges = NormalizeCodes(eligibleExchanges, nameof(eligibleExchanges));
        if (EligibleExchanges.Count == 0)
        {
            throw new ArgumentException(
                "At least one eligible quote exchange is required.",
                nameof(eligibleExchanges));
        }

        AllowUnconditionedQuotes = allowUnconditionedQuotes;
    }

    public IReadOnlySet<string> EligibleConditions { get; }
    public IReadOnlySet<string> EligibleExchanges { get; }
    public bool AllowUnconditionedQuotes { get; }

    public bool ConditionsAreEligible(IReadOnlyList<string> conditions)
    {
        if (conditions.Count == 0)
        {
            return AllowUnconditionedQuotes;
        }

        return conditions.All(EligibleConditions.Contains);
    }

    public bool ExchangeIsEligible(string? exchange) =>
        exchange is not null && EligibleExchanges.Contains(exchange);

    private static IReadOnlySet<string> NormalizeCodes(
        IEnumerable<string> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        return values
            .Where(value => !String.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);
    }
}

public sealed record CatalystExecutionCostModel
{
    public CatalystExecutionCostModel(
        decimal perShareFee,
        decimal minimumFeePerOrder,
        decimal entryMarketImpactBasisPoints,
        decimal exitMarketImpactBasisPoints)
    {
        if (perShareFee < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(perShareFee));
        }

        if (minimumFeePerOrder < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumFeePerOrder));
        }

        if (entryMarketImpactBasisPoints is < 0m or >= 10_000m)
        {
            throw new ArgumentOutOfRangeException(nameof(entryMarketImpactBasisPoints));
        }

        if (exitMarketImpactBasisPoints is < 0m or >= 10_000m)
        {
            throw new ArgumentOutOfRangeException(nameof(exitMarketImpactBasisPoints));
        }

        PerShareFee = perShareFee;
        MinimumFeePerOrder = minimumFeePerOrder;
        EntryMarketImpactBasisPoints = entryMarketImpactBasisPoints;
        ExitMarketImpactBasisPoints = exitMarketImpactBasisPoints;
    }

    public decimal PerShareFee { get; }
    public decimal MinimumFeePerOrder { get; }
    public decimal EntryMarketImpactBasisPoints { get; }
    public decimal ExitMarketImpactBasisPoints { get; }

    public decimal FeeFor(long quantity) =>
        Math.Max(MinimumFeePerOrder, PerShareFee * quantity);
}

public sealed record CatalystExecutableReturnRequest
{
    public CatalystExecutableReturnRequest(
        string securityId,
        string symbol,
        ExecutablePositionDirection direction,
        DateTimeOffset eligibleEntryAtUtc,
        DateTimeOffset eligibleExitAtUtc,
        UsEquityTradingSession session,
        DateTimeOffset sessionStartUtc,
        DateTimeOffset sessionEndUtc,
        TimeSpan maximumQuoteDelay,
        long requestedQuantity,
        SipQuoteEligibilityPolicy quoteEligibilityPolicy,
        CatalystExecutionCostModel costModel)
        : this(
            securityId,
            symbol,
            direction,
            eligibleEntryAtUtc,
            eligibleExitAtUtc,
            session,
            sessionStartUtc,
            sessionEndUtc,
            maximumQuoteDelay,
            requestedQuantity,
            quoteEligibilityPolicy,
            costModel,
            CatalystExecutionCalibrationPolicy.DiagnosticDefaults)
    {
    }

    public CatalystExecutableReturnRequest(
        string securityId,
        string symbol,
        ExecutablePositionDirection direction,
        DateTimeOffset eligibleEntryAtUtc,
        DateTimeOffset eligibleExitAtUtc,
        UsEquityTradingSession session,
        DateTimeOffset sessionStartUtc,
        DateTimeOffset sessionEndUtc,
        TimeSpan maximumQuoteDelay,
        long requestedQuantity,
        SipQuoteEligibilityPolicy quoteEligibilityPolicy,
        CatalystExecutionCostModel costModel,
        CatalystExecutionCalibrationPolicy calibrationPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(securityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        if (!Enum.IsDefined(session))
        {
            throw new ArgumentOutOfRangeException(nameof(session));
        }

        EnsureUtc(eligibleEntryAtUtc, nameof(eligibleEntryAtUtc));
        EnsureUtc(eligibleExitAtUtc, nameof(eligibleExitAtUtc));
        EnsureUtc(sessionStartUtc, nameof(sessionStartUtc));
        EnsureUtc(sessionEndUtc, nameof(sessionEndUtc));
        if (eligibleExitAtUtc <= eligibleEntryAtUtc)
        {
            throw new ArgumentException("Eligible exit must follow eligible entry.");
        }

        if (sessionEndUtc <= sessionStartUtc)
        {
            throw new ArgumentException("Session end must follow session start.");
        }

        if (eligibleEntryAtUtc < sessionStartUtc ||
            eligibleEntryAtUtc >= sessionEndUtc ||
            eligibleExitAtUtc < sessionStartUtc ||
            eligibleExitAtUtc >= sessionEndUtc)
        {
            throw new ArgumentException(
                "Eligible entry and exit must be inside the supplied point-in-time session window.");
        }

        if (maximumQuoteDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumQuoteDelay));
        }

        if (requestedQuantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedQuantity));
        }

        SecurityId = securityId.Trim();
        Symbol = symbol.Trim().ToUpperInvariant();
        Direction = direction;
        EligibleEntryAtUtc = eligibleEntryAtUtc;
        EligibleExitAtUtc = eligibleExitAtUtc;
        Session = session;
        SessionStartUtc = sessionStartUtc;
        SessionEndUtc = sessionEndUtc;
        MaximumQuoteDelay = maximumQuoteDelay;
        RequestedQuantity = requestedQuantity;
        QuoteEligibilityPolicy = quoteEligibilityPolicy
            ?? throw new ArgumentNullException(nameof(quoteEligibilityPolicy));
        CostModel = costModel ?? throw new ArgumentNullException(nameof(costModel));
        CalibrationPolicy = calibrationPolicy
            ?? throw new ArgumentNullException(nameof(calibrationPolicy));
    }

    public string SecurityId { get; }
    public string Symbol { get; }
    public ExecutablePositionDirection Direction { get; }
    public DateTimeOffset EligibleEntryAtUtc { get; }
    public DateTimeOffset EligibleExitAtUtc { get; }
    public UsEquityTradingSession Session { get; }
    public DateTimeOffset SessionStartUtc { get; }
    public DateTimeOffset SessionEndUtc { get; }
    public TimeSpan MaximumQuoteDelay { get; }
    public long RequestedQuantity { get; }
    public SipQuoteEligibilityPolicy QuoteEligibilityPolicy { get; }
    public CatalystExecutionCostModel CostModel { get; }
    public CatalystExecutionCalibrationPolicy CalibrationPolicy { get; }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be a non-default UTC value.", parameterName);
        }
    }
}

public sealed record CatalystExecutableReturnResult(
    string SecurityId,
    string Symbol,
    ExecutablePositionDirection Direction,
    CatalystExecutableReturnStatus Status,
    CatalystExecutableReturnFailure Failure,
    string FailureStage,
    long RequestedQuantity,
    DateTimeOffset? EntryQuoteTimestampUtc,
    DateTimeOffset? ExitQuoteTimestampUtc,
    decimal? EntryPrice,
    decimal? ExitPrice,
    decimal? EffectiveEntryPrice,
    decimal? EffectiveExitPrice,
    decimal? GrossReturnPercent,
    decimal? NetReturnPercent,
    decimal? EstimatedFees,
    decimal? EstimatedMarketImpactCost,
    decimal? EntrySpreadBasisPoints,
    decimal? ExitSpreadBasisPoints)
{
    public CatalystExecutionFillStatus FillStatus { get; init; }
    public long EntryFilledQuantity { get; init; }
    public long ExitFilledQuantity { get; init; }
    public long OpenQuantityAfterExit { get; init; }
    public decimal? EstimatedSpreadCost { get; init; }
    public TimeSpan? EntryQuoteAge { get; init; }
    public TimeSpan? ExitQuoteAge { get; init; }
    public TimeSpan OrderLatency { get; init; }
    public decimal MaximumDisplayedSizeParticipation { get; init; }
    public decimal CostMultiplier { get; init; }
    public bool PromotionEligible { get; init; }
    public IReadOnlyList<string> PromotionBlockers { get; init; } = [];
    public CatalystExecutionEvidenceStatus AuctionEvidenceStatus { get; init; }
    public CatalystExecutionEvidenceStatus QueuePositionEvidenceStatus { get; init; }
}

/// <summary>
/// Converts eligible signal timestamps into conservative, net historical return estimates.
/// It uses the first quote at or after each timestamp, validates SIP/exchange/condition/size
/// evidence, crosses the spread, and applies explicit fees and market impact.
/// </summary>
public sealed class CatalystExecutableReturnAnalyzer
{
    private const string RequiredFeed = "sip";

    public CatalystExecutableReturnResult Analyze(
        CatalystExecutableReturnRequest request,
        IReadOnlyList<SipQuoteEvidenceRow> quotes)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(quotes);

        if (request.CalibrationPolicy.Mechanism !=
            CatalystExecutionMechanism.ContinuousMarketableNbbo)
        {
            return DiagnosticFailure(
                request,
                CatalystExecutableReturnStatus.Rejected,
                CatalystExecutableReturnFailure.AuctionEvidenceUnavailable,
                "entry",
                null,
                null,
                CatalystExecutionFillStatus.EvidenceInsufficient,
                0,
                0,
                ["auction_execution_evidence_unavailable"],
                CatalystExecutionEvidenceStatus.Missing);
        }

        var ordered = quotes
            .Where(quote =>
                quote.SecurityId.Equals(request.SecurityId, StringComparison.Ordinal) &&
                quote.Symbol.Equals(request.Symbol, StringComparison.Ordinal))
            .OrderBy(quote => quote.ReceivedAtUtc)
            .ThenBy(quote => quote.QuoteTimestampUtc)
            .ToArray();

        var entry = SelectAndValidate(ordered, request, request.EligibleEntryAtUtc, isEntry: true);
        if (!entry.IsValid)
        {
            return DiagnosticFailure(
                request,
                entry.Failure == CatalystExecutableReturnFailure.NoDisplayedLiquidity
                    ? CatalystExecutableReturnStatus.NoFill
                    : CatalystExecutableReturnStatus.Rejected,
                entry.Failure,
                "entry",
                entry.Quote,
                null,
                entry.Failure == CatalystExecutableReturnFailure.NoDisplayedLiquidity
                    ? CatalystExecutionFillStatus.NoFill
                    : CatalystExecutionFillStatus.EvidenceInsufficient,
                0,
                0,
                entry.PromotionBlockers,
                CatalystExecutionEvidenceStatus.NotRequired);
        }

        var exit = SelectAndValidate(
            ordered,
            request,
            request.EligibleExitAtUtc,
            isEntry: false,
            entry.FilledQuantity);
        if (!exit.IsValid)
        {
            return DiagnosticFailure(
                request,
                CatalystExecutableReturnStatus.Censored,
                exit.Failure,
                "exit",
                entry.Quote,
                exit.Quote,
                exit.Failure == CatalystExecutableReturnFailure.NoDisplayedLiquidity
                    ? CatalystExecutionFillStatus.NoFill
                    : CatalystExecutionFillStatus.EvidenceInsufficient,
                entry.FilledQuantity,
                0,
                exit.PromotionBlockers,
                CatalystExecutionEvidenceStatus.NotRequired);
        }

        var quantity = Math.Min(entry.FilledQuantity, exit.FilledQuantity);
        if (quantity <= 0)
        {
            return DiagnosticFailure(
                request,
                CatalystExecutableReturnStatus.Censored,
                CatalystExecutableReturnFailure.NoDisplayedLiquidity,
                "exit",
                entry.Quote,
                exit.Quote,
                CatalystExecutionFillStatus.NoFill,
                entry.FilledQuantity,
                0,
                ["exit_has_no_evidence_backed_fill"],
                CatalystExecutionEvidenceStatus.NotRequired);
        }

        var rawEntry = EntryPrice(request.Direction, entry.Quote!)!.Value;
        var rawExit = ExitPrice(request.Direction, exit.Quote!)!.Value;
        var entryMidpoint = Midpoint(entry.Quote!);
        var exitMidpoint = Midpoint(exit.Quote!);
        var spreadScaledEntry = ScaleSpread(
            rawEntry,
            entryMidpoint,
            request.CalibrationPolicy.CostMultiplier);
        var spreadScaledExit = ScaleSpread(
            rawExit,
            exitMidpoint,
            request.CalibrationPolicy.CostMultiplier);
        var effectiveEntry = ApplyImpact(
            spreadScaledEntry,
            rawEntry,
            request.Direction,
            isEntry: true,
            request.CostModel.EntryMarketImpactBasisPoints *
            request.CalibrationPolicy.CostMultiplier);
        var effectiveExit = ApplyImpact(
            spreadScaledExit,
            rawExit,
            request.Direction,
            isEntry: false,
            request.CostModel.ExitMarketImpactBasisPoints *
            request.CalibrationPolicy.CostMultiplier);
        var grossPnl = PositionPnl(request.Direction, rawEntry, rawExit, quantity);
        var impactedPnl = PositionPnl(request.Direction, effectiveEntry, effectiveExit, quantity);
        var fees = request.CostModel.FeeFor(quantity) *
            2m *
            request.CalibrationPolicy.CostMultiplier;
        var netPnl = impactedPnl - fees;
        var grossNotional = rawEntry * quantity;
        var effectiveNotional = effectiveEntry * quantity;
        var grossReturn = grossNotional == 0m ? 0m : grossPnl / grossNotional * 100m;
        var netReturn = effectiveNotional == 0m ? 0m : netPnl / effectiveNotional * 100m;
        var spreadCost =
            Math.Abs(spreadScaledEntry - entryMidpoint) * quantity +
            Math.Abs(spreadScaledExit - exitMidpoint) * quantity;
        var impactCost = Math.Abs(effectiveEntry - spreadScaledEntry) * quantity +
            Math.Abs(effectiveExit - spreadScaledExit) * quantity;
        var openQuantity = entry.FilledQuantity - exit.FilledQuantity;
        var hasOpenQuantity = openQuantity > 0;
        var fillStatus = hasOpenQuantity
            ? CatalystExecutionFillStatus.PartialExit
            : entry.FilledQuantity < request.RequestedQuantity
                ? CatalystExecutionFillStatus.PartialEntry
                : CatalystExecutionFillStatus.Full;
        var blockers = request.CalibrationPolicy.PromotionBlockers().ToList();
        if (hasOpenQuantity)
        {
            blockers.Add("partial_exit_leaves_open_quantity");
        }

        var status = hasOpenQuantity
            ? CatalystExecutableReturnStatus.Censored
            : CatalystExecutableReturnStatus.NetExecutableEstimate;
        var failure = hasOpenQuantity
            ? CatalystExecutableReturnFailure.PartialExitNotFullyClosed
            : CatalystExecutableReturnFailure.None;

        return new CatalystExecutableReturnResult(
            request.SecurityId,
            request.Symbol,
            request.Direction,
            status,
            failure,
            hasOpenQuantity ? "exit" : String.Empty,
            request.RequestedQuantity,
            entry.Quote!.QuoteTimestampUtc,
            exit.Quote!.QuoteTimestampUtc,
            rawEntry,
            rawExit,
            Decimal.Round(effectiveEntry, 6),
            Decimal.Round(effectiveExit, 6),
            Decimal.Round(grossReturn, 6),
            Decimal.Round(netReturn, 6),
            Decimal.Round(fees, 6),
            Decimal.Round(impactCost, 6),
            SpreadBasisPoints(entry.Quote),
            SpreadBasisPoints(exit.Quote))
        {
            FillStatus = fillStatus,
            EntryFilledQuantity = entry.FilledQuantity,
            ExitFilledQuantity = exit.FilledQuantity,
            OpenQuantityAfterExit = Math.Max(0, openQuantity),
            EstimatedSpreadCost = Decimal.Round(spreadCost, 6),
            EntryQuoteAge = entry.QuoteAge,
            ExitQuoteAge = exit.QuoteAge,
            OrderLatency = request.CalibrationPolicy.OrderLatency,
            MaximumDisplayedSizeParticipation =
                request.CalibrationPolicy.MaximumDisplayedSizeParticipation,
            CostMultiplier = request.CalibrationPolicy.CostMultiplier,
            PromotionEligible =
                status == CatalystExecutableReturnStatus.NetExecutableEstimate &&
                blockers.Count == 0,
            PromotionBlockers = blockers,
            AuctionEvidenceStatus = CatalystExecutionEvidenceStatus.NotRequired,
            QueuePositionEvidenceStatus = CatalystExecutionEvidenceStatus.NotRequired
        };
    }

    private static QuoteSelection SelectAndValidate(
        IReadOnlyList<SipQuoteEvidenceRow> quotes,
        CatalystExecutableReturnRequest request,
        DateTimeOffset eligibleAtUtc,
        bool isEntry,
        long? quantityToFill = null)
    {
        var orderArrivalUtc = eligibleAtUtc + request.CalibrationPolicy.OrderLatency;
        var quote = quotes.FirstOrDefault(candidate => candidate.ReceivedAtUtc >= orderArrivalUtc);
        if (quote is null)
        {
            return QuoteSelection.Invalid(
                null,
                CatalystExecutableReturnFailure.QuoteUnavailable,
                "executable_nbbo_quote_unavailable");
        }

        if (!quote.DataFeed.Equals(RequiredFeed, StringComparison.Ordinal))
        {
            return QuoteSelection.Invalid(
                quote,
                CatalystExecutableReturnFailure.QuoteWrongFeed,
                "executable_quote_is_not_sip_nbbo");
        }

        if (quote.QuoteTimestampUtc < request.SessionStartUtc ||
            quote.QuoteTimestampUtc >= request.SessionEndUtc)
        {
            return QuoteSelection.Invalid(
                quote,
                CatalystExecutableReturnFailure.QuoteWrongSession,
                "executable_quote_outside_requested_session");
        }

        var quoteWait = quote.ReceivedAtUtc - orderArrivalUtc;
        var quoteAge = quote.ReceivedAtUtc - quote.QuoteTimestampUtc;
        if (quoteWait > request.MaximumQuoteDelay ||
            quoteAge > request.MaximumQuoteDelay)
        {
            return QuoteSelection.Invalid(
                quote,
                CatalystExecutableReturnFailure.QuoteStale,
                "quote_staleness_or_observation_latency_exceeds_limit",
                quoteAge);
        }

        if (quote.BidPriceUnits is null or <= 0 ||
            quote.AskPriceUnits is null or <= 0)
        {
            return QuoteSelection.Invalid(
                quote,
                CatalystExecutableReturnFailure.QuoteOneSided,
                "two_sided_nbbo_price_evidence_unavailable",
                quoteAge);
        }

        if (quote.BidPriceUnits > quote.AskPriceUnits)
        {
            return QuoteSelection.Invalid(
                quote,
                CatalystExecutableReturnFailure.QuoteCrossed,
                "crossed_nbbo_is_not_executable_evidence",
                quoteAge);
        }

        if (!request.QuoteEligibilityPolicy.ConditionsAreEligible(quote.Conditions))
        {
            return QuoteSelection.Invalid(
                quote,
                CatalystExecutableReturnFailure.QuoteConditionIneligible,
                "quote_condition_ineligible",
                quoteAge);
        }

        var executableSide = ResolveExecutableSide(request.Direction, isEntry, quote);
        if (!request.QuoteEligibilityPolicy.ExchangeIsEligible(executableSide.Exchange))
        {
            return QuoteSelection.Invalid(
                quote,
                CatalystExecutableReturnFailure.QuoteExchangeIneligible,
                "executable_quote_exchange_ineligible",
                quoteAge);
        }

        if (executableSide.DisplayedSize is null)
        {
            return QuoteSelection.Invalid(
                quote,
                CatalystExecutableReturnFailure.QuoteDisplayedSizeUnavailable,
                "executable_side_displayed_size_unavailable",
                quoteAge);
        }

        var spread = SpreadBasisPoints(quote);
        if (spread > request.CalibrationPolicy.MaximumSpreadBasisPoints)
        {
            return QuoteSelection.Invalid(
                quote,
                CatalystExecutableReturnFailure.SpreadExceedsMaximum,
                "nbbo_spread_exceeds_calibrated_limit",
                quoteAge);
        }

        var requiredQuantity = quantityToFill ?? request.RequestedQuantity;
        var fillCapacity = Decimal.ToInt64(Decimal.Floor(
            executableSide.DisplayedSize.Value *
            request.CalibrationPolicy.MaximumDisplayedSizeParticipation));
        var filledQuantity = Math.Min(requiredQuantity, fillCapacity);
        if (filledQuantity <= 0)
        {
            return QuoteSelection.Invalid(
                quote,
                CatalystExecutableReturnFailure.NoDisplayedLiquidity,
                "no_evidence_backed_displayed_liquidity_available",
                quoteAge);
        }

        return QuoteSelection.Valid(quote, quoteAge, filledQuantity);
    }

    private static ExecutableQuoteSide ResolveExecutableSide(
        ExecutablePositionDirection direction,
        bool isEntry,
        SipQuoteEvidenceRow quote)
    {
        var usesAsk = direction == ExecutablePositionDirection.Long
            ? isEntry
            : !isEntry;
        return usesAsk
            ? new ExecutableQuoteSide(quote.AskExchange, quote.AskSize)
            : new ExecutableQuoteSide(quote.BidExchange, quote.BidSize);
    }

    private static CatalystExecutableReturnResult DiagnosticFailure(
        CatalystExecutableReturnRequest request,
        CatalystExecutableReturnStatus status,
        CatalystExecutableReturnFailure failure,
        string failureStage,
        SipQuoteEvidenceRow? entry,
        SipQuoteEvidenceRow? exit,
        CatalystExecutionFillStatus fillStatus,
        long entryFilledQuantity,
        long exitFilledQuantity,
        IReadOnlyList<string> stageBlockers,
        CatalystExecutionEvidenceStatus auctionEvidenceStatus)
    {
        var blockers = request.CalibrationPolicy.PromotionBlockers()
            .Concat(stageBlockers)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new CatalystExecutableReturnResult(
            request.SecurityId,
            request.Symbol,
            request.Direction,
            status,
            failure,
            failureStage,
            request.RequestedQuantity,
            entry?.QuoteTimestampUtc,
            exit?.QuoteTimestampUtc,
            entry is null ? null : EntryPrice(request.Direction, entry),
            exit is null ? null : ExitPrice(request.Direction, exit),
            null,
            null,
            null,
            null,
            null,
            null,
            entry is null ? null : SpreadBasisPointsIfValid(entry),
            exit is null ? null : SpreadBasisPointsIfValid(exit))
        {
            FillStatus = fillStatus,
            EntryFilledQuantity = entryFilledQuantity,
            ExitFilledQuantity = exitFilledQuantity,
            OpenQuantityAfterExit = Math.Max(0, entryFilledQuantity - exitFilledQuantity),
            EntryQuoteAge = entry is null ? null : entry.ReceivedAtUtc - entry.QuoteTimestampUtc,
            ExitQuoteAge = exit is null ? null : exit.ReceivedAtUtc - exit.QuoteTimestampUtc,
            OrderLatency = request.CalibrationPolicy.OrderLatency,
            MaximumDisplayedSizeParticipation =
                request.CalibrationPolicy.MaximumDisplayedSizeParticipation,
            CostMultiplier = request.CalibrationPolicy.CostMultiplier,
            PromotionEligible = false,
            PromotionBlockers = blockers,
            AuctionEvidenceStatus = auctionEvidenceStatus,
            QueuePositionEvidenceStatus =
                request.CalibrationPolicy.Mechanism ==
                CatalystExecutionMechanism.ContinuousMarketableNbbo
                    ? CatalystExecutionEvidenceStatus.NotRequired
                    : CatalystExecutionEvidenceStatus.ProhibitedInference
        };
    }

    private static decimal? EntryPrice(
        ExecutablePositionDirection direction,
        SipQuoteEvidenceRow quote)
    {
        var units = direction == ExecutablePositionDirection.Long
            ? quote.AskPriceUnits
            : quote.BidPriceUnits;
        return units is > 0 ? EvidenceFixedDecimal.FromPriceUnits(units.Value) : null;
    }

    private static decimal? ExitPrice(
        ExecutablePositionDirection direction,
        SipQuoteEvidenceRow quote)
    {
        var units = direction == ExecutablePositionDirection.Long
            ? quote.BidPriceUnits
            : quote.AskPriceUnits;
        return units is > 0 ? EvidenceFixedDecimal.FromPriceUnits(units.Value) : null;
    }

    private static decimal ApplyImpact(
        decimal price,
        decimal referencePrice,
        ExecutablePositionDirection direction,
        bool isEntry,
        decimal impactBasisPoints)
    {
        var adverseSign = direction == ExecutablePositionDirection.Long
            ? (isEntry ? 1m : -1m)
            : (isEntry ? -1m : 1m);
        return price + referencePrice * adverseSign * impactBasisPoints / 10_000m;
    }

    private static decimal Midpoint(SipQuoteEvidenceRow quote)
    {
        var bid = EvidenceFixedDecimal.FromPriceUnits(quote.BidPriceUnits!.Value);
        var ask = EvidenceFixedDecimal.FromPriceUnits(quote.AskPriceUnits!.Value);
        return (bid + ask) / 2m;
    }

    private static decimal ScaleSpread(
        decimal executablePrice,
        decimal midpoint,
        decimal costMultiplier) =>
        midpoint + (executablePrice - midpoint) * costMultiplier;

    private static decimal PositionPnl(
        ExecutablePositionDirection direction,
        decimal entry,
        decimal exit,
        long quantity) =>
        direction == ExecutablePositionDirection.Long
            ? (exit - entry) * quantity
            : (entry - exit) * quantity;

    private static decimal SpreadBasisPoints(SipQuoteEvidenceRow quote)
    {
        var bid = EvidenceFixedDecimal.FromPriceUnits(quote.BidPriceUnits!.Value);
        var ask = EvidenceFixedDecimal.FromPriceUnits(quote.AskPriceUnits!.Value);
        var midpoint = (bid + ask) / 2m;
        return Decimal.Round((ask - bid) / midpoint * 10_000m, 4);
    }

    private static decimal? SpreadBasisPointsIfValid(SipQuoteEvidenceRow quote) =>
        quote.BidPriceUnits is > 0 &&
        quote.AskPriceUnits is > 0 &&
        quote.BidPriceUnits <= quote.AskPriceUnits
            ? SpreadBasisPoints(quote)
            : null;

    private sealed record QuoteSelection(
        SipQuoteEvidenceRow? Quote,
        CatalystExecutableReturnFailure Failure,
        TimeSpan? QuoteAge,
        long FilledQuantity,
        IReadOnlyList<string> PromotionBlockers)
    {
        public bool IsValid => Quote is not null && Failure == CatalystExecutableReturnFailure.None;

        public static QuoteSelection Valid(
            SipQuoteEvidenceRow quote,
            TimeSpan quoteAge,
            long filledQuantity) =>
            new(
                quote,
                CatalystExecutableReturnFailure.None,
                quoteAge,
                filledQuantity,
                []);

        public static QuoteSelection Invalid(
            SipQuoteEvidenceRow? quote,
            CatalystExecutableReturnFailure failure,
            string promotionBlocker,
            TimeSpan? quoteAge = null) =>
            new(quote, failure, quoteAge, 0, [promotionBlocker]);
    }

    private sealed record ExecutableQuoteSide(string? Exchange, long? DisplayedSize);
}
