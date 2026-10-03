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
    private readonly PaperRunViewService views;
    private readonly ConfigCatalogService catalog;


    public PaperJobModel(PaperJobService jobs, PaperRunViewService views, ConfigCatalogService catalog)
    {
        this.jobs = jobs;
        this.views = views;
        this.catalog = catalog;

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

            try { ChartDataList = views.LoadChartData(Job); } catch (IOException) { } catch (InvalidOperationException) { }

            try
            {
                RecentAudits = await views.LoadRecentAuditsAsync(Job, HttpContext.RequestAborted);
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

        var chartData = views.LoadChartData(job);
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
            audits = await views.LoadRecentAuditsAsync(job, HttpContext.RequestAborted);
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
                    slotMedianVolume = point.SlotMedianVolume,
                    cumulativeSameTimeMedianVolume = point.CumulativeSameTimeMedianVolume,
                    relativeVolume = point.RelativeVolume,
                    relativeVolumeSampleCount = point.RelativeVolumeSampleCount,
                    slotRelativeVolume = point.SlotRelativeVolume,
                    slotRelativeVolumeSampleCount = point.SlotRelativeVolumeSampleCount,
                    relativeVolumeSource = point.RelativeVolumeSource,
                    marketEvidenceProfileVersion = point.MarketEvidenceProfileVersion,
                    relativeVolumeCohort = point.RelativeVolumeCohort,
                    dataFeed = point.DataFeed,
                    adjustmentPolicy = point.AdjustmentPolicy,
                    marketEvidenceReliability = point.MarketEvidenceReliability,
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

    public async Task<IActionResult> OnPostCancelRunAsync(Guid id, CancellationToken cancellationToken)
    {
        await jobs.CancelJobAsync(id, cancellationToken);
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
