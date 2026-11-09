using TradingFlow.Cli;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Evidence.Normalization;
using TradingFlow.Domain.Research;

namespace TradingFlow.Tests;

/// <summary>
/// evidence-plan-news (9 November 2026): a saved backtest plan's stocks become a frozen Alpaca news collection request with
/// monthly windows, alphabetical stock batches, explicit provider-update-time availability and every stock mapped to one
/// security.
/// </summary>
public sealed class NewsCollectionPlanCommandTests
{
    private static readonly EvidenceSecurityIdentity[] Master =
    [
        new("sec-asts", "ASTS"), new("sec-bksy", "BKSY"), new("sec-crdo", "CRDO"), new("sec-dash", "DASH"),
        new("sec-mrvl", "MRVL"), new("sec-dup-1", "DUPE"), new("sec-dup-2", "DUPE")
    ];

    // Builds a saved plan holding the given groups of stocks; only its id and groups matter to the planner.
    private static BacktestPlanResponse Plan(params string[][] groups)
    {
        var run = new StartBacktestRequest("1.0", "snapback", Guid.NewGuid(), [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            10_000m, 1m, 25m, 4, "use_cache", "full", "key");
        var source = new UniversePreviewResponse("1.0", Guid.NewGuid(), "swing", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "sha", [], []);
        return new(91, DateTimeOffset.UnixEpoch, run,
            groups.Select((symbols, index) => new BacktestCohort($"Group {index + 1}", symbols, 10_000m)).ToArray(),
            groups.Length, 32, "", source, "config");
    }

    // 16 April to 21 October splits into 7 monthly windows; 5 stocks in batches of 2 make 3 batches, so 21 requests.
    [Fact]
    public void CreateRequest_SplitsByMonthAndBatchWithProviderUpdateTimeAvailability()
    {
        var request = NewsCollectionPlanCommand.CreateRequest(Plan(["MRVL", "ASTS"], ["DASH", "BKSY", "CRDO"]), "plan-sha", Master,
            new DateOnly(2026, 4, 16), new DateOnly(2026, 10, 21), 2, "master", "master-plan");

        Assert.Equal(EvidenceDatasetKind.NewsRevisions, request.DatasetKind);
        Assert.Equal(NewsAvailabilityEvidence.ProviderUpdatedTimestampOnly, request.NewsAvailabilityEvidence);
        Assert.Equal(21, request.Requests.Count);
        Assert.All(request.Requests, item =>
        {
            Assert.Equal("/v1beta1/news", item.Endpoint);
            Assert.Equal("50", item.Parameters["limit"]);
            Assert.InRange(item.Symbols.Count, 1, 2);
        });
        Assert.Equal(["ASTS", "BKSY"], request.Requests[0].Symbols);
        Assert.Equal(new DateTimeOffset(2026, 4, 16, 0, 0, 0, TimeSpan.Zero), request.Requests[0].RequestedStartUtc);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), request.Requests[0].RequestedEndUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero), request.Requests[^1].RequestedEndUtc);
        Assert.Equal(["sec-asts", "sec-bksy", "sec-crdo", "sec-dash", "sec-mrvl"], request.Securities.Select(item => item.SecurityId).Order());
        Assert.Equal("91", request.Attributes["saved_plan_id"]);
        Assert.Equal("false", request.Attributes["historical_availability_proven"]);
    }

    // The same inputs always give the same request identity, so a rerun collects into the same dataset.
    [Fact]
    public void CreateRequest_IsDeterministic()
    {
        var first = NewsCollectionPlanCommand.CreateRequest(Plan(["MRVL", "ASTS"]), "plan-sha", Master,
            new DateOnly(2026, 4, 16), new DateOnly(2026, 10, 21), 5, "master", "master-plan");
        var second = NewsCollectionPlanCommand.CreateRequest(Plan(["ASTS", "MRVL"]), "plan-sha", Master,
            new DateOnly(2026, 4, 16), new DateOnly(2026, 10, 21), 5, "master", "master-plan");

        Assert.Equal(first.ConfigHash, second.ConfigHash);
        Assert.Equal(first.Requests.Select(item => item.RequestId), second.Requests.Select(item => item.RequestId));
    }

    // A stock missing from the security master, or held by two securities, fails instead of collecting the wrong company.
    [Theory]
    [InlineData("SIVE")]
    [InlineData("DUPE")]
    public void CreateRequest_RejectsStocksWithoutOneSecurity(string symbol)
    {
        var error = Assert.Throws<InvalidDataException>(() => NewsCollectionPlanCommand.CreateRequest(Plan(["ASTS", symbol]), "plan-sha",
            Master, new DateOnly(2026, 4, 16), new DateOnly(2026, 10, 21), 5, "master", "master-plan"));
        Assert.Contains(symbol, error.Message, StringComparison.Ordinal);
    }
}
