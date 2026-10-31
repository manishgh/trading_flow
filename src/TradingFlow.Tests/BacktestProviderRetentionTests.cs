using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using TradingFlow.Alpaca;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Finviz;

namespace TradingFlow.Tests;

public sealed class BacktestProviderRetentionTests
{
    /// <summary>Exercises real provider preparation: terminal spools await quant review while first downloads remain byte-exact.</summary>
    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task ProviderOriginals_SurviveParentAndNestedWorkspaceRetention(string outcome)
    {
        await using var fixture = await BacktestMatrixFixture.CreateAsync(1, 1);
        var output = Path.Combine(fixture.Root, "retention-check");
        var originalRoot = Path.Combine(output, "originals");
        Directory.CreateDirectory(originalRoot);
        var sentinel = Path.Combine(originalRoot, "existing-source.txt");
        await File.WriteAllTextAsync(sentinel, "pre-existing shared source");
        var reader = new SimpleYamlReader();
        var run = reader.ReadBacktestRun(Path.Combine(fixture.Root, "configs", "backtest", "matrix.yaml"));
        run = run with
        {
            RawRoot = originalRoot,
            Tickers = ["AAPL"],
            Universe = null,
            TimeWindow = run.TimeWindow with
            {
                Type = "fixed",
                Start = DateTimeOffset.Parse("2026-06-08T04:00:00Z"),
                End = DateTimeOffset.Parse("2026-06-10T04:00:00Z"),
                WarmupLookbackDays = 0
            }
        };
        using var handler = new RetentionHttpHandler();
        var clients = new List<HttpClient>();
        // Every real adapter owns a client; only the synthetic transport is shared.
        HttpClient CreateClient()
        {
            var client = new HttpClient(handler, disposeHandler: false);
            clients.Add(client);
            return client;
        }
        var providers = new ConfiguredDataProviders(
            () => AlpacaOptions.Create(ProductionProfile.Paper) with { KeyId = "fixture", SecretKey = "fixture" },
            FinvizOptions.CreateDefault, () => null, CreateClient);
        var catalog = new StrategyArtifactCatalog(fixture.Root, Path.Combine(fixture.Root, "configs", "strategy-catalog.json"), reader);
        var runner = new BacktestRunner(reader, catalog, dataProviders: providers);
        var strategy = reader.ReadStrategy(run.Strategies.Single());
        var resultPath = Path.Combine(output, "result.json");
        var parent = runner.CreateWorkspace(run, resultPath);
        var nested = parent.Runner.CreateWorkspace(parent.Run, Path.Combine(output, "nested-result.json"));
        Assert.Equal(originalRoot, parent.Run.RawRoot);
        Assert.Equal(originalRoot, nested.Run.RawRoot);
        Assert.NotEqual(parent.InputRoot, nested.InputRoot);
        try
        {
            // Preparation uses actual HTTP adapters and durable response archiving, not a replacement market provider.
            async Task<int> Execute()
            {
                var prepared = await parent.Runner.PrepareMarketAsync(parent.Run, [strategy], default);
                var collected = handler.Payloads.Count;
                await nested.ExecuteAsync(async () =>
                {
                    var reused = await nested.Runner.PrepareMarketAsync(nested.Run, [strategy], default);
                    Assert.Equal(prepared.PreparedMarketData!.SnapshotSha256, reused.PreparedMarketData!.SnapshotSha256);
                    Assert.Equal(collected, handler.Payloads.Count);
                    return 1;
                });
                await File.WriteAllTextAsync(resultPath, "published outcome");
                if (outcome == "failure") throw new InvalidOperationException("execution failed after collection");
                if (outcome == "cancel") throw new OperationCanceledException();
                return 1;
            }

            if (outcome == "failure")
                await Assert.ThrowsAsync<InvalidOperationException>(() => parent.ExecuteAsync(Execute));
            else if (outcome == "cancel")
                await Assert.ThrowsAsync<OperationCanceledException>(() => parent.ExecuteAsync(Execute));
            else
                Assert.Equal(1, await parent.ExecuteAsync(Execute));

            Assert.True(Directory.Exists(parent.InputRoot));
            Assert.True(Directory.Exists(nested.InputRoot));
            Assert.Equal("published outcome", await File.ReadAllTextAsync(resultPath));
            Assert.Equal("pre-existing shared source", await File.ReadAllTextAsync(sentinel));
            Assert.True(handler.Payloads.Count >= 2, "Initial preparation must retain original calendar and bar responses.");
            var manifests = Directory.GetFiles(originalRoot, "*.manifest.json", SearchOption.AllDirectories);
            Assert.Equal(handler.Payloads.Count, manifests.Length);
            foreach (var path in manifests)
            {
                var manifest = JsonSerializer.Deserialize<RawArchiveManifest>(await File.ReadAllTextAsync(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                var payload = await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(path)!, manifest.PayloadFileName));
                Assert.Contains(Convert.ToHexStringLower(SHA256.HashData(payload)), handler.Payloads);
                Assert.Equal(manifest.Sha256, Convert.ToHexStringLower(SHA256.HashData(payload)));
                Assert.Equal(manifest.ByteLength, payload.LongLength);
            }
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
            // Fixture disposal owns these isolated synthetic inputs; executed workspaces already released prepared handles.
            parent.Runner.ReleasePreparedData();
            nested.Runner.ReleasePreparedData();
        }
    }

    /// <summary>Reusing an output location starts a new refresh, while each rolling execution holds one exact clock through preparation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneRefresh_RepeatedOutputHasFreshIdentityAndResolvedClock(bool rolling)
    {
        await using var fixture = await BacktestMatrixFixture.CreateAsync(1, 1);
        var reader = new SimpleYamlReader();
        var run = reader.ReadBacktestRun(Path.Combine(fixture.Root, "configs", "backtest", "matrix.yaml"));
        run = run with { CachePolicy = "refresh", Tickers = ["AAPL"], Universe = null,
            TimeWindow = run.TimeWindow with { Type = rolling ? "rolling" : "fixed",
                Start = DateTimeOffset.Parse("2026-06-08T04:00:00Z"),
                End = rolling ? null : DateTimeOffset.Parse("2026-06-10T04:00:00Z"), WarmupLookbackDays = 0 } };
        using var transport = new RetentionHttpHandler();
        var clients = new List<HttpClient>();
        // The configured adapter stays real; only its HTTP transport emits synthetic evidence.
        HttpClient CreateClient()
        {
            var client = new HttpClient(transport, false);
            clients.Add(client);
            return client;
        }
        var providers = new ConfiguredDataProviders(
            () => AlpacaOptions.Create(ProductionProfile.Paper) with { KeyId = "fixture", SecretKey = "fixture" },
            FinvizOptions.CreateDefault, () => null, CreateClient);
        var runner = new BacktestRunner(reader, new StrategyArtifactCatalog(fixture.Root,
            Path.Combine(fixture.Root, "configs", "strategy-catalog.json"), reader), dataProviders: providers);
        var strategy = reader.ReadStrategy(run.Strategies.Single());
        PreparedMarketDataSummary? first = null;
        var firstCaptureCount = 0;
        string[] committed = [];
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var workspace = runner.CreateWorkspace(run, Path.Combine(fixture.Root, "same-output.json"));
                Assert.NotNull(workspace.Run.TimeWindow.End);
                var market = await workspace.ExecuteAsync(() => workspace.Runner.PrepareMarketAsync(workspace.Run, [strategy], default));
                var summary = Assert.IsType<PreparedMarketDataSummary>(market.PreparedMarketData);
                Assert.Equal(workspace.Run.TimeWindow.End, market.WindowEnd);
                Assert.Equal(workspace.Run.TimeWindow.End, summary.EndUtc);
                Assert.True(Directory.Exists(workspace.InputRoot));
                if (attempt == 0)
                {
                    first = summary;
                    firstCaptureCount = transport.Payloads.Count;
                    committed = Directory.GetFiles(Path.Combine(run.NormalizedRoot, "_historical"), "version-*.json", SearchOption.AllDirectories);
                }
                else
                {
                    Assert.NotEqual(first!.SnapshotId, summary.SnapshotId);
                    Assert.NotEqual(first.SnapshotSha256, summary.SnapshotSha256);
                    Assert.True(transport.Payloads.Count > firstCaptureCount);
                    Assert.Equal(0, summary.ReusedQueryCount);
                    Assert.All(committed, path => Assert.True(File.Exists(path)));
                }
            }
        }
        finally { foreach (var client in clients) client.Dispose(); }
    }

    /// <summary>A resumed typed rolling run resolves its old admitted cutoff before workspace creation rather than using today's clock.</summary>
    [Fact]
    public async Task TypedRollingRecovery_UsesVerifiedAdmittedCutoff()
    {
        await using var fixture = await BacktestMatrixFixture.CreateAsync(1, 1);
        var reader = new SimpleYamlReader();
        var run = reader.ReadBacktestRun(Path.Combine(fixture.Root, "configs", "backtest", "matrix.yaml"));
        var cutoff = DateTimeOffset.Parse("2026-06-10T04:00:00Z");
        run = run with { Tickers = ["AAPL"], Universe = new(UniverseConfig.ResolvedSnapshotMode, [], 0, 0, 1, null, null), TimeWindow = run.TimeWindow with {
            Type = "rolling", Start = cutoff.AddDays(-2), End = null, WarmupLookbackDays = 0 } };
        var strategy = reader.ReadStrategy(run.Strategies.Single());
        var reference = await PreparedMarketDataService.WriteRequestAsync(new(1, "rolling-recovery", cutoff,
            [new(run.RunName, run with { Strategies = [] }, [strategy])]), Path.Combine(fixture.Root, "request.json"), default);
        using var transport = new RetentionHttpHandler();
        var clients = new List<HttpClient>();
        // Captures real adapter responses in isolated storage while forbidding any external request.
        HttpClient CreateClient()
        {
            var client = new HttpClient(transport, false);
            clients.Add(client);
            return client;
        }
        var providers = new ConfiguredDataProviders(
            () => AlpacaOptions.Create(ProductionProfile.Paper) with { KeyId = "fixture", SecretKey = "fixture" },
            FinvizOptions.CreateDefault, () => null, CreateClient);
        var runner = new BacktestRunner(reader, new StrategyArtifactCatalog(fixture.Root,
            Path.Combine(fixture.Root, "configs", "strategy-catalog.json"), reader), dataProviders: providers);
        try
        {
            var result = await runner.RunAsync(run, [strategy], cutoff, Path.Combine(fixture.Root, "recovered-result.json"),
                default, preparedDataReference: reference);
            Assert.Equal(cutoff, result.PreparedMarketData!.EndUtc);
            Assert.NotEmpty(Directory.GetDirectories(fixture.Root, ".run-inputs-*", SearchOption.AllDirectories));
        }
        finally { foreach (var client in clients) client.Dispose(); }
    }

    private sealed class RetentionHttpHandler : HttpMessageHandler
    {
        public List<string> Payloads { get; } = [];

        /// <summary>Returns original synthetic calendar/bar bytes and records their hashes without using credentials or external endpoints.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            string payload;
            if (uri.AbsolutePath.EndsWith("/calendar", StringComparison.Ordinal))
            {
                var query = QueryHelpers.ParseQuery(uri.Query);
                var first = DateOnly.Parse(query["start"].ToString());
                var last = DateOnly.Parse(query["end"].ToString());
                payload = JsonSerializer.Serialize(Enumerable.Range(0, last.DayNumber - first.DayNumber + 1)
                    .Select(first.AddDays).Where(date => date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
                    .Select(date => new { date = date.ToString("yyyy-MM-dd"), open = "09:30", close = "16:00" }));
            }
            else
            {
                Assert.EndsWith("/bars", uri.AbsolutePath);
                payload = """{"bars":{"AAPL":[{"t":"2026-06-09T04:00:00Z","o":100,"h":102,"l":99,"c":101,"v":1000}]},"next_page_token":null}""";
            }
            var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
            Payloads.Add(Convert.ToHexStringLower(SHA256.HashData(bytes)));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(bytes)
            });
        }
    }
}
