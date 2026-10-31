using System.Globalization;
using System.Text;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Pipeline;

namespace TradingFlow.Data.Csv;

public sealed partial class CachedMarketDataProvider
{
    /// <summary>Parses the existing CSV schema once per row so ordinary and prepared consumers share identical timestamp and price interpretation.</summary>
    private static IEnumerable<OhlcvBar> ParseCsvRows(TextReader reader, string timeframe)
    {
        var header = reader.ReadLine() ?? throw new InvalidDataException("Cached market-data CSV has no schema header.");
        if (!String.Equals(header, CsvHeader, StringComparison.Ordinal))
            throw new InvalidDataException("Cached market-data CSV schema does not match the manifest schema.");
        var columns = header.Split(',');
        var index = columns.Select((name, i) => (Name: name.Trim(), Index: i))
            .ToDictionary(column => column.Name, column => column.Index, StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            if (String.IsNullOrWhiteSpace(line)) continue;
            var fields = line.Split(',');
            if (fields.Length != columns.Length)
                throw new InvalidDataException("Cached market-data row does not match the CSV schema.");
            yield return new OhlcvBar(GetString(fields, index, "ticker"),
                DateTimeOffset.Parse(GetString(fields, index, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                timeframe, GetDecimal(fields, index, "open"), GetDecimal(fields, index, "high"),
                GetDecimal(fields, index, "low"), GetDecimal(fields, index, "close"), GetDecimal(fields, index, "volume"),
                GetString(fields, index, "data_feed"), GetString(fields, index, "adjustment_policy"),
                ParseOptionalTimestamp(GetString(fields, index, "known_at_utc")),
                ParseOptionalTimestamp(GetString(fields, index, "coverage_verified_through_utc")));
        }
    }

    /// <summary>Checks the complete parent row stream while retaining only the admitted slice, or only its count during selection.</summary>
    private static (IReadOnlyList<OhlcvBar> Bars, long RowCount) ReadPreparedBarSlice(PreparedMarketDataVersion version,
        PreparedVersionManifest manifest, DateTimeOffset start, DateTimeOffset end, long maximumBars,
        bool retainBars, CancellationToken cancellationToken)
    {
        var source = manifest.Bars ?? throw new InvalidDataException("Selected version is not a candle query.");
        using var stream = new FileStream(version.DataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var admitted = new List<OhlcvBar>();
        long sourceRows = 0, admittedRows = 0;
        DateTimeOffset? first = null, last = null;
        foreach (var bar in ParseCsvRows(reader, source.Timeframe))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timestamp = bar.Timestamp.ToUniversalTime();
            if (!bar.Ticker.Equals(source.Ticker, StringComparison.OrdinalIgnoreCase) ||
                timestamp < source.RequestedStartUtc || timestamp > source.RequestedEndUtc ||
                last.HasValue && timestamp <= last.Value || !manifest.Source.MatchesBar(bar))
                throw new InvalidDataException("Pinned candle rows do not match their immutable source request.");
            if (!CanonicalMarketBarValidator.IsValid(CanonicalMarketBarValidator.Normalize(bar), requireOneMinuteSource: false, out var failure))
                throw new InvalidDataException($"Prepared candle {bar.Ticker}/{bar.Timeframe} at {bar.Timestamp:O} failed shared ingestion checks: {failure}");
            first ??= timestamp;
            last = timestamp;
            sourceRows = checked(sourceRows + 1);
            if (timestamp < start || timestamp > end) continue;
            admittedRows = checked(admittedRows + 1);
            EnforcePreparedBudget(admittedRows, maximumBars);
            if (retainBars) admitted.Add(bar);
        }
        if (sourceRows != source.BarCoverage.BarCount || first != source.BarCoverage.FirstBarUtc || last != source.BarCoverage.LastBarUtc)
            throw new InvalidDataException("Pinned candle coverage does not match its immutable source manifest.");
        return (admitted, admittedRows);
    }
}
