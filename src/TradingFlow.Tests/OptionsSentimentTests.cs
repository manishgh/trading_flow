using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingFlow.Alpaca;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Storage;
using TradingFlow.Engine.Strategies;
using TradingFlow.Web;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

/// <summary>
/// Offline deterministic verification for Alpaca Paid OPRA Options Sentiment Context (20 Nov 2026 design):
/// OCC parsing, quote liquidity filtering, front straddle implied move, 25-delta put-call skew, IV term structure,
/// Alpaca OPRA pagination/caching/archiving, and refusal of removed skew vetoes on all existing strategies.
/// </summary>
public sealed class OptionsSentimentTests
{
    private static readonly DateTimeOffset ObservedUtc = new(2026, 11, 20, 15, 30, 0, TimeSpan.Zero); // Fri 10:30 NY
    private static readonly DateOnly ObservedNy = new(2026, 11, 20);

    [Fact]
    public void TryParseOccSymbol_ParsesValidCallAndPutSymbolsAndRejectsMalformed()
    {
        Assert.True(OptionsSentimentCalculator.TryParseOccSymbol("SPY261218C00600000", out var spyCall));
        Assert.Equal("SPY", spyCall.UnderlyingSymbol);
        Assert.Equal(new DateOnly(2026, 12, 18), spyCall.ExpirationDate);
        Assert.True(spyCall.IsCall);
        Assert.Equal(600m, spyCall.Strike);

        Assert.True(OptionsSentimentCalculator.TryParseOccSymbol("  nvda261127p00142500 ", out var nvdaPut));
        Assert.Equal("NVDA", nvdaPut.UnderlyingSymbol);
        Assert.Equal(new DateOnly(2026, 11, 27), nvdaPut.ExpirationDate);
        Assert.False(nvdaPut.IsCall);
        Assert.Equal(142.50m, nvdaPut.Strike);

        // Share-class punctuation (BRK.B / BF-B) parses cleanly and normalizes identically to BRKB / BFB.
        Assert.True(OptionsSentimentCalculator.TryParseOccSymbol("BRK.B261218C00450000", out var brkDotCall));
        Assert.Equal("BRK.B", brkDotCall.UnderlyingSymbol);
        Assert.Equal(450m, brkDotCall.Strike);
        Assert.Equal(
            OptionsSentimentCalculator.NormalizeOccRoot("BRK.B"),
            OptionsSentimentCalculator.NormalizeOccRoot("BRKB"));
        Assert.Equal(
            OptionsSentimentCalculator.NormalizeOccRoot("BF-B"),
            OptionsSentimentCalculator.NormalizeOccRoot("BFB"));

        Assert.False(OptionsSentimentCalculator.TryParseOccSymbol(null, out _));
        Assert.False(OptionsSentimentCalculator.TryParseOccSymbol("", out _));
        Assert.False(OptionsSentimentCalculator.TryParseOccSymbol("NVDA1261218C00150000", out _)); // Adjusted deliverable digit in root
        Assert.False(OptionsSentimentCalculator.TryParseOccSymbol("SPY260230C00600000", out _)); // Invalid Feb 30
        Assert.False(OptionsSentimentCalculator.TryParseOccSymbol("SPY261218X00600000", out _)); // Invalid type
        Assert.False(OptionsSentimentCalculator.TryParseOccSymbol("SPY261218C00000000", out _)); // Zero strike
        Assert.False(OptionsSentimentCalculator.TryParseOccSymbol("NOT_AN_OCC", out _));
    }

    [Fact]
    public void Calculator_FiltersWideSpreadsAndOutOfRangeContracts_AndComputesFrontStraddleAndAdrRatio()
    {
        var weeklyExpiry = ObservedNy.AddDays(7); // Fri 2026-11-27 -> 5 weekdays
        var quotes = new[]
        {
            // Liquid ATM weekly straddle at strike 100: call mid 2.00, put mid 2.00 -> straddle 4.00 / 100 = 4.0% implied move.
            Quote("AAA", weeklyExpiry, isCall: true, strike: 100m, bid: 1.90m, ask: 2.10m, iv: 0.30m, delta: 0.51m, volume: 600m),
            Quote("AAA", weeklyExpiry, isCall: false, strike: 100m, bid: 1.90m, ask: 2.10m, iv: 0.32m, delta: -0.49m, volume: 400m),
            // Wide bid-ask spread: bid 1.00, ask 2.00 -> mid 1.50, spread/mid = 66.7% (> 35%) -> MUST be rejected.
            Quote("AAA", weeklyExpiry, isCall: true, strike: 105m, bid: 1.00m, ask: 2.00m, iv: 0.40m, delta: 0.25m, volume: 5_000m),
            // Zero bid -> MUST be rejected.
            Quote("AAA", weeklyExpiry, isCall: false, strike: 95m, bid: 0m, ask: 0.50m, iv: 0.40m, delta: -0.25m, volume: 5_000m),
            // Outside [0.80 * 100, 1.20 * 100] moneyness -> MUST be rejected.
            Quote("AAA", weeklyExpiry, isCall: true, strike: 125m, bid: 0.50m, ask: 0.55m, iv: 0.25m, delta: 0.10m, volume: 5_000m),
            // Too short DTE (1 day < 2) -> MUST be rejected.
            Quote("AAA", ObservedNy.AddDays(1), isCall: true, strike: 100m, bid: 1.00m, ask: 1.05m, iv: 0.80m, delta: 0.50m, volume: 5_000m),
            // Too long DTE (70 days > 60) -> MUST be rejected.
            Quote("AAA", ObservedNy.AddDays(70), isCall: true, strike: 100m, bid: 5.00m, ask: 5.10m, iv: 0.20m, delta: 0.50m, volume: 5_000m),
            // Foreign underlying symbol mixed into quote list -> MUST be rejected.
            Quote("BBB", weeklyExpiry, isCall: true, strike: 100m, bid: 1.90m, ask: 2.10m, iv: 0.90m, delta: 0.50m, volume: 9_000m)
        };

        var snapshot = OptionsSentimentCalculator.Calculate("AAA", ObservedUtc, 100m, quotes, adr20Pct: 2.0m);

        Assert.Equal("AAA", snapshot.Symbol);
        Assert.Equal(2, snapshot.LiquidContractCount);
        Assert.Equal(weeklyExpiry, snapshot.FrontExpiry);
        Assert.Equal(5, snapshot.TradingDaysToFrontExpiry);
        Assert.Equal(4.0m, snapshot.FrontImpliedMovePct);
        Assert.Equal(31.0m, snapshot.AtmImpliedVolPct);
        Assert.Equal(600m, snapshot.CallVolume);
        Assert.Equal(400m, snapshot.PutVolume);
        Assert.Equal(1.5m, snapshot.CallPutVolumeRatio);
        // Daily implied move = 4.0 / sqrt(5) ~= 1.788854; divided by ADR 2.0% ~= 0.8944.
        Assert.NotNull(snapshot.ImpliedToRealizedAdrRatio);
        Assert.InRange(snapshot.ImpliedToRealizedAdrRatio!.Value, 0.8940m, 0.8950m);
        Assert.Equal(OptionsSentimentRegime.Neutral, snapshot.RegimeLabel);
    }

    [Fact]
    public void Calculator_Computes25DeltaSkew_AndClassifiesPutHedgingVsCallSqueeze()
    {
        var monthlyExpiry = ObservedNy.AddDays(28); // Inside 20-45 DTE window.

        // Heavy downside put hedging: Put 25d IV (0.34) - Call 25d IV (0.24) = +0.10 (> +0.06).
        var putHedgingQuotes = new[]
        {
            Quote("SPY", monthlyExpiry, isCall: true, strike: 100m, bid: 3.00m, ask: 3.10m, iv: 0.25m, delta: 0.50m, volume: 100m),
            Quote("SPY", monthlyExpiry, isCall: false, strike: 100m, bid: 2.95m, ask: 3.05m, iv: 0.26m, delta: -0.50m, volume: 120m),
            Quote("SPY", monthlyExpiry, isCall: true, strike: 108m, bid: 1.10m, ask: 1.20m, iv: 0.24m, delta: 0.24m, volume: 300m),
            Quote("SPY", monthlyExpiry, isCall: false, strike: 92m, bid: 1.40m, ask: 1.50m, iv: 0.34m, delta: -0.26m, volume: 900m)
        };
        var hedging = OptionsSentimentCalculator.Calculate("SPY", ObservedUtc, 100m, putHedgingQuotes);
        Assert.Equal(0.10m, hedging.Skew25Delta);
        Assert.Equal(OptionsSentimentRegime.PutSkewHedging, hedging.RegimeLabel);
        Assert.Contains("25Δ skew +10.0 vol pts", hedging.SummaryNote, StringComparison.Ordinal);

        // Aggressive call demand (squeeze risk): Put 25d IV (0.25) - Call 25d IV (0.33) = -0.08 (< -0.04).
        var callSqueezeQuotes = new[]
        {
            Quote("MEME", monthlyExpiry, isCall: true, strike: 100m, bid: 4.00m, ask: 4.20m, iv: 0.30m, delta: 0.50m, volume: 1_000m),
            Quote("MEME", monthlyExpiry, isCall: false, strike: 100m, bid: 3.90m, ask: 4.10m, iv: 0.29m, delta: -0.50m, volume: 400m),
            Quote("MEME", monthlyExpiry, isCall: true, strike: 110m, bid: 1.80m, ask: 1.95m, iv: 0.33m, delta: 0.25m, volume: 2_500m),
            Quote("MEME", monthlyExpiry, isCall: false, strike: 90m, bid: 1.10m, ask: 1.20m, iv: 0.25m, delta: -0.25m, volume: 500m)
        };
        var squeeze = OptionsSentimentCalculator.Calculate("MEME", ObservedUtc, 100m, callSqueezeQuotes);
        Assert.Equal(-0.08m, squeeze.Skew25Delta);
        Assert.Equal(OptionsSentimentRegime.CallSkewSqueezeRisk, squeeze.RegimeLabel);
        Assert.Contains("short-squeeze", squeeze.SummaryNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Calculator_ComputesTermStructureSpread_AndClassifiesBackwardationStress()
    {
        var nearExpiry = ObservedNy.AddDays(10); // Inside 7-21 DTE window.
        var farExpiry = ObservedNy.AddDays(42);  // Inside 30-60 DTE window.

        var backwardationQuotes = new[]
        {
            Quote("QQQ", nearExpiry, isCall: true, strike: 100m, bid: 3.10m, ask: 3.20m, iv: 0.38m, delta: 0.51m, volume: 500m),
            Quote("QQQ", nearExpiry, isCall: false, strike: 100m, bid: 3.05m, ask: 3.15m, iv: 0.40m, delta: -0.49m, volume: 500m),
            Quote("QQQ", farExpiry, isCall: true, strike: 100m, bid: 4.50m, ask: 4.65m, iv: 0.26m, delta: 0.52m, volume: 300m),
            Quote("QQQ", farExpiry, isCall: false, strike: 100m, bid: 4.40m, ask: 4.55m, iv: 0.28m, delta: -0.48m, volume: 300m)
        };

        var stressed = OptionsSentimentCalculator.Calculate("QQQ", ObservedUtc, 100m, backwardationQuotes);
        // Near ATM IV = 0.39, Far ATM IV = 0.27 -> TermStructureSpread = +0.12 > 0.
        Assert.Equal(0.12m, stressed.TermStructureSpread);
        Assert.Equal(OptionsSentimentRegime.TermBackwardationStress, stressed.RegimeLabel);

        var contangoQuotes = new[]
        {
            Quote("QQQ", nearExpiry, isCall: true, strike: 100m, bid: 2.10m, ask: 2.20m, iv: 0.20m, delta: 0.51m, volume: 500m),
            Quote("QQQ", nearExpiry, isCall: false, strike: 100m, bid: 2.05m, ask: 2.15m, iv: 0.22m, delta: -0.49m, volume: 500m),
            Quote("QQQ", farExpiry, isCall: true, strike: 100m, bid: 4.50m, ask: 4.65m, iv: 0.26m, delta: 0.52m, volume: 300m),
            Quote("QQQ", farExpiry, isCall: false, strike: 100m, bid: 4.40m, ask: 4.55m, iv: 0.28m, delta: -0.48m, volume: 300m)
        };

        var normal = OptionsSentimentCalculator.Calculate("QQQ", ObservedUtc, 100m, contangoQuotes);
        Assert.Equal(-0.06m, normal.TermStructureSpread);
        Assert.Equal(OptionsSentimentRegime.Neutral, normal.RegimeLabel);
    }

    [Fact]
    public async Task AlpacaOptionsSnapshotClient_ParsesOpraPaginationCaches5MinutesArchivesAndNeverCachesFailures()
    {
        var clock = new MutableClock(ObservedUtc);
        var archiveDir = Path.Combine(Path.GetTempPath(), "tf-opra-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archiveDir);
        try
        {
            var rawWriter = new FileSystemRawArchiveWriter(new RawArchiveOptions(archiveDir));
            var logger = new RecordingLogger<AlpacaOptionsSnapshotClient>();
            var requests = new List<Uri>();

            const string stockSnapshotJson = """{"latestTrade":{"p":100.00}}""";
            // Page 1: near expiry (7 DTE: 2026-11-27) ATM straddle + next_page_token.
            const string page1Json = """
            {
              "snapshots": {
                "NVDA261127C00100000": {
                  "greeks": { "delta": 0.51, "gamma": 0.04, "theta": -0.12, "vega": 0.09 },
                  "impliedVolatility": 0.35,
                  "latestQuote": { "bp": 2.40, "ap": 2.60 },
                  "dailyBar": { "v": 1200 }
                },
                "NVDA261127P00100000": {
                  "greeks": { "delta": -0.49, "gamma": 0.04, "theta": -0.11, "vega": 0.09 },
                  "impliedVolatility": 0.37,
                  "latestQuote": { "bp": 2.40, "ap": 2.60 },
                  "dailyBar": { "v": 800 }
                }
              },
              "next_page_token": "PAGE2TOK"
            }
            """;
            // Page 2: 28 DTE (2026-12-18) 25-delta wings -> put IV 0.24, call IV 0.32 -> skew -0.08 (call_skew_squeeze_risk).
            const string page2Json = """
            {
              "snapshots": {
                "NVDA261218C00110000": {
                  "greeks": { "delta": 0.25, "gamma": 0.02, "theta": -0.05, "vega": 0.14 },
                  "impliedVolatility": 0.32,
                  "latestQuote": { "bp": 1.70, "ap": 1.80 },
                  "dailyBar": { "v": 2000 }
                },
                "NVDA261218P00090000": {
                  "greeks": { "delta": -0.25, "gamma": 0.02, "theta": -0.05, "vega": 0.14 },
                  "impliedVolatility": 0.24,
                  "latestQuote": { "bp": 1.20, "ap": 1.30 },
                  "dailyBar": { "v": 600 }
                }
              },
              "next_page_token": null
            }
            """;

            var responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>([
                // First attempt: stock snapshot + 503 failure on OPRA page 1 -> Unavailable, NOT cached.
                _ => JsonResponse(HttpStatusCode.OK, stockSnapshotJson),
                _ => JsonResponse(HttpStatusCode.ServiceUnavailable, """{"message":"upstream busy"}"""),
                // Second attempt: stock snapshot + page 1 + page 2 -> succeeds and caches for 5 minutes.
                _ => JsonResponse(HttpStatusCode.OK, stockSnapshotJson),
                _ => JsonResponse(HttpStatusCode.OK, page1Json),
                _ => JsonResponse(HttpStatusCode.OK, page2Json),
                // Third attempt after 5+ minutes: fresh fetch with explicit spotPriceHint (skips stock snapshot!).
                _ => JsonResponse(HttpStatusCode.OK, page1Json),
                _ => JsonResponse(HttpStatusCode.OK, page2Json)
            ]);

            var options = AlpacaOptions.Create(ProductionProfile.Paper) with { KeyId = "test-key", SecretKey = "test-secret" };
            using var client = new AlpacaOptionsSnapshotClient(
                () => new HttpClient(new ScriptedHttpHandler(req =>
                {
                    requests.Add(req.RequestUri!);
                    Assert.Equal("test-key", req.Headers.GetValues("APCA-API-KEY-ID").Single());
                    Assert.Equal("test-secret", req.Headers.GetValues("APCA-API-SECRET-KEY").Single());
                    return responses.Dequeue()(req);
                })),
                options,
                clock,
                rawWriter,
                logger);

            // 1. Provider 503 returns Unavailable, emits warning log, and is NOT cached.
            var failed = await client.GetSnapshotAsync("NVDA");
            Assert.Equal(OptionsSentimentRegime.Unavailable, failed.RegimeLabel);
            Assert.Equal(2, requests.Count);
            Assert.Contains("/v2/stocks/NVDA/snapshot?feed=sip", requests[0].PathAndQuery, StringComparison.Ordinal);
            Assert.Contains(logger.Warnings, w => w.Contains("503", StringComparison.Ordinal));

            // 2. Immediate retry succeeds across 2 pages and archives raw responses.
            var first = await client.GetSnapshotAsync("NVDA");
            Assert.Equal(OptionsSentimentRegime.CallSkewSqueezeRisk, first.RegimeLabel);
            Assert.Equal(100m, first.SpotPrice);
            Assert.Equal(5.0m, first.FrontImpliedMovePct);
            Assert.Equal(-0.08m, first.Skew25Delta);
            Assert.Equal(4, first.LiquidContractCount);
            Assert.Equal(5, requests.Count);
            Assert.Contains("feed=opra", requests[3].Query, StringComparison.Ordinal);
            Assert.Contains("strike_price_gte=80", requests[3].Query, StringComparison.Ordinal);
            Assert.Contains("strike_price_lte=120", requests[3].Query, StringComparison.Ordinal);
            Assert.Contains("page_token=PAGE2TOK", requests[4].Query, StringComparison.Ordinal);
            Assert.NotEmpty(Directory.EnumerateFiles(archiveDir, "*.json", SearchOption.AllDirectories));

            // 3. Within 5 minutes: served from cache without HTTP calls, while recalculating ImpliedToRealizedAdrRatio.
            clock.Advance(TimeSpan.FromMinutes(4));
            var cached = await client.GetSnapshotAsync("NVDA", adr20Pct: 2.5m);
            Assert.Equal(5, requests.Count);
            Assert.Equal(-0.08m, cached.Skew25Delta);
            Assert.NotNull(cached.ImpliedToRealizedAdrRatio);

            // 4. After 5 minutes: cache expires and explicit spotPriceHint skips stock snapshot call (only 2 OPRA calls).
            clock.Advance(TimeSpan.FromMinutes(2));
            var refreshed = await client.GetSnapshotAsync("NVDA", spotPriceHint: 100m);
            Assert.Equal(7, requests.Count);
            Assert.Equal(OptionsSentimentRegime.CallSkewSqueezeRisk, refreshed.RegimeLabel);

            // 5. Unconfigured credentials return Unavailable immediately with zero HTTP calls.
            var unconfiguredCalls = 0;
            using var unconfigured = new AlpacaOptionsSnapshotClient(
                () => new HttpClient(new ScriptedHttpHandler(_ => { unconfiguredCalls++; return JsonResponse(HttpStatusCode.OK, "{}"); })),
                AlpacaOptions.Create(ProductionProfile.Paper),
                clock);
            var unconfiguredSnap = await unconfigured.GetSnapshotAsync("SPY");
            Assert.Equal(OptionsSentimentRegime.Unavailable, unconfiguredSnap.RegimeLabel);
            Assert.Equal(0, unconfiguredCalls);
        }
        finally
        {
            Directory.Delete(archiveDir, recursive: true);
        }
    }

    /// <summary>
    /// The options-skew vetoes were removed because no backtest can test them. A strategy file that still sets one is refused
    /// rather than run without the rule its author expected.
    /// </summary>
    [Theory]
    [InlineData("max_long_put_skew: 0.06")]
    [InlineData("min_short_put_call_skew: -0.04")]
    public void StrategyFileWithARemovedSkewVeto_IsRefused(string setting)
    {
        var reader = new SimpleYamlReader();
        var yaml = RunConfigWriter.SerializeStrategyYaml(reader.ReadStrategy(
            Path.Combine(TestRepository.FindRoot(), "configs/backtest/strategies/bollinger-squeeze-breakout.bt-v1.yaml")));
        var file = Path.Combine(Path.GetTempPath(), $"tf-skew-removed-{Guid.NewGuid():N}.yaml");
        try
        {
            File.WriteAllText(file, yaml.Replace("confluence:", $"  {setting}\nconfluence:", StringComparison.Ordinal));
            var refused = Assert.Throws<InvalidOperationException>(() => reader.ReadStrategy(file));
            Assert.Contains(setting.Split(':')[0], refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task AlpacaOptionsSnapshotClient_DenseChainTruncation_RunsTargetedSkewAndTermStructureFollowUps()
    {
        // Simulates a dense SPY chain where primary pages 1-3 only cover 7-10 DTE and still return next_page_token.
        // Client must run targeted 20-45 DTE skew ([0.80, 1.20]) and tight 30-60 DTE ATM ([0.95, 1.05]) follow-ups.
        var clock = new MutableClock(ObservedUtc);
        var logger = new RecordingLogger<AlpacaOptionsSnapshotClient>();
        var requests = new List<Uri>();

        const string primaryPage1 = """
        {
          "snapshots": {
            "SPY261127C00600000": {
              "greeks": { "delta": 0.51 },
              "impliedVolatility": 0.28,
              "latestQuote": { "bp": 8.80, "ap": 9.20 },
              "dailyBar": { "v": 10000 }
            },
            "SPY261127P00600000": {
              "greeks": { "delta": -0.49 },
              "impliedVolatility": 0.30,
              "latestQuote": { "bp": 8.80, "ap": 9.20 },
              "dailyBar": { "v": 9000 }
            }
          },
          "next_page_token": "PRIM_P2"
        }
        """;
        const string primaryPage2 = """
        {
          "snapshots": {
            "SPY261130C00600000": {
              "greeks": { "delta": 0.51 },
              "impliedVolatility": 0.27,
              "latestQuote": { "bp": 9.80, "ap": 10.20 },
              "dailyBar": { "v": 5000 }
            }
          },
          "next_page_token": "PRIM_P3"
        }
        """;
        const string primaryPage3Truncated = """
        {
          "snapshots": {
            "SPY261130P00600000": {
              "greeks": { "delta": -0.49 },
              "impliedVolatility": 0.29,
              "latestQuote": { "bp": 9.80, "ap": 10.20 },
              "dailyBar": { "v": 5000 }
            }
          },
          "next_page_token": "PRIM_P4_TRUNCATED"
        }
        """;
        // Targeted 20-45 DTE follow-up returns 28 DTE (2026-12-18) 25-delta wings: Put IV 0.27 - Call IV 0.19 = +0.08.
        const string skewWindowPage1 = """
        {
          "snapshots": {
            "SPY261218C00630000": {
              "greeks": { "delta": 0.25 },
              "impliedVolatility": 0.19,
              "latestQuote": { "bp": 4.90, "ap": 5.10 },
              "dailyBar": { "v": 4000 }
            },
            "SPY261218P00570000": {
              "greeks": { "delta": -0.25 },
              "impliedVolatility": 0.27,
              "latestQuote": { "bp": 5.90, "ap": 6.10 },
              "dailyBar": { "v": 7000 }
            }
          },
          "next_page_token": null
        }
        """;
        // Targeted 30-60 DTE tight ATM follow-up returns 42 DTE (2027-01-01) ATM IV = 0.20 -> TermStructure = 0.29 - 0.20 = +0.09.
        const string termWindowPage1 = """
        {
          "snapshots": {
            "SPY270101C00600000": {
              "greeks": { "delta": 0.51 },
              "impliedVolatility": 0.19,
              "latestQuote": { "bp": 15.80, "ap": 16.20 },
              "dailyBar": { "v": 3000 }
            },
            "SPY270101P00600000": {
              "greeks": { "delta": -0.49 },
              "impliedVolatility": 0.21,
              "latestQuote": { "bp": 15.80, "ap": 16.20 },
              "dailyBar": { "v": 3000 }
            }
          },
          "next_page_token": null
        }
        """;

        var responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>([
            _ => JsonResponse(HttpStatusCode.OK, primaryPage1),
            _ => JsonResponse(HttpStatusCode.OK, primaryPage2),
            _ => JsonResponse(HttpStatusCode.OK, primaryPage3Truncated),
            _ => JsonResponse(HttpStatusCode.OK, skewWindowPage1),
            _ => JsonResponse(HttpStatusCode.OK, termWindowPage1)
        ]);

        var options = AlpacaOptions.Create(ProductionProfile.Paper) with { KeyId = "k", SecretKey = "s" };
        using var client = new AlpacaOptionsSnapshotClient(
            () => new HttpClient(new ScriptedHttpHandler(req =>
            {
                requests.Add(req.RequestUri!);
                return responses.Dequeue()(req);
            })),
            options,
            clock,
            logger: logger);

        var snapshot = await client.GetSnapshotAsync("SPY", spotPriceHint: 600m);

        Assert.Equal(5, requests.Count);
        // Request 4: 20-45 DTE skew window across [0.80, 1.20] = [480, 720].
        Assert.Contains("expiration_date_gte=2026-12-10", requests[3].Query, StringComparison.Ordinal);
        Assert.Contains("expiration_date_lte=2027-01-04", requests[3].Query, StringComparison.Ordinal);
        Assert.Contains("strike_price_gte=480", requests[3].Query, StringComparison.Ordinal);
        Assert.Contains("strike_price_lte=720", requests[3].Query, StringComparison.Ordinal);
        // Request 5: 30-60 DTE tight ATM window across [0.95, 1.05] = [570, 630].
        Assert.Contains("expiration_date_gte=2026-12-20", requests[4].Query, StringComparison.Ordinal);
        Assert.Contains("expiration_date_lte=2027-01-19", requests[4].Query, StringComparison.Ordinal);
        Assert.Contains("strike_price_gte=570", requests[4].Query, StringComparison.Ordinal);
        Assert.Contains("strike_price_lte=630", requests[4].Query, StringComparison.Ordinal);

        Assert.Equal(3.0m, snapshot.FrontImpliedMovePct);
        Assert.Equal(0.08m, snapshot.Skew25Delta);
        Assert.Equal(0.09m, snapshot.TermStructureSpread);
        Assert.Equal(OptionsSentimentRegime.TermBackwardationStress, snapshot.RegimeLabel);
    }

    [Fact]
    public void DeskOptionsEndpoints_ValidatesEquitySymbolsStrictly()
    {
        Assert.True(DeskOptionsEndpoints.IsValidEquitySymbol("SPY"));
        Assert.True(DeskOptionsEndpoints.IsValidEquitySymbol("BRK.B"));
        Assert.True(DeskOptionsEndpoints.IsValidEquitySymbol("BF-B"));
        Assert.False(DeskOptionsEndpoints.IsValidEquitySymbol(""));
        Assert.False(DeskOptionsEndpoints.IsValidEquitySymbol("TOO_LONG_TICKER_NAME"));
        Assert.False(DeskOptionsEndpoints.IsValidEquitySymbol("../SPY"));
        Assert.False(DeskOptionsEndpoints.IsValidEquitySymbol("SPY QQQ"));
    }

    [Fact]
    public void ExpectedMoveRemovesIntrinsicValueWhenTheNearestStrikeIsNotAtThePrice()
    {
        // Cheap stock at $6.27 with $1 strikes: the nearest strike is 6, so the 6-strike call is $0.27 in the money.
        // Call mid 0.50 + put mid 0.25 = 0.75 straddle; removing the 0.27 intrinsic leaves 0.48 of expected movement:
        // 0.48 / 6.27 = 7.6555% (the old figure, 0.75 / 6.27 = 11.96%, counted the in-the-money amount as movement).
        var weeklyExpiry = ObservedNy.AddDays(7);
        var quotes = new[]
        {
            Quote("CHEAP", weeklyExpiry, isCall: true, strike: 6m, bid: 0.48m, ask: 0.52m, iv: 0.90m, delta: 0.60m, volume: 100m),
            Quote("CHEAP", weeklyExpiry, isCall: false, strike: 6m, bid: 0.24m, ask: 0.26m, iv: 0.95m, delta: -0.40m, volume: 100m)
        };

        var snapshot = OptionsSentimentCalculator.Calculate("CHEAP", ObservedUtc, 6.27m, quotes);

        Assert.Equal(Math.Round(0.48m / 6.27m * 100m, 4), snapshot.FrontImpliedMovePct);
    }

    [Fact]
    public void HolidayAwareTradingDays_ExcludesThanksgivingAndScalesImpliedToAdrRatioAndPreservesSkewProvenance()
    {
        // Monday 2026-11-23 15:30 UTC (10:30 NY) -> Friday 2026-11-27 expiry:
        // Weekdays only (Tue/Wed/Thu/Fri) = 4, but Thursday 2026-11-26 is US Thanksgiving (market closed) => 3 trading days!
        var mondayUtc = new DateTimeOffset(2026, 11, 23, 15, 30, 0, TimeSpan.Zero);
        var fridayExpiry = new DateOnly(2026, 11, 27);
        var monthlyExpiry = new DateOnly(2026, 12, 18); // 25 calendar DTE (inside 20-45 DTE skew window)
        var thanksgiving = new DateOnly(2026, 11, 26);

        var schedules = new Dictionary<DateOnly, MarketSessionSchedule>();
        for (var d = new DateOnly(2026, 11, 20); d <= new DateOnly(2026, 12, 20); d = d.AddDays(1))
        {
            var isOpen = d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && d != thanksgiving;
            schedules[d] = isOpen ? MarketSessionSchedule.RegularDay(d) : MarketSessionSchedule.Closed(d);
        }

        // Front straddle: call mid 3.00 + put mid 3.00 = 6.00 / 100 = 6.0% implied move; ADR20 = 2.0%.
        // 25-delta wings on 2026-12-18: Put $92 (delta -0.26, IV 34.25%) vs Call $108 (delta +0.24, IV 24.15%).
        var quotes = new[]
        {
            Quote("SPY", fridayExpiry, isCall: true, strike: 100m, bid: 2.95m, ask: 3.05m, iv: 0.30m, delta: 0.50m, volume: 500m),
            Quote("SPY", fridayExpiry, isCall: false, strike: 100m, bid: 2.95m, ask: 3.05m, iv: 0.30m, delta: -0.50m, volume: 500m),
            Quote("SPY", monthlyExpiry, isCall: true, strike: 108m, bid: 1.15m, ask: 1.25m, iv: 0.2415m, delta: 0.2412m, volume: 400m),
            Quote("SPY", monthlyExpiry, isCall: false, strike: 92m, bid: 1.45m, ask: 1.55m, iv: 0.3425m, delta: -0.2587m, volume: 700m)
        };

        // 1. Without calendar: 4 weekdays -> ratio = (6.0 / sqrt(4)) / 2.0 = 1.5000.
        var weekdayOnly = OptionsSentimentCalculator.Calculate("SPY", mondayUtc, 100m, quotes, adr20Pct: 2.0m);
        Assert.Equal(4, weekdayOnly.TradingDaysToFrontExpiry);
        Assert.Equal(1.5000m, weekdayOnly.ImpliedToRealizedAdrRatio);

        // 2. Thanksgiving excluded: 3 trading days -> ratio = (6.0 / sqrt(3)) / 2.0 ~= 1.7321.
        var expectedRatio = Math.Round(6.0m / (decimal)Math.Sqrt(3) / 2.0m, 4);

        // 3. WithMarketContext recomputes holiday-accurate trading days + sqrt(3)-scaled ADR ratio on a cached snapshot.
        var cachedWithoutContext = OptionsSentimentCalculator.Calculate("SPY", mondayUtc, 100m, quotes);
        Assert.Equal(4, cachedWithoutContext.TradingDaysToFrontExpiry);
        Assert.Null(cachedWithoutContext.ImpliedToRealizedAdrRatio);

        var enriched = OptionsSentimentCalculator.WithMarketContext(cachedWithoutContext, adr20Pct: 2.0m, schedules: schedules);
        Assert.Equal(3, enriched.TradingDaysToFrontExpiry);
        Assert.Equal(expectedRatio, enriched.ImpliedToRealizedAdrRatio);
        Assert.Contains("(3d)", enriched.SummaryNote, StringComparison.Ordinal);

        // 4. Trailing holiday boundary: [2026-11-23..2026-11-26] where Thanksgiving 2026-11-26 is closed must return 2 (Tue/Wed), NOT 3!
        var mondayNy = new DateOnly(2026, 11, 23);
        Assert.Equal(2, OptionsSentimentCalculator.CountTradingDaysExclusiveInclusive(mondayNy, thanksgiving, schedules));

        // 5. Non-holiday week: when WithMarketContext changes only ImpliedToRealizedAdrRatio (tradingDays unchanged 4 -> 4),
        //    SummaryNote must still be refreshed to include the updated implied/ADR ratio.
        var nonHolidaySchedules = schedules.ToDictionary(
            pair => pair.Key,
            pair => pair.Key == thanksgiving ? MarketSessionSchedule.RegularDay(pair.Key) : pair.Value);
        var normalWeekEnriched = OptionsSentimentCalculator.WithMarketContext(cachedWithoutContext, adr20Pct: 2.0m, schedules: nonHolidaySchedules);
        Assert.Equal(4, normalWeekEnriched.TradingDaysToFrontExpiry);
        Assert.Equal(1.5000m, normalWeekEnriched.ImpliedToRealizedAdrRatio);
        Assert.Contains("implied/ADR 1.50x", normalWeekEnriched.SummaryNote, StringComparison.Ordinal);

        // 6. SkewProvenance carries exact Put/Call strikes, deltas (3 dp), IV% (2 dp), and expiry, preserved across enrichment.
        foreach (var snap in new[] { weekdayOnly, OptionsSentimentCalculator.WithAdr20Pct(cachedWithoutContext, 2.0m), enriched, normalWeekEnriched })
        {
            Assert.NotNull(snap.SkewProvenance);
            Assert.Equal(monthlyExpiry, snap.SkewProvenance!.ExpirationDate);
            Assert.Equal(92m, snap.SkewProvenance.PutStrike);
            Assert.Equal(-0.259m, snap.SkewProvenance.PutDelta);
            Assert.Equal(34.25m, snap.SkewProvenance.PutIvPct);
            Assert.Equal(108m, snap.SkewProvenance.CallStrike);
            Assert.Equal(0.241m, snap.SkewProvenance.CallDelta);
            Assert.Equal(24.15m, snap.SkewProvenance.CallIvPct);
        }
    }

    // Helper constructing a synthetic OPRA OptionContractQuote with standard OCC formatting.
    private static OptionContractQuote Quote(
        string underlying,
        DateOnly expiry,
        bool isCall,
        decimal strike,
        decimal bid,
        decimal ask,
        decimal? iv,
        decimal? delta,
        decimal volume)
    {
        var occ = $"{underlying}{expiry:yyMMdd}{(isCall ? 'C' : 'P')}{(long)(strike * 1000m):D8}";
        return new OptionContractQuote(occ, underlying, expiry, isCall, strike, bid, ask, iv, delta, 0.02m, -0.05m, 0.10m, volume);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class ScriptedHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }

    private sealed class MutableClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset current = initial;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan delta) => current = current.Add(delta);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
