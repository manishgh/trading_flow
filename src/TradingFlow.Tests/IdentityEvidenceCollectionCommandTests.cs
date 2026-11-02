using TradingFlow.Cli;

namespace TradingFlow.Tests;

public sealed class IdentityEvidenceCollectionCommandTests
{
    [Fact]
    public void Plan_IsRepeatableAndCoversCorporateActionsInQuartersUpToTheAsOfDate()
    {
        var first = IdentityEvidenceCollectionCommand.CreatePlan(new DateOnly(2026, 10, 30), new DateOnly(2016, 1, 1));
        var second = IdentityEvidenceCollectionCommand.CreatePlan(new DateOnly(2026, 10, 30), new DateOnly(2016, 1, 1));

        Assert.Equal(first.JobId, second.JobId);
        Assert.Equal(first.ConfigHash, second.ConfigHash);
        var assets = Assert.Single(first.Requests, request => request.Endpoint == "/v2/assets");
        Assert.Equal("all", assets.Parameters["status"]);
        Assert.Equal("us_equity", assets.Parameters["asset_class"]);
        var actions = first.Requests.Where(request => request.Endpoint == "/v1/corporate-actions").ToArray();
        Assert.Equal(new DateTimeOffset(2016, 1, 1, 0, 0, 0, TimeSpan.Zero), actions[0].RequestedStartUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero), actions[^1].RequestedEndUtc);
        Assert.All(actions.Zip(actions.Skip(1)), pair => Assert.Equal(pair.First.RequestedEndUtc, pair.Second.RequestedStartUtc));
    }

    [Fact]
    public void Plan_RejectsCorporateActionsThatStartAfterTheAsOfDate()
    {
        Assert.Throws<ArgumentException>(() => IdentityEvidenceCollectionCommand.CreatePlan(new DateOnly(2026, 10, 30), new DateOnly(2026, 11, 1)));
    }
}
