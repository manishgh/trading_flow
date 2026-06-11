using TradingFlow.Domain.Logging;

namespace TradingFlow.Tests;

public class ApiProfilerTests
{
    [Fact]
    public void RecordRequest_InvokesConfiguredMetricSink()
    {
        var captured = new List<ApiProfiler.RequestMetric>();
        try
        {
            ApiProfiler.ConfigureMetricSink(metric =>
            {
                lock (captured)
                {
                    captured.Add(metric);
                }
            });

            ApiProfiler.RecordRequest("Alpaca", "/v2/stocks/bars", "GET", 12.5, isSuccess: false, errorMessage: "rate_limited");

            ApiProfiler.RequestMetric? metric;
            lock (captured)
            {
                metric = captured.LastOrDefault(item =>
                    item.Service == "Alpaca" &&
                    item.Endpoint == "/v2/stocks/bars" &&
                    item.Method == "GET" &&
                    !item.IsSuccess &&
                    item.ErrorMessage == "rate_limited");
            }

            Assert.NotNull(metric);
        }
        finally
        {
            ApiProfiler.ConfigureMetricSink(null);
        }
    }
}
