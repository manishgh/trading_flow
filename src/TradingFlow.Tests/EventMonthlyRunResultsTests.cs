using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Backtesting;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Risk;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class EventMonthlyRunResultsTests
{
    /// <summary>Actual fills and fees reconcile while open holdings use observed closes and each month uses its own opening equity.</summary>
    [Fact]
    public async Task VerifiedFills_ValueOpenHoldingsAndUseMonthlyDenominators()
    {
        using var fixture = await Fixture.CreateAsync();
        var report = fixture.Report();
        Assert.Null(report.UnavailableReason);
        Assert.Equal(2, report.Results.Count);
        var april = report.Results[0];
        var may = report.Results[1];
        Assert.Equal("2026-04", april.Month);
        Assert.Equal(1000m, april.OpeningEquity);
        Assert.Equal(1100m, april.ClosingEquity);
        Assert.Equal(10m, april.ReturnPct);
        Assert.Equal(0, april.ClosedTrades);
        Assert.Equal(1100m, may.OpeningEquity);
        Assert.Equal(990m, may.ClosingEquity);
        Assert.Equal(-10m, may.ReturnPct);
        Assert.Equal(1, may.ClosedTrades);
        Assert.All(report.Results, row => {
            Assert.Equal(fixture.Identity, row.Strategy);
            Assert.Equal("Volatile group 1", row.CohortName);
            Assert.Equal(EventMonthlyRunResults.ValuationBasis, row.ValuationBasis);
            Assert.True(row.PartialMonth);
        });
    }

    /// <summary>Daily closes, adjusted closes, another feed and an unaligned interval never replace an absent raw SIP session close.</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("daily")]
    [InlineData("adjusted")]
    [InlineData("other-feed")]
    [InlineData("unaligned")]
    public async Task MissingQualifyingClose_WithholdsAffectedMonthOnly(string mode)
    {
        using var fixture = await Fixture.CreateAsync();
        var bar = fixture.Request.MarketBars.Last();
        var replacement = mode switch {
            "daily" => bar with { Timeframe = "1d" },
            "adjusted" => bar with { AdjustmentPolicy = "split" },
            "other-feed" => bar with { DataFeed = "iex" },
            "unaligned" => bar with { Timestamp = bar.Timestamp.AddMinutes(-5) },
            _ => null
        };
        fixture.Request = fixture.Request with { MarketBars = replacement is null
            ? fixture.Request.MarketBars.Take(1).ToArray() : [fixture.Request.MarketBars.First(), replacement] };
        await fixture.WriteRequestAsync();
        var report = fixture.Report();
        Assert.Equal(10m, report.Results[0].ReturnPct);
        Assert.Null(report.Results[1].ReturnPct);
        Assert.Contains("MSFT", report.Results[1].UnavailableReason);
    }

    /// <summary>Changing any published source, calendar, request, outcome or audit byte invalidates its published hash.</summary>
    [Theory]
    [InlineData("event_source_manifest")]
    [InlineData("event_portfolio_request")]
    [InlineData("event_portfolio_outcome")]
    [InlineData("event_portfolio_source_audit")]
    [InlineData("event_exchange_calendar_receipt")]
    [InlineData("event_exchange_calendar")]
    public async Task ChangedPublishedEvidence_WithholdsAllMonths(string kind)
    {
        using var fixture = await Fixture.CreateAsync();
        var reference = fixture.Result.ArtifactReferences!.Single(item => item.Kind == kind);
        await File.AppendAllTextAsync(Path.Combine(fixture.Root, reference.Path), " ");
        var report = fixture.Report();
        Assert.Contains("hash or length", report.UnavailableReason);
        Assert.All(report.Results, row => Assert.Null(row.ReturnPct));
    }

    /// <summary>A structurally valid audit must prove complete physical consumption before any month is published.</summary>
    [Fact]
    public async Task IncompleteSourceAudit_WithholdsAllMonths()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.WriteAsync("event_portfolio_source_audit", ".event-portfolio-source-audit.json",
            fixture.Audit with { FullyConsumedAndVerified = false });
        var report = fixture.Report();
        Assert.Contains("source audit is incomplete", report.UnavailableReason);
        Assert.All(report.Results, row => Assert.Null(row.ReturnPct));
    }

    /// <summary>Matching artifact hashes do not excuse a closed-trade fee or quantity disagreement with its actual fills.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LedgerMismatch_WithholdsAllMonths(bool changeFees)
    {
        using var fixture = await Fixture.CreateAsync();
        var trade = fixture.Outcome.Trades[0];
        fixture.Outcome = fixture.Outcome with { Trades = [changeFees ? trade with { Fees = 3 } : trade with { ShareQuantity = 11 }] };
        await fixture.WriteOutcomeAsync();
        var report = fixture.Report();
        Assert.Contains("do not reconcile", report.UnavailableReason);
        Assert.All(report.Results, row => Assert.Null(row.ReturnPct));
    }

    /// <summary>An unknown interval preserves earlier completed months and withholds its entire month without treating open exposure as flat.</summary>
    [Theory]
    [InlineData("2026-05-02T14:00:00Z", true)]
    [InlineData("2026-04-30T20:00:00Z", false)]
    public async Task UnknownExecutionBoundary_WithholdsAffectedAndLaterMonths(string boundary, bool aprilAvailable)
    {
        using var fixture = await Fixture.CreateAsync();
        var buy = fixture.Outcome.Events[0];
        var candidate = fixture.Request.Candidates[0];
        var failure = new BacktestExecutionFailure(candidate.CandidateId, "MSFT", candidate.StrategyName,
            candidate.StrategyId, "long", buy.Event.OccurredAtUtc, "Unknown physical interval", 10, 10, 0, 10, 100, 0, 1,
            null, null, EvidenceUnavailable: true);
        fixture.Outcome = fixture.Outcome with {
            Trades = [], Events = [buy], Failures = [failure],
            UnavailableFromNanoseconds = SipNanosecondTime.Parse(boundary), UnavailableReasons = ["Unknown physical interval"]
        };
        fixture.Result = fixture.Result with { EconomicResultsComplete = false, CompletedTrades = [], AcceptedTradeCount = 0 };
        await fixture.WriteOutcomeAsync();
        var report = fixture.Report();
        if (aprilAvailable) Assert.Equal(10m, report.Results[0].ReturnPct);
        else Assert.Null(report.Results[0].ReturnPct);
        Assert.Null(report.Results[1].ReturnPct);
        Assert.Contains("Unknown physical interval", report.Results[1].UnavailableReason);
    }

    /// <summary>An intraday requested end cannot publish a trade count while its exit and fees are absent from the last session-close equity.</summary>
    [Fact]
    public async Task IntradayEnd_AfterExitBeforeCloseWithholdsFinalMonthAndPreservesEarlierMonth()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.EndAt(DateTimeOffset.Parse("2026-05-29T15:30:00Z"));
        var report = fixture.Report();
        Assert.Equal(10m, report.Results[0].ReturnPct);
        Assert.Equal(1100m, report.Results[0].ClosingEquity);
        var finalMonth = report.Results[1];
        Assert.Null(finalMonth.OpeningEquity);
        Assert.Null(finalMonth.ClosingEquity);
        Assert.Null(finalMonth.ReturnPct);
        Assert.Null(finalMonth.ClosedTrades);
        Assert.Contains("final fills and fees are not covered", finalMonth.UnavailableReason);
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly List<BacktestArtifactReference> references = [];
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tradingflow-event-monthly-" + Guid.NewGuid().ToString("N"));
        public MonthlyRunResultWriter Writer { get; private set; } = null!;
        public StrategyReference Identity { get; private set; } = null!;
        public BacktestRunConfig Config { get; private set; } = null!;
        public BacktestResult Result { get; set; } = null!;
        public EventPortfolioReplayRequest Request { get; set; } = null!;
        public EventPortfolioReplayOutcome Outcome { get; set; } = null!;
        public SipPortfolioSourceAudit Audit { get; private set; } = null!;
        private ExecutionEventSimulationPolicy policy = null!;

        /// <summary>Builds six isolated frozen evidence artifacts without provider access or raw-archive rereads.</summary>
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture.Root);
            try { await fixture.InitializeAsync(); return fixture; }
            catch { fixture.Dispose(); throw; }
        }

        /// <summary>Exercises the production writer, including its exact runtime authorization and evidence-unavailable handling.</summary>
        public MonthlyRunResultReport Report() => Writer.Create(Config, Result, Config.TimeWindow.End!.Value, "Volatile group 1", [Identity]);

        /// <summary>Changes only the test-owned requested period to exercise an end between an actual fill and the next session close.</summary>
        public void EndAt(DateTimeOffset end)
        {
            Config = Config with { TimeWindow = Config.TimeWindow with { End = end } };
            Result = Result with { FinishedAt = end };
        }

        /// <summary>Captures a coherent open-April/closed-May ledger whose net loss includes both fixed fees.</summary>
        private async Task InitializeAsync()
        {
            var original = MonthlyRunResultWriterTests.CreateFixture();
            Writer = original.Writer; Identity = original.Identity;
            var reader = new SimpleYamlReader();
            var repo = TestRepository.FindRoot();
            var catalog = new StrategyArtifactCatalog(repo, Path.Combine(repo, "configs", "strategy-catalog.json"), reader);
            var runtime = new StrategyRunArtifactValidator(reader, catalog).ReadAndValidateRuntimeArtifact(original.Config.Strategies[0], StrategySelectionMode.Backtest);
            var start = DateTimeOffset.Parse("2026-04-30T04:00:00Z");
            var end = DateTimeOffset.Parse("2026-05-30T04:00:00Z");
            var buy = DateTimeOffset.Parse("2026-04-30T14:00:00Z");
            var sell = DateTimeOffset.Parse("2026-05-29T15:00:00Z");
            Config = original.Config with { RunName = "event-monthly", Tickers = ["MSFT"],
                Portfolio = original.Config.Portfolio with { StartingCapital = 1000, FixedBuyFee = 1, FixedSellFee = 1 },
                TimeWindow = new("fixed", 0, start, end),
                Engine = original.Config.Engine with { ExecutionReplayPolicyId = ExecutionReplayPolicy.DailyRthSipPortfolioV1 } };
            var candidate = new BacktestCandidateTrade("MSFT", runtime.Definition.StrategyName, "long", buy.AddMinutes(-30),
                10, 8, 30, sell, 9.2m, "technical_exit", 2, StrategyId: runtime.Definition.StrategyId, CandidateId: "frozen-MSFT");
            Request = new(Config.RunName, Config.Portfolio, [runtime.Definition], [candidate],
                [Mark("2026-04-30T19:55:00Z", 20.1m), Mark("2026-05-01T19:55:00Z", 18)], new Dictionary<string, string>());
            var trade = new BacktestTrade("MSFT", candidate.StrategyName, "long", buy, sell, 10, 10, 9.2m, 8, 30,
                "technical_exit", -8, 2, -10, StrategyId: candidate.StrategyId, CandidateId: candidate.CandidateId);
            Outcome = new([trade], [], [], [Fill(candidate.CandidateId + ":entry", buy, true, 10), Fill(candidate.CandidateId + ":liquidation", sell, false, 9.2m)], null, []);
            Result = original.Result with { RunName = Config.RunName, ResultPath = Path.Combine(Root, "result.json"),
                StartedAt = start, FinishedAt = end, CandidateTradeCount = 1, AcceptedTradeCount = 1, CompletedTrades = [trade],
                TickerResults = [TickerBacktestResult.Completed("MSFT", 2, 1)],
                StrategyResults = [original.Result.StrategyResults[0] with { CandidateTradeCount = 1, AcceptedTradeCount = 1, CompletedTrades = [trade] }] };
            var portfolio = Config.Portfolio;
            policy = new(new(portfolio.MaxBarParticipationPct, runtime.Definition.Execution.SlippageBps, 0,
                portfolio.ImpactBpsAtMaxParticipation, portfolio.FixedBuyFee, portfolio.FixedSellFee, portfolio.SecFeeRate,
                portfolio.FinraTafPerShare, portfolio.FinraTafCap, MaximumTrackedOrderContracts: portfolio.MaxExecutionOrderHistory),
                ExecutionProtectionActivation.NativeParentFullyFilled, 30_000_000_000L, 0, true, 1, 1, "fixture-only");
            var dates = new[] { "2026-04-30", "2026-05-01", "2026-05-29" };
            var payload = dates.Select(date => new { date, open = "09:30", close = "16:00" }).ToArray();
            await WriteAsync("event_exchange_calendar", ".event-calendar.json", payload, dates.Length);
            var receipt = JsonSerializer.SerializeToElement(new { HttpStatus = 200, Sha256 = references.Single(item => item.Kind == "event_exchange_calendar").Sha256,
                RowCount = dates.Length, Start = dates[0], End = dates[^1], CapturedUtc = DateTimeOffset.Parse("2026-10-21T12:00:00Z"), OriginalHistoricalReceiptClaim = false });
            await WriteAsync("event_exchange_calendar_receipt", ".event-calendar.receipt.json", receipt);
            var sessions = dates.Select(date => Session(date)).ToArray();
            var sourcePolicy = SipPortfolioExecutionSourceTests.Policy();
            var manifest = new EventPortfolioSourceManifest(1, sourcePolicy, sessions,
                new(policy.ProtectionActivation, policy.MaximumQuoteAgeNanoseconds, 0, true, 1, 1, policy.EvidenceVersion),
                Path.ChangeExtension(Result.ResultPath, ".event-calendar.receipt.json"), references.Single(item => item.Kind == "event_exchange_calendar_receipt").Sha256);
            await WriteAsync("event_source_manifest", ".event-source-manifest.json", manifest);
            Config = Config with { Engine = Config.Engine with { ExecutionSourceManifestPath = Path.ChangeExtension(Result.ResultPath, ".event-source-manifest.json"),
                ExecutionSourceManifestSha256 = references.Single(item => item.Kind == "event_source_manifest").Sha256 } };
            Audit = new(SipPortfolioExecutionSource.Version, SipPortfolioExecutionSource.ComputePolicySha256(sourcePolicy),
                SipPortfolioExecutionSource.ComputeConditionContractSha256(sourcePolicy.Conditions), 3, 3, 6, 3, 0, 0,
                sessions.Select(session => new SipPortfolioVerifiedSession(session.SessionKey, session.RegularQuotesRequest,
                    session.PrefixQuotesRequest, Binding(session.PrefixQuotesDirectory, session.PrefixQuotesManifestSha256),
                    Binding(session.QuotesDirectory, session.QuotesManifestSha256), Binding(session.TradesDirectory, session.TradesManifestSha256))).ToImmutableArray(), true);
            await WriteAsync("event_portfolio_source_audit", ".event-portfolio-source-audit.json", Audit);
            await WriteRequestAsync();
            await WriteOutcomeAsync();
        }

        /// <summary>Freezes the request wrapper used by the normal portfolio source binding.</summary>
        public Task WriteRequestAsync() => WriteAsync("event_portfolio_request", ".event-portfolio-request.json",
            new { SchemaVersion = 1, SourceManifestSha256 = Config.Engine.ExecutionSourceManifestSha256, Request, Policy = policy });

        /// <summary>Changes outcome evidence only when a test intentionally publishes a newly hashed fixture.</summary>
        public Task WriteOutcomeAsync() => WriteAsync("event_portfolio_outcome", ".event-portfolio-outcome.json", Outcome);

        /// <summary>Writes exact bytes and refreshes only their test-owned published reference.</summary>
        public async Task WriteAsync<T>(string kind, string extension, T value, int count = 1)
        {
            var path = Path.ChangeExtension(Result.ResultPath, extension);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            await File.WriteAllBytesAsync(path, bytes);
            references.RemoveAll(item => item.Kind == kind);
            references.Add(new(1, kind, Path.GetFileName(path), count, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), true));
            Result = Result with { ArtifactReferences = references.ToArray() };
        }

        /// <summary>Records one explicit session descriptor with fixture-only physical archive identities.</summary>
        private SipPortfolioSessionArchive Session(string date)
        {
            var regular = new SipArchiveRequest("MSFT", "quotes", date + "T13:30:00Z", date + "T20:00:00Z", "2026-10-21");
            var prefix = regular with { StartInclusive = date + "T13:29:00Z", EndExclusive = date + "T13:30:00Z" };
            return new("MSFT-" + date, regular, Path.Combine(Root, date, "quotes"), new string('a', 64),
                Path.Combine(Root, date, "trades"), new string('b', 64), prefix, Path.Combine(Root, date, "prefix"), new string('c', 64));
        }

        /// <summary>Supplies already verified audit identity; the projector must never reopen fixture archive directories.</summary>
        private static SipPortfolioArchiveBinding Binding(string path, string sha) => new(path, sha, new string('d', 64), 1,
            DateTimeOffset.Parse("2026-10-21T12:00:00Z"), DateTimeOffset.Parse("2026-10-21T12:00:00Z"));

        /// <summary>Creates one observed raw SIP five-minute close without a daily-close fallback.</summary>
        private static OhlcvBar Mark(string at, decimal price) => new("MSFT", DateTimeOffset.Parse(at), "5m", price, price, price, price, 100, "sip", "raw");

        /// <summary>Encodes actual buy or sell execution with its source and supported zero-delay known timestamp.</summary>
        private static TimedSimulatedExecutionEvent Fill(string id, DateTimeOffset at, bool buy, decimal price)
        {
            var ns = checked((at.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100);
            return new(new(id, "MSFT", "filled", 10, 10, 0, price, 1, 0, 0, 0, buy ? 10 : 0, buy ? 0 : -8, at,
                "fixture", buy ? SimulatedOrderSide.Buy : SimulatedOrderSide.Sell, !buy), ns, ns);
        }

        /// <summary>Deletes only this fixture's resolved temporary directory after every test, including failed initialization.</summary>
        public void Dispose()
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(Path.GetFullPath(Root)), ignoreCase: true);
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
