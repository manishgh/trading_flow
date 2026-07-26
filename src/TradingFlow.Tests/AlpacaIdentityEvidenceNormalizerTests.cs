using System.Security.Cryptography;
using System.Text;
using TradingFlow.Data.Evidence.Normalization;
using TradingFlow.Domain.Research;

namespace TradingFlow.Tests;

public sealed class AlpacaIdentityEvidenceNormalizerTests
{
    private static readonly DateTimeOffset RangeStart = new(
        2026,
        6,
        1,
        0,
        0,
        0,
        TimeSpan.Zero);

    private static readonly DateTimeOffset RangeEnd = new(
        2026,
        7,
        1,
        0,
        0,
        0,
        TimeSpan.Zero);

    private static readonly DateTimeOffset ReceivedAt = new(
        2026,
        7,
        2,
        12,
        30,
        0,
        TimeSpan.Zero);

    public static TheoryData<string, CorporateActionEvidenceType, string> ActionSamples => new()
    {
        { "reverse_splits", CorporateActionEvidenceType.ReverseSplit, "\"symbol\":\"AAPL\",\"old_cusip\":\"037833100\",\"old_rate\":1,\"new_rate\":4" },
        { "forward_splits", CorporateActionEvidenceType.ForwardSplit, "\"symbol\":\"AAPL\",\"cusip\":\"037833100\",\"old_rate\":1,\"new_rate\":4" },
        { "unit_splits", CorporateActionEvidenceType.UnitSplit, "\"old_symbol\":\"AAPL\",\"old_cusip\":\"037833100\",\"old_rate\":1,\"new_rate\":2" },
        { "cash_dividends", CorporateActionEvidenceType.CashDividend, "\"symbol\":\"AAPL\",\"cusip\":\"037833100\",\"rate\":0.25,\"foreign\":false,\"special\":false" },
        { "stock_dividends", CorporateActionEvidenceType.StockDividend, "\"symbol\":\"AAPL\",\"cusip\":\"037833100\",\"rate\":0.1" },
        { "spin_offs", CorporateActionEvidenceType.SpinOff, "\"source_symbol\":\"AAPL\",\"source_cusip\":\"037833100\",\"new_symbol\":\"NEWC\",\"new_cusip\":\"123456789\",\"source_rate\":1,\"new_rate\":0.2" },
        { "cash_mergers", CorporateActionEvidenceType.CashMerger, "\"acquiree_symbol\":\"AAPL\",\"acquiree_cusip\":\"037833100\",\"rate\":250" },
        { "stock_mergers", CorporateActionEvidenceType.StockMerger, "\"acquiree_symbol\":\"AAPL\",\"acquiree_cusip\":\"037833100\",\"acquirer_symbol\":\"MSFT\",\"acquirer_cusip\":\"594918104\",\"acquiree_rate\":1,\"acquirer_rate\":0.5" },
        { "stock_and_cash_mergers", CorporateActionEvidenceType.StockAndCashMerger, "\"acquiree_symbol\":\"AAPL\",\"acquiree_cusip\":\"037833100\",\"acquirer_symbol\":\"MSFT\",\"acquirer_cusip\":\"594918104\",\"acquiree_rate\":1,\"acquirer_rate\":0.5,\"cash_rate\":10" },
        { "redemptions", CorporateActionEvidenceType.Redemption, "\"symbol\":\"AAPL\",\"cusip\":\"037833100\",\"rate\":100" },
        { "name_changes", CorporateActionEvidenceType.NameChange, "\"old_symbol\":\"AAPL\",\"old_cusip\":\"037833100\",\"new_symbol\":\"APPL\",\"new_cusip\":\"037833100\"" },
        { "worthless_removals", CorporateActionEvidenceType.WorthlessRemoval, "\"symbol\":\"AAPL\",\"cusip\":\"037833100\"" },
        { "rights_distributions", CorporateActionEvidenceType.RightsDistribution, "\"source_symbol\":\"AAPL\",\"source_cusip\":\"037833100\",\"new_symbol\":\"AAPL.R\",\"new_cusip\":\"123456789\",\"rate\":1" },
        { "partial_calls", CorporateActionEvidenceType.PartialCall, "\"symbol\":\"AAPL\",\"cusip\":\"037833100\",\"price\":20,\"lottery_type\":\"random\"" },
        { "reorganizations", CorporateActionEvidenceType.Reorganization, "\"symbol\":\"AAPL\",\"cusip\":\"037833100\",\"cash_rate\":2,\"stock_movements\":[]" }
    };

    [Fact]
    public void Assets_NormalizeCurrentSnapshotWithoutBackdatingOrInventedIssuer()
    {
        var payload = Bytes("""
            [
              {
                "id":"b0b6dd9d-8b9b-48a9-ba46-b9d54906e415",
                "class":"us_equity",
                "exchange":"NASDAQ",
                "symbol":"AAPL",
                "name":"Apple Inc. Common Stock",
                "status":"active",
                "tradable":true,
                "marginable":true,
                "maintenance_margin_requirement":30,
                "margin_requirement_long":30,
                "margin_requirement_short":30,
                "shortable":true,
                "easy_to_borrow":true,
                "fractionable":true,
                "borrow_status":"easy_to_borrow",
                "attributes":["has_options","overnight_tradable"]
              }
            ]
            """);
        var observation = Observation(
            payload,
            "/v2/assets",
            "alpaca-trading",
            [],
            null,
            null,
            new DateOnly(2026, 6, 30));

        var result = new AlpacaAssetEvidenceNormalizer().Normalize(
            observation,
            payload,
            schemaVersion: 1);

        var security = Assert.Single(result.Securities);
        var interval = Assert.Single(result.SymbolIntervals);
        Assert.Equal(ReceivedAt, security.ValidFromUtc);
        Assert.Equal(ReceivedAt, interval.ValidFromUtc);
        Assert.Null(security.ValidToUtc);
        Assert.Null(security.IssuerId);
        Assert.False(security.IsPointInTimeReady);
        Assert.Contains(
            IdentityEvidenceReadinessFailure.IssuerIdentityUnavailable,
            security.ReadinessFailures);
        Assert.Equal(observation.ObservationId, security.Sources[0].ObservationId);
        Assert.Equal(observation.Artifact.Content.Sha256, security.Sources[0].ObservationSha256);
        Assert.Equal(["has_options", "overnight_tradable"], security.Attributes);
    }

    [Fact]
    public void Assets_AmbiguousSymbolIsPreservedAndFailsReadiness()
    {
        var payload = Bytes("""
            [
              {
                "id":"b0b6dd9d-8b9b-48a9-ba46-b9d54906e415","class":"us_equity",
                "exchange":"NASDAQ","symbol":"TEST","name":"First","status":"active",
                "tradable":true,"marginable":true,"shortable":true,
                "easy_to_borrow":true,"fractionable":true,"attributes":[]
              },
              {
                "id":"c0b6dd9d-8b9b-48a9-ba46-b9d54906e416","class":"us_equity",
                "exchange":"NYSE","symbol":"TEST","name":"Second","status":"active",
                "tradable":true,"marginable":true,"shortable":true,
                "easy_to_borrow":true,"fractionable":true,"attributes":[]
              }
            ]
            """);

        var result = new AlpacaAssetEvidenceNormalizer().Normalize(
            Observation(payload, "/v2/assets", "alpaca-trading", [], null, null),
            payload,
            1);

        Assert.Equal(2, result.Securities.Count);
        Assert.All(
            result.Securities,
            row => Assert.Contains(
                IdentityEvidenceReadinessFailure.SymbolAmbiguousInSnapshot,
                row.ReadinessFailures));
    }

    [Theory]
    [MemberData(nameof(ActionSamples))]
    public void CorporateActions_NormalizeEveryDocumentedTypeWithReceiptAvailability(
        string bucket,
        CorporateActionEvidenceType expectedType,
        string fields)
    {
        var payload = Bytes($$"""
            {
              "corporate_actions": {
                "{{bucket}}": [{
                  "id":"1dbc7685-9517-4a77-a236-8527d49cefdc",
                  "process_date":"2026-06-15",
                  {{fields}}
                }]
              },
              "next_page_token": null
            }
            """);
        var observation = Observation(
            payload,
            "/v1/corporate-actions",
            "alpaca-market-data",
            [],
            RangeStart,
            RangeEnd);

        var row = Assert.Single(
            new AlpacaCorporateActionEvidenceNormalizer().Normalize(
                observation,
                payload,
                1));

        Assert.Equal(expectedType, row.ActionType);
        Assert.Equal(ReceivedAt, row.AvailabilityTimestampUtc);
        Assert.Null(row.SecurityId);
        Assert.Null(row.IssuerId);
        Assert.False(row.IsPointInTimeReady);
        Assert.Contains(
            IdentityEvidenceReadinessFailure.SecurityIdentityUnresolved,
            row.ReadinessFailures);
        Assert.Contains(
            IdentityEvidenceReadinessFailure.IssuerIdentityUnavailable,
            row.ReadinessFailures);
        Assert.Equal(observation.Artifact.Content.Sha256, row.Sources[0].ObservationSha256);
    }

    [Fact]
    public void CorporateActions_RejectUnknownBucketPaginationRangeAndIdentityFailures()
    {
        AssertFailure(
            """{"corporate_actions":{"unknown":[]},"next_page_token":null}""",
            EvidenceNormalizationFailureCode.InvalidField);
        AssertFailure(
            """{"corporate_actions":{},"next_page_token":42}""",
            EvidenceNormalizationFailureCode.InvalidPaginationShape);
        AssertFailure(
            """
            {"corporate_actions":{"cash_dividends":[{
              "id":"1dbc7685-9517-4a77-a236-8527d49cefdc",
              "process_date":"2026-07-15","symbol":"AAPL","cusip":"037833100","rate":0.2
            }]},"next_page_token":null}
            """,
            EvidenceNormalizationFailureCode.TimestampOutsideRequestedRange);
        AssertFailure(
            """
            {"corporate_actions":{"cash_dividends":[{
              "id":"1dbc7685-9517-4a77-a236-8527d49cefdc",
              "process_date":"2026-06-15","rate":0.2
            }]},"next_page_token":null}
            """,
            EvidenceNormalizationFailureCode.MissingRequiredField);
    }

    [Fact]
    public void IdentityNormalizers_RejectBytesThatDoNotMatchArchivedLineage()
    {
        var archived = Bytes("[]");
        var observation = Observation(
            archived,
            "/v2/assets",
            "alpaca-trading",
            [],
            null,
            null);

        var failure = Assert.Throws<EvidenceNormalizationException>(() =>
            new AlpacaAssetEvidenceNormalizer().Normalize(
                observation,
                Bytes("""[{"not":"the archived response"}]"""),
                1));

        Assert.Equal(EvidenceNormalizationFailureCode.ArtifactMismatch, failure.Code);
    }

    private static void AssertFailure(
        string json,
        EvidenceNormalizationFailureCode expected)
    {
        var payload = Bytes(json);
        var failure = Assert.Throws<EvidenceNormalizationException>(() =>
            new AlpacaCorporateActionEvidenceNormalizer().Normalize(
                Observation(
                    payload,
                    "/v1/corporate-actions",
                    "alpaca-market-data",
                    [],
                    RangeStart,
                    RangeEnd),
                payload,
                1));
        Assert.Equal(expected, failure.Code);
    }

    private static EvidenceSourceObservation Observation(
        byte[] archivedBytes,
        string endpoint,
        string feed,
        IReadOnlyList<string> symbols,
        DateTimeOffset? start,
        DateTimeOffset? end,
        DateOnly? asOfDate = null)
    {
        var attemptHash = Hash($"attempt|{endpoint}|{feed}");
        var contentHash = Convert.ToHexString(SHA256.HashData(archivedBytes)).ToLowerInvariant();
        return new EvidenceSourceObservation(
            $"observation-{contentHash[..24]}",
            $"evidence-{attemptHash[..24]}",
            Hash("plan"),
            attemptHash,
            "identity-request",
            "page-1",
            "alpaca",
            endpoint,
            EvidenceTransportKind.Http,
            200,
            symbols,
            start,
            end,
            feed,
            "raw",
            "USD",
            asOfDate ?? DateOnly.FromDateTime(ReceivedAt.UtcDateTime),
            null,
            null,
            null,
            ReceivedAt,
            new EvidenceArtifactReference(
                new EvidenceContentAddress(
                    contentHash,
                    archivedBytes.Length,
                    "application/json"),
                new EvidenceObjectNamespace("raw/alpaca")),
            "identity-normalization-run",
            Hash("config"),
            "commit-identity-normalization");
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
