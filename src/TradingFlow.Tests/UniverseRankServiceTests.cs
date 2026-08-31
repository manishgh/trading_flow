using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Tests;

/// <summary>
/// The desk ranks a universe, not a symbol. These cover the two properties the
/// ranking has to keep: advisory model evidence cannot alter operational order,
/// and every operational score can be taken apart into its factors.
/// </summary>
public sealed class UniverseRankServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 14, 30, 0, TimeSpan.Zero);

    private readonly string dataRoot = Path.Combine(
        Path.GetTempPath(),
        $"tf-rank-{Guid.NewGuid():N}");

    [Fact]
    public async Task RankAsync_OrdersByScoreAndNumbersFromOne()
    {
        var service = CreateService(SignalFor("MU", "watch_for_entry"), SignalFor("NVDA", "watch_for_entry"));

        var run = await service.RankAsync(
            [EligibleRow("MU", spreadBps: 1m), WatchingRow("NVDA", spreadBps: 6m)],
            UniverseRankConfig.Default,
            "intraday",
            "unified",
            "auto",
            EmptySet,
            EmptySet,
            "wishlist:test",
            CancellationToken.None);

        Assert.Equal(["MU", "NVDA"], run.Rows.Select(row => row.Ticker));
        Assert.Equal([1, 2], run.Rows.Select(row => row.Rank));
        Assert.True(run.Rows[0].Score > run.Rows[1].Score);
    }

    [Fact]
    public async Task RankAsync_PredictorConflictIsAdvisoryAndDoesNotPenalizeCandidate()
    {
        var service = CreateService(SignalFor("MU", "avoid_entry"));

        var run = await service.RankAsync(
            [EligibleRow("MU", spreadBps: 1m)],
            UniverseRankConfig.Default,
            "intraday",
            "unified",
            "auto",
            EmptySet,
            EmptySet,
            "wishlist:test",
            CancellationToken.None);

        var row = Assert.Single(run.Rows);
        Assert.Equal(AgreementFlag.Conflict, row.Agreement);
        Assert.False(row.IsVetoed);
        Assert.Equal(0m, row.VetoPenalty);
        Assert.Equal(row.Factors.Sum(factor => factor.Contribution), row.Score);
        Assert.Equal(0m, row.Factors.Single(factor => factor.Key == "model_edge").Contribution);
        Assert.Equal(0m, row.Factors.Single(factor => factor.Key == "market_structure").Contribution);
    }

    [Fact]
    public async Task RankAsync_DoesNotVetoWhenTheTechnicalVerdictIsNotEligible()
    {
        // Watching plus "stand aside" is agreement, not conflict: both sides are
        // saying the same thing.
        var service = CreateService(SignalFor("MU", "avoid_entry"));

        var run = await service.RankAsync(
            [WatchingRow("MU", spreadBps: 1m)],
            UniverseRankConfig.Default,
            "intraday",
            "unified",
            "auto",
            EmptySet,
            EmptySet,
            "wishlist:test",
            CancellationToken.None);

        var row = Assert.Single(run.Rows);
        Assert.Equal(AgreementFlag.Agree, row.Agreement);
        Assert.False(row.IsVetoed);
    }

    [Fact]
    public async Task RankAsync_UnreadableEvidenceIsPartialAndContributesNothing()
    {
        var service = CreateService(handler: new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var run = await service.RankAsync(
            [EligibleRow("MU", spreadBps: 1m)],
            UniverseRankConfig.Default,
            "intraday",
            "unified",
            "auto",
            EmptySet,
            EmptySet,
            "wishlist:test",
            CancellationToken.None);

        var row = Assert.Single(run.Rows);
        Assert.Equal(AgreementFlag.Partial, row.Agreement);
        Assert.False(row.IsVetoed);
        var modelEdge = row.Factors.Single(factor => factor.Key == "model_edge");
        Assert.Null(modelEdge.Value);
        Assert.Equal(0m, modelEdge.Contribution);
    }

    [Fact]
    public async Task RankAsync_CarriesEveryFactorSoAScoreCanBeExplained()
    {
        var service = CreateService(SignalFor("MU", "watch_for_entry"));

        var run = await service.RankAsync(
            [EligibleRow("MU", spreadBps: 2m)],
            UniverseRankConfig.Default,
            "intraday",
            "unified",
            "auto",
            EmptySet,
            EmptySet,
            "wishlist:test",
            CancellationToken.None);

        var row = Assert.Single(run.Rows);
        Assert.Equal(
            ["model_edge", "market_structure", "catalyst", "technical_state", "liquidity"],
            row.Factors.Select(factor => factor.Key));
        Assert.All(row.Factors, factor => Assert.False(String.IsNullOrWhiteSpace(factor.Detail)));
    }

    [Fact]
    public async Task RankAsync_AdvisoryFactorsHaveZeroWeightForEveryHorizon()
    {
        var service = CreateService(SignalFor("MU", "watch_for_entry"));
        var config = UniverseRankConfig.Default;

        var intraday = await RankSingleAsync(service, config, "intraday");
        var swing = await RankSingleAsync(service, config, "swing");

        Assert.Equal(0m, intraday.Factors.Single(factor => factor.Key == "model_edge").Weight);
        Assert.Equal(0m, intraday.Factors.Single(factor => factor.Key == "market_structure").Weight);
        Assert.Equal(0m, swing.Factors.Single(factor => factor.Key == "model_edge").Weight);
        Assert.Equal(0m, swing.Factors.Single(factor => factor.Key == "market_structure").Weight);
    }

    [Fact]
    public async Task RankAsync_PredictorDirectionAndAvailabilityCannotChangeOperationalOrder()
    {
        var rows = new[]
        {
            EligibleRow("AAA", spreadBps: 2m),
            EligibleRow("BBB", spreadBps: 2m)
        };
        var conflicting = CreateService(
            SignalFor("AAA", "avoid_entry"),
            SignalFor("BBB", "watch_for_entry"));
        var unavailable = CreateService(
            handler: new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var withPredictions = await conflicting.RankAsync(
            rows, UniverseRankConfig.Default, "intraday", "unified", "auto",
            EmptySet, EmptySet, "wishlist:test", CancellationToken.None);
        var withoutPredictions = await unavailable.RankAsync(
            rows, UniverseRankConfig.Default, "intraday", "unified", "auto",
            EmptySet, EmptySet, "wishlist:test", CancellationToken.None);

        Assert.Equal(["AAA", "BBB"], withPredictions.Rows.Select(row => row.Ticker));
        Assert.Equal(
            withoutPredictions.Rows.Select(row => (row.Ticker, row.Score)),
            withPredictions.Rows.Select(row => (row.Ticker, row.Score)));
    }

    [Fact]
    public async Task RankAsync_ScoresAnEarningsCandidateAboveABareScreenerOne()
    {
        // The catalyst table is the reason the desk surfaces an earnings name over
        // an otherwise identical screener hit.
        var service = CreateService(SignalFor("MU", "watch_for_entry"), SignalFor("NVDA", "watch_for_entry"));

        var run = await service.RankAsync(
            [EligibleRow("MU", spreadBps: 2m), EligibleRow("NVDA", spreadBps: 2m)],
            UniverseRankConfig.Default,
            "intraday",
            "unified",
            "auto",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NVDA" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MU" },
            "wishlist:test",
            CancellationToken.None);

        Assert.Equal(UniverseRankConfig.EarningsCatalyst, run.Rows.Single(row => row.Ticker == "MU").CatalystKind);
        Assert.Equal("MU", run.Rows[0].Ticker);
    }

    [Fact]
    public async Task RankAsync_PersistsTheRunForAudit()
    {
        var service = CreateService(SignalFor("MU", "watch_for_entry"));

        var run = await service.RankAsync(
            [EligibleRow("MU", spreadBps: 2m)],
            UniverseRankConfig.Default,
            "intraday",
            "unified",
            "auto",
            EmptySet,
            EmptySet,
            "wishlist:test",
            CancellationToken.None);

        var ledger = Path.Combine(dataRoot, "ranking", $"{Now.UtcDateTime:yyyy-MM-dd}.jsonl");
        Assert.True(File.Exists(ledger));
        var contents = await File.ReadAllTextAsync(ledger);
        Assert.Contains(run.RunId.ToString(), contents, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"weightsVersion\":\"rank.v1\"", contents, StringComparison.Ordinal);
        Assert.Contains("model_edge", contents, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, ModelDirection.Supportive, AgreementFlag.Agree)]
    [InlineData(true, ModelDirection.Opposed, AgreementFlag.Conflict)]
    [InlineData(true, ModelDirection.Neutral, AgreementFlag.Partial)]
    [InlineData(false, ModelDirection.Supportive, AgreementFlag.Partial)]
    [InlineData(false, ModelDirection.Opposed, AgreementFlag.Agree)]
    [InlineData(false, ModelDirection.Neutral, AgreementFlag.Partial)]
    public void ResolveAgreement_CoversTheFlagTable(bool eligible, ModelDirection direction, AgreementFlag expected)
    {
        Assert.Equal(expected, UniverseRankService.ResolveAgreement(eligible, direction));
    }

    public void Dispose()
    {
        if (Directory.Exists(dataRoot))
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    private static readonly IReadOnlySet<string> EmptySet =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static async Task<RankedDeskRow> RankSingleAsync(
        UniverseRankService service,
        UniverseRankConfig config,
        string horizon)
    {
        var run = await service.RankAsync(
            [EligibleRow("MU", spreadBps: 2m)],
            config,
            horizon,
            "unified",
            "auto",
            EmptySet,
            EmptySet,
            "wishlist:test",
            CancellationToken.None);
        return run.Rows[0];
    }

    private UniverseRankService CreateService(params string[] predictions) =>
        CreateService(new StubHandler(_ => JsonResponse(Payload(predictions))));

    private UniverseRankService CreateService(StubHandler handler)
    {
        var baseUri = new Uri("http://predictor.test/");
        var predictor = new MarketPredictorHttpClient(
            new HttpClient(handler) { BaseAddress = baseUri },
            new MarketPredictorOptions(baseUri, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(15)),
            new FixedTimeProvider(Now),
            NullLogger<MarketPredictorHttpClient>.Instance);
        return new UniverseRankService(
            predictor,
            new ProjectPaths(dataRoot, dataRoot, Path.Combine(dataRoot, "cache")),
            new FixedTimeProvider(Now),
            NullLogger<UniverseRankService>.Instance);
    }

    private static WishlistDeskRow EligibleRow(string ticker, decimal spreadBps) =>
        BuildRow(ticker, spreadBps, signal: new WishlistSignal
        {
            Id = Guid.NewGuid(),
            Ticker = ticker,
            SignalType = "breakout_reclaim",
            Severity = "actionable",
            DetectedAtUtc = Now.AddMinutes(-3),
            Price = 100m,
            Reason = "Completed bar closed above the reclaim level on elevated volume."
        });

    private static WishlistDeskRow WatchingRow(string ticker, decimal spreadBps) =>
        BuildRow(ticker, spreadBps, signal: null);

    private static WishlistDeskRow BuildRow(string ticker, decimal spreadBps, WishlistSignal? signal)
    {
        // Bid and ask are derived from the wanted spread so the liquidity factor
        // reads a real quote rather than a hand-set basis-point number.
        const decimal mid = 100m;
        var half = mid * spreadBps / 20_000m;
        var quote = new AlpacaLatestQuote(ticker, mid - half, mid + half, 500m, 500m, Now.AddSeconds(-2));
        return new WishlistDeskRow(
            new WishlistItem { Ticker = ticker, DisplayName = $"{ticker} Inc", Active = true },
            quote,
            null,
            signal,
            null);
    }

    private static string SignalFor(string ticker, string finalSignal) => $$"""
      {
        "ticker": "{{ticker}}",
        "final_signal": "{{finalSignal}}",
        "readiness_status": "valid",
        "errors": [],
        "swing": {
          "probability": 0.61, "decision_score": 0.22, "signal": "bullish_watch", "rank": 4,
          "return_1d": 0.01, "volume_z20": 1.2,
          "global_context": {"net_impact":0.1,"active_flashpoints":[]},
          "catalyst": {"status":"none","direction":"neutral","score":0.0,"event_count":0,"relevance":0.0,"minutes_since_latest":null,"reasons":[]},
          "readiness": {"status":"valid","reasons":[],"latest_price_date":"2026-08-06","price_feed":"sip","benchmark_status":"valid","market_context_status":"valid","model_status":"promoted","source_status":"valid"}
        },
        "intraday": {
          "opportunity_probability": 0.58, "downside_probability": 0.21, "decision_score": 0.19,
          "signal": "watch_for_confirmation", "rank": 8, "relative_volume": 1.8, "rsi_14": 58.0,
          "macd_signal_diff": 0.04, "entry_stop_pct": 0.01, "entry_target_pct": 0.03,
          "catalyst": {"status":"none","direction":"neutral","score":0.0,"event_count":0,"relevance":0.0,"minutes_since_latest":null,"reasons":[]},
          "readiness": {"status":"valid","reasons":[],"latest_price_date":"2026-08-06","price_feed":"sip","benchmark_status":"valid","market_context_status":"valid","model_status":"promoted","source_status":"valid"}
        }
      }
      """;

    private static string Payload(IReadOnlyList<string> predictions) => $$$"""
    {
      "request_id": "req-rank",
      "generated_at_utc": "{{{Now.AddSeconds(-5):O}}}",
      "mode": "unified",
      "horizon": "auto",
      "resolved_horizons": {"swing":"5d","intraday":"30m"},
      "models": {"swing":{"status":"promoted","model_type":"lightgbm","schema_version":"1","target":"return_5d"}},
      "predictions": [{{{String.Join(",", predictions)}}}],
      "errors": [],
      "snapshot_id": "snapshot-rank"
    }
    """;

    private static HttpResponseMessage JsonResponse(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
