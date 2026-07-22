using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Web.Services;

public sealed record ManualOrderDraft(
    string Ticker,
    string Side,
    decimal Quantity,
    decimal LimitPrice,
    decimal? StopLossPrice,
    decimal? TakeProfitPrice,
    string Horizon,
    bool AllowExtendedHoursTrading,
    string Feed = "sip");

public sealed record ManualOrderTicketPreview(
    bool CanSubmit,
    string? TicketToken,
    Guid TicketId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string Environment,
    string Ticker,
    string Side,
    decimal Quantity,
    decimal LimitPrice,
    decimal? StopLossPrice,
    decimal? TakeProfitPrice,
    decimal Notional,
    decimal? BidPrice,
    decimal? AskPrice,
    DateTimeOffset? QuoteTimestampUtc,
    long? QuoteAgeMilliseconds,
    decimal? SpreadBps,
    string Session,
    string TimeInForce,
    bool AllowExtendedHoursTrading,
    string Policy,
    IReadOnlyList<string> Rejections);

public sealed record ManualOrderTicketConfirmation(
    string OrderId,
    Guid TicketId,
    string Ticker,
    string Side,
    decimal Quantity,
    decimal LimitPrice);

public interface IManualOrderMarketGateway
{
    Task<AlpacaLatestQuote> GetLatestQuoteAsync(string ticker, string feed, CancellationToken cancellationToken);
    Task<ManualBrokerContext> GetBrokerContextAsync(string ticker, CancellationToken cancellationToken);
    Task<ManualOrderResult> SubmitAsync(ManualOrderDraft draft, Guid ticketId, CancellationToken cancellationToken);
}

public sealed class AlpacaManualOrderMarketGateway(
    AlpacaQuoteService quotes,
    AlpacaManualOrderService orders) : IManualOrderMarketGateway
{
    public async Task<AlpacaLatestQuote> GetLatestQuoteAsync(
        string ticker,
        string feed,
        CancellationToken cancellationToken)
    {
        var results = await quotes.GetLatestQuotesAsync([ticker], feed, cancellationToken);
        return results.GetValueOrDefault(ticker) ?? new AlpacaLatestQuote(ticker, null, null, null, null, null);
    }

    public Task<ManualBrokerContext> GetBrokerContextAsync(string ticker, CancellationToken cancellationToken) =>
        orders.GetBrokerContextAsync(ticker, cancellationToken);

    public Task<ManualOrderResult> SubmitAsync(
        ManualOrderDraft draft,
        Guid ticketId,
        CancellationToken cancellationToken) =>
        orders.SubmitLimitOrderAsync(
            draft.Ticker,
            draft.Side,
            draft.Quantity,
            draft.LimitPrice,
            draft.StopLossPrice,
            draft.TakeProfitPrice,
            draft.Horizon,
            cancellationToken,
            draft.AllowExtendedHoursTrading,
            ticketId);
}

/// <summary>
/// Owns the review/confirm boundary for manual paper orders. The protected ticket
/// is immutable and carries a stable intent identity; confirmation rechecks the
/// quote before delegating to the existing server-side order path.
/// </summary>
public sealed class ManualOrderTicketService
{
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(2);
    private readonly IManualOrderMarketGateway market;
    private readonly ManualEntryOptions manualEntry;
    private readonly EntryGateOptions entryOptions;
    private readonly IEntryAdmissionControl admission;
    private readonly TimeProvider timeProvider;
    private readonly IDataProtector protector;

    public ManualOrderTicketService(
        IManualOrderMarketGateway market,
        ManualEntryOptions manualEntry,
        EntryGateOptions entryOptions,
        IEntryAdmissionControl admission,
        TimeProvider timeProvider,
        IDataProtectionProvider dataProtection)
    {
        this.market = market;
        this.manualEntry = manualEntry;
        this.entryOptions = entryOptions;
        this.admission = admission;
        this.timeProvider = timeProvider;
        protector = dataProtection.CreateProtector("TradingFlow.ManualOrderTicket.v1");
    }

    public async Task<ManualOrderTicketPreview> PreviewAsync(
        ManualOrderDraft draft,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(draft);
        var now = timeProvider.GetUtcNow();
        var ticket = new TicketPayload(Guid.NewGuid(), now, normalized, null, null);
        return await ValidateAsync(ticket, issueToken: true, cancellationToken);
    }

    public async Task<ManualOrderTicketConfirmation> ConfirmAsync(
        string ticketToken,
        CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(ticketToken))
        {
            throw new InvalidOperationException("Order review token is required.");
        }

        TicketPayload ticket;
        try
        {
            ticket = JsonSerializer.Deserialize<TicketPayload>(protector.Unprotect(ticketToken))
                ?? throw new InvalidOperationException("Order review token is invalid.");
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            throw new InvalidOperationException("Order review token is invalid or expired.", exception);
        }

        var preview = await ValidateAsync(ticket, issueToken: false, cancellationToken);
        if (!preview.CanSubmit)
        {
            throw new InvalidOperationException($"Order requires a new review: {String.Join("; ", preview.Rejections)}");
        }

        var result = await market.SubmitAsync(ticket.Draft, ticket.TicketId, cancellationToken);
        return new ManualOrderTicketConfirmation(
            result.OrderId,
            ticket.TicketId,
            result.Ticker,
            result.Side,
            result.Quantity,
            result.LimitPrice);
    }

    private async Task<ManualOrderTicketPreview> ValidateAsync(
        TicketPayload ticket,
        bool issueToken,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var rejections = new List<string>();
        if (now - ticket.CreatedAtUtc > TicketLifetime || now < ticket.CreatedAtUtc)
        {
            rejections.Add("The order review expired.");
        }

        var admissionState = admission.GetSnapshot();
        if (!admissionState.EntriesAllowed && ticket.Draft.Side == "buy")
        {
            rejections.AddRange(admissionState.Blocks.Select(block => $"{block.Code}: {block.Detail}"));
        }
        if (ticket.Draft.Side == "buy" && manualEntry.Policy != ManualEntryPolicy.OperatorDirect)
        {
            rejections.Add("Manual buys are strategy-gated by server policy.");
        }

        var latest = await market.GetLatestQuoteAsync(ticket.Draft.Ticker, ticket.Draft.Feed, cancellationToken);
        var quoteAge = latest?.Timestamp is { } timestamp ? now - timestamp.ToUniversalTime() : (TimeSpan?)null;
        if (latest?.BidPrice is not > 0m || latest.AskPrice is not > 0m || quoteAge is null ||
            quoteAge < TimeSpan.Zero || quoteAge > entryOptions.QuoteMaxAge)
        {
            rejections.Add("A fresh two-sided SIP quote is required.");
        }

        decimal? spreadBps = latest?.BidPrice is { } bid && latest.AskPrice is { } ask && (bid + ask) > 0m
            ? (ask - bid) / ((ask + bid) / 2m) * 10_000m
            : null;
        if (spreadBps is null || spreadBps > entryOptions.MaxSpreadBps)
        {
            rejections.Add($"Spread exceeds the server limit of {entryOptions.MaxSpreadBps:0.##} bps.");
        }

        var currentSidePrice = ticket.Draft.Side == "buy" ? latest?.AskPrice : latest?.BidPrice;
        var reviewedSidePrice = ticket.Draft.Side == "buy" ? ticket.ReviewedAskPrice : ticket.ReviewedBidPrice;
        if (!issueToken && currentSidePrice is { } current && reviewedSidePrice is > 0m)
        {
            var driftBps = Math.Abs(current - reviewedSidePrice.Value) / reviewedSidePrice.Value * 10_000m;
            if (driftBps > entryOptions.MaxExpectedSlippageBps)
            {
                rejections.Add($"Quote moved {driftBps:0.##} bps since review; review the order again.");
            }
        }

        ManualBrokerContext? broker = null;
        try
        {
            broker = await market.GetBrokerContextAsync(ticket.Draft.Ticker, cancellationToken);
            ExtendedHoursOrderPolicy.Validate(
                new TradingSessionSnapshot(broker.TradeDate, broker.Session, now, null, null),
                "limit",
                "day",
                ticket.Draft.AllowExtendedHoursTrading);
            if (!broker.AssetActive || !broker.AssetTradable)
            {
                rejections.Add("Alpaca reports the asset as inactive or not tradable.");
            }
            if (ticket.Draft.Side == "buy" && broker.BuyingPower < ticket.Draft.Quantity * ticket.Draft.LimitPrice)
            {
                rejections.Add("Buying power is below the reviewed notional.");
            }
            if (ticket.Draft.Side == "sell" && broker.OpenPositionQuantity < ticket.Draft.Quantity)
            {
                rejections.Add("Sell quantity exceeds the open paper position.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            rejections.Add(exception.Message);
        }

        var reviewedTicket = issueToken
            ? ticket with { ReviewedBidPrice = latest?.BidPrice, ReviewedAskPrice = latest?.AskPrice }
            : ticket;
        var token = issueToken && rejections.Count == 0
            ? protector.Protect(JsonSerializer.Serialize(reviewedTicket))
            : null;
        return new ManualOrderTicketPreview(
            rejections.Count == 0,
            token,
            ticket.TicketId,
            ticket.CreatedAtUtc,
            ticket.CreatedAtUtc + TicketLifetime,
            "PAPER",
            ticket.Draft.Ticker,
            ticket.Draft.Side,
            ticket.Draft.Quantity,
            ticket.Draft.LimitPrice,
            ticket.Draft.StopLossPrice,
            ticket.Draft.TakeProfitPrice,
            ticket.Draft.Quantity * ticket.Draft.LimitPrice,
            latest?.BidPrice,
            latest?.AskPrice,
            latest?.Timestamp,
            quoteAge is null ? null : (long)quoteAge.Value.TotalMilliseconds,
            spreadBps,
            broker?.Session.ToString().ToLowerInvariant() ?? "unknown",
            "DAY",
            ticket.Draft.AllowExtendedHoursTrading,
            manualEntry.Policy == ManualEntryPolicy.OperatorDirect ? "operator_direct" : "strategy_gated",
            rejections);
    }

    private static ManualOrderDraft Normalize(ManualOrderDraft draft)
    {
        var ticker = (draft.Ticker ?? String.Empty).Trim().ToUpperInvariant();
        var side = (draft.Side ?? String.Empty).Trim().ToLowerInvariant();
        var horizon = (draft.Horizon ?? String.Empty).Trim().ToLowerInvariant();
        if (ticker.Length is < 1 or > 16 || ticker.Any(character => !Char.IsLetterOrDigit(character) && character is not '.' and not '-'))
        {
            throw new InvalidOperationException("Ticker is invalid.");
        }
        if (side != "buy")
        {
            throw new InvalidOperationException("Reviewed manual tickets support protected buy entries only. Close an open position from the position workflow.");
        }
        if (horizon is not ("intraday" or "swing"))
        {
            throw new InvalidOperationException("Horizon must be intraday or swing.");
        }
        if (draft.Quantity <= 0m || draft.LimitPrice <= 0m)
        {
            throw new InvalidOperationException("Quantity and limit price must be greater than zero.");
        }
        if (draft.Quantity != Decimal.Truncate(draft.Quantity) || draft.Quantity > Int32.MaxValue)
        {
            throw new InvalidOperationException("Protected buy quantity must be a positive whole-share value.");
        }
        if (side == "buy" && (draft.StopLossPrice is not > 0m || draft.StopLossPrice >= draft.LimitPrice ||
            draft.TakeProfitPrice is not > 0m || draft.TakeProfitPrice <= draft.LimitPrice))
        {
            throw new InvalidOperationException("Buy orders require a stop below entry and a target above entry.");
        }
        return draft with { Ticker = ticker, Side = side, Horizon = horizon, Feed = "sip" };
    }

    private sealed record TicketPayload(
        Guid TicketId,
        DateTimeOffset CreatedAtUtc,
        ManualOrderDraft Draft,
        decimal? ReviewedBidPrice,
        decimal? ReviewedAskPrice);
}
