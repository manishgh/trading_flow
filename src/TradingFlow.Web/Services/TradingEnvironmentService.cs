namespace TradingFlow.Web.Services;

/// <summary>The execution environment a screen is operating against.</summary>
public enum TradingEnvironment
{
    /// <summary>Simulated execution against the broker's paper account.</summary>
    Paper,

    /// <summary>Real-money execution. Gated - see <see cref="TradingEnvironmentService"/>.</summary>
    Live
}

/// <summary>
/// Whether an environment may be operated, and why not when it may not.
/// </summary>
/// <param name="Environment">The environment described.</param>
/// <param name="IsEnabled">Whether order entry is permitted at all.</param>
/// <param name="LockReason">Operator-facing sentence explaining a disabled environment.</param>
/// <param name="OutstandingGates">The named promotion gates still to be satisfied.</param>
public sealed record TradingEnvironmentState(
    TradingEnvironment Environment,
    bool IsEnabled,
    string? LockReason,
    IReadOnlyList<string> OutstandingGates)
{
    /// <summary>Short uppercase label, e.g. <c>PAPER</c>.</summary>
    public string Label => Environment.ToString().ToUpperInvariant();

    /// <summary>Lowercase route segment, e.g. <c>paper</c>.</summary>
    public string Slug => Environment.ToString().ToLowerInvariant();
}

/// <summary>
/// Single source of truth for which trading environments exist and which may be
/// operated.
/// </summary>
/// <remarks>
/// Live routing is disabled by <c>docs/operating-boundaries.md</c> until a frozen
/// strategy clears its research, holdout, paper-shadow, execution-calibration, and
/// human-approval gates. This service is the only place that decides that, so the
/// UI cannot drift from the boundary and enabling live later is a change here rather
/// than a change across every screen.
///
/// The lock fails closed: live surfaces render no order controls at all, rather than
/// rendering them disabled.
/// </remarks>
public sealed class TradingEnvironmentService
{
    private static readonly IReadOnlyList<string> LivePromotionGates =
    [
        "Research evidence for a frozen strategy",
        "Out-of-sample holdout validation",
        "Paper-shadow agreement",
        "Execution calibration",
        "Explicit human promotion approval"
    ];

    private const string LiveLockReason =
        "Live routing is disabled. A frozen strategy must clear every promotion gate in " +
        "docs/operating-boundaries.md before real-money execution can be enabled.";

    /// <summary>The environment used when a request does not name one.</summary>
    public TradingEnvironment Default => TradingEnvironment.Paper;

    /// <summary>Every environment the operator can navigate to, in display order.</summary>
    public IReadOnlyList<TradingEnvironmentState> All =>
        [GetState(TradingEnvironment.Paper), GetState(TradingEnvironment.Live)];

    public TradingEnvironmentState GetState(TradingEnvironment environment) => environment switch
    {
        TradingEnvironment.Live => new TradingEnvironmentState(
            TradingEnvironment.Live, false, LiveLockReason, LivePromotionGates),
        _ => new TradingEnvironmentState(
            TradingEnvironment.Paper, true, null, [])
    };

    /// <summary>
    /// Resolves a route segment to an environment. Anything unrecognised resolves to
    /// the default rather than throwing, so a malformed URL cannot land the operator
    /// on an ambiguous screen.
    /// </summary>
    public TradingEnvironment Parse(string? slug) =>
        String.Equals(slug, "live", StringComparison.OrdinalIgnoreCase)
            ? TradingEnvironment.Live
            : TradingEnvironment.Paper;

    /// <summary>Whether order entry is permitted in <paramref name="environment"/>.</summary>
    public bool IsOrderEntryAllowed(TradingEnvironment environment) =>
        GetState(environment).IsEnabled;
}
