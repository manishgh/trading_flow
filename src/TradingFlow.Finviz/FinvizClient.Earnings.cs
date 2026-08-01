using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingFlow.Domain.Earnings;

namespace TradingFlow.Finviz;

public sealed partial class FinvizClient
{
    private const int MaximumEarningsCalendarPages = 200;

    /// <summary>
    /// Reads every Finviz earnings-calendar page for the requested exchange-date window.
    /// Each response is committed to the raw archive before its embedded JSON is parsed.
    /// </summary>
    public async Task<IReadOnlyList<EarningsCalendarEvent>> GetEarningsCalendarAsync(
        DateOnly fromExchangeDate,
        DateOnly toExchangeDate,
        CancellationToken cancellationToken = default)
    {
        if (toExchangeDate < fromExchangeDate)
        {
            throw new ArgumentOutOfRangeException(nameof(toExchangeDate), "End date cannot precede start date.");
        }

        var results = new List<EarningsCalendarEvent>();
        // The JSON endpoint is date-scoped even though Finviz's browser calendar is week-oriented.
        // Query every exchange date so a today/tomorrow request cannot silently skip tomorrow.
        for (var queryDate = fromExchangeDate; queryDate <= toExchangeDate; queryDate = queryDate.AddDays(1))
        {
            var page = 1;
            do
            {
                var safeSourceUrl = $"/api/calendar/earnings?dateFrom={queryDate:yyyy-MM-dd}&page={page}";
                var archived = await GetArchivedTextAsync(
                    $"{safeSourceUrl}&auth={_options.AuthToken}",
                    "earnings_calendar_json",
                    "json",
                    $"earnings-{queryDate:yyyyMMdd}-page-{page}",
                    cancellationToken);
                var parsed = ParseEarningsCalendarJson(
                    archived.Text,
                    archived.Receipt.Manifest.ReceivedAtUtc,
                    archived.Receipt.Manifest.Sha256,
                    new Uri(_options.BaseUrl, safeSourceUrl).ToString());
                results.AddRange(parsed.Items.Where(item =>
                    item.ReportDateExchange >= fromExchangeDate &&
                    item.ReportDateExchange <= toExchangeDate));

                if (parsed.Page != page)
                {
                    throw new InvalidDataException($"Finviz returned earnings page {parsed.Page} while page {page} was requested.");
                }

                if (parsed.TotalPages == 0)
                {
                    if (parsed.TotalItems != 0 || parsed.Items.Count != 0)
                    {
                        throw new InvalidDataException("Finviz returned a zero earnings page count with non-empty results.");
                    }

                    break;
                }

                if (parsed.TotalPages is < 0 or > MaximumEarningsCalendarPages)
                {
                    throw new InvalidDataException($"Finviz returned invalid earnings page count {parsed.TotalPages}.");
                }

                if (page >= parsed.TotalPages)
                {
                    break;
                }

                page++;
            }
            while (page <= MaximumEarningsCalendarPages);
        }

        return results
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.ProviderReceivedAtUtc).First())
            .OrderBy(item => item.ScheduledAtUtc)
            .ThenBy(item => item.Ticker, StringComparer.Ordinal)
            .ToArray();
    }

    public static FinvizEarningsCalendarPage ParseEarningsCalendarJson(
        string jsonText,
        DateTimeOffset receivedAtUtc,
        string sourceArtifactSha256,
        string sourceUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonText);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceArtifactSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceUrl);

        using var json = JsonDocument.Parse(jsonText);
        var entries = json.RootElement;
        var page = entries.GetProperty("page").GetInt32();
        var totalPages = entries.GetProperty("totalPages").GetInt32();
        var totalItems = entries.GetProperty("totalItemsCount").GetInt32();
        var items = new List<EarningsCalendarEvent>();

        foreach (var element in entries.GetProperty("items").EnumerateArray())
        {
            var ticker = GetRequiredString(element, "ticker").Trim().ToUpperInvariant();
            var company = GetRequiredString(element, "company").Trim();
            var providerTimestamp = GetRequiredString(element, "earningsDate");
            if (!DateTime.TryParseExact(
                    providerTimestamp,
                    "yyyy-MM-dd'T'HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var exchangeLocal))
            {
                throw new InvalidDataException($"Finviz returned invalid earningsDate '{providerTimestamp}' for {ticker}.");
            }

            exchangeLocal = DateTime.SpecifyKind(exchangeLocal, DateTimeKind.Unspecified);
            var scheduledAtUtc = new DateTimeOffset(
                TimeZoneInfo.ConvertTimeToUtc(exchangeLocal, ResolveMarketTimeZone()),
                TimeSpan.Zero);
            var reportDate = DateOnly.FromDateTime(exchangeLocal);
            var releaseWindow = ResolveReleaseWindow(TimeOnly.FromDateTime(exchangeLocal));
            var id = BuildEarningsEventId(ticker, reportDate);
            var epsActual = GetNullableDecimal(element, "epsActual");
            var epsSurprise = GetNullableDecimal(element, "epsSurprise");
            var reportedEpsActual = GetNullableDecimal(element, "epsReportedActual");
            var reportedEpsSurprise = GetNullableDecimal(element, "epsReportedSurprise");
            var revenueActual = GetNullableDecimal(element, "salesActual");
            var revenueSurprise = GetNullableDecimal(element, "salesSurprise");
            var resultObserved = epsActual.HasValue || epsSurprise.HasValue ||
                reportedEpsActual.HasValue || reportedEpsSurprise.HasValue ||
                revenueActual.HasValue || revenueSurprise.HasValue;

            items.Add(new EarningsCalendarEvent
            {
                Id = id,
                Ticker = ticker,
                CompanyName = company,
                ReportDateExchange = reportDate,
                ScheduledAtUtc = scheduledAtUtc,
                ReleaseWindow = releaseWindow,
                IsScheduleEstimate = element.TryGetProperty("isEarningDateEstimate", out var estimate) && estimate.GetBoolean(),
                MarketCapMillions = GetNullableDecimal(element, "marketCap"),
                EpsEstimate = GetNullableDecimal(element, "epsEstimate"),
                EpsActual = epsActual,
                EpsSurprisePercent = epsSurprise,
                ReportedEpsEstimate = GetNullableDecimal(element, "epsReportedEstimate"),
                ReportedEpsActual = reportedEpsActual,
                ReportedEpsSurprisePercent = reportedEpsSurprise,
                RevenueEstimateMillions = GetNullableDecimal(element, "salesEstimate"),
                RevenueActualMillions = revenueActual,
                RevenueSurprisePercent = revenueSurprise,
                OneDayPriceReactionPercent = GetNullableDecimal(element, "oneDayPriceReaction"),
                Provider = "finviz",
                SourceUrl = sourceUrl,
                SourceArtifactSha256 = sourceArtifactSha256,
                ProviderReceivedAtUtc = receivedAtUtc.ToUniversalTime(),
                ResultFirstSeenAtUtc = resultObserved ? receivedAtUtc.ToUniversalTime() : null,
                FirstSeenAtUtc = receivedAtUtc.ToUniversalTime(),
                LastSeenAtUtc = receivedAtUtc.ToUniversalTime()
            });
        }

        return new FinvizEarningsCalendarPage(items, page, totalPages, totalItems);
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            String.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException($"Finviz earnings item is missing required property '{propertyName}'.");
        }

        return property.GetString()!;
    }

    private static decimal? GetNullableDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.TryGetDecimal(out var value)
            ? value
            : throw new InvalidDataException($"Finviz earnings property '{propertyName}' is not numeric.");
    }

    private static EarningsReleaseWindow ResolveReleaseWindow(TimeOnly exchangeTime)
    {
        if (exchangeTime < new TimeOnly(9, 30))
        {
            return EarningsReleaseWindow.BeforeMarketOpen;
        }

        return exchangeTime >= new TimeOnly(16, 0)
            ? EarningsReleaseWindow.AfterMarketClose
            : EarningsReleaseWindow.DuringMarket;
    }

    private static string BuildEarningsEventId(string ticker, DateOnly reportDate)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"finviz|{ticker}|{reportDate:yyyy-MM-dd}"));
        return $"finviz:{Convert.ToHexString(bytes)[..32].ToLowerInvariant()}";
    }
}
