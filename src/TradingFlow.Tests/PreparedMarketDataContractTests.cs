using System.Text.Json;
using TradingFlow.Contracts.V1;

namespace TradingFlow.Tests;

public sealed class PreparedMarketDataContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Checks that list and detail clients receive the same complete, path-free committed evidence.</summary>
    [Fact]
    public void JobAndDetails_RoundTripCommittedPreparedCandleEvidence()
    {
        var captured = DateTimeOffset.Parse("2026-10-21T09:00:00Z");
        var prepared = new PreparedMarketDataSummaryResponse("synthetic-comparison", new string('a', 64),
            captured, captured.AddMonths(-6), captured.AddDays(-1), ["fixture"], ["fixture-feed"], ["raw", "all"],
            3, 2, "Historical candles captured now; original revision availability is unproved.");
        var job = Job() with { PreparedMarketData = prepared };
        var expected = new RunJobDetailsResponse(job, [], null, [], PreparedMarketData: prepared);
        var json = JsonSerializer.Serialize(expected, JsonOptions);
        var actual = JsonSerializer.Deserialize<RunJobDetailsResponse>(json, JsonOptions);

        Assert.NotNull(actual);
        Assert.NotNull(actual.PreparedMarketData);
        Assert.Equal(JsonSerializer.Serialize(prepared, JsonOptions), JsonSerializer.Serialize(actual.PreparedMarketData, JsonOptions));
        Assert.Equal(JsonSerializer.Serialize(prepared, JsonOptions), JsonSerializer.Serialize(actual.Job.PreparedMarketData, JsonOptions));
        using var document = JsonDocument.Parse(json);
        var evidence = document.RootElement.GetProperty("preparedMarketData");
        Assert.Equal(11, evidence.EnumerateObject().Count());
        Assert.Equal("fixture-feed", evidence.GetProperty("dataFeeds")[0].GetString());
        Assert.Equal("raw", evidence.GetProperty("priceAdjustments")[0].GetString());
        Assert.Equal("all", evidence.GetProperty("priceAdjustments")[1].GetString());
        Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Allows queued and older job payloads to omit selected-data evidence until commitment.</summary>
    [Fact]
    public void LegacyAndQueuedJobs_DeserializeWithoutPreparedEvidence()
    {
        var json = JsonSerializer.Serialize(new RunJobDetailsResponse(Job(), [], null, []), JsonOptions);
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        legacy.AsObject().Remove("preparedMarketData");
        legacy["job"]!.AsObject().Remove("preparedMarketData");
        var actual = JsonSerializer.Deserialize<RunJobDetailsResponse>(legacy.ToJsonString(), JsonOptions);

        Assert.NotNull(actual);
        Assert.Null(actual.PreparedMarketData);
        Assert.Null(actual.Job.PreparedMarketData);
        Assert.Equal("queued", actual.Job.Status);
    }

    /// <summary>Retains the worker's preparation phase and explicit failure before any snapshot was selected.</summary>
    [Fact]
    public void PreparationFailure_PreservesStageAndReasonWithoutInventingSnapshot()
    {
        var expected = Job() with { Status = "failed", CurrentStage = "preparing_candles",
            Failure = new("prepared_data_changed", "Selected candle hash changed.", false, DateTimeOffset.Parse("2026-10-21T09:01:00Z")) };
        var actual = JsonSerializer.Deserialize<RunJobResponse>(JsonSerializer.Serialize(expected, JsonOptions), JsonOptions);

        Assert.NotNull(actual);
        Assert.Equal("preparing_candles", actual.CurrentStage);
        Assert.Equal("Selected candle hash changed.", actual.Failure?.Message);
        Assert.Null(actual.PreparedMarketData);
    }

    /// <summary>Creates an in-memory synthetic queued job without touching operator data or provider services.</summary>
    private static RunJobResponse Job() => new(ContractVersions.VersionOne, Guid.NewGuid(), "backtest", "synthetic",
        "queued", null, DateTimeOffset.Parse("2026-10-21T09:00:00Z"), null, null, null, "queued", 0, 0, 0, null, null);
}
