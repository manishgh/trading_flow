using TradingFlow.Domain.Research;

namespace TradingFlow.Tests;

public sealed class SipQuoteEvidenceContractTests
{
    [Fact]
    public void Constructor_NormalizesIdentityAndPreservesIncompleteSidesForQualityAnalysis()
    {
        var timestamp = new DateTimeOffset(2026, 7, 6, 14, 0, 0, TimeSpan.Zero);
        var row = new SipQuoteEvidenceRow(
            1,
            "run-1",
            new string('b', 64),
            "code-1",
            "security-1",
            " msft ",
            timestamp,
            EvidenceFixedDecimal.ToPriceUnits(100.10m),
            null,
            12,
            null,
            " q ",
            "",
            "SIP",
            "usd",
            ["R", "R"],
            timestamp,
            timestamp.AddSeconds(1),
            [Source()]);

        Assert.Equal("MSFT", row.Symbol);
        Assert.Equal("sip", row.DataFeed);
        Assert.Equal("USD", row.Currency);
        Assert.Equal("Q", row.BidExchange);
        Assert.Null(row.AskExchange);
        Assert.Null(row.AskPriceUnits);
        Assert.Single(row.Conditions);
        Assert.Equal(timestamp, row.SourceTimestampUtc);
    }

    [Fact]
    public void Constructor_RejectsTimestampIdentityMismatch()
    {
        var timestamp = new DateTimeOffset(2026, 7, 6, 14, 0, 0, TimeSpan.Zero);

        Assert.Throws<ArgumentException>(() => new SipQuoteEvidenceRow(
            1,
            "run-1",
            new string('b', 64),
            "code-1",
            "security-1",
            "MSFT",
            timestamp,
            100,
            101,
            1,
            1,
            "Q",
            "P",
            "sip",
            "USD",
            [],
            timestamp.AddMilliseconds(1),
            timestamp.AddSeconds(1),
            [Source()]));
    }

    private static EvidenceRowSourceAddress Source() =>
        new("observation-1", new string('a', 64));
}
