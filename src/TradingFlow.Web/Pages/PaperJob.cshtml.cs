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
        LatencyProfile = TradingFlow.Domain.Logging.ApiProfiler.GetSummary("Alpaca");
        
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
            marketDataFeed = ResolveMarketDataFeedLabel(job),
            events = job.Events.Reverse().Take(100).ToArray(),
            metrics = LoadChartData(job)
                .OrderBy(x => x.Ticker, StringComparer.OrdinalIgnoreCase)
                .Select(point => new
                {
                    time = FormatLocalTime(point.Timestamp),
                    ticker = point.Ticker,
                    close = point.Close,
                    rsi = point.Rsi,
                    volume = point.Volume,
                    averageVolume = point.AverageVolume,
                    vwap = point.Vwap
                })
                .ToArray(),
            audits = audits.Select(a => new
            {
                time = FormatLocalTime(a.Timestamp),
                ticker = a.Ticker,
                decision = a.Decision,
                reason = FormatRejectionReason(a.RejectionReason),
                rawReason = a.RejectionReason ?? "",
                accepted = a.Decision.Equals("Accepted", StringComparison.OrdinalIgnoreCase)
            }).ToArray()
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
        try
        {
            var config = catalog.GetConfig(job.ConfigPath);
            resultsRoot = config.Config.ResultsRoot;
        }
        catch
        {
            // Keep the page responsive if the generated config was deleted.
        }

        var resultsDir = string.IsNullOrEmpty(resultsRoot)
            ? Path.Combine(Environment.CurrentDirectory, "data", "paper", "results", "live", job.RunName)
            : Path.Combine(Environment.CurrentDirectory, resultsRoot, "live", job.RunName);

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
                    if (data != null)
                    {
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
}

public class ChartDataPoint
{
    public DateTimeOffset Timestamp { get; set; }
    public string Ticker { get; set; } = String.Empty;
    public string StrategyName { get; set; } = String.Empty;
    public decimal Close { get; set; }
    public decimal Atr { get; set; }
    public decimal RelativeVolume { get; set; }
    public decimal Volume { get; set; }
    public decimal? Rsi { get; set; }
    public decimal? Vwap { get; set; }

    public decimal AverageVolume => RelativeVolume > 0 ? Volume / RelativeVolume : 0m;
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
