using TradingFlow.Web.Models;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class MobilePaperRunRequestValidatorTests
{
    [Theory]
    [InlineData(null, "strategy.yaml", "paper_base_config_missing")]
    [InlineData("paper.yaml", null, "paper_strategy_missing")]
    public void MissingRequiredPath_ReturnsStableBadRequestCode(
        string? baseConfigPath,
        string? strategyPath,
        string expected)
    {
        var request = ValidRequest() with
        {
            BaseConfigPath = baseConfigPath!,
            StrategyPath = strategyPath!
        };

        Assert.False(MobilePaperRunRequestValidator.TryValidate(request, out var error));
        Assert.Equal(expected, error);
    }

    [Fact]
    public void CompleteRequest_IsAccepted()
    {
        Assert.True(MobilePaperRunRequestValidator.TryValidate(ValidRequest(), out var error));
        Assert.Empty(error);
    }

    private static MobilePaperRunRequest ValidRequest() => new(
        "paper.yaml",
        "strategy.yaml",
        "mobile-paper",
        ["MU"],
        null,
        false,
        false,
        "day",
        "limit",
        null,
        "experiment");
}
