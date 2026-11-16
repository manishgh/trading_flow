using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Data.Identity;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Charts;
using TradingFlow.Web.Services.ScreenCaptures;

namespace TradingFlow.Web.Pages;

/// <summary>
/// Operator health. Authoritative server state assembled into one view.
///
/// Opening this screen only reads. An explicitly enabled local administrator
/// can use the separate chart acceptance controls for a read-only observation.
/// </summary>
[Authorize]
public sealed class OperationsModel : PageModel
{
    private readonly OperationsHealthService health;
    private readonly ConfigCatalogService catalog;
    private readonly TradingEnvironmentService environments;
    private readonly AlpacaQuoteService quotes;
    private readonly PaperJobService paperJobs;
    private readonly MobileAutomationService automation;
    private readonly FinvizScreenCaptureStatusReader captures;
    private readonly ChartAcceptanceOptions chartAcceptance;

    // Add observational capture status alongside the existing operational report.
    public OperationsModel(
        OperationsHealthService health,
        ConfigCatalogService catalog,
        TradingEnvironmentService environments,
        AlpacaQuoteService quotes,
        PaperJobService paperJobs,
        MobileAutomationService automation,
        FinvizScreenCaptureStatusReader captures,
        ChartAcceptanceOptions chartAcceptance)
    {
        this.health = health;
        this.catalog = catalog;
        this.environments = environments;
        this.quotes = quotes;
        this.paperJobs = paperJobs;
        this.automation = automation;
        this.captures = captures;
        this.chartAcceptance = chartAcceptance;
    }

    /// <summary>Environment this screen is reporting on, from the query segment.</summary>
    [BindProperty(SupportsGet = true)] public string? Env { get; set; }

    public TradingEnvironmentState EnvironmentState => environments.GetState(environments.Parse(Env));

    public OperationsReport? Report { get; private set; }
    public FinvizScreenCaptureStatus? ScreenCapture { get; private set; }
    public bool ChartAcceptanceEnabled => chartAcceptance.Enabled && User.IsInRole(TradingFlowRoles.Administrator) &&
        ChartAcceptanceEndpoints.IsLocalRequest(HttpContext);

    // Read subsystem health and independent observational capture evidence for this request.
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        // Quote freshness is measured against the symbols the operator actually
        // holds. With no open book there is nothing to be stale about, and the
        // row says that rather than reporting a disconnected feed.
        var trades = await RunningTradesBuilder.BuildAsync(paperJobs, automation);
        var tickers = trades.Select(trade => trade.Ticker).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var observations = Array.Empty<DateTimeOffset?>();
        if (tickers.Length > 0)
        {
            var latest = await quotes.GetLatestQuotesAsync(tickers, ResolveQuoteFeed(), cancellationToken);
            observations = latest.Values.Select(quote => quote.Timestamp).ToArray();
        }

        Report = await health.BuildAsync(ResolveQuoteFeed(), observations, cancellationToken);
        ScreenCapture = await captures.ReadAsync(cancellationToken);
    }

    private string ResolveQuoteFeed()
    {
        var configs = catalog.GetPaperConfigs();
        var selected = configs.FirstOrDefault(config =>
                config.FileName.Equals("alpaca-paper.yaml", StringComparison.OrdinalIgnoreCase))
            ?? configs.FirstOrDefault();
        return selected?.Config.Providers.Alpaca.DataFeed ?? "sip";
    }
}
