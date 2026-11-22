using TradingFlow.Web.Services;

namespace TradingFlow.Web;

/// <summary>
/// Read-only summary of one index benchmark against the configured regime rule for the workspace status strip.
/// </summary>
/// <param name="SummaryLabel">Plain sentence explaining the result (or why it is unavailable).</param>
/// <param name="RuleLabel">Short name of the rule's average (for example "50-day"), or null when unavailable.</param>
public sealed record BenchmarkRegimePulse(
    string Symbol,
    bool Available,
    bool LongGateOn,
    bool ShortGateOn,
    decimal? LastClose,
    DateOnly? LastSessionDate,
    string SummaryLabel,
    string? RuleLabel);

/// <summary>
/// Read-only summary of the previous session's FRED VIXCLS close and its resulting swing risk multipliers. When that
/// session's close is not published yet, <c>LatestPublishedClose</c>/<c>LatestPublishedDate</c> still show the newest
/// close FRED has, for display only (sizing keeps waiting for the exact previous-session close).
/// </summary>
public sealed record VixRegimePulse(
    bool Available,
    DateOnly? SessionDate,
    decimal? Close,
    decimal LongRiskScale,
    string LongLabel,
    decimal ShortRiskScale,
    string ShortLabel,
    decimal? LatestPublishedClose = null,
    DateOnly? LatestPublishedDate = null);

/// <summary>
/// Authenticated workspace-wide market regime, VIX sizing and leading-sector snapshot displayed in the status strip.
/// <c>LongRuleSource</c>/<c>ShortRuleSource</c> say which strategy configurations the regime rules came from.
/// </summary>
public sealed record WorkspaceRegimePulseResponse(
    string ContractVersion,
    DateTimeOffset ObservedAtUtc,
    BenchmarkRegimePulse Spy,
    BenchmarkRegimePulse Qqq,
    VixRegimePulse Vix,
    IReadOnlyList<string> TopSectors,
    string? TopSectorsAsOfDate,
    IReadOnlyList<SectorLeader> TopSectorLeaders,
    string? TopSectorsUnavailableReason,
    string LongRuleSource,
    string ShortRuleSource);

/// <summary>Maps <c>GET /api/v1/workspace/regime-pulse</c> onto <see cref="WorkspacePulseService"/>.</summary>
public static class WorkspacePulseEndpoints
{
    /// <summary>Registers the read-only workspace regime pulse endpoint on the authenticated v1 API group.</summary>
    public static void MapWorkspacePulseEndpoints(this RouteGroupBuilder group) =>
        group.MapGet("/workspace/regime-pulse", async (WorkspacePulseService pulse, CancellationToken cancellationToken) =>
            Results.Ok(await pulse.GetAsync(cancellationToken)));
}
