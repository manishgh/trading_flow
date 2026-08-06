using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingFlow.Web.Services;

namespace TradingFlow.Web.Pages;

/// <summary>
/// Operator health. Authoritative server state assembled into one view.
///
/// This screen only reads. It cannot start, stop or reconfigure a subsystem, so
/// opening it never changes what the system is doing. Anything unreadable is
/// reported as unknown rather than guessed, and execution stays fail-closed
/// while it is.
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

    public OperationsModel(
        OperationsHealthService health,
        ConfigCatalogService catalog,
        TradingEnvironmentService environments,
        AlpacaQuoteService quotes,
        PaperJobService paperJobs,
        MobileAutomationService automation)
    {
        this.health = health;
        this.catalog = catalog;
        this.environments = environments;
        this.quotes = quotes;
        this.paperJobs = paperJobs;
        this.automation = automation;
    }

    /// <summary>Environment this screen is reporting on, from the query segment.</summary>
    [BindProperty(SupportsGet = true)] public string? Env { get; set; }

    public TradingEnvironmentState EnvironmentState => environments.GetState(environments.Parse(Env));

    public OperationsReport? Report { get; private set; }

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
