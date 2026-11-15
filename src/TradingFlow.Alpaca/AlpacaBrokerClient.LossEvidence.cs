using System.Globalization;
using System.Text.Json;
using TradingFlow.Domain.Execution;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Alpaca;

public sealed partial class AlpacaBrokerClient
{
    private sealed record LossHistory(
        IReadOnlyList<AccountClosingEquity> Closes, IReadOnlySet<DateOnly> FundingDates, string ArchiveId);

    private readonly SemaphoreSlim lossHistoryGate = new(1, 1);
    private (DateOnly Session, DateOnly Start, LossHistory History)? cachedLossHistory;
    private static readonly HashSet<string> OperatingCashflow = new(StringComparer.Ordinal)
    {
        "DIV", "DIVCGL", "DIVCGS", "DIVFEE", "DIVFT", "DIVNRA", "DIVROC", "DIVTW", "DIVTXEX", "DIVWH",
        "FEE", "CFEE", "INT", "INTNRA", "INTTW", "WH"
    };

    // Freeze prior-close and prior-week-close evidence from archived broker histories;
    // completed-session marks can be reused during the same trading session.
    public async Task<AccountLossEvidence> GetAccountLossEvidenceAsync(
        BrokerAccountSnapshot account, DateOnly? lastTrustedSession, CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(account.ObservedAtUtc, cancellationToken);
        if (session.Session == EquityTradingSession.Closed)
        {
            throw new InvalidDataException("No open NY exchange session is available for account loss evaluation.");
        }
        if (account.IntradayAdjustments is null or not 0m)
        {
            throw new InvalidDataException("Current broker non-trade adjustments are missing or nonzero; cash-flow attribution is unavailable.");
        }
        if (account.LastEquity is null or <= 0m)
        {
            throw new InvalidDataException("Broker prior-session closing equity is unavailable.");
        }

        var week = AccountLossLimitPolicy.WeekOf(session.TradeDate);
        var start = week.AddDays(-14);
        if (lastTrustedSession is { } previous && previous < start)
        {
            start = previous;
        }
        var history = await LoadAccountLossHistoryAsync(session.TradeDate, start, cancellationToken);
        var prior = history.Closes.LastOrDefault();
        var weekly = history.Closes.LastOrDefault(close => close.Session < week);
        if (prior is null || weekly is null || !MatchesReportedCents(account.LastEquity.Value, prior.Equity))
        {
            throw new InvalidDataException("Broker history cannot confirm the exact prior-session and prior-week closing equity.");
        }

        var fundingSince = lastTrustedSession ?? weekly.Session.AddDays(1);
        var fundingChanged = history.FundingDates.Any(date => date >= fundingSince);
        return new AccountLossEvidence(session.TradeDate, prior with { Equity = account.LastEquity.Value }, weekly, history.Closes,
            fundingChanged ? "Broker history contains a transfer or unclassified cash flow. Review the equity basis before resuming entries." : null,
            history.ArchiveId, history.FundingDates.Select(date => (DateOnly?)date).Max());
    }

    // Match each completed account mark to a known exchange session and reject
    // partial histories or an unclassified cash-flow response.
    private async Task<LossHistory> LoadAccountLossHistoryAsync(
        DateOnly session, DateOnly start, CancellationToken cancellationToken)
    {
        await lossHistoryGate.WaitAsync(cancellationToken);
        try
        {
            if (cachedLossHistory is { } cached && cached.Session == session && cached.Start <= start)
            {
                return cached.History;
            }

            var end = session.AddDays(-1);
            var calendar = await LoadLossCalendarAsync(start, end, cancellationToken);
            var startUtc = TradingLimitDay.StartUtc(start).AddHours(12);
            var endUtc = TradingLimitDay.StartUtc(session).AddTicks(-1);
            var first = Uri.EscapeDataString(startUtc.ToString("O", CultureInfo.InvariantCulture));
            var last = Uri.EscapeDataString(endUtc.ToString("O", CultureInfo.InvariantCulture));
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"/v2/account/portfolio/history?timeframe=1D&cashflow_types=ALL&start={first}&end={last}");
            var response = await _tradingClient.SendAsync(request, "broker-account-loss-history", cancellationToken: cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidDataException(AlpacaTradingRestClient.DescribeFailure("account loss history", response));
            }

            using var document = JsonDocument.Parse(response.Payload);
            var history = ParseLossHistory(document.RootElement, calendar, response.Archive.Manifest.ArchiveId);
            cachedLossHistory = (session, start, history);
            return history;
        }
        finally
        {
            lossHistoryGate.Release();
        }
    }

    // Use the broker's actual close, including half days. A date absent from an
    // authoritative range has no trading session to supply a baseline.
    private async Task<IReadOnlyDictionary<DateOnly, DateTimeOffset>> LoadLossCalendarAsync(
        DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            FormattableString.Invariant($"/v2/calendar?start={start:yyyy-MM-dd}&end={end:yyyy-MM-dd}"));
        var response = await _tradingClient.SendAsync(request, "broker-account-loss-calendar", cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidDataException(AlpacaTradingRestClient.DescribeFailure("account loss calendar", response));
        }

        using var document = JsonDocument.Parse(response.Payload);
        var zone = ResolveEasternTimeZone();
        var closes = new Dictionary<DateOnly, DateTimeOffset>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            var date = DateOnly.ParseExact(row.GetProperty("date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var close = TimeOnly.Parse(row.GetProperty("close").GetString()!, CultureInfo.InvariantCulture);
            if (date < start || date > end || !closes.TryAdd(date, ToUtc(date, close, zone)))
            {
                throw new InvalidDataException("Broker loss calendar contains a duplicate or out-of-range session.");
            }
        }
        if (closes.Count == 0)
        {
            throw new InvalidDataException("Broker loss calendar contains no completed sessions.");
        }
        return closes;
    }

    // Alpaca's daily account history labels a trading date by the exact close or
    // the following UTC midnight. Never accept a label for an unrequested session.
    private static LossHistory ParseLossHistory(
        JsonElement body, IReadOnlyDictionary<DateOnly, DateTimeOffset> calendar, string archiveId)
    {
        var timestamps = body.GetProperty("timestamp").EnumerateArray().Select(item => item.GetInt64()).ToArray();
        var equity = body.GetProperty("equity").EnumerateArray().Select(item => item.GetDecimal()).ToArray();
        if (body.GetProperty("timeframe").GetString() != "1D" || timestamps.Length != equity.Length ||
            !body.TryGetProperty("cashflow", out var cashflow) || cashflow.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Broker account history lacks complete daily marked equity and cash-flow arrays.");
        }

        var observed = new Dictionary<DateOnly, AccountClosingEquity>();
        var dates = new DateOnly[timestamps.Length];
        for (var index = 0; index < timestamps.Length; index++)
        {
            var timestamp = DateTimeOffset.FromUnixTimeSeconds(timestamps[index]);
            var date = TradingLimitDay.At(timestamp);
            dates[index] = date;
            if (!calendar.TryGetValue(date, out var close) ||
                (timestamp != close && timestamp.TimeOfDay != TimeSpan.Zero) ||
                equity[index] <= 0m || !observed.TryAdd(date, new AccountClosingEquity(date, close, equity[index])))
            {
                throw new InvalidDataException("Broker account history contains a duplicate, unexpected or unfunded equity observation.");
            }
        }
        if (observed.Count != calendar.Count)
        {
            throw new InvalidDataException("Broker account history is missing one or more completed exchange sessions.");
        }

        var fundingDates = new HashSet<DateOnly>();
        foreach (var series in cashflow.EnumerateObject())
        {
            var amounts = series.Value.EnumerateArray().Select(item => item.GetDecimal()).ToArray();
            if (amounts.Length != dates.Length)
            {
                throw new InvalidDataException("Broker cash-flow arrays do not cover the equity observations.");
            }
            if (!OperatingCashflow.Contains(series.Name))
            {
                for (var index = 0; index < amounts.Length; index++)
                {
                    if (amounts[index] != 0m)
                    {
                        fundingDates.Add(dates[index]);
                    }
                }
            }
        }

        return new LossHistory(observed.Values.OrderBy(close => close.Session).ToArray(), fundingDates, archiveId);
    }

    // Portfolio history reports cents, while /account returns more precision.
    // Permit only the two documented common midpoint choices at a half cent.
    private static bool MatchesReportedCents(decimal full, decimal reported) =>
        Decimal.Round(full, 2, MidpointRounding.ToEven) == reported ||
        Decimal.Round(full, 2, MidpointRounding.AwayFromZero) == reported;

    // Account snapshots can still support exits when a loss-specific field is absent.
    private static decimal? ReadLossDecimal(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
        {
            return null;
        }
        return decimal.TryParse(value.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ? amount : null;
    }
}
