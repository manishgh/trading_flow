using System.Text.Json;
using Moq;
using TradingFlow.Contracts.Operator;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Desk;

namespace TradingFlow.Tests;

public sealed class AccountPositionsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-21T15:00:00Z");

    /// <summary>Without any local jobs, actual holdings and broker P/L still reach both clients through the shared response.</summary>
    [Fact]
    public async Task NoActiveJobs_StillShowsEveryBrokerHolding_AndPreservesNullableFields()
    {
        var account = new Mock<IDeskAccountReader>(MockBehavior.Strict);
        account.Setup(item => item.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new DeskAccount(true, "fixture account", Now,
            [new("AAPL", 2, 100, 200, 220, 20), new("MISSING", 3, null, null, null, null)]));
        var protection = new Mock<IPositionProtectionReader>(MockBehavior.Strict);
        protection.Setup(item => item.GetAsync(It.Is<IReadOnlyCollection<DeskHolding>>(holdings => holdings.Any(item => item.Symbol == "AAPL" && item.Quantity == 2) && holdings.Any(item => item.Symbol == "MISSING" && item.Quantity == 3)),
            It.IsAny<CancellationToken>())).ReturnsAsync(new Dictionary<string, PositionProtection> {
                ["AAPL"] = new("AAPL", false, true, "No broker stop on file")
            });
        var response = await RunningTradesBuilder.ReadAccountAsync(account.Object,
            _ => Task.FromResult<IReadOnlyList<MobileRunningTrade>>([]), protection.Object, 5, "all", default);
        Assert.True(response.Available);
        Assert.Equal(2, response.SlotsUsed);
        Assert.Equal(2, response.Count);
        var apple = Assert.Single(response.Trades, item => item.Ticker == "AAPL");
        Assert.Equal(110m, apple.CurrentPrice);
        Assert.Equal(20m, apple.UnrealizedPl);
        Assert.Empty(apple.Runs);
        Assert.False(apple.CanReviewExit);
        Assert.Equal("account", apple.Source);
        var missing = Assert.Single(response.Trades, item => item.Ticker == "MISSING");
        Assert.Null(missing.EntryPrice);
        Assert.Null(missing.CurrentPrice);
        Assert.Null(missing.UnrealizedPl);
        Assert.Null(missing.UnrealizedPlPct);
        Assert.Null(response.TotalUnrealizedPl);
        Assert.Null(response.GrossExposure);
        Assert.Equal(1, response.UnprotectedCount);
        Assert.Equal(1, response.UnknownProtectionCount);
        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var android = JsonSerializer.Deserialize<AccountPositionsResponse>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(response.SlotsUsed, android.SlotsUsed);
        Assert.Null(android.Trades.Single(item => item.Ticker == "MISSING").EntryPrice);
        Assert.Contains("unavailable", android.Trades.Single(item => item.Ticker == "MISSING").PlText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A failed account read must differ from a successful empty account, and must not consult local position substitutes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedAndEmptyReads_RemainDistinct_WithoutLocalFallback(bool available)
    {
        var reader = new Mock<IDeskAccountReader>();
        reader.Setup(item => item.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new DeskAccount(available, "fixture", Now, []));
        var protection = new Mock<IPositionProtectionReader>(MockBehavior.Strict);
        var localReads = 0;
        var response = await RunningTradesBuilder.ReadAccountAsync(reader.Object, _ => {
            localReads++;
            throw new InvalidOperationException("Local data must not substitute for broker holdings.");
        }, protection.Object, 5, "all", default);
        Assert.Equal(available, response.Available);
        Assert.Empty(response.Trades);
        Assert.Equal(0, localReads);
        Assert.Equal(available ? 0 : (int?)null, response.SlotsUsed);
        Assert.Equal(available ? 0m : (decimal?)null, response.GrossExposure);
        Assert.Equal(available ? 0m : (decimal?)null, response.TotalUnrealizedPl);
        protection.VerifyNoOtherCalls();
    }

    /// <summary>Source filtering cannot reduce account exposure/counts, and local prices/protection cannot overwrite broker fields.</summary>
    [Fact]
    public void FilteredRows_KeepFullAccountTotals_AndUseLocalDataOnlyForLabels()
    {
        var account = new DeskAccount(true, "fixture", Now,
            [new("AAPL", 2, 100, 200, 220, 20), new("SHORT", -3, 50, -150, -120, 30)]);
        var metadata = new MobileRunningTrade("wishlist", "AAPL", 2, 999, 888, -777, -1, "running", "wishlist fixture",
            Guid.NewGuid(), null, "paper_job", "Actual strategy", 1, 9999, null, Now, "Local bracket intended");
        var response = RunningTradesBuilder.ProjectAccount(account, [metadata], new Dictionary<string, PositionProtection>(), 5, "wishlist");
        var displayed = Assert.Single(response.Trades);
        Assert.Equal("AAPL", displayed.Ticker);
        Assert.Equal(100m, displayed.EntryPrice);
        Assert.Equal(110m, displayed.CurrentPrice);
        Assert.Equal(20m, displayed.UnrealizedPl);
        Assert.False(displayed.ProtectionKnown);
        Assert.False(displayed.HasBrokerStop);
        Assert.Contains("not been read", displayed.ProtectionSummary);
        Assert.Equal(1m, Assert.Single(displayed.Runs).PlannedStopLossPrice);
        Assert.Equal(50m, response.TotalUnrealizedPl);
        Assert.Equal(340m, response.GrossExposure); // Absolute long and short market values.
        Assert.Equal(2, response.SlotsUsed);
        Assert.Equal(2, response.UnknownProtectionCount);
        Assert.True(displayed.CanReviewExit);
        var mismatched = displayed with { Runs = [displayed.Runs[0] with { TrackedQuantity = 1 }] };
        Assert.False(mismatched.CanReviewExit);
        Assert.Null(mismatched.JobId);
    }

    /// <summary>A missing local config/session read cannot remove successfully read account holdings.</summary>
    [Fact]
    public async Task LocalMetadataFailure_DoesNotHideActualAccountHolding()
    {
        var reader = new Mock<IDeskAccountReader>();
        reader.Setup(item => item.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new DeskAccount(true, "fixture", Now,
            [new("AAPL", 1, 10, 10, 11, 1)]));
        var protection = new Mock<IPositionProtectionReader>();
        protection.Setup(item => item.GetAsync(It.IsAny<IReadOnlyCollection<DeskHolding>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, PositionProtection>());
        var response = await RunningTradesBuilder.ReadAccountAsync(reader.Object,
            _ => throw new InvalidOperationException("old local config removed"), protection.Object, 5, "all", default);
        Assert.True(response.Available);
        Assert.Single(response.Trades);
        Assert.Contains("Local run details are unavailable", response.Detail);
        Assert.Equal(1m, response.TotalUnrealizedPl);
    }
}
