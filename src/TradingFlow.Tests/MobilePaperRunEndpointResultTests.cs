using Microsoft.AspNetCore.Http;
using TradingFlow.Domain.Strategies;
using TradingFlow.Web;

namespace TradingFlow.Tests;

public sealed class MobilePaperRunEndpointResultTests
{
    [Fact]
    public void ConfigurationPublicationFailure_ReturnsStableBadRequestCode()
    {
        var result = TradingApiV1Endpoints.PaperRunConfigurationInvalid();

        AssertResult(result, StatusCodes.Status400BadRequest, "paper_run_configuration_invalid");
    }

    [Theory]
    [InlineData(StrategySelectionMode.RunPaperExperiment, "no_paper_experiment_strategy")]
    [InlineData(StrategySelectionMode.RunPaperShadow, "no_paper_shadow_strategy")]
    public void UnavailableStrategy_ReturnsStableConflictCode(
        StrategySelectionMode selectionMode,
        string expectedCode)
    {
        var result = TradingApiV1Endpoints.PaperStrategyUnavailable(selectionMode);

        AssertResult(result, StatusCodes.Status409Conflict, expectedCode);
    }

    private static void AssertResult(IResult result, int expectedStatus, string expectedCode)
    {
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result);

        Assert.Equal(expectedStatus, status.StatusCode);
        Assert.Equal(expectedCode, value.Value);
    }
}
