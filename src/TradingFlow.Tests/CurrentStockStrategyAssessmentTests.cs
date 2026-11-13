using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using TradingFlow.Application.Candidates;
using TradingFlow.Application.Jobs;
using TradingFlow.Application.Preparation;
using TradingFlow.Backtesting;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Jobs;
using TradingFlow.Data.Wishlists;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;
using TradingFlow.Engine.Strategies;
using TradingFlow.Engine.Regime;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Desk;
using TradingFlow.Web.Services.Wishlists;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingFlow.Contracts.Transport;
using TradingFlow.Web;

namespace TradingFlow.Tests;

public sealed class CurrentStockStrategyAssessmentTests
{
    /// <summary>Real durable work, genuine persisted discovery and the shared kernel produce checks without orders or new wishlists.</summary>
    [Fact]
    public async Task DurableAssessment_PreservesKernelChecksAndOperatorData()
    {
        await using var h = await Harness.CreateAsync();
        var request = h.Request("first", ["RGTI", "MSFT"]);
        var accepted = await h.Service.StartAsync("operator", request, default);
        Assert.Equal("queued", accepted.Status);
        Assert.Equal(accepted.JobId, (await h.Service.StartAsync("operator", request, default)).JobId);
        await h.ProcessAsync();
        var completed = await h.Service.ReadAsync("operator", accepted.JobId, default);
        Assert.Equal("completed", completed.Status);
        var result = Assert.IsType<StockStrategyAssessmentResponse>(completed.Result);
        var stock = Assert.Single(result.Stocks, item => item.Symbol == "RGTI");
        Assert.Equal("evaluated", stock.State);
        Assert.Contains(stock.Checks, item => item.GateName == "strategy:market_state_not_warm" && !item.Passed);
        Assert.DoesNotContain(stock.Checks, item => item.GateName.Contains("generic", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("missing", Assert.Single(result.Stocks, item => item.Symbol == "MSFT").State);
        var candidate = (await h.Database.Candidates.GetAsync(stock.CandidateId!.Value))!;
        var transitions = await h.Database.Candidates.GetTransitionsAsync(candidate.CandidateId);
        Assert.NotEmpty(transitions);
        Assert.All(transitions, transition => Assert.Equal("strategy_decision_kernel", transition.Source));
        Assert.Equal(completed.ProfileSha256, candidate.ConfigHash);
        var audit = (await h.Database.Queries(h.Clock).GetAsync(candidate.CandidateId))!;
        Assert.Equal(audit.GateEvaluations.Where(item => item.GateName.StartsWith("strategy:")).Select(item => (item.GateName, item.Passed)),
            stock.Checks.Select(item => (item.GateName, item.Passed)));
        h.Clock.Now = h.Clock.Now.AddMinutes(4);
        Assert.True(Assert.Single((await h.Service.ReadAsync("operator", accepted.JobId, default)).Result!.Stocks,
            item => item.Symbol == "RGTI").Expired);
        await using var db = h.Database.CreateDbContext();
        Assert.Single(await db.Wishlists.ToArrayAsync());
        Assert.True((await db.Wishlists.SingleAsync()).IsObserved);
        Assert.Empty(await db.WishlistSignals.ToArrayAsync());
        Assert.Empty(await db.OrderIntents.ToArrayAsync());
    }

    /// <summary>An incomplete news query or missing benchmark cannot become an eligible strategy candidate.</summary>
    [Fact]
    public async Task MissingEvidence_DoesNotCreateCandidateOrPassChecks()
    {
        await using var h = await Harness.CreateAsync();
        h.Evidence.Missing = "Alpaca news returned HTTP 401.";
        var accepted = await h.Service.StartAsync("operator", h.Request("missing", ["RGTI"]), default);
        await h.ProcessAsync();
        var completed = await h.Service.ReadAsync("operator", accepted.JobId, default);
        var stock = Assert.Single(completed.Result!.Stocks);
        Assert.Equal("missing", stock.State);
        Assert.Contains("401", stock.Detail);
        Assert.Empty(stock.Checks);
        await using var db = h.Database.CreateDbContext();
        Assert.Empty(await db.Candidates.ToArrayAsync());
    }

    /// <summary>Durable admission enforces ownership, idempotent inputs and the existing queue budget before collection.</summary>
    [Fact]
    public async Task Admission_BoundsJobsAndRejectsChangedInputsAndOtherOwners()
    {
        await using var h = await Harness.CreateAsync();
        var request = h.Request("same", ["RGTI"]);
        var accepted = await h.Service.StartAsync("operator", request, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Service.ReadAsync("other", accepted.JobId, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.StartAsync("operator", request with { Symbols = ["MSFT"] }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.StartAsync("operator", request with { IdempotencyKey = "full" }, default));
        Assert.Equal(0, h.Evidence.Calls);
        await h.Service.CancelAsync("operator", accepted.JobId, default);
        await h.ProcessAsync();
        Assert.Equal("cancelled", (await h.Service.ReadAsync("operator", accepted.JobId, default)).Status);
        Assert.Equal(0, h.Evidence.Calls);
    }

    /// <summary>Losing the response after real server admission must retry the same stored request, not consume another queue slot.</summary>
    [Fact]
    public async Task LostAcceptedResponse_SharedClientRetainsAttemptKeyAndRecoversOneDurableJob()
    {
        await using var h = await Harness.CreateAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(h.Service);
        builder.Services.AddSingleton(h.Recorded);
        await using var app = builder.Build();
        app.Use((context, next) => {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "operator")], "fixture"));
            return next(context);
        });
        app.MapGroup("/api/v1/desk").MapStockStrategyEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var handler = new DropAcceptedResponseHandler();
            using var http = new HttpClient(handler);
            var client = new TradingWorkflowClient(http, () => address);
            var submission = new StockStrategyAssessmentSubmission(h.Strategy, "all", ["RGTI"]);
            var key = submission.Request.IdempotencyKey;
            await Assert.ThrowsAsync<HttpRequestException>(() => submission.ResolveAsync(client));
            Assert.Null(submission.Job);
            Assert.Single(await h.Jobs.GetAllJobsAsync(default));
            var recovered = await submission.ResolveAsync(client);
            Assert.Equal("queued", recovered.Status);
            Assert.Equal(new[] { key, key }, handler.SubmittedKeys);
            Assert.Single(await h.Jobs.GetAllJobsAsync(default));
            await h.ProcessAsync();
            Assert.Equal("completed", (await submission.ResolveAsync(client)).Status);
            Assert.True(submission.IsTerminal);
            Assert.Equal(2, handler.SubmittedKeys.Count);
            Assert.Equal(recovered.JobId, (await submission.ResolveAsync(client)).JobId);
            Assert.Equal(key, submission.Request.IdempotencyKey);
        }
        finally { await app.StopAsync(); }
    }

    /// <summary>Regular-session freshness uses actual calendar dates and never treats yesterday's candle as today's latest one.</summary>
    [Fact]
    public void CurrentMarket_RejectsMissingLatestSessionAndAcceptsAuthoritativeClosedDay()
    {
        var date = new DateOnly(2026, 6, 10);
        var calendar = new Dictionary<DateOnly, MarketSessionSchedule> {
            [date] = MarketSessionSchedule.RegularDay(date),
            [date.AddDays(1)] = MarketSessionSchedule.RegularDay(date.AddDays(1)) };
        var original = StrategyDecisionKernelTests.CreateRequest();
        var bar = original.BarsByTimeframe["1d"][^1] with { Timestamp = new(2026, 6, 10, 4, 0, 0, TimeSpan.Zero) };
        var market = new TickerMarketState("RGTI", new Dictionary<string, IReadOnlyList<OhlcvBar>> { ["1d"] = new[] { bar } },
            new Dictionary<string, IReadOnlyList<IndicatorSnapshot>>());
        Assert.Null(StockStrategyEvidenceLoader.MissingCurrentMarket(market, ["1d"], calendar,
            new(2026, 6, 11, 15, 0, 0, TimeSpan.Zero), "America/New_York"));
        Assert.Contains("stale", StockStrategyEvidenceLoader.MissingCurrentMarket(market, ["1d"], calendar,
            new(2026, 6, 12, 15, 0, 0, TimeSpan.Zero), "America/New_York"));
    }

    /// <summary>A genuine source refresh that crosses a candle close must not label pre-close collected bars as current at the later decision time.</summary>
    [Theory]
    [InlineData(5)]
    [InlineData(15)]
    public async Task FinvizRefresh_CrossingCompletedCandleBoundaryRequiresNewMarketEvidence(int minutes)
    {
        await using var h = await Harness.CreateAsync(minutes);
        h.Clock.Now = new DateTimeOffset(2026, 6, 11, 14, minutes - 1, 59, TimeSpan.Zero);
        h.BeforeSourceCapture = () => h.Clock.Now = h.Clock.Now.AddSeconds(2);
        var accepted = await h.Service.StartAsync("operator", h.Request("boundary", ["RGTI"]) with { Source = h.FinvizSource }, default);
        await h.ProcessAsync();
        var completed = await h.Service.ReadAsync("operator", accepted.JobId, default);
        Assert.Equal("completed", completed.Status);
        var result = completed.Result!;
        var stock = Assert.Single(result.Stocks);
        Assert.Equal("finviz", h.LastSourceKind);
        Assert.Equal("missing", stock.State);
        Assert.Contains($"{minutes}m candles are stale", stock.Detail);
        Assert.Equal(h.Clock.Now, result.ReadAtUtc);
        Assert.Empty(stock.Checks);
        await using var db = h.Database.CreateDbContext();
        Assert.Empty(await db.Candidates.ToArrayAsync());
    }

    private sealed class Harness : IAsyncDisposable
    {
        public CandidateWorkflowTestDatabase Database { get; } = new();
        public CandidateWorkflowTestClock Clock { get; } = new(StrategyDecisionKernelTests.CreateRequest().AsOfUtc);
        public FakeEvidence Evidence { get; }
        public CurrentStockStrategyAssessmentService Service { get; private set; } = null!;
        public DeskStrategyAssessmentService Recorded { get; private set; } = null!;
        public SqliteJobRepository Jobs => new(Database);
        public StrategyReference Strategy { get; private set; } = null!;
        public string FinvizSource { get; private set; } = String.Empty;
        public string? LastSourceKind;
        public Action? BeforeSourceCapture;
        private readonly MemoryCache cache = new(new MemoryCacheOptions());
        private readonly string root = Path.Combine(Path.GetTempPath(), "tradingflow-strategy-observation", Guid.NewGuid().ToString("N"));
        /// <summary>Shares one fixed clock between collection and source-refresh fixtures.</summary>
        private Harness() => Evidence = new(Clock);

        /// <summary>Creates disposable SQLite/provider fixtures and cleans them if setup fails before the caller can own their lifetime.</summary>
        public static async Task<Harness> CreateAsync(int? executionMinutes = null)
        {
            var h = new Harness();
            try
            {
                await h.Database.InitializeAsync();
                var repo = TestRepository.FindRoot();
                var yaml = new SimpleYamlReader();
                var artifacts = executionMinutes is { } minutes ? h.BoundaryCatalog(repo, minutes, yaml) :
                    RetiredStrategyFixtures.Catalog(yaml);
                var experiments = new StrategyExperimentArtifactStore(h.root, artifacts, yaml, AtomicFileArtifactWriter.Instance);
                var profiles = new ConfigCatalogService(new(repo), yaml, artifacts, experiments, new StrategyAuthorizationTestRegistry());
                // The desk serves price-only daily strategies; the historical-news diagnostic recipe is backtest-only.
                var artifact = artifacts.GetSnapshot().ExecutableArtifacts.First(item => item.ResolvedStrategy.Timeframe == "1d" &&
                    item.AdmissionProfile.HistoricalNewsDiagnosticPolicyId is null);
                h.Strategy = new(artifact.Identity.StrategyId, artifact.Identity.SemanticVersion, artifact.Identity.ContentSha256);
                var lists = new SqliteWishlistRepository(h.Database);
                await lists.SaveAsync(new Wishlist { Id = Guid.NewGuid(), Name = "Fixture operator list", IsObserved = true }, default);
                var presets = new ScreenerPresetService(h.Database, h.Clock);
                var preset = await presets.SaveAsync("Fixture Finviz", ScreenerScope.Swing, "sh_price_o10", default);
                h.FinvizSource = "saved:" + preset.Id;
                var sources = new DeskSources(h.Database, lists, presets, null!, h.cache);
                var source = new Mock<IUniverseSourceResolver>();
                source.Setup(item => item.ResolveAsync(It.IsAny<UniverseSourceSelectionRequest>(), "swing", It.IsAny<CancellationToken>()))
                    .Returns((UniverseSourceSelectionRequest request, string _, CancellationToken _) => {
                        h.BeforeSourceCapture?.Invoke();
                        h.LastSourceKind = request.SourceKind;
                        return Task.FromResult(new UniverseSourceCapture(request.SourceKind, request.SourceKey,
                            h.Clock.Now, h.Clock.Now.AddMinutes(3), [new("RGTI")]));
                    });
                var discovery = new UniverseDiscoveryService(source.Object, h.Database.Discovery, h.Database.Workflows, h.Database.Workflows, h.Clock);
                var auth = new Mock<IPreparationAuthorization>();
                auth.Setup(item => item.StampAsync("operator", It.IsAny<CancellationToken>())).ReturnsAsync("stamp");
                h.Recorded = new DeskStrategyAssessmentService(h.Database, h.Database.Queries(h.Clock), h.Database.Queries(h.Clock), artifacts, h.cache, h.Clock);
                h.Service = new(h.Jobs, new(1, 1), profiles, artifacts, new StrategyIdentityResolver(profiles), auth.Object,
                    sources, discovery, h.Database.Discovery, h.Database.Candidates, h.Evidence, h.Recorded, h.Clock);
                return h;
            }
            catch
            {
                // The caller cannot enter await using when construction fails; release fixture-owned state here.
                await h.DisposeAsync();
                throw;
            }
        }
        /// <summary>Creates a disposable daily strategy with 5/15-minute confirmation; canonical source files are never edited.</summary>
        private StrategyArtifactCatalog BoundaryCatalog(string repo, int minutes, SimpleYamlReader yaml)
        {
            Directory.CreateDirectory(root);
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, "configs", "strategy-catalog.json")))!.AsObject();
            var source = File.ReadAllText(Path.Combine(repo, "src", "TradingFlow.Tests", "Fixtures", "Strategies", "daily-trailing-swing.fixture.yaml"));
            source = Regex.Replace(source, @"(?m)^(execution:\r?\n  timeframe:) 1d", "$1 " + minutes + "m");
            var strategyRoot = Path.Combine(root, "configs", "strategies");
            Directory.CreateDirectory(strategyRoot);
            File.WriteAllText(Path.Combine(strategyRoot, "strategy.yaml"), source);
            manifest["strategies"] = new JsonArray(new JsonObject {
                ["sourcePath"] = "configs/strategies/strategy.yaml", ["strategyId"] = "fixture.current-assessment", ["semanticVersion"] = "1.0.0",
                ["lifecycle"] = "research", ["admissionProfileId"] = "causal-market-exits-v3", ["reason"] = "Disposable boundary fixture" });
            // The next-slice daily profile left the repository catalog with its last strategy; the fixture declares its own copy.
            manifest["admissionProfiles"] = new JsonArray(new JsonObject {
                ["profileId"] = "causal-market-exits-v3", ["profileVersion"] = "3.0.0", ["parserProfile"] = "simple-yaml-strict-v2",
                ["standardIndicatorLibrary"] = "Skender.Stock.Indicators", ["standardIndicatorLibraryVersion"] = "2.7.1",
                ["derivedIndicatorProfile"] = "TradingFlow.DerivedIndicators", ["derivedIndicatorProfileVersion"] = "2.1.0",
                ["riskPolicy"] = "account-risk-budget-v1", ["executionTimingPolicy"] = "completed-bar-next-slice-market-exits-v3" });
            var path = Path.Combine(root, "strategy-catalog.json");
            File.WriteAllText(path, manifest.ToJsonString());
            return new(root, path, yaml);
        }
        /// <summary>Creates one explicit bounded operator request.</summary>
        public StartStockStrategyAssessmentRequest Request(string key, string[] symbols) => new(ContractVersions.VersionOne, key, Strategy, "all", symbols);
        /// <summary>Runs only this job handler through the actual lease/cancellation processor.</summary>
        public Task<bool> ProcessAsync() => new DurableJobProcessor(Jobs, Clock).ProcessNextAsync("fixture-worker", Service, DurableJobWorkerOptions.Default, default);
        /// <summary>Removes only fixture-owned temporary state after closing its SQLite connections.</summary>
        public async ValueTask DisposeAsync()
        {
            cache.Dispose();
            await Database.DisposeAsync();
            var full = Path.GetFullPath(root);
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tradingflow-strategy-observation"));
            if (Path.GetDirectoryName(full) != parent) throw new InvalidOperationException("Fixture cleanup escaped its temporary root.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }

    private sealed class FakeEvidence(CandidateWorkflowTestClock clock) : IStockStrategyEvidenceLoader
    {
        public string? Missing;
        public int Calls;
        /// <summary>Supplies deliberately insufficient but genuine fixture bars to verify the shared warming decision, without provider traffic.</summary>
        public Task<StockStrategyEvidenceBatch> LoadAsync(BacktestRunConfig profile, AuthorizedRuntimeStrategy strategy, IReadOnlyList<string> symbols, CancellationToken token)
        {
            Calls++;
            var original = StrategyDecisionKernelTests.CreateRequest();
            var required = StrategyDecisionRequestAssembler.ResolveRequiredTimeframes(strategy.Definition);
            var calendarEnd = DateOnly.FromDateTime(clock.Now.UtcDateTime).AddDays(1);
            var calendar = Enumerable.Range(0, 401).Select(index => calendarEnd.AddDays(index - 400))
                .ToDictionary(date => date, date => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                    ? MarketSessionSchedule.Closed(date) : MarketSessionSchedule.RegularDay(date));
            var resampler = new BarResampler(MarketEvidenceProfile.ProductionDefault.WithSessionSchedules(calendar));
            // Fixtures contain only candles already complete at the collection clock; the later source read cannot add one.
            DateTimeOffset Timestamp(string timeframe, int index)
            {
                if (TimeframeParser.IsDailyOrHigher(timeframe)) return original.BarsByTimeframe["1d"][index].Timestamp;
                var duration = TimeframeParser.Parse(timeframe);
                return resampler.GetBucketWindow(clock.Now, timeframe).StartUtc.AddTicks(-duration.Ticks * (3 - index));
            }
            // Benchmarks are independently complete controlled fixtures, not inferred from the stock's three warm-up bars.
            IReadOnlyList<OhlcvBar> Benchmark(string symbol, int count)
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
                return TechnicalFactorCalculator.ExpectedCompletedSessions(calendar, clock.Now, count).Select((date, index) => {
                    var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
                    var timestamp = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
                    return new OhlcvBar(symbol, timestamp, "1d", 100 + index, 101 + index, 99 + index, 100 + index,
                        1000000, "sip", "all", timestamp.AddDays(1), clock.Now);
                }).ToArray();
            }
            var regime = strategy.Definition.Regime;
            var regimeBars = regime is { IsActive: true } ? Benchmark(regime.BenchmarkSymbol, regime.SmaPeriod) : null;
            var factor = strategy.Definition.EntryRules.TechnicalFactor;
            var referenceBars = factor is { Metric: TechnicalFactorRules.RelativeRoc } ? Benchmark(factor.BenchmarkSymbol, factor.LookbackBars + 1) : null;
            var query = StrategyCapabilityInspector.UsesNews(strategy.Definition) ? new CatalystQueryResult([], true, "Complete fixture window", 1,
                clock.Now.AddHours(-(double)StrategyCapabilityInspector.RequiredNewsLookbackHours(strategy.Definition)), clock.Now) : null;
            var result = symbols.ToDictionary(symbol => symbol, symbol => new StockStrategyEvidence(new(symbol,
                required.ToDictionary(timeframe => timeframe, timeframe => (IReadOnlyList<OhlcvBar>)original.BarsByTimeframe["1d"]
                    .Select((item, index) => item with { Ticker = symbol, Timeframe = timeframe, Timestamp = Timestamp(timeframe, index) }).ToArray()),
                required.ToDictionary(timeframe => timeframe, timeframe => (IReadOnlyList<IndicatorSnapshot>)original.SnapshotsByTimeframe["1d"]
                    .Select((item, index) => item with { Ticker = symbol, Timeframe = timeframe, Timestamp = Timestamp(timeframe, index) }).ToArray())), [],
                new(regime is not { IsActive: true } || RegimeCalendarBuilder.IsRegimeOnAt(regimeBars!, regime.SmaPeriod, clock.Now),
                    "fixture_proven_regime", "fixture", original.AsOfUtc, "{}"), referenceBars,
                factor is { Metric: TechnicalFactorRules.RelativeRoc } ? TechnicalFactorCalculator.ExpectedCompletedSessions(calendar, clock.Now, factor.LookbackBars + 1) : null,
                null, Missing, query, regimeBars));
            return Task.FromResult(new StockStrategyEvidenceBatch(clock.Now, result, calendar));
        }
    }

    private sealed class DropAcceptedResponseHandler() : DelegatingHandler(new HttpClientHandler())
    {
        public List<string> SubmittedKeys { get; } = [];
        /// <summary>Forwards to the real API before dropping the first accepted response, reproducing an ambiguous network outcome.</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var submission = request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/strategy-assessments", StringComparison.Ordinal);
            if (submission) SubmittedKeys.Add((await request.Content!.ReadFromJsonAsync<StartStockStrategyAssessmentRequest>(token))!.IdempotencyKey);
            var response = await base.SendAsync(request, token);
            if (submission && SubmittedKeys.Count == 1)
            {
                response.Dispose();
                throw new HttpRequestException("Fixture lost the already accepted response.");
            }
            return response;
        }
    }
}
