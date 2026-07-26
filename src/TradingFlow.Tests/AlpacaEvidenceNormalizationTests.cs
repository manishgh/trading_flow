using System.Security.Cryptography;
using System.Text;
using TradingFlow.Data.Evidence.Normalization;
using TradingFlow.Domain.Research;

namespace TradingFlow.Tests;

public sealed class AlpacaEvidenceNormalizationTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 1, 13, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddDays(1);
    private static readonly DateOnly AsOfDate = new(2026, 6, 2);
    private static readonly AlpacaEvidenceNormalizationContext SipContext =
        Context("sip", "AAPL");

    [Fact]
    public void Bars_PreserveExplicitRawAndAllAdjustmentSemantics()
    {
        var rawPayload = Bytes("""
            {
              "bars": {
                "AAPL": [
                  {"t":"2026-06-01T13:30:00Z","o":100,"h":104,"l":99,"c":102,"v":1200,"n":40,"vw":101.5}
                ]
              },
              "next_page_token": null
            }
            """);
        var adjustedPayload = Bytes("""
            {
              "bars": {
                "AAPL": [
                  {"t":"2026-06-01T13:30:00Z","o":50,"h":52,"l":49.5,"c":51,"v":2400,"n":40,"vw":50.75}
                ]
              },
              "next_page_token": null
            }
            """);
        var normalizer = new AlpacaMarketBarEvidenceNormalizer();

        var raw = Assert.Single(normalizer.Normalize(
            Observation(rawPayload, "/v2/stocks/bars", ["AAPL"], "sip", "raw"),
            rawPayload,
            SipContext,
            "raw",
            "1Min"));
        var adjusted = Assert.Single(normalizer.Normalize(
            Observation(adjustedPayload, "/v2/stocks/bars", ["AAPL"], "sip", "all"),
            adjustedPayload,
            SipContext,
            "all",
            "1Min"));

        Assert.Equal("raw", raw.Adjustment);
        Assert.Equal("all", adjusted.Adjustment);
        Assert.Equal(102m, EvidenceFixedDecimal.FromPriceUnits(raw.ClosePriceUnits));
        Assert.Equal(51m, EvidenceFixedDecimal.FromPriceUnits(adjusted.ClosePriceUnits));
        Assert.Equal(1200, raw.Volume);
        Assert.Equal(2400, adjusted.Volume);
        Assert.Equal("security-aapl", raw.SecurityId);
    }

    [Fact]
    public void Bars_MissingTimestamp_QuarantinesWholePage()
    {
        var payload = Bytes("""
            {
              "bars": {"AAPL":[{"o":100,"h":104,"l":99,"c":102,"v":1200}]},
              "next_page_token": null
            }
            """);

        var exception = Assert.Throws<EvidenceNormalizationException>(() =>
            new AlpacaMarketBarEvidenceNormalizer().Normalize(
                Observation(payload, "/v2/stocks/bars", ["AAPL"], "sip", "raw"),
                payload,
                SipContext,
                "raw",
                "1Min"));

        Assert.Equal(EvidenceNormalizationFailureCode.MissingRequiredField, exception.Code);
        Assert.True(exception.QuarantineRecommended);
    }

    [Fact]
    public void Bars_WrongSymbolAndWrongFeed_AreRejectedBeforePublication()
    {
        var wrongSymbolPayload = Bytes("""
            {
              "bars": {
                "MSFT": [
                  {"t":"2026-06-01T13:30:00Z","o":100,"h":104,"l":99,"c":102,"v":1200}
                ]
              },
              "next_page_token": null
            }
            """);
        var wrongSymbol = Assert.Throws<EvidenceNormalizationException>(() =>
            new AlpacaMarketBarEvidenceNormalizer().Normalize(
                Observation(wrongSymbolPayload, "/v2/stocks/bars", ["AAPL"], "sip", "raw"),
                wrongSymbolPayload,
                SipContext,
                "raw",
                "1Min"));

        var validPayload = ValidBarPayload();
        var wrongFeed = Assert.Throws<EvidenceNormalizationException>(() =>
            new AlpacaMarketBarEvidenceNormalizer().Normalize(
                Observation(validPayload, "/v2/stocks/bars", ["AAPL"], "iex", "raw"),
                validPayload,
                SipContext,
                "raw",
                "1Min"));

        Assert.Equal(EvidenceNormalizationFailureCode.UnexpectedSymbol, wrongSymbol.Code);
        Assert.Equal(EvidenceNormalizationFailureCode.UnexpectedFeed, wrongFeed.Code);
    }

    [Fact]
    public void Bars_MalformedJsonAndDuplicateLogicalRow_ArePageFailures()
    {
        var malformed = Bytes("""{"bars":""");
        var malformedFailure = Assert.Throws<EvidenceNormalizationException>(() =>
            new AlpacaMarketBarEvidenceNormalizer().Normalize(
                Observation(malformed, "/v2/stocks/bars", ["AAPL"], "sip", "raw"),
                malformed,
                SipContext,
                "raw",
                "1Min"));

        var duplicate = Bytes("""
            {
              "bars": {
                "AAPL": [
                  {"t":"2026-06-01T13:30:00Z","o":100,"h":104,"l":99,"c":102,"v":1200},
                  {"t":"2026-06-01T13:30:00Z","o":101,"h":105,"l":100,"c":103,"v":1300}
                ]
              },
              "next_page_token": null
            }
            """);
        var duplicateFailure = Assert.Throws<EvidenceNormalizationException>(() =>
            new AlpacaMarketBarEvidenceNormalizer().Normalize(
                Observation(duplicate, "/v2/stocks/bars", ["AAPL"], "sip", "raw"),
                duplicate,
                SipContext,
                "raw",
                "1Min"));

        Assert.Equal(EvidenceNormalizationFailureCode.MalformedPayload, malformedFailure.Code);
        Assert.Equal(EvidenceNormalizationFailureCode.DuplicateLogicalKey, duplicateFailure.Code);
    }

    [Fact]
    public void News_PreservesLaterProviderRevisionWithoutInventingReceiptAvailability()
    {
        var payload = Bytes("""
            {
              "news": [
                {
                  "id": 7001,
                  "headline": "Company files update",
                  "summary": "Initial filing.",
                  "url": "https://example.test/articles/7001",
                  "symbols": ["AAPL"],
                  "created_at": "2026-06-01T14:00:00Z",
                  "updated_at": "2026-06-01T14:00:00Z"
                },
                {
                  "id": 7001,
                  "headline": "Company files corrected update",
                  "summary": "Corrected filing.",
                  "url": "https://example.test/articles/7001",
                  "symbols": ["AAPL"],
                  "created_at": "2026-06-01T14:00:00Z",
                  "updated_at": "2026-06-01T14:05:00Z"
                }
              ],
              "next_page_token": null
            }
            """);
        var context = Context("alpaca_news", "AAPL");
        var observation = Observation(
            payload,
            "/v1beta1/news",
            ["AAPL"],
            "alpaca_news",
            "raw");

        var rows = new AlpacaNewsEvidenceNormalizer().Normalize(
            observation,
            payload,
            context,
            NewsAvailabilityEvidence.ProviderUpdatedTimestampOnly);

        Assert.Equal(2, rows.Count);
        Assert.All(
            rows,
            row => Assert.Equal(
                NewsAvailabilityEvidence.ProviderUpdatedTimestampOnly,
                row.AvailabilityEvidence));
        Assert.NotEqual(rows[0].RevisionId, rows[1].RevisionId);
        Assert.Equal(Start.AddMinutes(35), rows[1].AvailabilityTimestampUtc);

        var receiptFailure = Assert.Throws<EvidenceNormalizationException>(() =>
            new AlpacaNewsEvidenceNormalizer().Normalize(
                observation,
                payload,
                context,
                NewsAvailabilityEvidence.ObservedReceiptTime));
        Assert.Equal(
            EvidenceNormalizationFailureCode.HistoricalAvailabilityViolation,
            receiptFailure.Code);
    }

    [Fact]
    public void News_ValidatesTheSelectedAvailabilityClockAndPreservesOlderCreationMetadata()
    {
        var payload = Bytes("""
            {
              "news": [{
                "id": 7004,
                "headline": "Long-lived article receives an in-window provider update",
                "summary": "Creation metadata predates the requested revision window.",
                "url": "https://example.test/articles/7004",
                "symbols": ["AAPL"],
                "created_at": "2016-08-22T00:02:18Z",
                "updated_at": "2026-06-01T14:00:00Z"
              }],
              "next_page_token": null
            }
            """);
        var observation = Observation(
            payload,
            "/v1beta1/news",
            ["AAPL"],
            "alpaca_news",
            "raw");
        var normalizer = new AlpacaNewsEvidenceNormalizer();

        var updatedClockRow = Assert.Single(normalizer.Normalize(
            observation,
            payload,
            Context("alpaca_news", "AAPL"),
            NewsAvailabilityEvidence.ProviderUpdatedTimestampOnly));
        var createdClockFailure = Assert.Throws<EvidenceNormalizationException>(() =>
            normalizer.Normalize(
                observation,
                payload,
                Context("alpaca_news", "AAPL"),
                NewsAvailabilityEvidence.ProviderTimestampOnly));

        Assert.Equal(
            new DateTimeOffset(2016, 8, 22, 0, 2, 18, TimeSpan.Zero),
            updatedClockRow.ProviderCreatedAtUtc);
        Assert.Equal(
            new DateTimeOffset(2026, 6, 1, 14, 0, 0, TimeSpan.Zero),
            updatedClockRow.SourceTimestampUtc);
        Assert.Equal(
            EvidenceNormalizationFailureCode.TimestampOutsideRequestedRange,
            createdClockFailure.Code);
    }

    [Fact]
    public void News_MissingTimestampAndDuplicateRevision_AreRejected()
    {
        var missingTimestamp = Bytes("""
            {
              "news": [{
                "id": 1,
                "headline": "Headline",
                "url": "https://example.test/1",
                "symbols": ["AAPL"],
                "created_at": "2026-06-01T14:00:00Z"
              }],
              "next_page_token": null
            }
            """);
        var duplicate = Bytes("""
            {
              "news": [
                {
                  "id": 1,
                  "headline": "Headline",
                  "url": "https://example.test/1",
                  "symbols": ["AAPL"],
                  "created_at": "2026-06-01T14:00:00Z",
                  "updated_at": "2026-06-01T14:00:00Z"
                },
                {
                  "id": 1,
                  "headline": "Changed without provider revision",
                  "url": "https://example.test/1",
                  "symbols": ["AAPL"],
                  "created_at": "2026-06-01T14:00:00Z",
                  "updated_at": "2026-06-01T14:00:00Z"
                }
              ],
              "next_page_token": null
            }
            """);
        var context = Context("alpaca_news", "AAPL");

        var missingFailure = Assert.Throws<EvidenceNormalizationException>(() =>
            new AlpacaNewsEvidenceNormalizer().Normalize(
                Observation(
                    missingTimestamp,
                    "/v1beta1/news",
                    ["AAPL"],
                    "alpaca_news",
                    "raw"),
                missingTimestamp,
                context,
                NewsAvailabilityEvidence.ProviderTimestampOnly));
        var duplicateFailure = Assert.Throws<EvidenceNormalizationException>(() =>
            new AlpacaNewsEvidenceNormalizer().Normalize(
                Observation(duplicate, "/v1beta1/news", ["AAPL"], "alpaca_news", "raw"),
                duplicate,
                context,
                NewsAvailabilityEvidence.ProviderTimestampOnly));

        Assert.Equal(EvidenceNormalizationFailureCode.MissingRequiredField, missingFailure.Code);
        Assert.Equal(EvidenceNormalizationFailureCode.DuplicateLogicalKey, duplicateFailure.Code);
    }

    [Fact]
    public void News_ProjectsProviderTagsOntoRequestedUniverse()
    {
        var payload = Bytes("""
            {
              "news": [{
                "id": 7002,
                "headline": "Alphabet announces product update",
                "summary": "The provider tags both listed share classes.",
                "url": "https://example.test/articles/7002",
                "symbols": ["GOOGL", "GOOG"],
                "created_at": "2026-06-01T14:00:00Z",
                "updated_at": "2026-06-01T14:00:00Z"
              }],
              "next_page_token": null
            }
            """);
        var observation = Observation(
            payload,
            "/v1beta1/news",
            ["GOOGL"],
            "alpaca_news",
            "raw");

        var rows = new AlpacaNewsEvidenceNormalizer().Normalize(
            observation,
            payload,
            Context("alpaca_news", "GOOGL", "GOOG"),
            NewsAvailabilityEvidence.ProviderTimestampOnly);

        var row = Assert.Single(rows);
        Assert.Equal(["GOOGL"], row.Symbols);
    }

    [Fact]
    public void News_RejectsArticleWithoutAnyRequestedSymbol()
    {
        var payload = Bytes("""
            {
              "news": [{
                "id": 7003,
                "headline": "Unrelated company update",
                "summary": "No requested security is tagged.",
                "url": "https://example.test/articles/7003",
                "symbols": ["MSFT"],
                "created_at": "2026-06-01T14:00:00Z",
                "updated_at": "2026-06-01T14:00:00Z"
              }],
              "next_page_token": null
            }
            """);
        var observation = Observation(
            payload,
            "/v1beta1/news",
            ["AAPL"],
            "alpaca_news",
            "raw");

        var failure = Assert.Throws<EvidenceNormalizationException>(() =>
            new AlpacaNewsEvidenceNormalizer().Normalize(
                observation,
                payload,
                Context("alpaca_news", "AAPL"),
                NewsAvailabilityEvidence.ProviderTimestampOnly));

        Assert.Equal(EvidenceNormalizationFailureCode.UnexpectedSymbol, failure.Code);
    }

    [Fact]
    public void SipQuotes_PreserveCrossedAndOneSidedObservationsForQualityEvaluation()
    {
        var payload = Bytes("""
            {
              "quotes": {
                "AAPL": [
                  {
                    "t":"2026-06-01T13:30:01Z",
                    "bp":101.00,"bs":10,"bx":"Q",
                    "ap":100.00,"as":12,"ax":"P",
                    "c":["R"]
                  },
                  {
                    "t":"2026-06-01T13:30:02Z",
                    "bp":100.50,"bs":8,"bx":"Q",
                    "c":["R","T"]
                  }
                ]
              },
              "next_page_token": "opaque-next"
            }
            """);

        var rows = new AlpacaSipQuoteEvidenceNormalizer().Normalize(
            Observation(payload, "/v2/stocks/quotes", ["AAPL"], "sip", "raw"),
            payload,
            SipContext);

        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].BidPriceUnits > rows[0].AskPriceUnits);
        Assert.Equal("Q", rows[0].BidExchange);
        Assert.Equal("P", rows[0].AskExchange);
        Assert.Null(rows[1].AskPriceUnits);
        Assert.Null(rows[1].AskSize);
        Assert.Null(rows[1].AskExchange);
        Assert.Equal(["R", "T"], rows[1].Conditions);
    }

    [Fact]
    public void SipQuotes_WrongFeedDuplicateAndInvalidPagination_AreRejected()
    {
        var payload = ValidQuotePayload();
        var wrongFeed = Assert.Throws<EvidenceNormalizationException>(() =>
            new AlpacaSipQuoteEvidenceNormalizer().Normalize(
                Observation(payload, "/v2/stocks/quotes", ["AAPL"], "iex", "raw"),
                payload,
                SipContext));

        var duplicate = Bytes("""
            {
              "quotes": {
                "AAPL": [
                  {"t":"2026-06-01T13:30:01Z","bp":100,"bs":10,"bx":"Q","ap":100.1,"as":10,"ax":"P"},
                  {"t":"2026-06-01T13:30:01Z","bp":100.1,"bs":10,"bx":"Q","ap":100.2,"as":10,"ax":"P"}
                ]
              },
              "next_page_token": null
            }
            """);
        var duplicateFailure = Assert.Throws<EvidenceNormalizationException>(() =>
            new AlpacaSipQuoteEvidenceNormalizer().Normalize(
                Observation(duplicate, "/v2/stocks/quotes", ["AAPL"], "sip", "raw"),
                duplicate,
                SipContext));

        var invalidPagination = Bytes("""
            {
              "quotes": {"AAPL":[]},
              "next_page_token": 42
            }
            """);
        var paginationFailure = Assert.Throws<EvidenceNormalizationException>(() =>
            new AlpacaSipQuoteEvidenceNormalizer().Normalize(
                Observation(invalidPagination, "/v2/stocks/quotes", ["AAPL"], "sip", "raw"),
                invalidPagination,
                SipContext));

        Assert.Equal(EvidenceNormalizationFailureCode.UnexpectedFeed, wrongFeed.Code);
        Assert.Equal(EvidenceNormalizationFailureCode.DuplicateLogicalKey, duplicateFailure.Code);
        Assert.Equal(
            EvidenceNormalizationFailureCode.InvalidPaginationShape,
            paginationFailure.Code);
    }

    [Fact]
    public void ArchivedByteMismatch_IsRejectedBeforeJsonParsing()
    {
        var archived = ValidBarPayload();
        var altered = Bytes("""not json and not the archived object""");
        var observation = Observation(
            archived,
            "/v2/stocks/bars",
            ["AAPL"],
            "sip",
            "raw");

        var exception = Assert.Throws<EvidenceNormalizationException>(() =>
            new AlpacaMarketBarEvidenceNormalizer().Normalize(
                observation,
                altered,
                SipContext,
                "raw",
                "1Min"));

        Assert.Equal(EvidenceNormalizationFailureCode.ArtifactMismatch, exception.Code);
    }

    private static AlpacaEvidenceNormalizationContext Context(
        string feed,
        params string[] symbols) =>
        new(
            1,
            feed,
            "USD",
            AsOfDate,
            symbols.Select(symbol =>
                new EvidenceSecurityIdentity($"security-{symbol.ToLowerInvariant()}", symbol))
                .ToArray());

    private static EvidenceSourceObservation Observation(
        byte[] archivedBytes,
        string endpoint,
        IReadOnlyList<string> symbols,
        string feed,
        string adjustment)
    {
        var attemptHash = Hash($"attempt|{endpoint}|{feed}|{adjustment}");
        var contentHash = Convert.ToHexString(SHA256.HashData(archivedBytes)).ToLowerInvariant();
        return new EvidenceSourceObservation(
            $"observation-{contentHash[..24]}",
            $"evidence-{attemptHash[..24]}",
            Hash("plan"),
            attemptHash,
            "request-1",
            "page-1",
            "alpaca",
            endpoint,
            EvidenceTransportKind.Http,
            200,
            symbols,
            Start,
            End,
            feed,
            adjustment,
            "USD",
            AsOfDate,
            null,
            null,
            null,
            End.AddHours(1),
            new EvidenceArtifactReference(
                new EvidenceContentAddress(contentHash, archivedBytes.Length, "application/json"),
                new EvidenceObjectNamespace("raw/alpaca")),
            "normalization-run",
            Hash("config"),
            "commit-normalization");
    }

    private static byte[] ValidBarPayload() => Bytes("""
        {
          "bars": {
            "AAPL": [
              {"t":"2026-06-01T13:30:00Z","o":100,"h":104,"l":99,"c":102,"v":1200}
            ]
          },
          "next_page_token": null
        }
        """);

    private static byte[] ValidQuotePayload() => Bytes("""
        {
          "quotes": {
            "AAPL": [
              {"t":"2026-06-01T13:30:01Z","bp":100,"bs":10,"bx":"Q","ap":100.1,"as":10,"ax":"P"}
            ]
          },
          "next_page_token": null
        }
        """);

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
