using TradingFlow.Contracts.Presentation;
using TradingFlow.Contracts.V1;

namespace TradingFlow.Tests;

public sealed class MonthlyBenchmarkPresentationTests
{
    // The common formatter feeds the web and Android clients without recomputing any returns.
    [Fact]
    public void Lines_ShowTheMonthlyReturnAndItsOpeningBasis()
    {
        var benchmark = new MonthlyRunBenchmark("SPY", 5m, "price_only_regular_session",
            "prior_month_final_session_close", null, "raw");

        var lines = MonthlyBenchmarkPresentation.Lines(benchmark);

        Assert.StartsWith("Benchmark SPY +5", lines[0]);
        Assert.EndsWith("%", lines[0]);
        Assert.Contains("adjustment basis raw", lines[1]);
        Assert.Equal("Prior month's last close to this period's last close", lines[2]);
    }

    // An unavailable value retains its reason. Older results without a benchmark omit these lines.
    [Fact]
    public void Lines_ShowWhyAResultIsUnavailableAndOmitLegacyAbsence()
    {
        var benchmark = new MonthlyRunBenchmark("SPY", null, "price_only_regular_session",
            "first_evaluation_session_open", "The price adjustment basis is unverified.", "unknown");

        var lines = MonthlyBenchmarkPresentation.Lines(benchmark);

        Assert.Equal("Benchmark SPY unavailable", lines[0]);
        Assert.Contains("adjustment basis unknown", lines[1]);
        Assert.Equal(benchmark.UnavailableReason, lines[2]);
        Assert.Empty(MonthlyBenchmarkPresentation.Lines(null));
    }
}
