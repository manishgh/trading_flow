using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using TradingFlow.Application.Jobs;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Context;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Collection;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Data.Evidence.Screens;
using TradingFlow.Data.Jobs;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;
using TradingFlow.Engine.Universe;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

/// <summary>
/// Deterministic offline unit and integration tests for Checkpoint 9:
/// <see cref="ResearchDailyScreenPublisher"/>, <see cref="IntradayBarsCollectionPlanner"/>, and
/// <see cref="ResearchEvidenceJobService"/> (durable screen build + two-step 5-minute bar plan/collect + catalog binding).
/// </summary>
public sealed class ResearchScreenWebWorkflowTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private const string RunId = "checkpoint9-test-run";
    private const string ConfigHash = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string CodeVersion = "checkpoint9-test-code";
    private const string Normalizer = "checkpoint9-test-normalizer-v1";

    private static readonly EvidenceSourceReference SyntheticSource = new(
        "synthetic-obs-cp9",
        new EvidenceArtifactReference(
            new EvidenceContentAddress(new string('b', 64), 1, "application/json"),
            new EvidenceObjectNamespace("raw/synthetic")),
        new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    private const string SampleFredVixCsv =
        "observation_date,VIXCLS\n" +
        "2026-04-29,16.50\n" +
        "2026-04-30,17.20\n" +
        "2026-05-01,19.40\n" +
        "2026-05-04,23.10\n" +
        "2026-05-05,21.00\n" +
        "2026-05-06,18.75\n";

    [Fact]
    public async Task ResearchDailyScreenPublisher_PublishesCompleteScreenWithFredVixAndRefusesOverwrite()
    {
        using var fixture = new TestEvidenceFixture();
        var (barsId, actionsId, sessionsId, secMasterId, windowFrom, windowTo) = await fixture.SeedStandardDatasetsAsync();
        var excludePath = Path.Combine(fixture.Paths.ResearchScreensRoot, "finviz-funds-20261102.txt");
        Directory.CreateDirectory(fixture.Paths.ResearchScreensRoot);
        await File.WriteAllLinesAsync(excludePath, ["ETF1"]);

        var fredRequests = 0;
        var publisher = new ResearchDailyScreenPublisher(
            fixture.Catalog,
            fixture.Store,
            fredHttpClientFactory: () => new HttpClient(new StubHttpHandler(() =>
            {
                fredRequests++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SampleFredVixCsv, Encoding.UTF8, "text/csv")
                };
            })));

        var outputFolder = Path.Combine(fixture.Paths.ResearchScreensRoot, "swing-cp9-screen");
        var rules = new DailyScreenRules(
            5m,
            DailyScreenPriceBasis.PriorClose,
            20,
            MinAverageDollarVolume: 20_000_000m,
            MinHistorySessions: 20,
            MinAverageRangePercent: 2.5m);
        var request = new ResearchDailyScreenRequest(
            barsId,
            actionsId,
            windowFrom,
            windowTo,
            outputFolder,
            rules,
            ExcludedSymbolsFile: excludePath,
            SplitLookbackSessions: 10,
            ExchangeSessionsDatasetId: sessionsId,
            SecurityMasterDatasetId: secMasterId,
            FredVixSeries: RegimeRules.FredVixSeries);

        var result = await publisher.PublishAsync(request);

        Assert.Equal(1, fredRequests);
        Assert.True(result.HasFredVix);
        Assert.NotNull(result.FredVixSha256);
        Assert.True(result.EligibleStockDays > 0);
        Assert.True(result.SplitExcludedCount > 0, "Expected stock with a split inside its 10-session lookback to be excluded.");

        var published = PublishedResearchScreen.Read(outputFolder, result.ManifestSha256);
        Assert.Equal(barsId, published.Manifest.Inputs.DailyBarsDatasetId);
        Assert.Equal(actionsId, published.Manifest.Inputs.CorporateActionsDatasetId);
        Assert.Equal(sessionsId, published.Manifest.Inputs.ExchangeSessionsDatasetId);
        Assert.Equal(secMasterId, published.Manifest.Inputs.SecurityMasterDatasetId);
        Assert.True(published.Manifest.Outputs.ContainsKey(ResearchScreenManifest.FredVixFileName));
        Assert.True(published.Manifest.Outputs.ContainsKey(ResearchScreenManifest.FredVixReceiptFileName));

        var eligibleLines = await File.ReadAllLinesAsync(Path.Combine(outputFolder, "eligible.csv"));
        Assert.Contains(eligibleLines, line => line.Contains(",AAA,", StringComparison.Ordinal));
        Assert.DoesNotContain(eligibleLines, line => line.Contains(",ETF1,", StringComparison.Ordinal));
        Assert.DoesNotContain(eligibleLines, line => line.Contains(",LOWADR,", StringComparison.Ordinal));

        // Core data protection: refusing to overwrite an existing published screen folder happens BEFORE any FRED HTTP call.
        var overwriteError = await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(request));
        Assert.Contains("Refusing to overwrite", overwriteError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fredRequests);

        // Transient FRED failure during a new screen build cleans up its run-owned staging folder so a retry succeeds.
        var retryFolder = Path.Combine(fixture.Paths.ResearchScreensRoot, "swing-cp9-retry");
        var retryAttempt = 0;
        var retryPublisher = new ResearchDailyScreenPublisher(
            fixture.Catalog,
            fixture.Store,
            fredHttpClientFactory: () => new HttpClient(new StubHttpHandler(() =>
            {
                retryAttempt++;
                return retryAttempt == 1
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(SampleFredVixCsv, Encoding.UTF8, "text/csv")
                    };
            })));
        var retryRequest = request with { OutputFolder = retryFolder };
        await Assert.ThrowsAsync<HttpRequestException>(() => retryPublisher.PublishAsync(retryRequest));
        Assert.False(Directory.Exists(retryFolder));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.ResearchScreensRoot, "swing-cp9-retry.staging-*"));

        var retryResult = await retryPublisher.PublishAsync(retryRequest);
        Assert.True(retryResult.HasFredVix);
        Assert.True(PublishedResearchScreen.IsPublished(retryFolder));
    }

    [Fact]
    public async Task IntradayBarsCollectionPlanner_PlansPriorBaselineAndHeldSessionsAndSubtractsCatalogCoverage()
    {
        using var fixture = new TestEvidenceFixture();
        var tradingDays = BuildWeekdays(new DateOnly(2026, 3, 2), 35);
        // AAA and BBB trade every day; AAA is eligible on day 25, BBB is eligible on day 27. Window [day 25 .. day 30].
        var dailyRows = new List<MarketBarEvidenceRow>();
        foreach (var day in tradingDays)
        {
            var start = ToNewYorkUtc(day, TimeOnly.MinValue);
            dailyRows.Add(BarRow("AAA", "1d", start, 50m, high: 52m, low: 49m, close: 50.5m, volume: 1_000_000));
            dailyRows.Add(BarRow("BBB", "1d", start, 60m, high: 62.5m, low: 59m, close: 60.5m, volume: 1_000_000));
        }

        var dailyId = await fixture.CommitBarsAsync("1d", rankedListSha256: null, dailyRows);
        var actionsId = await fixture.CommitSplitsAsync([("AAA", new DateOnly(2025, 1, 2), 2m)]);
        var sessionsId = await fixture.CommitSessionsAsync(tradingDays[0], tradingDays[^1].AddDays(1));

        // Publish a minimal unranked screen where AAA is eligible only on tradingDays[25] and BBB only on tradingDays[27].
        var screenFolder = Path.Combine(fixture.Paths.ResearchScreensRoot, "plan-test-screen");
        Directory.CreateDirectory(screenFolder);
        await File.WriteAllLinesAsync(Path.Combine(screenFolder, "eligible.csv"),
        [
            "session_date,ticker,price,average_volume,average_dollar_volume,atr,history_sessions,average_range_pct",
            $"{tradingDays[25]:yyyy-MM-dd},AAA,50,1000000,50000000,2,25,6",
            $"{tradingDays[27]:yyyy-MM-dd},BBB,60,1000000,60000000,2,27,5.8"
        ]);
        await File.WriteAllLinesAsync(Path.Combine(screenFolder, "daily-counts.csv"),
        [
            "session_date,eligible",
            $"{tradingDays[25]:yyyy-MM-dd},1",
            $"{tradingDays[27]:yyyy-MM-dd},1"
        ]);
        var rules = new DailyScreenRules(5m, DailyScreenPriceBasis.PriorClose, 20, MinAverageDollarVolume: 20_000_000m, MinHistorySessions: 20, MinAverageRangePercent: 2.5m);
        await ResearchScreenManifest.PublishAsync(
            screenFolder,
            new ResearchScreenInputs(dailyId, actionsId, null, sessionsId),
            rules,
            ranking: null,
            tradingDays[25],
            tradingDays[30],
            DailyRankingOutput.IssuerIdentityBasis,
            null,
            default);

        var screen = PublishedResearchScreen.Read(screenFolder);
        var requestsDir = Path.Combine(fixture.Paths.EvidenceRoot, "requests");
        var planner = new IntradayBarsCollectionPlanner(fixture.Catalog, fixture.Store);

        // Default PriorSessions = 21 (entry day E = D+1 needs signal day D plus 20 prior sessions [D-20..D-1]):
        // AAA needs: prior 21 sessions (days 4..24 = 21) + eligible day 25 (1) + held non-eligible days 26..30 (5) = 27.
        // BBB needs: prior 21 sessions (days 6..26 = 21) + eligible day 27 (1) + held non-eligible days 28..30 (3) = 25.
        // Total deduplicated stock-sessions = 27 + 25 = 52.
        var (plan1, req1) = await planner.PlanScreenFiveMinuteBarsAsync(new MinuteBarsCollectionPlanRequest(
            "plan-test-screen",
            screenFolder,
            tradingDays[25],
            tradingDays[30],
            IncludeHeldSessions: true,
            BatchSize: 50,
            RequestsOutputDirectory: requestsDir));

        Assert.False(plan1.AlreadyComplete);
        Assert.Equal(21, plan1.PriorSessions);
        Assert.Equal(52, plan1.TotalRequiredStockSessions);
        Assert.Equal(0, plan1.AlreadyCollectedStockSessions);
        Assert.Equal(52, plan1.MissingStockSessions);
        Assert.Equal(2, plan1.UniqueSymbolCount);
        Assert.Equal(52L * 78L, plan1.EstimatedBars);
        Assert.Equal(52L * 78L * 110L, plan1.EstimatedDownloadBytes);
        Assert.Equal(Math.Round(52L * 78L * 110L / (1024m * 1024m), 2, MidpointRounding.AwayFromZero), plan1.EstimatedDownloadMegabytes);
        Assert.NotNull(plan1.PlanFileName);
        Assert.NotNull(plan1.RequestSha256);
        Assert.NotNull(req1);

        // Verify zero duplicate (session, symbol) pairs across generated HTTP requests.
        var scheduledPairs = req1!.Requests
            .SelectMany(r => r.Symbols.Select(s => (r.AsOfDate!.Value, s)))
            .ToArray();
        Assert.Equal(52, scheduledPairs.Length);
        Assert.Equal(52, scheduledPairs.Distinct().Count());

        // Idempotent re-plan returns identical PlanFileName and RequestSha256.
        var (plan1Again, _) = await planner.PlanScreenFiveMinuteBarsAsync(new MinuteBarsCollectionPlanRequest(
            "plan-test-screen",
            screenFolder,
            tradingDays[25],
            tradingDays[30],
            IncludeHeldSessions: true,
            BatchSize: 50,
            RequestsOutputDirectory: requestsDir));
        Assert.Equal(plan1.PlanFileName, plan1Again.PlanFileName);
        Assert.Equal(plan1.RequestSha256, plan1Again.RequestSha256);

        // An opening-only 09:30-09:35 5m dataset tagged with the same ranked_list_sha256 must NOT count as full-session coverage.
        var firstFivePairs = scheduledPairs.Take(5).ToArray();
        await fixture.CommitBarsAsync(
            "5m",
            screen.RankedListSha256,
            firstFivePairs.Select(pair => BarRow(pair.s, "5m", ToNewYorkUtc(pair.Value, new TimeOnly(9, 30)), 50m)).ToArray(),
            newYorkWindow: "09:30-09:35",
            sessionEndTime: new TimeOnly(9, 35));
        var (planAfterOpeningOnly, _) = await planner.PlanScreenFiveMinuteBarsAsync(new MinuteBarsCollectionPlanRequest(
            "plan-test-screen",
            screenFolder,
            tradingDays[25],
            tradingDays[30],
            IncludeHeldSessions: true,
            BatchSize: 50,
            RequestsOutputDirectory: requestsDir));
        Assert.Equal(0, planAfterOpeningOnly.AlreadyCollectedStockSessions);
        Assert.Equal(52, planAfterOpeningOnly.MissingStockSessions);

        // Commit 5 already-collected full-RTH stock-sessions partitioned by session date (same symbol across multiple partitions!).
        await fixture.CommitBarsAsync(
            "5m",
            screen.RankedListSha256,
            firstFivePairs.Select(pair => BarRow(pair.s, "5m", ToNewYorkUtc(pair.Value, new TimeOnly(9, 30)), 50m)).ToArray());

        var (planPartial, _) = await planner.PlanScreenFiveMinuteBarsAsync(new MinuteBarsCollectionPlanRequest(
            "plan-test-screen",
            screenFolder,
            tradingDays[25],
            tradingDays[30],
            IncludeHeldSessions: true,
            BatchSize: 50,
            RequestsOutputDirectory: requestsDir));
        Assert.False(planPartial.AlreadyComplete);
        Assert.Equal(52, planPartial.TotalRequiredStockSessions);
        Assert.Equal(5, planPartial.AlreadyCollectedStockSessions);
        Assert.Equal(47, planPartial.MissingStockSessions);

        // Commit the remaining 47 stock-sessions -> planner reports AlreadyComplete with 0 missing and no plan file.
        var remainingPairs = scheduledPairs.Skip(5).ToArray();
        await fixture.CommitBarsAsync(
            "5m",
            screen.RankedListSha256,
            remainingPairs.Select(pair => BarRow(pair.s, "5m", ToNewYorkUtc(pair.Value, new TimeOnly(9, 30)), 50m)).ToArray());

        var (planDone, reqDone) = await planner.PlanScreenFiveMinuteBarsAsync(new MinuteBarsCollectionPlanRequest(
            "plan-test-screen",
            screenFolder,
            tradingDays[25],
            tradingDays[30],
            IncludeHeldSessions: true,
            BatchSize: 50,
            RequestsOutputDirectory: requestsDir));
        Assert.True(planDone.AlreadyComplete);
        Assert.Equal(52, planDone.TotalRequiredStockSessions);
        Assert.Equal(52, planDone.AlreadyCollectedStockSessions);
        Assert.Equal(0, planDone.MissingStockSessions);
        Assert.Null(planDone.PlanFileName);
        Assert.Null(planDone.RequestSha256);
        Assert.Null(reqDone);
    }

    [Fact]
    public async Task ResearchEvidenceJobService_ValidatesInputsAndExecutesBuildAndCollectViaDurableWorker()
    {
        using var fixture = new TestEvidenceFixture();
        var (barsId, actionsId, sessionsId, secMasterId, windowFrom, windowTo) = await fixture.SeedStandardDatasetsAsync();
        Directory.CreateDirectory(fixture.Paths.ResearchScreensRoot);
        await File.WriteAllLinesAsync(Path.Combine(fixture.Paths.ResearchScreensRoot, "finviz-funds-20261102.txt"), ["ETF1"]);

        var fakeRunner = new FakeCollectionRunner(fixture);
        var noCredsService = fixture.CreateJobService(fakeRunner, alpacaKeyId: "", alpacaSecretKey: "");
        var credsService = fixture.CreateJobService(fakeRunner, alpacaKeyId: "test-key", alpacaSecretKey: "test-secret");

        // 1. Security & input validation: reject path traversal, missing ExchangeSessionsDatasetId, and holdout overlap.
        await Assert.ThrowsAsync<ArgumentException>(() => credsService.StartBuildScreenAsync(
            new BuildResearchScreenHttpRequest("../escape", barsId, actionsId, windowFrom, windowTo, ExchangeSessionsDatasetId: sessionsId), default));
        await Assert.ThrowsAsync<ArgumentException>(() => credsService.StartBuildScreenAsync(
            new BuildResearchScreenHttpRequest("valid-screen", barsId, actionsId, windowFrom, windowTo, ExchangeSessionsDatasetId: sessionsId, ExclusionListKey: @"C:\Windows\win.ini"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => credsService.StartBuildScreenAsync(
            new BuildResearchScreenHttpRequest("valid-screen", barsId, actionsId, windowFrom, windowTo, ExchangeSessionsDatasetId: sessionsId, ExclusionListKey: "screens:../secret.txt"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => credsService.StartBuildScreenAsync(
            new BuildResearchScreenHttpRequest("missing-sessions", barsId, actionsId, windowFrom, windowTo, ExchangeSessionsDatasetId: null), default));
        var holdoutError = await Assert.ThrowsAsync<InvalidOperationException>(() => credsService.StartBuildScreenAsync(
            new BuildResearchScreenHttpRequest("holdout-screen", barsId, actionsId, new DateOnly(2025, 6, 1), new DateOnly(2025, 9, 30), ExchangeSessionsDatasetId: sessionsId), default));
        Assert.Contains("2024-01-01", holdoutError.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => credsService.StartCollectFiveMinuteBarsAsync(
            new CollectMinuteBarsHttpRequest("../escape.json", new string('a', 64)), default));

        // 2. Durable build_screen job executes through DurableJobProcessor and publishes screen + frozen FRED VIX.
        var buildQueued = await credsService.StartBuildScreenAsync(new BuildResearchScreenHttpRequest(
            ScreenName: "web-swing-screen",
            DailyBarsDatasetId: barsId,
            CorporateActionsDatasetId: actionsId,
            From: windowFrom,
            To: windowTo,
            MinPrice: 5m,
            PriceBasis: "prior_close",
            AverageSessions: 20,
            MinAverageDollarVolume: 20_000_000m,
            MinHistorySessions: 20,
            MinAverageRangePercent: 2.5m,
            SplitLookbackSessions: 10,
            ExchangeSessionsDatasetId: sessionsId,
            SecurityMasterDatasetId: secMasterId,
            ExclusionListKey: "screens:finviz-funds-20261102.txt",
            IncludeFredVix: true), default);

        var processor = new DurableJobProcessor(fixture.JobRepository);
        Assert.True(await processor.ProcessNextAsync("test-worker", credsService, DurableJobWorkerOptions.Default, default));

        var buildDone = await credsService.GetJobAsync(buildQueued.JobId, default);
        Assert.NotNull(buildDone);
        Assert.Equal("completed", buildDone!.Status);
        Assert.NotNull(buildDone.ScreenResult);
        Assert.Equal("web-swing-screen", buildDone.ScreenResult!.ScreenName);
        Assert.True(buildDone.ScreenResult.HasFredVix);

        var options = await credsService.GetOptionsAsync(default);
        Assert.Contains(options.PublishedScreens, s => s.Name == "web-swing-screen" && s.HasFredVix && s.HasSecurityMaster);

        // 3. Step 1 dry-run plan (zero collection calls).
        var plan = await credsService.PlanFiveMinuteBarsAsync(new PlanMinuteBarsHttpRequest(
            "web-swing-screen",
            windowFrom,
            windowTo,
            PriorSessions: 20,
            IncludeHeldSessions: true), default);
        Assert.False(plan.AlreadyComplete);
        Assert.True(plan.MissingStockSessions > 0);
        Assert.NotNull(plan.PlanFileName);
        Assert.NotNull(plan.RequestSha256);
        Assert.Equal(0, fakeRunner.CallCount);

        // 4. Step 2 rejects mismatched SHA-256 AND rejects missing Alpaca credentials with a clear message.
        var badSha = await Assert.ThrowsAsync<InvalidOperationException>(() => credsService.StartCollectFiveMinuteBarsAsync(
            new CollectMinuteBarsHttpRequest(plan.PlanFileName!, new string('0', 64)), default));
        Assert.Contains("fingerprint mismatch", badSha.Message, StringComparison.OrdinalIgnoreCase);

        var missingCreds = await Assert.ThrowsAsync<InvalidOperationException>(() => noCredsService.StartCollectFiveMinuteBarsAsync(
            new CollectMinuteBarsHttpRequest(plan.PlanFileName!, plan.RequestSha256!), default));
        Assert.Contains("ALPACA_KEY_ID", missingCreds.Message, StringComparison.Ordinal);

        // 5. Step 2 confirmed collect executes via DurableJobProcessor and binds the new 5m dataset in ResearchScreenSources.
        var collectQueued = await credsService.StartCollectFiveMinuteBarsAsync(
            new CollectMinuteBarsHttpRequest(plan.PlanFileName!, plan.RequestSha256!), default);
        Assert.True(await processor.ProcessNextAsync("test-worker", credsService, DurableJobWorkerOptions.Default, default));

        var collectDone = await credsService.GetJobAsync(collectQueued.JobId, default);
        Assert.NotNull(collectDone);
        Assert.Equal("completed", collectDone!.Status);
        Assert.NotNull(collectDone.CollectionResult);
        Assert.Equal(1, fakeRunner.CallCount);
        var committedFiveMinuteDatasetId = collectDone.CollectionResult!.DatasetId;

        var sources = new ResearchScreenSources(fixture.Paths, fixture.Catalog);
        var publishedScreen = sources.Read("web-swing-screen");
        var reference = await sources.ResolveReferenceAsync(publishedScreen, default);
        Assert.Contains(committedFiveMinuteDatasetId, reference.FiveMinuteDatasetIds);
        Assert.Equal(secMasterId, reference.SecurityMasterDatasetId);
        Assert.NotNull(reference.FredVixSha256);
    }

    [Fact]
    public void BacktestEligibility_MapsAndRoundTripsMinAverageRangePct()
    {
        var withAdr = new BacktestEligibilityRules(
            MinPrice: 5m,
            PriceBasis: "prior_close",
            AverageSessions: 20,
            MinHistorySessions: 253,
            MinAverageDollarVolume: 20_000_000m,
            MinAverageRangePct: 2.5m);
        var jsonWithAdr = JsonSerializer.Serialize(withAdr, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"minAverageRangePct\":2.5", jsonWithAdr, StringComparison.Ordinal);

        var roundTripped = JsonSerializer.Deserialize<BacktestEligibilityRules>(jsonWithAdr, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(withAdr, roundTripped);

        var mapped = BacktestEligibility.ToRules(roundTripped, executionReplayPolicyId: null);
        Assert.NotNull(mapped);
        Assert.Equal(2.5m, mapped!.MinAverageRangePercent);
        Assert.Equal(20_000_000m, mapped.MinAverageDollarVolume);

        // Omitting MinAverageRangePct keeps existing saved-plan JSON byte-compatible (property omitted when null).
        var withoutAdr = new BacktestEligibilityRules(5m, "prior_close", 20, 20);
        var jsonWithoutAdr = JsonSerializer.Serialize(withoutAdr, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("minAverageRangePct", jsonWithoutAdr, StringComparison.Ordinal);
        Assert.Null(BacktestEligibility.ToRules(withoutAdr, executionReplayPolicyId: null)!.MinAverageRangePercent);
    }

    // Builds one synthetic as-traded market bar row matching this fixture's exact RunId/ConfigHash/CodeVersion provenance.
    private static MarketBarEvidenceRow BarRow(
        string symbol,
        string timeframe,
        DateTimeOffset start,
        decimal price,
        decimal? high = null,
        decimal? low = null,
        decimal? close = null,
        long volume = 1000)
    {
        var length = timeframe switch
        {
            "1d" => TimeSpan.FromDays(1),
            "1h" => TimeSpan.FromHours(1),
            "1m" => TimeSpan.FromMinutes(1),
            _ => TimeSpan.FromMinutes(5)
        };
        return new MarketBarEvidenceRow(
            1,
            RunId,
            ConfigHash,
            CodeVersion,
            "sip",
            "sec-" + symbol.ToLowerInvariant(),
            symbol,
            start,
            start + length,
            timeframe,
            EvidenceFixedDecimal.ToPriceUnits(price),
            EvidenceFixedDecimal.ToPriceUnits(high ?? price + 1m),
            EvidenceFixedDecimal.ToPriceUnits(low ?? price - 1m),
            EvidenceFixedDecimal.ToPriceUnits(close ?? price + 0.5m),
            null,
            volume,
            null,
            "raw",
            "USD",
            start + length,
            start + length,
            [new EvidenceRowSourceAddress(SyntheticSource.ObservationId, SyntheticSource.Artifact.Content.Sha256)]);
    }

    private static DateOnly[] BuildWeekdays(DateOnly start, int count)
    {
        var days = new List<DateOnly>(count);
        var current = start;
        while (days.Count < count)
        {
            if (current.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                days.Add(current);
            }
            current = current.AddDays(1);
        }
        return days.ToArray();
    }

    private static DateTimeOffset ToNewYorkUtc(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, NewYork.GetUtcOffset(local)).ToUniversalTime();
    }

    private sealed class StubHttpHandler(Func<HttpResponseMessage> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(factory());
    }

    private sealed class FakeCollectionRunner(TestEvidenceFixture fixture) : IEvidenceDatasetCollectionRunner
    {
        public int CallCount { get; private set; }

        public async Task<EvidenceCollectionRunOutcome> RunAsync(
            FrozenEvidenceCollectionRequest request,
            string alpacaKeyId,
            string alpacaSecretKey,
            Uri marketDataRestEndpoint,
            Uri tradingRestEndpoint,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var rows = request.Requests
                .SelectMany(r => r.Symbols.Select(symbol => BarRow(
                    symbol,
                    "5m",
                    ToNewYorkUtc(r.AsOfDate!.Value, new TimeOnly(9, 30)),
                    50m)))
                .ToArray();
            var datasetId = await fixture.CommitBarsAsync("5m", request.Attributes["ranked_list_sha256"], rows);
            return new EvidenceCollectionRunOutcome(request.RunId, datasetId, AlreadyCommitted: false, PartitionCount: 1, RowCount: rows.Length);
        }
    }

    private sealed class TestEvidenceFixture : IDisposable
    {
        private readonly TemporaryFolder root = new();
        private readonly EvidenceParquetCodec codec = new();
        private readonly SqliteDbContextFactory dbFactory;
        private readonly Dictionary<string, EvidenceDatasetManifest> datasets = new(StringComparer.Ordinal);

        public TestEvidenceFixture()
        {
            Paths = new ProjectPaths(root.Path, Path.Combine(root.Path, "data"), Path.Combine(root.Path, "cache"));
            Directory.CreateDirectory(Paths.EvidenceRoot);
            Store = new FileSystemImmutableArtifactStore(new ImmutableArtifactStoreOptions(Paths.EvidenceObjectRoot));
            var catalogMock = new Mock<IEvidenceCatalog>();
            catalogMock.Setup(c => c.GetDatasetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string id, CancellationToken _) => Task.FromResult(datasets.GetValueOrDefault(id)));
            catalogMock.Setup(c => c.FindDatasetsAsync(It.IsAny<EvidenceDatasetQuery>(), It.IsAny<CancellationToken>()))
                .Returns((EvidenceDatasetQuery query, CancellationToken _) => Task.FromResult<IReadOnlyList<EvidenceDatasetManifest>>(
                    datasets.Values.Where(d => query.Kind is null || d.Kind == query.Kind).ToArray()));
            catalogMock.Setup(c => c.CommitDatasetAsync(It.IsAny<EvidenceDatasetManifest>(), It.IsAny<CancellationToken>()))
                .Returns((EvidenceDatasetManifest manifest, CancellationToken _) =>
                {
                    var existed = !datasets.TryAdd(manifest.DatasetId, manifest);
                    return Task.FromResult(new EvidenceCatalogCommitResult(manifest.DatasetId, existed));
                });
            catalogMock.Setup(c => c.GetCollectionPlanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult<EvidenceCollectionPlan?>(null));
            catalogMock.Setup(c => c.GetCollectionCheckpointAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult<EvidenceCollectionCheckpoint?>(null));
            Catalog = catalogMock.Object;
            var dbPath = Path.Combine(root.Path, "jobs.db");
            var options = new DbContextOptionsBuilder<TradingFlowDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            dbFactory = new SqliteDbContextFactory(options);
            using var db = dbFactory.CreateDbContext();
            db.Database.EnsureCreated();
            JobRepository = new SqliteJobRepository(dbFactory);
        }

        public ProjectPaths Paths { get; }
        public FileSystemImmutableArtifactStore Store { get; }
        public IEvidenceCatalog Catalog { get; }
        public SqliteJobRepository JobRepository { get; }

        public ResearchEvidenceJobService CreateJobService(
            IEvidenceDatasetCollectionRunner runner,
            string alpacaKeyId,
            string alpacaSecretKey)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Alpaca:KeyId"] = alpacaKeyId,
                    ["Alpaca:SecretKey"] = alpacaSecretKey
                })
                .Build();
            var creds = new AlpacaCredentialProvider(config, _ => null);
            return new ResearchEvidenceJobService(
                JobRepository,
                Paths,
                Catalog,
                Store,
                creds,
                runner,
                fredHttpClientFactory: () => new HttpClient(new StubHttpHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SampleFredVixCsv, Encoding.UTF8, "text/csv")
                })));
        }

        public async Task<(string BarsId, string ActionsId, string SessionsId, string SecMasterId, DateOnly From, DateOnly To)> SeedStandardDatasetsAsync()
        {
            var days = BuildWeekdays(new DateOnly(2026, 3, 2), 30);
            var from = days[24];
            var to = days[28];
            var rows = new List<MarketBarEvidenceRow>();
            foreach (var day in days)
            {
                var start = ToNewYorkUtc(day, TimeOnly.MinValue);
                // AAA: $50, high 52 / low 49 -> range 3 / 50 = 6% ADR >= 2.5%, $50M DV >= $20M -> qualifies.
                rows.Add(BarRow("AAA", "1d", start, 50m, high: 52m, low: 49m, close: 50m, volume: 1_000_000));
                // ETF1: qualifies on metrics, but excluded by finviz-funds-20261102.txt.
                rows.Add(BarRow("ETF1", "1d", start, 50m, high: 52m, low: 49m, close: 50m, volume: 1_000_000));
                // LOWADR: $50, high 50.2 / low 49.8 -> range 0.4 / 50 = 0.8% ADR < 2.5% -> filtered out.
                rows.Add(BarRow("LOWADR", "1d", start, 50m, high: 50.2m, low: 49.8m, close: 50m, volume: 1_000_000));
                // SPLT: qualifies on metrics, but has a split on days[22] -> excluded during 10-session lookback.
                rows.Add(BarRow("SPLT", "1d", start, 50m, high: 52m, low: 49m, close: 50m, volume: 1_000_000));
            }

            var barsId = await CommitBarsAsync("1d", null, rows);
            var actionsId = await CommitSplitsAsync([("SPLT", days[22], 2m)]);
            var sessionsId = await CommitSessionsAsync(days[0], days[^1].AddDays(1));
            var secMasterId = await CommitSecurityMasterAsync(["AAA", "ETF1", "LOWADR", "SPLT"], days[0]);
            return (barsId, actionsId, sessionsId, secMasterId, from, to);
        }

        public async Task<string> CommitBarsAsync(
            string timeframe,
            string? rankedListSha256,
            IReadOnlyList<MarketBarEvidenceRow> rows,
            string? newYorkWindow = null,
            TimeOnly? sessionEndTime = null)
        {
            var publisher = new EvidenceParquetPartitionPublisher(codec, Store);
            var partitions = new List<EvidenceDatasetPartitionManifest>();
            if (String.Equals(timeframe, "5m", StringComparison.OrdinalIgnoreCase))
            {
                // Real 5m datasets are partitioned by New York trading session so multi-day symbols span multiple partitions.
                var endClock = sessionEndTime ?? new TimeOnly(16, 0);
                foreach (var group in rows.GroupBy(r => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(r.BarStartUtc, NewYork).DateTime)))
                {
                    var symbols = group.Select(r => r.Symbol).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                    var securityIds = group.Select(r => r.SecurityId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                    var provenance = new EvidencePartitionProvenance(
                        "alpaca",
                        "/v2/stocks/bars",
                        "sip",
                        "raw",
                        "USD",
                        group.Key,
                        securityIds,
                        [],
                        symbols,
                        timeframe,
                        ToNewYorkUtc(group.Key, new TimeOnly(9, 30)),
                        ToNewYorkUtc(group.Key, endClock));
                    partitions.Add(await publisher.PublishMarketBarsAsTradedAsync(
                        new EvidenceParquetPartitionRequest(
                            $"{timeframe}-{group.Key:yyyyMMdd}-{Guid.NewGuid():N}",
                            EvidenceDatasetKind.MarketBarsAsTraded,
                            1,
                            provenance,
                            [SyntheticSource],
                            RunId,
                            ConfigHash,
                            Normalizer,
                            CodeVersion,
                            new EvidenceQualityReport(),
                            new EvidenceObjectNamespace("normalized/synthetic")),
                        group.ToArray()));
                }
            }
            else
            {
                foreach (var group in rows.GroupBy(r => r.Symbol, StringComparer.Ordinal))
                {
                    var provenance = new EvidencePartitionProvenance(
                        "alpaca",
                        "/v2/stocks/bars",
                        "sip",
                        "raw",
                        "USD",
                        DateOnly.FromDateTime(group.Min(r => r.BarStartUtc).UtcDateTime),
                        [group.First().SecurityId],
                        [],
                        [group.Key],
                        timeframe,
                        group.Min(r => r.BarStartUtc),
                        group.Max(r => r.BarStartUtc).AddDays(1));
                    partitions.Add(await publisher.PublishMarketBarsAsTradedAsync(
                        new EvidenceParquetPartitionRequest(
                            $"{timeframe}-{group.Key}-{Guid.NewGuid():N}",
                            EvidenceDatasetKind.MarketBarsAsTraded,
                            1,
                            provenance,
                            [SyntheticSource],
                            RunId,
                            ConfigHash,
                            Normalizer,
                            CodeVersion,
                            new EvidenceQualityReport(),
                            new EvidenceObjectNamespace("normalized/synthetic")),
                        group.ToArray()));
                }
            }

            Dictionary<string, string>? attributes = null;
            if (rankedListSha256 is not null || newYorkWindow is not null)
            {
                attributes = new Dictionary<string, string>(StringComparer.Ordinal);
                if (rankedListSha256 is not null) attributes["ranked_list_sha256"] = rankedListSha256;
                if (newYorkWindow is not null) attributes["new_york_window"] = newYorkWindow;
                else if (String.Equals(timeframe, "5m", StringComparison.OrdinalIgnoreCase)) attributes["new_york_window"] = IntradayBarsCollectionPlanner.FullRegularSessionWindow;
            }

            var manifest = EvidenceParquetPartitionPublisher.BuildDatasetManifest(
                EvidenceDatasetKind.MarketBarsAsTraded,
                1,
                DateTimeOffset.UtcNow,
                "job-" + Guid.NewGuid().ToString("N"),
                new string('a', 64),
                ConfigHash,
                CodeVersion,
                Normalizer,
                "sip",
                partitions,
                new EvidenceQualityReport(),
                attributes);
            var commit = await Catalog.CommitDatasetAsync(manifest);
            return commit.Id;
        }

        public async Task<string> CommitSplitsAsync(IReadOnlyList<(string Symbol, DateOnly ExDate, decimal Ratio)> splits)
        {
            var received = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
            var rows = splits.Select((split, index) => new CorporateActionEvidenceRow(
                1, RunId, ConfigHash, CodeVersion, "alpaca-market-data", "alpaca",
                CorporateActionEvidenceType.ForwardSplit, $"split-{index}-{split.Symbol}", null, null, split.Symbol, null,
                split.ExDate, split.ExDate, split.ExDate, null, null,
                new Dictionary<string, string>
                {
                    ["symbol"] = split.Symbol,
                    ["old_rate"] = "1",
                    ["new_rate"] = split.Ratio.ToString(System.Globalization.CultureInfo.InvariantCulture)
                },
                received,
                [new EvidenceRowSourceAddress(SyntheticSource.ObservationId, SyntheticSource.Artifact.Content.Sha256)])).ToArray();
            var first = splits.Min(s => s.ExDate);
            var provenance = new EvidencePartitionProvenance(
                "alpaca", "/v1/corporate-actions", "alpaca-market-data", "raw", "USD", first, [], [],
                splits.Select(s => s.Symbol).Distinct().ToArray(), "event",
                new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                new DateTimeOffset(splits.Max(s => s.ExDate).AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
            var partition = await new EvidenceParquetPartitionPublisher(codec, Store).PublishCorporateActionsAsync(
                new EvidenceParquetPartitionRequest(
                    "actions-" + Guid.NewGuid().ToString("N"),
                    EvidenceDatasetKind.CorporateActions,
                    1,
                    provenance,
                    [SyntheticSource],
                    RunId,
                    ConfigHash,
                    Normalizer,
                    CodeVersion,
                    new EvidenceQualityReport(),
                    new EvidenceObjectNamespace("normalized/synthetic-actions")),
                rows);
            var manifest = EvidenceParquetPartitionPublisher.BuildDatasetManifest(
                EvidenceDatasetKind.CorporateActions,
                1,
                DateTimeOffset.UtcNow,
                "job-" + Guid.NewGuid().ToString("N"),
                new string('c', 64),
                ConfigHash,
                CodeVersion,
                Normalizer,
                "alpaca-market-data",
                [partition],
                new EvidenceQualityReport());
            return (await Catalog.CommitDatasetAsync(manifest)).Id;
        }

        public async Task<string> CommitSessionsAsync(DateOnly from, DateOnly toExclusive)
        {
            var rows = new List<ExchangeSessionEvidenceRow>();
            for (var day = from; day < toExclusive; day = day.AddDays(1))
            {
                if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                rows.Add(new ExchangeSessionEvidenceRow(
                    1, RunId, ConfigHash, CodeVersion, "alpaca-trading", "alpaca", "XNYS", day,
                    ToNewYorkUtc(day, new TimeOnly(4, 0)),
                    ToNewYorkUtc(day, new TimeOnly(9, 30)),
                    ToNewYorkUtc(day, new TimeOnly(16, 0)),
                    ToNewYorkUtc(day, new TimeOnly(20, 0)),
                    false,
                    "alpaca_official_market_calendar",
                    ToNewYorkUtc(day, TimeOnly.MinValue),
                    [new EvidenceRowSourceAddress(SyntheticSource.ObservationId, SyntheticSource.Artifact.Content.Sha256)]));
            }

            var provenance = new EvidencePartitionProvenance(
                "alpaca", "/v2/calendar", "alpaca-trading", "raw", "USD", from, [], [], ["US_EQUITIES"], "session",
                new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                new DateTimeOffset(toExclusive.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
            var partition = await new EvidenceParquetPartitionPublisher(codec, Store).PublishExchangeSessionsAsync(
                new EvidenceParquetPartitionRequest(
                    "sessions-" + Guid.NewGuid().ToString("N"),
                    EvidenceDatasetKind.ExchangeSessions,
                    1,
                    provenance,
                    [SyntheticSource],
                    RunId,
                    ConfigHash,
                    Normalizer,
                    CodeVersion,
                    new EvidenceQualityReport(),
                    new EvidenceObjectNamespace("normalized/synthetic-sessions")),
                rows);
            var manifest = EvidenceParquetPartitionPublisher.BuildDatasetManifest(
                EvidenceDatasetKind.ExchangeSessions,
                1,
                DateTimeOffset.UtcNow,
                "job-" + Guid.NewGuid().ToString("N"),
                new string('d', 64),
                ConfigHash,
                CodeVersion,
                Normalizer,
                "alpaca-trading",
                [partition],
                new EvidenceQualityReport());
            return (await Catalog.CommitDatasetAsync(manifest)).Id;
        }

        public async Task<string> CommitSecurityMasterAsync(IReadOnlyList<string> symbols, DateOnly asOf)
        {
            var stamp = new DateTimeOffset(asOf.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var rows = symbols.Select(symbol => new SecurityMasterEvidenceRow(
                1, RunId, ConfigHash, CodeVersion, "alpaca-trading", "alpaca",
                "sec-" + symbol.ToLowerInvariant(),
                "sec-" + symbol.ToLowerInvariant(),
                symbol,
                "NASDAQ",
                "us_equity",
                "USD",
                "active",
                stamp,
                null,
                stamp,
                stamp,
                [new EvidenceRowSourceAddress(SyntheticSource.ObservationId, SyntheticSource.Artifact.Content.Sha256)])).ToArray();
            var provenance = new EvidencePartitionProvenance(
                "alpaca", "/v2/assets", "alpaca-trading", "raw", "USD", asOf,
                rows.Select(r => r.SecurityId).ToArray(),
                rows.Select(r => r.IssuerId).ToArray(),
                symbols.ToArray(),
                "snapshot",
                stamp,
                stamp.AddDays(1));
            var partition = await new EvidenceParquetPartitionPublisher(codec, Store).PublishSecurityMasterAsync(
                new EvidenceParquetPartitionRequest(
                    "secmaster-" + Guid.NewGuid().ToString("N"),
                    EvidenceDatasetKind.SecurityMaster,
                    1,
                    provenance,
                    [SyntheticSource],
                    RunId,
                    ConfigHash,
                    Normalizer,
                    CodeVersion,
                    new EvidenceQualityReport(),
                    new EvidenceObjectNamespace("normalized/synthetic-secmaster")),
                rows);
            var manifest = EvidenceParquetPartitionPublisher.BuildDatasetManifest(
                EvidenceDatasetKind.SecurityMaster,
                1,
                DateTimeOffset.UtcNow,
                "job-" + Guid.NewGuid().ToString("N"),
                new string('e', 64),
                ConfigHash,
                CodeVersion,
                Normalizer,
                "alpaca-trading",
                [partition],
                new EvidenceQualityReport());
            return (await Catalog.CommitDatasetAsync(manifest)).Id;
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            root.Dispose();
        }
    }

    private sealed class SqliteDbContextFactory(DbContextOptions<TradingFlowDbContext> options)
        : IDbContextFactory<TradingFlowDbContext>
    {
        public TradingFlowDbContext CreateDbContext() => new(options);
    }
}
