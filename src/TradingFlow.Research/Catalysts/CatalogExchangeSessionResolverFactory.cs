using TradingFlow.Domain.Research;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Research;

namespace TradingFlow.Research.Catalysts;

/// <summary>
/// Creates an offline exchange-session resolver exclusively from a committed evidence dataset.
/// Catalog lookup and the partition reader are the only dependencies; provider clients are
/// intentionally absent.
/// </summary>
public sealed class CatalogExchangeSessionResolverFactory(
    IEvidenceCatalog catalog,
    IEvidencePartitionDataReader partitionReader)
{
    private readonly IEvidenceCatalog catalog =
        catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly IEvidencePartitionDataReader partitionReader =
        partitionReader ?? throw new ArgumentNullException(nameof(partitionReader));

    public async Task<IExchangeSessionResolver> CreateAsync(
        string datasetId,
        DateTimeOffset researchAsOfUtc,
        string exchange = "XNYS",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);
        EnsureUtc(researchAsOfUtc, nameof(researchAsOfUtc));
        ArgumentException.ThrowIfNullOrWhiteSpace(exchange);

        var manifest = await catalog.GetDatasetAsync(datasetId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Exchange-session dataset '{datasetId}' is not committed.");
        if (manifest.Kind != EvidenceDatasetKind.ExchangeSessions)
        {
            throw new InvalidDataException(
                $"Dataset '{datasetId}' has kind '{manifest.Kind}', expected '{EvidenceDatasetKind.ExchangeSessions}'.");
        }

        if (!manifest.Quality.Passed ||
            manifest.Partitions.Any(partition => !partition.Quality.Passed))
        {
            throw new InvalidDataException(
                $"Exchange-session dataset '{datasetId}' failed evidence quality checks.");
        }

        var normalizedExchange = exchange.Trim().ToUpperInvariant();
        var coverage = ExchangeCalendarEvidenceCoverage.FromManifest(manifest);
        var rows = await partitionReader.ReadExchangeSessionsAsync(
            manifest,
            cancellationToken);
        var selected = rows
            .Where(row => row.Exchange.Equals(normalizedExchange, StringComparison.Ordinal))
            .ToArray();

        if (selected.Any(row => row.AvailableAtUtc > researchAsOfUtc))
        {
            throw new InvalidDataException(
                $"Exchange-session dataset '{datasetId}' contains evidence unavailable at the research cutoff.");
        }

        return new CatalogBackedExchangeSessionResolver(
            manifest.DatasetId,
            selected,
            coverage,
            ResolveNewYorkTimeZone());
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

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be a non-default UTC value.", parameterName);
        }
    }
}

/// <summary>
/// Complete, half-open date coverage supplied by an official calendar collection.
/// Omitted dates have closure meaning only inside this validated envelope.
/// </summary>
public sealed record ExchangeCalendarEvidenceCoverage
{
    private const string CompleteDimension = "calendar_source_complete";
    private const string StartDimension = "calendar_coverage_start_date";
    private const string EndDimension = "calendar_coverage_end_date_exclusive";
    private const string SourceDimension = "calendar_source";

    public ExchangeCalendarEvidenceCoverage(
        DateOnly startDate,
        DateOnly endDateExclusive,
        string source,
        bool sourceComplete)
    {
        if (endDateExclusive <= startDate)
        {
            throw new ArgumentException(
                "Calendar coverage end must follow its start.",
                nameof(endDateExclusive));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        StartDate = startDate;
        EndDateExclusive = endDateExclusive;
        Source = source.Trim();
        SourceComplete = sourceComplete;
    }

    public DateOnly StartDate { get; }

    public DateOnly EndDateExclusive { get; }

    public string Source { get; }

    public bool SourceComplete { get; }

    public bool Contains(DateOnly date) =>
        date >= StartDate && date < EndDateExclusive;

    public static ExchangeCalendarEvidenceCoverage FromManifest(
        EvidenceDatasetManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Partitions.Count == 0)
        {
            throw new InvalidDataException(
                "Exchange-calendar evidence has no coverage partitions.");
        }

        var segments = manifest.Partitions
            .Select(ReadSegment)
            .OrderBy(segment => segment.StartDate)
            .ToArray();
        var source = segments[0].Source;
        var cursor = segments[0].EndDateExclusive;
        for (var index = 1; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (segment.StartDate != cursor)
            {
                throw new InvalidDataException(
                    "Exchange-calendar evidence coverage is incomplete, overlapping, or gapped.");
            }

            if (!segment.Source.Equals(source, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Exchange-calendar evidence mixes calendar sources.");
            }

            cursor = segment.EndDateExclusive;
        }

        return new(
            segments[0].StartDate,
            cursor,
            source,
            sourceComplete: true);
    }

    private static CoverageSegment ReadSegment(
        EvidenceDatasetPartitionManifest partition)
    {
        if (!partition.Provenance.Provider.Equals("alpaca", StringComparison.Ordinal) ||
            !partition.Provenance.Endpoint.Equals("/v2/calendar", StringComparison.OrdinalIgnoreCase) ||
            !partition.Provenance.DataFeed.Equals("alpaca-trading", StringComparison.Ordinal) ||
            !partition.Provenance.Adjustment.Equals("raw", StringComparison.Ordinal) ||
            !partition.Provenance.Currency.Equals("USD", StringComparison.Ordinal) ||
            partition.Provenance.AsOfDate is null ||
            !partition.Provenance.Symbols.SequenceEqual(["US_EQUITIES"], StringComparer.Ordinal) ||
            !partition.Provenance.Timeframe.Equals("session", StringComparison.Ordinal) ||
            partition.SourceObservations.Count != 1)
        {
            throw new InvalidDataException(
                $"Partition '{partition.PartitionId}' is not complete official Alpaca calendar evidence.");
        }

        if (!partition.Dimensions.TryGetValue(
                "authoritative_page_count",
                out var pageCountText) ||
            !Int32.TryParse(
                pageCountText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var pageCount) ||
            pageCount != 1)
        {
            throw new InvalidDataException(
                $"Partition '{partition.PartitionId}' does not prove a complete non-paginated calendar response.");
        }

        if (!partition.Dimensions.TryGetValue(CompleteDimension, out var complete) ||
            !Boolean.TryParse(complete, out var isComplete) ||
            !isComplete)
        {
            throw new InvalidDataException(
                $"Partition '{partition.PartitionId}' does not prove complete calendar-source coverage.");
        }

        if (!partition.Dimensions.TryGetValue(SourceDimension, out var source) ||
            String.IsNullOrWhiteSpace(source) ||
            !source.Equals("alpaca_official_market_calendar", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Partition '{partition.PartitionId}' has no supported official calendar source.");
        }

        var startDate = ParseDateDimension(partition, StartDimension);
        var endDateExclusive = ParseDateDimension(partition, EndDimension);
        if (endDateExclusive <= startDate)
        {
            throw new InvalidDataException(
                $"Partition '{partition.PartitionId}' has invalid calendar coverage.");
        }

        if (partition.Provenance.RequestedStartUtc.TimeOfDay != TimeSpan.Zero ||
            partition.Provenance.RequestedEndUtc.TimeOfDay != TimeSpan.Zero ||
            DateOnly.FromDateTime(partition.Provenance.RequestedStartUtc.UtcDateTime) != startDate ||
            DateOnly.FromDateTime(partition.Provenance.RequestedEndUtc.UtcDateTime) != endDateExclusive)
        {
            throw new InvalidDataException(
                $"Partition '{partition.PartitionId}' coverage does not match its frozen request range.");
        }

        return new(startDate, endDateExclusive, source);
    }

    private static DateOnly ParseDateDimension(
        EvidenceDatasetPartitionManifest partition,
        string name)
    {
        if (!partition.Dimensions.TryGetValue(name, out var text) ||
            !DateOnly.TryParseExact(
                text,
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var value))
        {
            throw new InvalidDataException(
                $"Partition '{partition.PartitionId}' has invalid or missing '{name}'.");
        }

        return value;
    }

    private sealed record CoverageSegment(
        DateOnly StartDate,
        DateOnly EndDateExclusive,
        string Source);
}

/// <summary>
/// Resolves timestamps against explicit immutable session bounds. An omitted date is closed only
/// inside a complete official-calendar coverage envelope; every other omission fails closed.
/// </summary>
public sealed class CatalogBackedExchangeSessionResolver : IExchangeSessionResolver
{
    private static readonly TimeOnly ExpectedPremarketOpen = new(4, 0);
    private static readonly TimeOnly ExpectedRegularOpen = new(9, 30);
    private static readonly TimeOnly ExpectedRegularClose = new(16, 0);
    private static readonly TimeOnly ExpectedPostmarketClose = new(20, 0);

    private readonly IReadOnlyDictionary<DateOnly, ExchangeSessionEvidenceRow> sessions;
    private readonly ExchangeCalendarEvidenceCoverage coverage;
    private readonly TimeZoneInfo exchangeTimeZone;

    public CatalogBackedExchangeSessionResolver(
        string datasetId,
        IEnumerable<ExchangeSessionEvidenceRow> sessions,
        ExchangeCalendarEvidenceCoverage coverage,
        TimeZoneInfo exchangeTimeZone)
    {
        DatasetId = Required(datasetId, nameof(datasetId));
        ArgumentNullException.ThrowIfNull(sessions);
        this.exchangeTimeZone = exchangeTimeZone
            ?? throw new ArgumentNullException(nameof(exchangeTimeZone));
        this.coverage = coverage
            ?? throw new ArgumentNullException(nameof(coverage));
        if (!coverage.SourceComplete)
        {
            throw new InvalidDataException(
                "Exchange-calendar evidence source is incomplete.");
        }

        var validated = sessions.Select(Validate).ToArray();
        if (validated
            .GroupBy(row => row.TradeDate)
            .Any(group => group.Count() != 1))
        {
            throw new InvalidDataException(
                "Exchange-session evidence contains duplicate trade dates.");
        }

        if (validated.Any(row => !coverage.Contains(row.TradeDate)))
        {
            throw new InvalidDataException(
                "Exchange-session rows fall outside committed calendar coverage.");
        }

        this.sessions = validated.ToDictionary(row => row.TradeDate);
    }

    public string DatasetId { get; }

    public ExchangeSessionResolution Resolve(DateTimeOffset timestampUtc)
    {
        EnsureUtc(timestampUtc, nameof(timestampUtc));
        var exchangeLocal = TimeZoneInfo.ConvertTime(timestampUtc, exchangeTimeZone);
        if (exchangeTimeZone.IsAmbiguousTime(exchangeLocal.DateTime) ||
            exchangeTimeZone.IsInvalidTime(exchangeLocal.DateTime))
        {
            throw new InvalidDataException(
                $"Timestamp '{timestampUtc:O}' has ambiguous or invalid New York local time.");
        }

        var tradeDate = DateOnly.FromDateTime(exchangeLocal.DateTime);
        if (!coverage.Contains(tradeDate))
        {
            throw new InvalidDataException(
                $"Trade date {tradeDate:yyyy-MM-dd} is outside committed exchange-calendar coverage " +
                $"[{coverage.StartDate:yyyy-MM-dd}, {coverage.EndDateExclusive:yyyy-MM-dd}).");
        }

        if (!sessions.TryGetValue(tradeDate, out var session))
        {
            return new(
                tradeDate,
                EquityTradingSession.Closed,
                StartOfTradeDateUtc(tradeDate),
                StartOfTradeDateUtc(tradeDate.AddDays(1)));
        }

        if (timestampUtc < session.PremarketOpenUtc)
        {
            return new(
                tradeDate,
                EquityTradingSession.Overnight,
                StartOfTradeDateUtc(tradeDate),
                session.PremarketOpenUtc);
        }

        if (timestampUtc < session.RegularOpenUtc)
        {
            return new(
                tradeDate,
                EquityTradingSession.Premarket,
                session.PremarketOpenUtc,
                session.RegularOpenUtc);
        }

        if (timestampUtc < session.RegularCloseUtc)
        {
            return new(
                tradeDate,
                EquityTradingSession.Regular,
                session.RegularOpenUtc,
                session.RegularCloseUtc);
        }

        if (timestampUtc < session.PostmarketCloseUtc)
        {
            return new(
                tradeDate,
                EquityTradingSession.AfterHours,
                session.RegularCloseUtc,
                session.PostmarketCloseUtc);
        }

        return new(
            tradeDate,
            EquityTradingSession.Overnight,
            session.PostmarketCloseUtc,
            StartOfTradeDateUtc(tradeDate.AddDays(1)));
    }

    private ExchangeSessionEvidenceRow Validate(ExchangeSessionEvidenceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.TradeDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            throw new InvalidDataException(
                $"Exchange session {row.TradeDate:yyyy-MM-dd} cannot fall on a weekend.");
        }

        var premarket = RequireLocal(row.PremarketOpenUtc, row.TradeDate, "premarket open");
        var regularOpen = RequireLocal(row.RegularOpenUtc, row.TradeDate, "regular open");
        var regularClose = RequireLocal(row.RegularCloseUtc, row.TradeDate, "regular close");
        var postmarketClose = RequireLocal(row.PostmarketCloseUtc, row.TradeDate, "postmarket close");
        if (TimeOnly.FromDateTime(premarket) != ExpectedPremarketOpen ||
            TimeOnly.FromDateTime(regularOpen) != ExpectedRegularOpen ||
            TimeOnly.FromDateTime(postmarketClose) != ExpectedPostmarketClose)
        {
            throw new InvalidDataException(
                $"Exchange session {row.TradeDate:yyyy-MM-dd} has unsupported US equity session bounds.");
        }

        var closeTime = TimeOnly.FromDateTime(regularClose);
        if ((!row.IsEarlyClose && closeTime != ExpectedRegularClose) ||
            (row.IsEarlyClose && closeTime >= ExpectedRegularClose))
        {
            throw new InvalidDataException(
                $"Exchange session {row.TradeDate:yyyy-MM-dd} has an inconsistent early-close marker.");
        }

        return row;
    }

    private DateTime RequireLocal(
        DateTimeOffset utc,
        DateOnly tradeDate,
        string boundName)
    {
        EnsureUtc(utc, boundName);
        var local = TimeZoneInfo.ConvertTime(utc, exchangeTimeZone).DateTime;
        if (exchangeTimeZone.IsAmbiguousTime(local) ||
            exchangeTimeZone.IsInvalidTime(local))
        {
            throw new InvalidDataException(
                $"Exchange session {tradeDate:yyyy-MM-dd} has an ambiguous or invalid {boundName}.");
        }

        if (DateOnly.FromDateTime(local) != tradeDate)
        {
            throw new InvalidDataException(
                $"Exchange session {boundName} does not resolve to trade date {tradeDate:yyyy-MM-dd}.");
        }

        return local;
    }

    private DateTimeOffset StartOfTradeDateUtc(DateOnly tradeDate)
    {
        var local = tradeDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        if (exchangeTimeZone.IsAmbiguousTime(local) || exchangeTimeZone.IsInvalidTime(local))
        {
            throw new InvalidDataException(
                $"Trade-date boundary {tradeDate:yyyy-MM-dd} is ambiguous or invalid.");
        }

        return new DateTimeOffset(local, exchangeTimeZone.GetUtcOffset(local))
            .ToUniversalTime();
    }

    private static string Required(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be a non-default UTC value.", parameterName);
        }
    }
}
