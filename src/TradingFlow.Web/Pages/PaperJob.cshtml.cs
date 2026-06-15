using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;
using TradingFlow.Engine.Configuration;
using TradingFlow.Domain.Strategies;
using TradingFlow.Domain.Audit;

namespace TradingFlow.Web.Pages;

public sealed class PaperJobModel : PageModel
{
    private readonly PaperJobService jobs;
    private readonly ProjectPaths paths;
    private readonly ConfigCatalogService catalog;
    private readonly TradingFlow.Domain.Audit.IDecisionAuditRepository auditRepo;

    public PaperJobModel(PaperJobService jobs, ProjectPaths paths, ConfigCatalogService catalog, TradingFlow.Domain.Audit.IDecisionAuditRepository auditRepo)
    {
        this.jobs = jobs;
        this.paths = paths;
        this.catalog = catalog;
        this.auditRepo = auditRepo;
    }

    public BacktestJobSnapshot? Job { get; private set; }
    public StrategyDefinition? Strategy { get; private set; }
    public List<ChartDataPoint> ChartDataList { get; private set; } = new();
    public System.Collections.Generic.IReadOnlyList<OrderDisplayViewModel> OpenOrders { get; private set; } = Array.Empty<OrderDisplayViewModel>();
    public System.Collections.Generic.IReadOnlyList<TradingFlow.Domain.Orders.BrokerPosition> OpenPositions { get; private set; } = Array.Empty<TradingFlow.Domain.Orders.BrokerPosition>();
    public TradingFlow.Domain.Logging.ProfilerSummary? LatencyProfile { get; private set; }
    public List<DecisionAuditRecord> RecentAudits { get; private set; } = new();
    public string LocalTimeZoneLabel => UiDisplayFormatter.LocalTradingTimeZoneLabel;
    public string MarketDataFeedLabel { get; private set; } = "Market data feed: unknown";

    public async Task OnGetAsync(Guid id)
    {
        Job = jobs.Get(id);
        var brokerOrders = await jobs.GetOpenOrdersAsync(id);
        OpenPositions = await jobs.GetOpenPositionsAsync(id);
        LatencyProfile = TradingFlow.Domain.Logging.ApiProfiler.GetSummary("Alpaca", Job?.RunName);

        if (Job != null)
        {
            try
            {
                var config = catalog.GetConfig(Job.ConfigPath);
                Strategy = config.Strategies.FirstOrDefault()?.Definition;
                MarketDataFeedLabel = FormatMarketDataFeedLabel(config.Config);
            }
            catch { /* Ignore if config missing */ }

            ChartDataList = LoadChartData(Job);

            try
            {
                RecentAudits = await LoadRecentAuditsAsync(Job.RunName);
            }
            catch { /* Ignore audit db errors */ }
        }

        OpenOrders = brokerOrders.Select(o =>
        {
            var currentPx = ChartDataList.FirstOrDefault(c => c.Ticker == o.Ticker)?.Close;
            return new OrderDisplayViewModel(o, currentPx);
        }).ToList();
    }

    public async Task<IActionResult> OnGetSnapshotAsync(Guid id)
    {
        var job = jobs.Get(id);
        if (job is null)
        {
            return new JsonResult(new { success = false });
        }

        var chartData = LoadChartData(job);
        var brokerOrders = await jobs.GetOpenOrdersAsync(id);
        var openOrders = brokerOrders
            .Select(order =>
            {
                var currentPrice = chartData.FirstOrDefault(point => point.Ticker.Equals(order.Ticker, StringComparison.OrdinalIgnoreCase))?.Close;
                var display = new OrderDisplayViewModel(order, currentPrice);
                return new
                {
                    ticker = order.Ticker,
                    side = order.Side,
                    orderType = order.OrderType,
                    qty = order.Qty,
                    limitPrice = order.LimitPrice,
                    currentPrice,
                    distancePct = display.DistancePct,
                    status = order.Status
                };
            })
            .ToArray();
        var openPositions = (await jobs.GetOpenPositionsAsync(id))
            .Select(position => new
            {
                ticker = position.Ticker,
                side = position.Side,
                qty = position.Qty,
                entryPrice = position.EntryPrice,
                currentPrice = position.CurrentPrice,
                unrealizedPl = position.UnrealizedPl
            })
            .ToArray();

        List<DecisionAuditRecord> audits;
        try
        {
            audits = await LoadRecentAuditsAsync(job.RunName);
        }
        catch
        {
            audits = new List<DecisionAuditRecord>();
        }

        return new JsonResult(new
        {
            success = true,
            status = job.Status,
            errorMessage = job.ErrorMessage,
            startedAt = job.StartedAt is null ? "Queued" : FormatLocal(job.StartedAt.Value),
            refreshedAt = FormatLocal(DateTimeOffset.UtcNow),
            marketDataFeed = ResolveMarketDataFeedLabel(job),
            events = job.Events.Reverse().Take(100).ToArray(),
            metrics = chartData
                .OrderBy(x => x.Ticker, StringComparer.OrdinalIgnoreCase)
                .Select(point => new
                {
                    time = FormatMetricTimestamp(point.Timestamp, point.Timeframe),
                    ticker = point.Ticker,
                    timeframe = point.Timeframe,
                    close = point.Close,
                    rsi = point.Rsi,
                    volume = point.Volume,
                    averageVolume = point.SlotAverageVolume,
                    cumulativeAverageVolume = point.CumulativeAverageVolume,
                    relativeVolume = point.RelativeVolume,
                    relativeVolumeSampleCount = point.RelativeVolumeSampleCount,
                    slotRelativeVolume = point.SlotRelativeVolume,
                    sessionRelativeVolume = point.SessionRelativeVolume,
                    vwap = point.Vwap
                })
                .ToArray(),
            openOrders,
            openPositions,
            audits = audits.Select(a => new
            {
                time = FormatLocalTime(a.Timestamp),
                ticker = a.Ticker,
                decision = a.Decision,
                reason = FormatRejectionReason(a.RejectionReason),
                rawReason = a.RejectionReason ?? "",
                accepted = a.Decision.Equals("Accepted", StringComparison.OrdinalIgnoreCase)
            }).ToArray(),
            profiler = FormatProfiler(TradingFlow.Domain.Logging.ApiProfiler.GetSummary("Alpaca", job.RunName))
        });
    }

    public IActionResult OnPostCancelRun(Guid id)
    {
        jobs.CancelJob(id);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostCancelOrders(Guid id)
    {
        await jobs.CancelAllBrokerOrdersAsync(id);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostClosePosition(Guid id, string ticker)
    {
        await jobs.ClosePositionAsync(id, ticker);
        return RedirectToPage(new { id });
    }

    public string FormatLocal(DateTimeOffset timestamp)
    {
        return UiDisplayFormatter.FormatLocal(timestamp);
    }

    public string FormatLocalTime(DateTimeOffset timestamp)
    {
        return UiDisplayFormatter.FormatLocalTime(timestamp);
    }

    public string FormatMetricTimestamp(DateTimeOffset timestamp, string timeframe)
    {
        return timeframe.EndsWith("d", StringComparison.OrdinalIgnoreCase)
            ? UiDisplayFormatter.FormatLocalDate(timestamp)
            : UiDisplayFormatter.FormatLocalTime(timestamp);
    }

    public string FormatRejectionReason(string? reason)
    {
        return UiDisplayFormatter.FormatRejectionReason(reason);
    }

    private async Task<List<DecisionAuditRecord>> LoadRecentAuditsAsync(string runName)
    {
        var allAudits = await auditRepo.GetAuditsByRunNameAsync(runName, default);
        return allAudits.OrderByDescending(a => a.Timestamp).Take(50).ToList();
    }

    private List<ChartDataPoint> LoadChartData(BacktestJobSnapshot job)
    {
        string? resultsRoot = null;
        var allowedTickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var strategyTimeframes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var config = catalog.GetConfig(job.ConfigPath);
            resultsRoot = paths.ResolveRepositoryPath(config.Config.ResultsRoot);
            foreach (var ticker in config.Config.Tickers)
            {
                allowedTickers.Add(ticker);
            }

            foreach (var strategy in config.Strategies.Select(x => x.Definition))
            {
                strategyTimeframes[strategy.StrategyName] = strategy.Timeframe;
            }
        }
        catch
        {
            // Keep the page responsive if the generated config was deleted.
        }

        var resultsDir = Path.Combine(resultsRoot ?? paths.ResolveRepositoryPath(Path.Combine("data", "paper", "results")), "live", job.RunName);

        if (!Directory.Exists(resultsDir))
        {
            return new List<ChartDataPoint>();
        }

        var points = new List<ChartDataPoint>();
        foreach (var tickerDir in Directory.GetDirectories(resultsDir))
        {
            foreach (var file in Directory.GetFiles(tickerDir, "*_chart.json"))
            {
                try
                {
                    var json = System.IO.File.ReadAllText(file);
                    var data = JsonSerializer.Deserialize<ChartDataPoint>(json);
                    if (data != null && (allowedTickers.Count == 0 || allowedTickers.Contains(data.Ticker)))
                    {
                        if (String.IsNullOrWhiteSpace(data.Timeframe) &&
                            strategyTimeframes.TryGetValue(data.StrategyName, out var timeframe))
                        {
                            data.Timeframe = timeframe;
                        }

                        points.Add(data);
                    }
                }
                catch
                {
                    // Skip partial writes while the live runner is replacing the chart file.
                }
            }
        }

        return points;
    }

    private string ResolveMarketDataFeedLabel(BacktestJobSnapshot job)
    {
        try
        {
            var config = catalog.GetConfig(job.ConfigPath);
            return FormatMarketDataFeedLabel(config.Config);
        }
        catch
        {
            return "Market data feed: unknown";
        }
    }

    private static string FormatMarketDataFeedLabel(TradingFlow.Domain.Backtesting.BacktestRunConfig config)
    {
        return config.Provider.Equals("alpaca", StringComparison.OrdinalIgnoreCase)
            ? $"Market data feed: Alpaca {config.Providers.Alpaca.DataFeed.ToUpperInvariant()}"
            : $"Market data feed: {config.Provider.ToUpperInvariant()}";
    }

    private object FormatProfiler(TradingFlow.Domain.Logging.ProfilerSummary profile)
    {
        return new
        {
            name = profile.Name,
            appStartTime = FormatLocal(profile.AppStartTime),
            totalRequests = profile.TotalRequests,
            avgDurationMs = profile.AvgDurationMs,
            minDurationMs = profile.MinDurationMs,
            maxDurationMs = profile.MaxDurationMs,
            successRate = profile.SuccessRate,
            endpoints = profile.Endpoints.Select(endpoint => new
            {
                route = endpoint.Route,
                count = endpoint.Count,
                avgDurationMs = endpoint.AvgDurationMs,
                minDurationMs = endpoint.MinDurationMs,
                maxDurationMs = endpoint.MaxDurationMs,
                successRate = endpoint.SuccessRate
            }).ToArray()
        };
    }
}

public class ChartDataPoint
{
    public DateTimeOffset Timestamp { get; set; }
    public string Ticker { get; set; } = String.Empty;
    public string StrategyName { get; set; } = String.Empty;
    public string Timeframe { get; set; } = String.Empty;
    public decimal Close { get; set; }
    public decimal Atr { get; set; }
    public decimal RelativeVolume { get; set; }
    public decimal SlotRelativeVolume { get; set; }
    public decimal SessionRelativeVolume { get; set; }
    public decimal SlotAverageVolume { get; set; }
    public decimal CumulativeAverageVolume { get; set; }
    public decimal AverageSessionVolume { get; set; }
    public int RelativeVolumeSampleCount { get; set; }
    public decimal Volume { get; set; }
    public decimal? Rsi { get; set; }
    public decimal? Vwap { get; set; }
}

public class OrderDisplayViewModel
{
    public OrderDisplayViewModel(TradingFlow.Domain.Orders.ActiveBrokerOrder order, decimal? currentPrice)
    {
        Order = order;
        CurrentPrice = currentPrice;
    }

    public TradingFlow.Domain.Orders.ActiveBrokerOrder Order { get; }
    public decimal? CurrentPrice { get; }

    public decimal? DistancePct
    {
        get
        {
            if (Order.LimitPrice == null || CurrentPrice == null || CurrentPrice == 0) return null;
            return (CurrentPrice - Order.LimitPrice) / CurrentPrice * 100m;
        }
    }

}
