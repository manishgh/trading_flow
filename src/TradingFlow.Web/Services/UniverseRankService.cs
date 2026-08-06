using System.Text.Json;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Web.Services;

/// <summary>
/// Whether TradingFlow's eligibility verdict and the model's evidence point the
/// same way. This is a reading, never an authority: eligibility decides whether
/// an entry may be taken, and a conflict is shown so the operator can see the
/// disagreement rather than have it resolved for them.
/// </summary>
public enum AgreementFlag
{
    /// <summary>Eligibility and the model point the same way.</summary>
    Agree,

    /// <summary>Eligible, but the model signal is opposite.</summary>
    Conflict,

    /// <summary>Either side is neutral, unscored or not ready.</summary>
    Partial
}

/// <summary>
/// The direction the model's evidence points, reduced from the predictor's
/// signal vocabulary to the three states a desk can act on.
/// </summary>
public enum ModelDirection
{
    /// <summary>Not ready, not scored, or explicitly neutral.</summary>
    Neutral,

    /// <summary>Watch, confirm or enter - the model supports a long entry.</summary>
    Supportive,

    /// <summary>Low probability or avoid - the model says stand aside.</summary>
    Opposed
}

/// <summary>
/// One factor's contribution to a symbol's score. Stored per symbol so an audit
/// page can explain why symbol N ranked where it did rather than showing a total
/// nobody can take apart.
/// </summary>
/// <param name="Key">Stable factor key.</param>
/// <param name="Label">Operator-facing name.</param>
/// <param name="Value">Normalised factor value in 0..1, or null when unreadable.</param>
/// <param name="Weight">Weight applied for this horizon.</param>
/// <param name="Contribution">Value times weight, or 0 when the value is unknown.</param>
/// <param name="Detail">What the value was actually read from.</param>
public sealed record RankFactor(
    string Key,
    string Label,
    decimal? Value,
    decimal Weight,
    decimal Contribution,
    string Detail);

/// <summary>A desk row with its rank, score and the reasoning behind both.</summary>
public sealed record RankedDeskRow(
    WishlistDeskRow Row,
    int Rank,
    decimal Score,
    IReadOnlyList<RankFactor> Factors,
    decimal VetoPenalty,
    AgreementFlag Agreement,
    ModelDirection ModelDirection,
    MarketPredictorResult Evidence,
    string CatalystKind,
    bool FromScreener)
{
    public string Ticker => Row.Ticker;

    /// <summary>Whether the model opposed an otherwise eligible technical verdict.</summary>
    public bool IsVetoed => VetoPenalty > 0m;

    public string AgreementLabel => Agreement switch
    {
        AgreementFlag.Agree => "AGREE",
        AgreementFlag.Conflict => "CONFLICT",
        _ => "PARTIAL"
    };

    public string AgreementCssClass => Agreement switch
    {
        AgreementFlag.Agree => "agreement agreement-agree",
        AgreementFlag.Conflict => "agreement agreement-conflict",
        _ => "agreement agreement-partial"
    };

    /// <summary>
    /// One sentence naming what the two sides said. Rendered under the agreement
    /// band, where a bare label would leave the operator to guess the reason.
    /// </summary>
    public string AgreementNote => Agreement switch
    {
        AgreementFlag.Agree =>
            "TradingFlow eligibility and the predictor point the same way. Eligibility remains authoritative for entry.",
        AgreementFlag.Conflict =>
            "Technicals are eligible and the predictor says stand aside. The disagreement is recorded, not resolved: eligibility still authorises the entry.",
        _ =>
            "One side is neutral or has no valid evidence, so the two cannot be compared. Eligibility remains authoritative for entry."
    };
}

/// <summary>The whole ranking run, persisted so a score can be re-derived.</summary>
public sealed record UniverseRankingRun(
    Guid RunId,
    DateTimeOffset RankedAtUtc,
    string UniverseSource,
    string Horizon,
    string Mode,
    string WeightsVersion,
    IReadOnlyList<RankedDeskRow> Rows);

/// <summary>
/// Ranks a resolved candidate universe.
///
/// The desk selects a universe source, not a single symbol, so this scores the
/// whole set in one pass and returns an ordered list with per-symbol factor
/// contributions. Per-symbol prediction is one input, not the ranking.
///
/// A model veto subtracts a flat penalty rather than removing the candidate. A
/// removed row cannot be argued with; a demoted row with a CONFLICT flag can.
/// </summary>
public sealed class UniverseRankService
{
    private static readonly HashSet<string> SupportiveSignals = new(StringComparer.OrdinalIgnoreCase)
    {
        // final_signal
        "high_conviction_watch",
        "watch_for_entry",
        "intraday_watch",
        // swing.signal
        "strong_bullish_watch",
        "bullish_watch",
        // intraday.signal
        "entry_candidate",
        "watch_for_confirmation"
    };

    private static readonly HashSet<string> OpposedSignals = new(StringComparer.OrdinalIgnoreCase)
    {
        "low_probability",
        "avoid_entry",
        "avoid_entry_downside_risk"
    };

    private readonly MarketPredictorHttpClient predictor;
    private readonly ProjectPaths paths;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<UniverseRankService> logger;

    public UniverseRankService(
        MarketPredictorHttpClient predictor,
        ProjectPaths paths,
        TimeProvider timeProvider,
        ILogger<UniverseRankService> logger)
    {
        this.predictor = predictor;
        this.paths = paths;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>
    /// Scores and orders <paramref name="rows"/>.
    /// </summary>
    /// <param name="rows">The resolved universe - wishlist, screener, or both.</param>
    /// <param name="config">Weights from the active profile.</param>
    /// <param name="horizon">intraday or swing; selects the weight set.</param>
    /// <param name="mode">Prediction mode passed to the predictor.</param>
    /// <param name="predictionHorizon">Requested predictor horizon, or auto.</param>
    /// <param name="screenerSymbols">Symbols that came from the active screener result.</param>
    /// <param name="earningsSymbols">Symbols with an earnings print in the monitored window.</param>
    /// <param name="universeSource">Recorded on the run for audit.</param>
    public async Task<UniverseRankingRun> RankAsync(
        IReadOnlyList<WishlistDeskRow> rows,
        UniverseRankConfig config,
        string horizon,
        string mode,
        string predictionHorizon,
        IReadOnlySet<string> screenerSymbols,
        IReadOnlySet<string> earningsSymbols,
        string universeSource,
        CancellationToken cancellationToken)
    {
        var weights = config.WeightsFor(horizon);
        var evidence = rows.Count == 0
            ? new Dictionary<string, MarketPredictorResult>(StringComparer.OrdinalIgnoreCase)
            : await predictor.GetBatchAsync(
                rows.Select(row => row.Ticker).ToArray(),
                mode,
                predictionHorizon,
                cancellationToken);

        var scored = new List<RankedDeskRow>(rows.Count);
        foreach (var row in rows)
        {
            if (!evidence.TryGetValue(row.Ticker, out var result))
            {
                result = MarketPredictorResult.Unavailable(
                    row.Ticker, mode, predictionHorizon, "unavailable", "No prediction was returned for this symbol.");
            }

            var fromScreener = screenerSymbols.Contains(row.Ticker);
            var catalystKind = ResolveCatalystKind(row, result, earningsSymbols, fromScreener);
            var direction = ResolveDirection(result);
            var eligible = row.HasSignal;
            var agreement = ResolveAgreement(eligible, direction);

            var factors = new List<RankFactor>(5)
            {
                BuildModelEdge(result, horizon, weights.ModelEdge),
                BuildMarketStructure(row, result, horizon, weights.MarketStructure),
                BuildCatalyst(catalystKind, config, weights.Catalyst),
                BuildTechnicalState(row, weights.TechnicalState),
                BuildLiquidity(row, config, weights.Liquidity)
            };

            // The veto is applied only where the two sides genuinely disagree:
            // eligible technicals against an opposing model. A neutral model is
            // not a veto, and neither is an unreadable one.
            var veto = agreement == AgreementFlag.Conflict ? config.VetoPenalty : 0m;
            var score = factors.Sum(factor => factor.Contribution) - veto;

            scored.Add(new RankedDeskRow(
                row,
                0,
                score,
                factors,
                veto,
                agreement,
                direction,
                result,
                catalystKind,
                fromScreener));
        }

        // Ticker is the tiebreaker so equal scores keep a stable, predictable
        // order instead of shuffling between refreshes.
        var ordered = scored
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Ticker, StringComparer.OrdinalIgnoreCase)
            .Select((item, index) => item with { Rank = index + 1 })
            .ToArray();

        var run = new UniverseRankingRun(
            Guid.NewGuid(),
            timeProvider.GetUtcNow(),
            universeSource,
            horizon,
            mode,
            config.WeightsVersion,
            ordered);
        await PersistAsync(run, cancellationToken);
        return run;
    }

    // -----------------------------------------------------------------------
    // Factors
    // -----------------------------------------------------------------------

    /// <summary>
    /// Direction-adjusted model probability. An opposing signal is scored as its
    /// complement rather than as a raw probability, so a confident "stand aside"
    /// lowers the rank instead of raising it.
    /// </summary>
    private static RankFactor BuildModelEdge(MarketPredictorResult result, string horizon, decimal weight)
    {
        var intraday = horizon.Equals("intraday", StringComparison.OrdinalIgnoreCase);
        var probability = intraday
            ? result.Intraday?.OpportunityProbability
            : result.Swing?.Probability;
        if (result.AvailabilityStatus != "available" || probability is null)
        {
            return new RankFactor(
                "model_edge",
                "Model edge",
                null,
                weight,
                0m,
                result.AvailabilityReason ?? "No probability was returned for this horizon.");
        }

        var direction = ResolveDirection(result);
        var adjusted = direction == ModelDirection.Opposed
            ? 1m - Clamp(probability.Value)
            : Clamp(probability.Value);
        // A downside probability is only meaningful intraday, where the model
        // scores both sides; subtracting it keeps a high-opportunity, high-risk
        // symbol from outranking a clean one.
        if (intraday && result.Intraday?.DownsideProbability is { } downside)
        {
            adjusted = Clamp(adjusted - Clamp(downside) * 0.5m);
        }

        return new RankFactor(
            "model_edge",
            "Model edge",
            adjusted,
            weight,
            adjusted * weight,
            $"{result.FinalSignal} · p={probability.Value:0.000}{(intraday && result.Intraday?.DownsideProbability is { } d ? $" · downside {d:0.000}" : String.Empty)}");
    }

    /// <summary>
    /// RVOL intraday, trend quality swing. Relative volume comes from the model
    /// payload where the indicator engine has not produced one for this row; a
    /// value of 1.0 is an ordinary day and scores 0.5.
    /// </summary>
    private static RankFactor BuildMarketStructure(
        WishlistDeskRow row,
        MarketPredictorResult result,
        string horizon,
        decimal weight)
    {
        if (horizon.Equals("intraday", StringComparison.OrdinalIgnoreCase))
        {
            var rvol = result.Intraday?.RelativeVolume;
            if (rvol is null)
            {
                return new RankFactor("market_structure", "Market structure", null, weight, 0m, "No relative volume available.");
            }

            // 1.0x is ordinary and scores 0.5; 3.0x and above saturates at 1.0.
            var value = Clamp(rvol.Value / 3m + 0.166m);
            return new RankFactor("market_structure", "Market structure", value, weight, value * weight, $"RVOL {rvol.Value:0.00}x");
        }

        var volumeZ = result.Swing?.VolumeZ20;
        var return1D = result.Swing?.Return1D;
        if (volumeZ is null && return1D is null)
        {
            return new RankFactor("market_structure", "Market structure", null, weight, 0m, "No trend-quality inputs available.");
        }

        // Trend quality: a positive 20-day volume z-score with a positive
        // one-day return is participation confirming direction.
        var zComponent = volumeZ is { } z ? Clamp(z / 4m + 0.5m) : 0.5m;
        var returnComponent = return1D is { } r ? Clamp(r * 10m + 0.5m) : 0.5m;
        var quality = Clamp(zComponent * 0.6m + returnComponent * 0.4m);
        return new RankFactor(
            "market_structure",
            "Market structure",
            quality,
            weight,
            quality * weight,
            $"volume z20 {(volumeZ is { } zz ? zz.ToString("0.00") : "unknown")} · 1d return {(return1D is { } rr ? rr.ToString("P2") : "unknown")}");
    }

    private static RankFactor BuildCatalyst(string kind, UniverseRankConfig config, decimal weight)
    {
        var value = config.CatalystWeightFor(kind);
        return new RankFactor("catalyst", "Catalyst", value, weight, value * weight, $"{kind} catalyst");
    }

    /// <summary>
    /// The evaluator's own verdict. Eligible is a completed-bar trigger; watching
    /// is a candidate that has not triggered; blocked is a rejected one.
    /// </summary>
    private static RankFactor BuildTechnicalState(WishlistDeskRow row, decimal weight)
    {
        var (value, label) = row.HasSignal
            ? (1.0m, "eligible")
            : row.HasQuote
                ? (0.42m, "watching")
                : (0.1m, "blocked - no quote");
        return new RankFactor("technical_state", "Technical state", value, weight, value * weight, label);
    }

    private static RankFactor BuildLiquidity(WishlistDeskRow row, UniverseRankConfig config, decimal weight)
    {
        var spread = row.SpreadBps;
        if (spread is null)
        {
            // No quote is not "wide"; it is unreadable, and scoring it as zero
            // spread would rank an unquotable symbol above a real one.
            return new RankFactor("liquidity", "Liquidity", null, weight, 0m, "No inside quote.");
        }

        var value = Clamp(1m - spread.Value / config.LiquiditySpreadCeilingBps);
        return new RankFactor("liquidity", "Liquidity", value, weight, value * weight, $"{spread.Value:0.0} bps inside spread");
    }

    // -----------------------------------------------------------------------
    // Classification
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reduces the predictor's signal vocabulary to a direction. The final
    /// signal is preferred; the per-leg signals are consulted only when the
    /// final one is neutral, so a leg cannot override the model's own summary.
    /// </summary>
    public static ModelDirection ResolveDirection(MarketPredictorResult result)
    {
        if (result.AvailabilityStatus != "available")
        {
            return ModelDirection.Neutral;
        }

        if (SupportiveSignals.Contains(result.FinalSignal))
        {
            return ModelDirection.Supportive;
        }

        if (OpposedSignals.Contains(result.FinalSignal))
        {
            return ModelDirection.Opposed;
        }

        foreach (var signal in new[] { result.Intraday?.Signal, result.Swing?.Signal })
        {
            if (signal is null)
            {
                continue;
            }
            if (SupportiveSignals.Contains(signal))
            {
                return ModelDirection.Supportive;
            }
            if (OpposedSignals.Contains(signal))
            {
                return ModelDirection.Opposed;
            }
        }

        return ModelDirection.Neutral;
    }

    /// <summary>
    /// AGREE when both point the same way, CONFLICT when eligibility says go and
    /// the model says stand aside, PARTIAL when either side is neutral.
    /// </summary>
    public static AgreementFlag ResolveAgreement(bool eligible, ModelDirection direction) => direction switch
    {
        ModelDirection.Neutral => AgreementFlag.Partial,
        ModelDirection.Supportive => eligible ? AgreementFlag.Agree : AgreementFlag.Partial,
        _ => eligible ? AgreementFlag.Conflict : AgreementFlag.Agree
    };

    /// <summary>
    /// The strongest catalyst behind a candidate. Earnings outranks news, which
    /// outranks a filing, which outranks bare screener membership.
    /// </summary>
    private static string ResolveCatalystKind(
        WishlistDeskRow row,
        MarketPredictorResult result,
        IReadOnlySet<string> earningsSymbols,
        bool fromScreener)
    {
        if (earningsSymbols.Contains(row.Ticker))
        {
            return UniverseRankConfig.EarningsCatalyst;
        }

        var catalyst = result.Intraday?.Catalyst ?? result.Swing?.Catalyst;
        if (catalyst is not null && catalyst.EventCount > 0)
        {
            // The predictor's catalyst status names the kind it scored; a filing
            // is distinguished from a headline because they decay differently.
            if (catalyst.Status.Contains("filing", StringComparison.OrdinalIgnoreCase) ||
                catalyst.Status.Contains("sec", StringComparison.OrdinalIgnoreCase))
            {
                return UniverseRankConfig.FilingCatalyst;
            }
            return UniverseRankConfig.NewsCatalyst;
        }

        if (row.HasNews)
        {
            return UniverseRankConfig.NewsCatalyst;
        }

        return fromScreener ? UniverseRankConfig.ScreenerCatalyst : UniverseRankConfig.NoCatalyst;
    }

    private static decimal Clamp(decimal value) => Math.Clamp(value, 0m, 1m);

    // -----------------------------------------------------------------------
    // Audit
    // -----------------------------------------------------------------------

    /// <summary>
    /// Appends the run to a daily JSONL ledger so an audit page can answer "why
    /// did symbol N rank where it did" from the stored factor values rather than
    /// by re-running the ranker against different market state.
    ///
    /// A failed write is logged and swallowed: losing an audit line must not
    /// take the desk down.
    /// </summary>
    private async Task PersistAsync(UniverseRankingRun run, CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.Combine(paths.DataRoot, "ranking");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{run.RankedAtUtc.UtcDateTime:yyyy-MM-dd}.jsonl");
            var record = new
            {
                runId = run.RunId,
                rankedAtUtc = run.RankedAtUtc,
                universeSource = run.UniverseSource,
                horizon = run.Horizon,
                mode = run.Mode,
                weightsVersion = run.WeightsVersion,
                rows = run.Rows.Select(row => new
                {
                    ticker = row.Ticker,
                    rank = row.Rank,
                    score = row.Score,
                    vetoPenalty = row.VetoPenalty,
                    agreement = row.AgreementLabel,
                    modelDirection = row.ModelDirection.ToString(),
                    catalyst = row.CatalystKind,
                    fromScreener = row.FromScreener,
                    evidenceStatus = row.Evidence.AvailabilityStatus,
                    finalSignal = row.Evidence.FinalSignal,
                    snapshotId = row.Evidence.SnapshotId,
                    factors = row.Factors.Select(factor => new
                    {
                        factor.Key,
                        factor.Value,
                        factor.Weight,
                        factor.Contribution,
                        factor.Detail
                    })
                })
            };
            var line = JsonSerializer.Serialize(record, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await File.AppendAllTextAsync(path, line + Environment.NewLine, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(exception, "Unable to persist the universe ranking run for audit.");
        }
    }
}
