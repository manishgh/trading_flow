using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;
using TradingFlow.Engine.Configuration;
using TradingFlow.Domain.Strategies;

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
    public List<TradingFlow.Domain.Audit.DecisionAuditRecord> RecentAudits { get; private set; } = new();

    public async Task OnGetAsync(Guid id)
    {
        Job = jobs.Get(id);
        var brokerOrders = await jobs.GetOpenOrdersAsync(id);
        OpenPositions = await jobs.GetOpenPositionsAsync(id);
        LatencyProfile = TradingFlow.Domain.Logging.ApiProfiler.GetSummary("Alpaca");
        
        if (Job != null)
        {
            string? resultsRoot = null;
            try
            {
                var config = catalog.GetConfig(Job.ConfigPath);
                Strategy = config.Strategies.FirstOrDefault()?.Definition;
                resultsRoot = config.Config.ResultsRoot;
            }
            catch { /* Ignore if config missing */ }

            var resultsDir = string.IsNullOrEmpty(resultsRoot)
                ? Path.Combine(Environment.CurrentDirectory, "data", "paper", "results", "live", Job.RunName)
                : Path.Combine(Environment.CurrentDirectory, resultsRoot, "live", Job.RunName);

            System.IO.File.WriteAllText("paperjob_debug.txt", $"ResultsRoot: {resultsRoot}\nResultsDir: {resultsDir}\nExists: {Directory.Exists(resultsDir)}");

            if (Directory.Exists(resultsDir))
            {
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
                                ChartDataList.Add(data);
                            }
                        }
                        catch { /* skip bad json */ }
                    }
                }
            }

            try
            {
                var allAudits = await auditRepo.GetAuditsByRunNameAsync(Job.RunName, default);
                RecentAudits = allAudits.OrderByDescending(a => a.Timestamp).Take(50).ToList();
            }
            catch { /* Ignore audit db errors */ }
        }

        OpenOrders = brokerOrders.Select(o => 
        {
            var currentPx = ChartDataList.FirstOrDefault(c => c.Ticker == o.Ticker)?.Close;
            return new OrderDisplayViewModel(o, currentPx);
        }).ToList();

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
