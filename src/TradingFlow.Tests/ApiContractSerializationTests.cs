using System.Text.Json;
using System.Text.Json.Nodes;
using TradingFlow.Contracts.V1;

namespace TradingFlow.Tests;

public sealed class ApiContractSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void StrategyCatalog_RoundTripsExactIdentityAndEvidence()
    {
        var timestamp = DateTimeOffset.Parse("2026-09-06T14:00:00Z");
        var expected = new StrategyCatalogResponse(
            ContractVersions.VersionOne,
            "paper_shadow",
            timestamp,
            [new StrategyCatalogItemResponse(
                new StrategyReference("swing.momentum", "2.1.0", new string('9', 64)),
                "Swing Momentum V2.1",
                "swing",
                "long",
                "paper_shadow",
                false,
                ["1m", "5m"],
                new StrategyEvidenceSummaryResponse(Guid.NewGuid(), timestamp.AddDays(-1), 4.2m, 1.1m, 57m, 21, "approved", new string('8', 64))) ]);

        var json = JsonSerializer.Serialize(expected, JsonOptions);
        var actual = JsonSerializer.Deserialize<StrategyCatalogResponse>(json, JsonOptions);

        Assert.NotNull(actual);
        AssertJsonEquivalent(expected, actual);
        Assert.Contains("\"semanticVersion\":\"2.1.0\"", json, StringComparison.Ordinal);
        Assert.Contains("\"latestEvidence\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("strategyPath", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UniversePreview_RoundTripsSourceProvenanceAndExclusions()
    {
        var timestamp = DateTimeOffset.Parse("2026-09-06T14:15:00Z");
        var expected = new UniversePreviewResponse(
            ContractVersions.VersionOne,
            Guid.NewGuid(),
            "swing",
            timestamp,
            timestamp.AddMinutes(3),
            new string('7', 64),
            [new UniverseMemberResponse(
                "POET",
                "warm",
                [new UniverseSourceEvidenceResponse("wishlist", "volatile", timestamp, timestamp.AddMinutes(3), new string('6', 64), false)])],
            [new UniverseExclusionResponse("MISSING", "asset_not_tradable", "Broker reports the asset is not tradable.")]);

        var json = JsonSerializer.Serialize(expected, JsonOptions);
        var actual = JsonSerializer.Deserialize<UniversePreviewResponse>(json, JsonOptions);

        Assert.NotNull(actual);
        AssertJsonEquivalent(expected, actual);
        Assert.Contains("\"universeSnapshotId\":", json, StringComparison.Ordinal);
        Assert.Contains("\"sourceKind\":\"wishlist\"", json, StringComparison.Ordinal);
        Assert.Contains("\"exclusions\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateAudit_RoundTripsCompletePointInTimeEvidence()
    {
        var timestamp = DateTimeOffset.Parse("2026-09-06T14:30:00Z");
        var strategy = new StrategyReference("swing.momentum", "2.1.0", new string('a', 64));
        var candidate = new CandidateStateResponse(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            7,
            "RGTI",
            strategy,
            "triggered",
            "warm",
            "ema_cross_with_macd_histogram",
            timestamp.AddMinutes(-20),
            timestamp,
            timestamp.AddMinutes(10),
            timestamp.AddMinutes(-1),
            2.35m,
            58,
            []);
        var expected = new CandidateAuditResponse(
            ContractVersions.VersionOne,
            candidate,
            [new CandidateTransitionResponse(7, "armed", "triggered", timestamp, "entry_triggered", "decision_kernel", new string('b', 64))],
            [new GateEvaluationResponse(1, "completed_bar", true, null, timestamp, new Dictionary<string, string?> { ["barTimeUtc"] = timestamp.ToString("O") })],
            new MarketEvidenceResponse(timestamp, timestamp.AddMinutes(-1), "1m", "regular_market", 14.10m, 14.12m, 14.11m, 14.17m, 2.35m, 58, new string('c', 64)),
            new CatalystEvidenceResponse(Guid.NewGuid(), "alpaca", "article-42", timestamp.AddMinutes(-5), timestamp.AddMinutes(-4), "earnings", "positive", 0.82m, "Guidance raised", "https://example.test/news/42", new string('d', 64)),
            new ExecutionEvidenceResponse(Guid.NewGuid(), "client-order-1", "broker-order-1", "acknowledged", 100m, 0m, null, timestamp, null),
            new string('e', 64),
            timestamp);

        var json = JsonSerializer.Serialize(expected, JsonOptions);
        var actual = JsonSerializer.Deserialize<CandidateAuditResponse>(json, JsonOptions);

        Assert.NotNull(actual);
        AssertJsonEquivalent(expected, actual);
        Assert.Contains("\"providerPublishedAtUtc\":", json, StringComparison.Ordinal);
        Assert.Contains("\"firstReceivedAtUtc\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RunJob_RoundTripsImmutableProvenanceWithoutServerPaths()
    {
        var timestamp = DateTimeOffset.Parse("2026-09-06T15:00:00Z");
        var strategy = new StrategyReference("swing.momentum", "1.4.0", new string('f', 64));
        var expected = new RunJobResponse(
            ContractVersions.VersionOne,
            Guid.NewGuid(),
            "backtest",
            "swing-six-months",
            "running",
            new RunProvenance("backtest", Guid.NewGuid(), Guid.NewGuid(), [strategy]),
            timestamp,
            timestamp.AddSeconds(1),
            null,
            null,
            "evaluating_candidates",
            12,
            100,
            81,
            null,
            null);

        var json = JsonSerializer.Serialize(expected, JsonOptions);
        var actual = JsonSerializer.Deserialize<RunJobResponse>(json, JsonOptions);

        Assert.NotNull(actual);
        AssertJsonEquivalent(expected, actual);
        Assert.Contains("\"universeSnapshotId\":", json, StringComparison.Ordinal);
        Assert.Contains("\"decisionRunId\":", json, StringComparison.Ordinal);
        Assert.Contains("\"contentSha256\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("configPath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("strategyPath", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EventEnvelope_RoundTripsResumeSequenceAndTypedPayload()
    {
        var timestamp = DateTimeOffset.Parse("2026-09-06T15:05:00Z");
        var jobId = Guid.NewGuid();
        var expected = new EventStreamEnvelope<JobProgressEvent>(
            ContractVersions.VersionOne,
            $"jobs/{jobId:N}",
            438,
            "job.progress",
            timestamp,
            new JobProgressEvent(new JobProgressResponse(jobId, "running", "market_pipeline", 44, 100, timestamp)));

        var json = JsonSerializer.Serialize(expected, JsonOptions);
        var actual = JsonSerializer.Deserialize<EventStreamEnvelope<JobProgressEvent>>(json, JsonOptions);

        Assert.NotNull(actual);
        Assert.Equal(438, actual.EventSequence);
        Assert.Equal("job.progress", actual.EventType);
        Assert.Equal(expected.Payload.Job, actual.Payload.Job);
        Assert.Contains("\"eventSequence\":438", json, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderPreview_RoundTripsEveryStaleStateBinding()
    {
        var timestamp = DateTimeOffset.Parse("2026-09-06T15:10:00Z");
        var expected = new OrderPreviewResponse(
            ContractVersions.VersionOne,
            Guid.NewGuid(),
            "protected-preview-token",
            timestamp,
            timestamp.AddMinutes(2),
            "paper-primary",
            Guid.NewGuid(),
            4,
            Guid.NewGuid(),
            new StrategyReference("swing.momentum", "2.1.0", new string('1', 64)),
            "POET",
            "buy",
            250m,
            "limit",
            "day",
            12.34m,
            12.10m,
            13.06m,
            3085m,
            60m,
            "sip:POET:20260906T151000Z",
            timestamp,
            "risk-snapshot:19",
            "strategy_validated",
            "order-attempt-123",
            []);

        var json = JsonSerializer.Serialize(expected, JsonOptions);
        var actual = JsonSerializer.Deserialize<OrderPreviewResponse>(json, JsonOptions);

        Assert.NotNull(actual);
        AssertJsonEquivalent(expected, actual);
        Assert.Contains("\"candidateVersion\":4", json, StringComparison.Ordinal);
        Assert.Contains("\"quoteIdentity\":", json, StringComparison.Ordinal);
        Assert.Contains("\"riskSnapshotVersion\":", json, StringComparison.Ordinal);
        Assert.Contains("\"idempotencyKey\":", json, StringComparison.Ordinal);
    }

    private static void AssertJsonEquivalent<T>(T expected, T actual)
    {
        var expectedNode = JsonSerializer.SerializeToNode(expected, JsonOptions);
        var actualNode = JsonSerializer.SerializeToNode(actual, JsonOptions);

        Assert.True(JsonNode.DeepEquals(expectedNode, actualNode));
    }
}
