using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Research;
using Xunit;

namespace TradingFlow.Tests;

public sealed class ExchangeSessionEvidenceTests
{
    private const string ConfigHash =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string SourceHash =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Codec_IsDeterministicAndRejectsWrongKind()
    {
        var codec = new EvidenceParquetCodec();
        var earlier = Session(new DateOnly(2026, 1, 12), earlyClose: false);
        var later = Session(new DateOnly(2026, 7, 13), earlyClose: false);

        var first = await codec.WriteExchangeSessionsAsync(
            [later, earlier],
            "calendar-normalizer-v1");
        var replay = await codec.WriteExchangeSessionsAsync(
            [earlier, later],
            "calendar-normalizer-v1");
        var decoded = await codec.ReadExchangeSessionsAsync(first);

        Assert.Equal(first, replay);
        Assert.Equal(EvidenceDatasetKind.ExchangeSessions, decoded.DatasetKind);
        Assert.Equal(
            [earlier.TradeDate, later.TradeDate],
            decoded.Rows.Select(row => row.TradeDate));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            codec.ReadSipQuotesAsync(first));
    }

    private static ExchangeSessionEvidenceRow Session(
        DateOnly date,
        bool earlyClose)
    {
        var zone = NewYorkTimeZone();
        return new(
            1,
            "calendar-run",
            ConfigHash,
            "calendar-code-v1",
            "alpaca-trading",
            "alpaca",
            "XNYS",
            date,
            LocalToUtc(date, new TimeOnly(4, 0), zone),
            LocalToUtc(date, new TimeOnly(9, 30), zone),
            LocalToUtc(date, earlyClose ? new TimeOnly(13, 0) : new TimeOnly(16, 0), zone),
            LocalToUtc(date, new TimeOnly(20, 0), zone),
            earlyClose,
            "alpaca_official_market_calendar",
            Utc(2026, 1, 1),
            [new EvidenceRowSourceAddress("calendar-observation", SourceHash)]);
    }

    private static DateTimeOffset LocalToUtc(
        DateOnly date,
        TimeOnly time,
        TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static TimeZoneInfo NewYorkTimeZone()
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

        throw new InvalidOperationException("New York timezone is unavailable.");
    }

    private static DateTimeOffset Utc(
        int year,
        int month,
        int day,
        int hour = 0,
        int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

}
