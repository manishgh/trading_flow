using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Backtesting;
using TradingFlow.Backtesting.Optimization;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Finviz;

namespace TradingFlow.Tests;

public sealed class PreparedOptimizerExecutionTests
{
    /// <summary>Runs the real optimizer and normal archived adapters against synthetic HTTP bytes, freezing one source selection for both permutations.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Optimizer_PinsOnePreparedSelectionAcrossPermutationsAndFreezesRollingEnd(bool configuredEnd)
    {
        await using var fixture = await BacktestMatrixFixture.CreateAsync(1, 1);
        var plan = await fixture.Plans.SaveAsync(new(fixture.Request, 1));
        var accepted = Assert.Single((await fixture.Plans.StartAsync(plan.PlanId, new("1.0", "synthetic-optimizer-profile"))).Jobs);
        var job = (await fixture.Jobs.GetJobAsync(accepted.JobId, default))!;
        var reader = new SimpleYamlReader();
        var admitted = reader.ReadBacktestRun(job.ConfigPath);
        var expectedEnd = fixture.Request.TestWindowEndUtc;
        var days = checked((int)Math.Ceiling((expectedEnd - fixture.Request.TestWindowStartUtc).TotalDays));
        var profilePath = Path.Combine(Path.GetDirectoryName(job.ConfigPath)!, "synthetic-optimizer-rolling.yaml");
        await File.WriteAllTextAsync(profilePath, RollingProfile(await File.ReadAllTextAsync(job.ConfigPath), days, configuredEnd));
        var profile = reader.ReadBacktestRun(profilePath);
        Assert.Equal("rolling", profile.TimeWindow.Type);
        Assert.Null(profile.TimeWindow.Start);
        Assert.Equal(configuredEnd ? expectedEnd : null, profile.TimeWindow.End);
        var optimizerPath = Path.Combine(Path.GetDirectoryName(job.ConfigPath)!, "synthetic-optimizer.yaml");
        await File.WriteAllTextAsync(optimizerPath, $"""
            run_name: synthetic-prepared-optimizer
            base_strategy: {admitted.Strategies.Single().Replace('\\', '/')}
            backtest_config: {profilePath.Replace('\\', '/')}
            metric: NetProfit
            top_n_results: 2
            parameters:
              exit_rules.target_r_multiple: [1.0, 1.5]
            """);
        using var transport = new SyntheticAlpacaTransport();
        var providers = new ConfiguredDataProviders(
            () => AlpacaOptions.Create(ProductionProfile.Paper) with { KeyId = "synthetic-fixture-key", SecretKey = "synthetic-fixture-secret" },
            FinvizOptions.CreateDefault, () => null, transport.CreateClient);
        var artifacts = new StrategyArtifactCatalog(fixture.Root, Path.Combine(fixture.Root, "configs", "strategy-catalog.json"), reader);
        // No injected provider factory: the ordinary runner must prepare and archive through ConfiguredDataProviders.
        var runner = new BacktestRunner(reader, artifacts, dataProviders: providers);
        var owner = Path.Combine(fixture.Root, "optimizer-results");
        var result = await new StrategyOptimizer(reader, runner).OptimizeAsync(optimizerPath, default,
            durableAttemptRoot: owner, evaluationCutoffUtc: configuredEnd ? expectedEnd.AddDays(5) : expectedEnd);
        Assert.Equal(2, result.TotalPermutations);
        Assert.Equal(2, result.TopRuns.Count);
        Assert.Equal(new[] { 1m, 1.5m }, result.TopRuns.Select(run => Convert.ToDecimal(run.ParameterValues["exit_rules.target_r_multiple"], CultureInfo.InvariantCulture)).Order());
        var summaries = result.TopRuns.Select(run => Assert.IsType<TradingFlow.Domain.Backtesting.PreparedMarketDataSummary>(run.BacktestResult.PreparedMarketData)).ToArray();
        Assert.Single(summaries.Select(summary => summary.SnapshotSha256).Distinct(StringComparer.Ordinal));
        Assert.Single(summaries.Select(summary => summary.SnapshotId).Distinct(StringComparer.Ordinal));
        Assert.All(summaries, summary => {
            Assert.Equal(expectedEnd, summary.EndUtc);
            Assert.Equal(expectedEnd.AddDays(-days - profile.TimeWindow.WarmupLookbackDays), summary.StartUtc);
            // The test-window calendar is a slice of the captured warmup calendar.
            Assert.Equal(1, summary.ReusedQueryCount);
            Assert.Equal(2, summary.DownloadedQueryCount);
        });
        Assert.All(result.TopRuns, run => {
            Assert.True(run.BacktestResult.EconomicResultsComplete);
            Assert.True(run.BacktestResult.ProcessedBarCount > 0);
            Assert.True(File.Exists(run.BacktestResult.ResultPath));
        });
        Assert.Single(transport.BarQueries);
        Assert.Single(transport.CalendarQueries);
        Assert.All(transport.BarQueries.Values.Concat(transport.CalendarQueries.Values), count => Assert.Equal(1, count));
        Assert.Equal(expectedEnd, transport.BarEndUtc);
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(profile.NormalizedRoot, "_historical"), "version-*.json", SearchOption.AllDirectories).Count());
        var receipts = Directory.EnumerateFiles(profile.RawRoot, "*.manifest.json", SearchOption.AllDirectories).ToArray();
        Assert.Equal(2, receipts.Length);
        foreach (var path in receipts)
        {
            var receipt = JsonSerializer.Deserialize<RawArchiveManifest>(await File.ReadAllTextAsync(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var bytes = await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(path)!, receipt.PayloadFileName));
            Assert.Equal(receipt.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            Assert.NotNull(receipt.Http);
            Assert.Equal(200, receipt.Http.StatusCode);
            Assert.Contains("synthetic_fixture", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        }
        Assert.True(File.Exists(Path.Combine(owner, "summary.json")));
        Assert.Single(Directory.EnumerateFiles(fixture.Root, "snapshot.json", SearchOption.AllDirectories));
        Assert.NotEmpty(Directory.EnumerateDirectories(fixture.Root, ".run-inputs-*", SearchOption.AllDirectories));
    }

    /// <summary>Creates a new rolling profile beside its captured strategy paths; accepted source files remain immutable.</summary>
    private static string RollingProfile(string source, int lookbackDays, bool keepEnd)
    {
        var output = new List<string>();
        var inWindow = false;
        foreach (var line in source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length > 0 && !Char.IsWhiteSpace(line[0])) inWindow = line == "time_window:";
            if (inWindow && line.StartsWith("  type:", StringComparison.Ordinal)) output.Add("  type: rolling");
            else if (inWindow && line.StartsWith("  lookback_days:", StringComparison.Ordinal)) output.Add("  lookback_days: " + lookbackDays.ToString(CultureInfo.InvariantCulture));
            else if (inWindow && (line.StartsWith("  start:", StringComparison.Ordinal) || (!keepEnd && line.StartsWith("  end:", StringComparison.Ordinal)))) continue;
            else output.Add(line);
        }
        return String.Join(Environment.NewLine, output);
    }

    /// <summary>Seals every HTTP response inside this fixture; it cannot delegate requests to a network handler.</summary>
    private sealed class SyntheticAlpacaTransport : HttpMessageHandler
    {
        private readonly List<HttpClient> clients = [];
        public ConcurrentDictionary<string, int> BarQueries { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, int> CalendarQueries { get; } = new(StringComparer.Ordinal);
        public DateTimeOffset? BarEndUtc { get; private set; }

        /// <summary>Owns each adapter client while sharing only this deterministic in-memory transport.</summary>
        public HttpClient CreateClient()
        {
            var client = new HttpClient(this, disposeHandler: false);
            clients.Add(client);
            return client;
        }

        /// <summary>Returns complete synthetic bars or open-calendar dates with explicit completed pagination and records exact query repetition.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(HttpMethod.Get, request.Method);
            var uri = request.RequestUri!;
            var query = uri.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2))
                .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);
            string payload;
            if (uri.AbsolutePath == "/v2/stocks/bars")
            {
                BarQueries.AddOrUpdate(uri.AbsoluteUri, 1, static (_, count) => count + 1);
                Assert.Equal("S000", query["symbols"]);
                Assert.Equal("1Day", query["timeframe"]);
                Assert.Equal("sip", query["feed"]);
                Assert.Equal("all", query["adjustment"]);
                var start = DateTimeOffset.Parse(query["start"], CultureInfo.InvariantCulture);
                var end = DateTimeOffset.Parse(query["end"], CultureInfo.InvariantCulture);
                BarEndUtc = end;
                payload = JsonSerializer.Serialize(new { synthetic_fixture = true,
                    bars = new Dictionary<string, object[]> { ["S000"] = Bars(start, end) }, next_page_token = (string?)null });
            }
            else
            {
                Assert.Equal("/v2/calendar", uri.AbsolutePath);
                CalendarQueries.AddOrUpdate(uri.AbsoluteUri, 1, static (_, count) => count + 1);
                var start = DateOnly.Parse(query["start"], CultureInfo.InvariantCulture);
                var end = DateOnly.Parse(query["end"], CultureInfo.InvariantCulture);
                var sessions = new List<object>();
                for (var date = start; date <= end; date = date.AddDays(1))
                    if (date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
                        sessions.Add(new { date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), open = "09:30", close = "16:00", synthetic_fixture = true });
                payload = JsonSerializer.Serialize(sessions);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                RequestMessage = request, Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json") });
        }

        /// <summary>Reproduces the matrix's fixed New York daily samples while honoring the adapter's exact half-open UTC bounds.</summary>
        private static object[] Bars(DateTimeOffset start, DateTimeOffset end)
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            var bars = new List<object>();
            var index = 0;
            for (var date = new DateOnly(2025, 1, 2); index < 300; date = date.AddDays(1))
            {
                if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                var time = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), zone));
                var close = index < 290 ? 100m + index / 10m : 100m - (index - 290);
                if (time >= start && time < end)
                    bars.Add(new { t = time.ToString("O", CultureInfo.InvariantCulture), o = close - .05m, h = close + 1m, l = close - 1m, c = close, v = 1000000m });
                index++;
            }
            return bars.ToArray();
        }

        /// <summary>Stops every client created by this test without disposing unrelated adapters or services.</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var client in clients) client.Dispose();
            base.Dispose(disposing);
        }
    }
}
