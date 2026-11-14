using TradingFlow.Contracts.V1;

namespace TradingFlow.Contracts.Presentation;

/// <summary>Displays the monthly benchmark reported by the server, including its price basis and availability.</summary>
public static class MonthlyBenchmarkPresentation
{
    /// <summary>Each client can use these lines beside the corresponding strategy and month.</summary>
    public static IReadOnlyList<string> Lines(MonthlyRunBenchmark? benchmark)
    {
        if (benchmark is null) return [];

        var value = benchmark.ReturnPct is { } percent ? percent.ToString("+0.00;−0.00;0.00") + "%" : "unavailable";
        var basis = benchmark.OpeningBasis switch
        {
            "first_evaluation_session_open" => "First test session open to last in-window close",
            "prior_month_final_session_close" => "Prior month's last close to this period's last close",
            _ => "Opening price basis unavailable"
        };
        return [
            $"Benchmark {benchmark.Ticker} {value}",
            $"Price-only comparison, regular session · adjustment basis {benchmark.PriceAdjustmentPolicy ?? "unknown"}",
            benchmark.UnavailableReason ?? basis
        ];
    }
}
